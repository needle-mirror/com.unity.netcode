using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Scenes;
using Unity.Transforms;

public struct PreSpawnsInitialized : IComponentData
{}

public struct HighestPrespawnIDAllocated : IComponentData
{
    public int GhostId;
}

[UpdateInGroup(typeof(GhostSimulationSystemGroup))]
[UpdateAfter(typeof(GhostCollectionSystem))]
public class PopulatePreSpawnedGhosts : SystemBase
{
    private EntityQuery m_UninitializedScenes;
    private EntityQuery m_Prespawns;

    protected override void OnCreate()
    {
        // Assumes that at this point all subscenes should be already loaded
        m_UninitializedScenes = EntityManager.CreateEntityQuery(new EntityQueryDesc()
        {
            All = new[] { ComponentType.ReadOnly<SceneReference>() },
            None = new[] { ComponentType.ReadOnly<PreSpawnsInitialized>() }
        });
        m_Prespawns =
            EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostId>());
        // Require any number of in-game tags, server can have one per client
        var inGame = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
        RequireForUpdate(inGame);
        RequireForUpdate(m_UninitializedScenes);
        RequireSingletonForUpdate<GhostCollection>();
    }

    protected override unsafe void OnUpdate()
    {
        var preSpawnsAlreadyProcessed = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnsInitialized>())
            .CalculateEntityCount();
        if (preSpawnsAlreadyProcessed > 0)
        {
            // Check if an uninitialized scene has appeared with no ghosts present inside, then just mark it as initialized and continue
            var prespawns = m_Prespawns.ToEntityArray(Allocator.TempJob);
            var newScenes = m_UninitializedScenes.ToEntityArray(Allocator.TempJob);
            for (int j = 0; j < newScenes.Length; ++j)
            {
                for (int i = 0; i < prespawns.Length; ++i)
                {
                    var scenes = EntityManager.GetSharedComponentData<SceneSection>(prespawns[i]);
                    var sceneSystem = World.GetExistingSystem<SceneSystem>();
                    var sceneEntity = sceneSystem.GetSceneEntity(scenes.SceneGUID);
                    if (sceneEntity == newScenes[j])
                    {
                        UnityEngine.Debug.LogError("[" + World.Name +
                                                   "] Prespawned ghosts have already been initialized, this needs to happen for all subscenes at the same time.");
                        return;
                    }
                }
                EntityManager.AddComponent<PreSpawnsInitialized>(newScenes[j]);
            }
            newScenes.Dispose();
            prespawns.Dispose();
            return;
        }

        // Handle the chunk for an entity type, then handle each entity in the chunk (prespawned entities)
        var prespawnChunk = m_Prespawns.CreateArchetypeChunkArray(Allocator.TempJob);
        var entityType = GetEntityTypeHandle();
        var preSpawnedIds = GetComponentDataFromEntity<PreSpawnedGhostId>();
        var subsceneMap = new NativeMultiHashMap<ulong, Entity>(32, Allocator.Temp);
        var subscenePadding = new NativeHashMap<ulong, int>(32, Allocator.Temp);
        var ghostComponents = GetComponentDataFromEntity<GhostComponent>();

        // Put all known subscene hashes tracked by the prespawned ghosts into a map for sorting
        for (int i = 0; i < prespawnChunk.Length; ++i)
        {
            var chunk = prespawnChunk[i];
            var entities = chunk.GetNativeArray(entityType);
            for (int j = 0; j < entities.Length; ++j)
            {
                var entity = entities[j];
                var subsceneHash = EntityManager.GetSharedComponentData<SubSceneGhostComponentHash>(entity).Value;
                subsceneMap.Add(subsceneHash, entity);
            }
        }

        var subsceneArray = subsceneMap.GetUniqueKeyArray(Allocator.Temp);

        // Figure out scene id padding or how many IDs were used by the previous scenes in the sorted list, continue
        // where it left off so each ghost in a scene starts of at the ID+1 of last ghost in the previous scene
        var scenePadding = 0;
        for (int i = 0; i < subsceneArray.Item2; ++i)
        {
            subscenePadding.Add(subsceneArray.Item1[i], scenePadding);
            scenePadding += subsceneMap.CountValuesForKey(subsceneArray.Item1[i]);
        }

        var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
        var serverSystems = World.GetExistingSystem<ServerSimulationSystemGroup>();
        var ghostTypes = GetComponentDataFromEntity<GhostTypeComponent>();
        var ghostReceiveSystem = World.GetExistingSystem<GhostReceiveSystem>();
        var ghostSendSystem = World.GetExistingSystem<GhostSendSystem>();
        var ghostTypeCollection = EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(GetSingletonEntity<GhostCollection>());
        int highestPrespawnId = -1;
        var spawnedGhosts = new NativeList<SpawnedGhostMapping>(1024, Allocator.Temp);

        // Create a lookup from ghost type component to prefab entity, used to figure out how to strip components on the client
        var prefabFromType = new NativeHashMap<GhostTypeComponent, Entity>(1024, Allocator.Temp);
        Entities.WithNone<GhostPrefabRuntimeStrip>().WithAll<Prefab>().ForEach((Entity ent, in GhostTypeComponent ghostType) =>
        {
            prefabFromType.TryAdd(ghostType, ent);
        }).Run();

        for (int i = 0; i < prespawnChunk.Length; ++i)
        {
            var chunk = prespawnChunk[i];
            var entities = chunk.GetNativeArray(entityType);

            for (int j = 0; j < entities.Length; ++j)
            {
                var entity = entities[j];

                var ghostTypeComponent = ghostTypes[entity];
                if (!prefabFromType.TryGetValue(ghostTypeComponent, out var ghostPrefabEntity))
                {
                    UnityEngine.Debug.LogError("Failed to look up ghost type for entity");
                    return;
                }

                // Check if this entity has already been handled
                if (ghostComponents[entity].ghostId != 0)
                {
                    UnityEngine.Debug.LogWarning(entity + " already has ghostId=" + ghostComponents[entity].ghostId + " prespawn=" + preSpawnedIds[entity].Value);
                    continue;
                }

                // Modfy the entity to its proper version
                if (EntityManager.HasComponent<GhostPrefabMetaDataComponent>(ghostPrefabEntity))
                {
                    ref var ghostMetaData = ref EntityManager.GetComponentData<GhostPrefabMetaDataComponent>(ghostPrefabEntity).Value.Value;
                    var linkedEntityGroup = EntityManager.GetBuffer<LinkedEntityGroup>(entity);
                    if (serverSystems != null)
                    {
                        for (int rm = 0; rm < ghostMetaData.RemoveOnServer.Length; ++rm)
                        {
                            var childIndexCompHashPair = ghostMetaData.RemoveOnServer[rm];
                            var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                            PostUpdateCommands.RemoveComponent(linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                        }
                    }
                    else
                    {
                        for (int rm = 0; rm < ghostMetaData.RemoveOnClient.Length; ++rm)
                        {
                            var childIndexCompHashPair = ghostMetaData.RemoveOnClient[rm];
                            var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                            PostUpdateCommands.RemoveComponent(linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                        }
                        // FIXME: should disable instead of removing once we have a way of doing that without structural changes
                        if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Predicted)
                        {
                            for (int rm = 0; rm < ghostMetaData.DisableOnPredictedClient.Length; ++rm)
                            {
                                var childIndexCompHashPair = ghostMetaData.DisableOnPredictedClient[rm];
                                var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                                PostUpdateCommands.RemoveComponent(linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                            }
                        }
                        else if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Interpolated)
                        {
                            for (int rm = 0; rm < ghostMetaData.DisableOnInterpolatedClient.Length; ++rm)
                            {
                                var childIndexCompHashPair = ghostMetaData.DisableOnInterpolatedClient[rm];
                                var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                                PostUpdateCommands.RemoveComponent(linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                            }
                        }
                    }
                }
                else
                {
                    UnityEngine.Debug.LogWarning("Could not find a valid ghost prefab for " + entity);
                }

                var subsceneHash = EntityManager.GetSharedComponentData<SubSceneGhostComponentHash>(entity).Value;
                var newId = preSpawnedIds[entity].Value + subscenePadding[subsceneHash];
                if (newId > highestPrespawnId)
                    highestPrespawnId = newId;

                // If on a server we need to allocate the ghost ID for the pre-spawned entity so runtime spawns
                // will happen from the right start index
                if (serverSystems != null)
                {
                    spawnedGhosts.Add(new SpawnedGhostMapping{ghost = new SpawnedGhost{ghostId = newId, spawnTick = 0}, entity = entity});
                    var ghostSystemStateComponent = new GhostSystemStateComponent
                    {
                        ghostId = newId, despawnTick = 0, spawnTick = 0
                    };
                    PostUpdateCommands.AddComponent(entity, ghostSystemStateComponent);
                }
                else if (ghostReceiveSystem != null)
                {
                    spawnedGhosts.Add(new SpawnedGhostMapping{ghost = new SpawnedGhost{ghostId = newId, spawnTick = 0}, entity = entity});
                }

                // GhostType -1 is a special case for prespawned ghosts which is converted to a proper ghost id in the send / receive systems
                // once the ghost ids are known
                // Pre-spawned uses spawnTick = 0, if there is a reference to a ghost and it has spawnTick 0 the ref is always resolved
                // This works because there despawns are high priority and we never create pre-spawned ghosts after connection
                var ghostComponent = new GhostComponent {ghostId = newId, ghostType = -1, spawnTick = 0};
                PostUpdateCommands.SetComponent(entity, ghostComponent);

                // Mark scene as processed, as whole scene will have been loaded when this entity appeared
                var sceneSection = EntityManager.GetSharedComponentData<SceneSection>(entity);
                var sceneSystem = World.GetExistingSystem<SceneSystem>();
                var sceneEntity = sceneSystem.GetSceneEntity(sceneSection.SceneGUID);
                PostUpdateCommands.AddComponent<PreSpawnsInitialized>(sceneEntity);
            }
        }
        if (ghostReceiveSystem != null && spawnedGhosts.Length > 0)
            ghostReceiveSystem.AddSpawnedGhosts(spawnedGhosts);
        else if (ghostSendSystem != null && spawnedGhosts.Length > 0)
            ghostSendSystem.AddSpawnedGhosts(spawnedGhosts);

        if (serverSystems != null)
        {
            var sendSystem = World.GetExistingSystem<GhostSendSystem>();
            if (sendSystem != null)
            {
                sendSystem.SetAllocatedGhostId(highestPrespawnId);
            }
            else
            {
                UnityEngine.Debug.LogError("Failed to look up ghost send system");
                return;
            }
        }

        // If there are no prespawns at all mark all subscenes as initialized
        if (m_Prespawns.CalculateEntityCount() == 0)
        {
            var newScenes = m_UninitializedScenes.ToEntityArray(Allocator.TempJob);
            for (int i = 0; i < newScenes.Length; ++i)
                PostUpdateCommands.AddComponent<PreSpawnsInitialized>(newScenes[i]);
            newScenes.Dispose();
        }

        var highestIdEntity = PostUpdateCommands.CreateEntity();
        PostUpdateCommands.AddComponent(highestIdEntity,
            new HighestPrespawnIDAllocated{ GhostId = highestPrespawnId});

        PostUpdateCommands.Playback(EntityManager);
        prespawnChunk.Dispose();
    }
}

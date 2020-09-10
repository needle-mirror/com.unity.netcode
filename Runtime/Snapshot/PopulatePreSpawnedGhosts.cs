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

[UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
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
        RequireSingletonForUpdate<GhostPrefabCollectionComponent>();
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
        var ghostPrefabBufferFromEntity = GetBufferFromEntity<GhostPrefabBuffer>(true);
        var prefabEntity = GetSingletonEntity<GhostPrefabCollectionComponent>();
        var ghostReceiveSystem = World.GetExistingSystem<GhostReceiveSystem>();
        var ghostCollectionSystem = World.GetExistingSystem<GhostCollectionSystem>();
        DynamicBuffer<GhostPrefabBuffer> prefabList = ghostPrefabBufferFromEntity[prefabEntity];
        int highestPrespawnId = -1;
        var spawnedGhosts = new NativeList<SpawnedGhostMapping>(1024, Allocator.Temp);
        for (int i = 0; i < prespawnChunk.Length; ++i)
        {
            var chunk = prespawnChunk[i];
            var entities = chunk.GetNativeArray(entityType);

            for (int j = 0; j < entities.Length; ++j)
            {
                var entity = entities[j];

                var ghostTypeComponent = ghostTypes[entity];
                int ghostType;
                for (ghostType = 0; ghostType < prefabList.Length; ++ghostType)
                {
                    if (ghostTypes[prefabList[ghostType].Value] == ghostTypeComponent)
                        break;
                }
                if (ghostType >= prefabList.Length)
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
                if (EntityManager.HasComponent<GhostPrefabMetaDataComponent>(prefabList[ghostType].Value))
                {
                    ref var ghostMetaData = ref EntityManager.GetComponentData<GhostPrefabMetaDataComponent>(prefabList[ghostType].Value).Value.Value;
                    if (serverSystems != null)
                    {
                        for (int rm = 0; rm < ghostMetaData.RemoveOnServer.Length; ++rm)
                        {
                            var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(ghostMetaData.RemoveOnServer[rm]));
                            PostUpdateCommands.RemoveComponent(entity, rmCompType);
                        }
                    }
                    else
                    {
                        for (int rm = 0; rm < ghostMetaData.RemoveOnClient.Length; ++rm)
                        {
                            var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(ghostMetaData.RemoveOnClient[rm]));
                            PostUpdateCommands.RemoveComponent(entity, rmCompType);
                        }
                        // FIXME: should disable instead of removing once we have a way of doing that without structural changes
                        if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Predicted)
                        {
                            for (int rm = 0; rm < ghostMetaData.DisableOnPredictedClient.Length; ++rm)
                            {
                                var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(ghostMetaData.DisableOnPredictedClient[rm]));
                                PostUpdateCommands.RemoveComponent(entity, rmCompType);
                            }
                        }
                        else if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Interpolated)
                        {
                            for (int rm = 0; rm < ghostMetaData.DisableOnInterpolatedClient.Length; ++rm)
                            {
                                var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(ghostMetaData.DisableOnInterpolatedClient[rm]));
                                PostUpdateCommands.RemoveComponent(entity, rmCompType);
                            }
                        }
                    }
                }

                var subsceneHash = EntityManager.GetSharedComponentData<SubSceneGhostComponentHash>(entity).Value;
                var newId = preSpawnedIds[entity].Value + subscenePadding[subsceneHash];
                if (newId > highestPrespawnId)
                    highestPrespawnId = newId;

                // If on a server we need to allocate the ghost ID for the pre-spawned entity so runtime spawns
                // will happen from the right start index
                if (serverSystems != null)
                {
                    var ghostSystemStateComponent = new GhostSystemStateComponent
                    {
                        ghostId = newId, despawnTick = 0
                    };
                    PostUpdateCommands.AddComponent(entity, ghostSystemStateComponent);
                }
                else if (ghostReceiveSystem != null)
                {
                    var snapshotSize = ghostCollectionSystem.m_GhostTypeCollection[ghostType].SnapshotSize;
                    spawnedGhosts.Add(new SpawnedGhostMapping{ghost = new SpawnedGhost{ghostId = newId, spawnTick = 0}, entity = entity});
                    var newBuffer = PostUpdateCommands.SetBuffer<SnapshotDataBuffer>(entity);
                    newBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    UnsafeUtility.MemClear(newBuffer.GetUnsafePtr(), snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    PostUpdateCommands.SetComponent(entity, new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0});
                }

                // Pre-spawned uses spawnTick = 0, if there is a reference to a ghost and it has spawnTick 0 the ref is always resolved
                // This works because there despawns are high priority and we never create pre-spawned ghosts after connection
                var ghostComponent = new GhostComponent {ghostId = newId, ghostType = ghostType, spawnTick = 0};
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

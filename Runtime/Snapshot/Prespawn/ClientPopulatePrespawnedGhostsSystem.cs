#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Unity.NetCode
{
    /// <summary>
    /// ClientPopulatePrespawnedGhostsSystem systems is responsible for assign to the pre-spawned ghosts
    /// their ghost ids and adding them to the spawned ghosts maps.
    /// It relies on the previous initializations step to determine the subscene subset to process.
    ///
    /// Clients expect to receive as part ot the protocol:
    /// - subscene hash and baseline hash for validation
    /// - the ghost id range for each subscene.
    /// ======================================================================
    /// THE PRESPAWN SUBSCENE SYNC PROTOCOL
    /// ======================================================================
    /// Client> will eventually receive the subscene data and will store it into the PrespawnHashElement collection
    /// Client> (in parallel, before or after) will serialize the prespawn baseline when a new scene is loaded
    /// Client> should validate that:
    ///   - the prespawn scenes are present on the server.
    ///   - that count, subscene hash and baseline hash match the one on the server
    /// Client> will assign the ghost ids to the prespawns
    /// Client> must notify the server what scene sections has been loaded and initialized.
    /// </summary>
    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(PrespawnGhostInitializationSystem))]
    public partial class ClientPopulatePrespawnedGhostsSystem : SystemBase
    {
        private EntityQuery m_UninitializedScenes;
        private EntityQuery m_Prespawns;
        private EntityQuery m_StreamInGame;
        private NetDebugSystem m_NetDebugSystem;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private GhostReceiveSystem m_GhostReceiveSystem;

        enum ValidationResult
        {
            ValidationSucceed = 0,
            SubSceneNotFound,
            MetadataNotMatch
        }

        protected override void OnCreate()
        {
            m_Barrier = World.GetExistingSystem<BeginSimulationEntityCommandBufferSystem>();
            // Assumes that at this point all subscenes should be already loaded
            m_UninitializedScenes = GetEntityQuery(
                ComponentType.ReadOnly<SubSceneWithPrespawnGhosts>(),
                ComponentType.ReadOnly<SubScenePrespawnBaselineResolved>(),
                ComponentType.Exclude<PrespawnsSceneInitialized>());
            m_Prespawns = GetEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                ComponentType.ReadOnly<SubSceneGhostComponentHash>());
            m_NetDebugSystem = World.GetOrCreateSystem<NetDebugSystem>();
            m_StreamInGame = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>(), ComponentType.Exclude<NetworkStreamDisconnected>());
            m_GhostReceiveSystem = World.GetExistingSystem<GhostReceiveSystem>();
            RequireForUpdate(m_UninitializedScenes);
            RequireForUpdate(m_Prespawns);
            RequireForUpdate(m_StreamInGame);
            RequireSingletonForUpdate<GhostCollection>();
            RequireSingletonForUpdate<PrespawnSceneLoaded>();
        }

        protected override void OnUpdate()
        {
            var subsceneCollection = EntityManager.GetBuffer<PrespawnSceneLoaded>(GetSingletonEntity<PrespawnSceneLoaded>());
            //Early exit. Nothing to process (the list is empty). That means the server has not sent yet the data OR the
            //subscene must be unloaded. In either cases, the client can't assign ids.
            if(subsceneCollection.Length == 0)
                return;

            var subScenesWithGhosts = m_UninitializedScenes.ToComponentDataArray<SubSceneWithPrespawnGhosts>(Allocator.Temp);
            //x -> the subScene index
            //y -> the collection index
            var validScenes = new NativeList<int2>(subScenesWithGhosts.Length, Allocator.Temp);
            //First validate all the data before scheduling any job
            //We are not checking for missing sub scenes on the client that are present on the server. By design it is possible
            //for a client to load just a subset of all server's subscene at any given time.
            var totalValidPrespawns = 0;
            var hasValidationError = false;
            for (int i = 0; i < subScenesWithGhosts.Length; ++i)
            {
                var validationResult = ValidatePrespawnGhostSubSceneData(subScenesWithGhosts[i].SubSceneHash,
                    subScenesWithGhosts[i].BaselinesHash, subScenesWithGhosts[i].PrespawnCount, subsceneCollection,
                    out var collectionIndex);
                if (validationResult == ValidationResult.SubSceneNotFound)
                {
                    //What that means:
                    // - Client loaded the scene at the same time or before the server did and the updated scene list
                    //   has been not received yet.
                    // - The server has unloaded the scene. In that case, it is responsibility of the client to unloading it
                    //   (usually using a higher level protocol that is user/game dependent). Most likely
                    // On both cases is not really an error. The client should just wait for the new list in the first case and remove
                    // the scene in the second
                    // Would be nice being able to differentiate in between the two cases.
                    continue;
                }
                if (validationResult == ValidationResult.MetadataNotMatch)
                {
                    //We log all the errors first and the we will request a disconnection
                    hasValidationError = true;
                    continue;
                }
                validScenes.Add(new int2(i, collectionIndex));
                totalValidPrespawns += subScenesWithGhosts[i].PrespawnCount;
            }
            if(hasValidationError)
            {
                //Disconnect the client
                EntityManager.AddComponent<NetworkStreamRequestDisconnect>(GetSingletonEntity<NetworkIdComponent>());
                return;
            }
            //Kick a job for each sub-scene that assign the ghost id to all scene prespawn ghosts.
            var scenePadding = 0;
            var subscenes = m_UninitializedScenes.ToEntityArray(Allocator.Temp);
            var entityCommandBuffer = m_Barrier.CreateCommandBuffer();
            //This temporary list is necessary because we forcibly re-assign the entity to spawn maps in case the ghost is already registered.
            var spawnedGhosts = new NativeArray<SpawnedGhostMapping>(totalValidPrespawns, Allocator.TempJob);
            for (int i = 0; i < validScenes.Length; ++i)
            {
                var sceneIndex = validScenes[i].x;
                var collectionIndex = validScenes[i].y;
                var sharedFilter = new SubSceneGhostComponentHash {Value = subScenesWithGhosts[sceneIndex].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                LogAssignPrespawnGhostIds(subScenesWithGhosts[sceneIndex]);
                var assignPrespawnGhostIdJob = new AssignPrespawnGhostIdJob
                {
                    entityType = GetEntityTypeHandle(),
                    prespawnIdType = GetComponentTypeHandle<PreSpawnedGhostIndex>(true),
                    ghostComponentType = GetComponentTypeHandle<GhostComponent>(),
                    ghostStateTypeHandle = GetComponentTypeHandle<GhostSystemStateComponent>(),
                    startGhostId = subsceneCollection[collectionIndex].FirstGhostId,
                    spawnedGhosts = spawnedGhosts.GetSubArray(scenePadding, subScenesWithGhosts[sceneIndex].PrespawnCount),
                    netDebug = m_NetDebugSystem.NetDebug
                };
                Dependency = assignPrespawnGhostIdJob.ScheduleParallel(m_Prespawns, Dependency);
                scenePadding += subScenesWithGhosts[sceneIndex].PrespawnCount;
                //Add a state component to track the scene lifetime.
                var sceneSectionData = default(SceneSectionData);
#if UNITY_EDITOR
                if (EntityManager.HasComponent<LiveLinkPrespawnSectionReference>(subscenes[i]))
                {
                    var sceneSectionRef = EntityManager.GetComponentData<LiveLinkPrespawnSectionReference>(subscenes[i]);
                    sceneSectionData.SceneGUID = sceneSectionRef.SceneGUID;
                    sceneSectionData.SubSectionIndex = sceneSectionRef.Section;
                }
                else
#endif
                    sceneSectionData = EntityManager.GetComponentData<SceneSectionData>(subscenes[sceneIndex]);
                entityCommandBuffer.AddComponent(subscenes[sceneIndex], new SubSceneWithGhostStateComponent
                {
                    SubSceneHash = subScenesWithGhosts[sceneIndex].SubSceneHash,
                    FirstGhostId = subsceneCollection[collectionIndex].FirstGhostId,
                    PrespawnCount = subScenesWithGhosts[sceneIndex].PrespawnCount,
                    SceneGUID =  sceneSectionData.SceneGUID,
                    SectionIndex =  sceneSectionData.SubSectionIndex,
                });
                entityCommandBuffer.AddComponent<PrespawnsSceneInitialized>(subscenes[sceneIndex]);
            }
            m_Prespawns.ResetFilter();
            var netDebug = m_NetDebugSystem.NetDebug;
            var ghostMap = m_GhostReceiveSystem.SpawnedGhostEntityMap;
            var ghostEntityMap = m_GhostReceiveSystem.GhostEntityMap;
            Dependency = Job.WithName("ClientAddPrespawn").WithDisposeOnCompletion(spawnedGhosts).WithCode(() =>
            {
                for (int i = 0; i < spawnedGhosts.Length; ++i)
                {
                    var newGhost = spawnedGhosts[i];
                    if (newGhost.ghost.ghostId == 0)
                    {
                        netDebug.LogError("Prespawn ghost id not assigned.");
                        return;
                    }

                    if (!ghostMap.TryAdd(newGhost.ghost, newGhost.entity))
                    {
                        netDebug.LogError($"GhostID {newGhost.ghost.ghostId} already present in the spawned ghost entity map.");
                        ghostMap[newGhost.ghost] = newGhost.entity;
                    }

                    if (!ghostEntityMap.TryAdd(newGhost.ghost.ghostId, newGhost.entity))
                    {
                        netDebug.LogError($"GhostID {newGhost.ghost.ghostId} already present in the ghost entity map. Overwrite");
                        ghostEntityMap[newGhost.ghost.ghostId] = newGhost.entity;
                    }
                }
            }).Schedule(JobHandle.CombineDependencies(Dependency, m_GhostReceiveSystem.LastGhostMapWriter));
            m_GhostReceiveSystem.LastGhostMapWriter = Dependency;
            m_Barrier.AddJobHandleForProducer(Dependency);
        }

        ValidationResult ValidatePrespawnGhostSubSceneData(ulong subSceneHash, ulong subSceneBaselineHash, int prespawnCount,
            in DynamicBuffer<PrespawnSceneLoaded> serverPrespawnHashBuffer, out int index)
        {
            //find a matching entry
            index = -1;
            for (int i = 0; i < serverPrespawnHashBuffer.Length; ++i)
            {
                if (serverPrespawnHashBuffer[i].SubSceneHash == subSceneHash)
                {
                    //check if the baseline matches
                    if (serverPrespawnHashBuffer[i].BaselineHash != subSceneBaselineHash)
                    {
                        m_NetDebugSystem.NetDebug.LogError(
                            $"Subscene {subSceneHash} baseline mismatch. Server:{serverPrespawnHashBuffer[i].BaselineHash} Client:{subSceneBaselineHash}");
                        return ValidationResult.MetadataNotMatch;
                    }

                    if (serverPrespawnHashBuffer[i].PrespawnCount != prespawnCount)
                    {
                        m_NetDebugSystem.NetDebug.LogError(
                            $"Subscene {subSceneHash} has different prespawn count. Server:{serverPrespawnHashBuffer[i].PrespawnCount} Client:{prespawnCount}");
                        return ValidationResult.MetadataNotMatch;
                    }

                    index = i;
                    return ValidationResult.ValidationSucceed;
                }
            }
            return ValidationResult.SubSceneNotFound;
        }

        [Conditional("NETCODE_DEBUG")]
        void LogAssignPrespawnGhostIds(in SubSceneWithPrespawnGhosts subScenesWithGhosts)
        {
            m_NetDebugSystem.NetDebug.DebugLog(FixedString.Format("Assinging prespawn ghost ids for scene Hash:{0} Count:{1}",
                NetDebug.PrintHex(subScenesWithGhosts.SubSceneHash), subScenesWithGhosts.PrespawnCount));
        }
    }
}

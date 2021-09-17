#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// ServerPopulatePrespawnedGhostsSystem systems is responsible for assign to the pre-spawned ghosts
    /// their ghost ids and add add them to the spawn maps.
    /// It rely on the previous initialization steps to determine the subscene subset to process.
    ///
    /// The server is authoritative and it is responsible for assigning unique id ranges to the each scene.
    /// For each section that present prespawn ghosts, the prespawn hash, id range and baseline hash are sent to client
    /// as part of the streaming protocol.
    /// Clients will use the received subscene hash and baseline hash for validation and the ghost range to assign
    /// the ghost id to the pre-spawned ghosts like the server. This remove any necessity for loading order determinism.
    /// Finally, clients will ack the server about the loaded scenes and the server, upon ack receipt,
    /// will start streaming the pre-spawned ghosts
    ///
    /// ======================================================================
    /// THE FULL PRESPAWN SUBSCENE SYNC PROTOCOL
    /// ======================================================================
    /// Server> calculates the prespawn baselines
    /// Server> assign runtime ghost IDs to the prespawned ghosts
    /// Server> store the SubSceneHash, BaselineHash and AssignedFirstId, PrespawnCount inside the the PrespawnHashElement collection
    /// Server> create a new ghost with a PrespawnSceneLoaded bufffer that is serialized to the clients
    ///
    /// Client> will eventually receive the ghost with the subscene list
    /// Client> (in parallel or before) will serialize the prespawn baseline when a new scene is loaded
    /// Client> should validate that subscene data match
    /// Client> will assign the ghost ids to the prespawns
    /// Client> will ack the server that the scene has been received and witch sub-set of the scenes must be streamed to him
    ///
    /// </summary>
    [UpdateInWorld(TargetWorld.Server)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(PrespawnGhostInitializationSystem))]
    public partial class ServerPopulatePrespawnedGhostsSystem : SystemBase
    {
        private EntityQuery m_UninitializedScenes;
        private EntityQuery m_Prespawns;
        private EntityQuery m_StreamInGame;
        private NetDebugSystem m_NetDebugSystem;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private GhostSendSystem m_GhostSendSystem;
        private Entity m_GhostIdAllocator;

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
            // Require any number of in-game tags, server can have one per client
            m_StreamInGame = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
            m_GhostSendSystem = World.GetExistingSystem<GhostSendSystem>();
            m_GhostIdAllocator = EntityManager.CreateEntity(typeof(PrespawnGhostIdRange));
            EntityManager.SetName(m_GhostIdAllocator, "PrespawnGhostIdAllocator");
            RequireForUpdate(m_UninitializedScenes);
            RequireForUpdate(m_Prespawns);
            RequireForUpdate(m_StreamInGame);
            RequireSingletonForUpdate<GhostCollection>();
        }

        protected override void OnUpdate()
        {
            if (!TryGetSingletonEntity<PrespawnSceneLoaded>(out var prespawnSceneListEntity))
            {
                var prefab = World.GetExistingSystem<PrespawnGhostInitializationSystem>().SubSceneListPrefab;
                prespawnSceneListEntity = EntityManager.Instantiate(prefab);
                EntityManager.RemoveComponent<GhostPrefabMetaDataComponent>(prespawnSceneListEntity);
                EntityManager.GetBuffer<PrespawnSceneLoaded>(prespawnSceneListEntity).EnsureCapacity(128);
            }
            var subScenesWithGhosts = m_UninitializedScenes.ToComponentDataArray<SubSceneWithPrespawnGhosts>(Allocator.Temp);
            var subSceneEntities = m_UninitializedScenes.ToEntityArray(Allocator.Temp);
            // Add GhostSystemStateComponent to all ghosts
            // After some measurement this is the fastest way to achieve it. Is roughly 5/6x faster than
            // adding all the components change one by one via command buffer in a IJobChunk or IJobEntityBatch
            // with a decent amout of entities (> 3000)
            for (int i = 0; i < subScenesWithGhosts.Length; ++i)
            {
                var sharedFilter = new SubSceneGhostComponentHash {Value = subScenesWithGhosts[i].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                EntityManager.AddComponent<GhostSystemStateComponent>(m_Prespawns);
            }
            //This temporary list is necessary because we forcibly re-assign the entity to spawn maps for both client and server in case the
            //ghost is already registered.
            var totalPrespawns = 0;
            for (int i = 0; i < subScenesWithGhosts.Length; ++i)
                totalPrespawns += subScenesWithGhosts[i].PrespawnCount;
            var spawnedGhosts = new NativeArray<SpawnedGhostMapping>(totalPrespawns, Allocator.TempJob);
            //Kick a job for each sub-scene that assign the ghost id to all scene prespawn ghosts.
            //It also fill the array of prespawned ghosts that is going to be used to populate the ghost maps in the send/receive systems.
            var scenePadding = 0;
            var subsceneCollection = EntityManager.GetBuffer<PrespawnSceneLoaded>(prespawnSceneListEntity);
            var entityCommandBuffer = m_Barrier.CreateCommandBuffer();
            for (int i = 0; i < subScenesWithGhosts.Length; ++i)
            {
                LogAssignPrespawnGhostIds(subScenesWithGhosts[i]);
                var sharedFilter = new SubSceneGhostComponentHash {Value = subScenesWithGhosts[i].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                //Allocate or reuse an id-range for that subscene and assign the ids to the ghosts
                int startId = AllocatePrespawnGhostRange(subScenesWithGhosts[i].SubSceneHash, subScenesWithGhosts[i].PrespawnCount);
                var assignPrespawnGhostIdJob = new AssignPrespawnGhostIdJob
                {
                    entityType = GetEntityTypeHandle(),
                    prespawnIdType = GetComponentTypeHandle<PreSpawnedGhostIndex>(true),
                    ghostComponentType = GetComponentTypeHandle<GhostComponent>(),
                    ghostStateTypeHandle = GetComponentTypeHandle<GhostSystemStateComponent>(),
                    startGhostId = startId,
                    spawnedGhosts = spawnedGhosts.GetSubArray(scenePadding, subScenesWithGhosts[i].PrespawnCount),
                    netDebug = m_NetDebugSystem.NetDebug
                };
                Dependency = assignPrespawnGhostIdJob.ScheduleParallel(m_Prespawns, Dependency);
                scenePadding += subScenesWithGhosts[i].PrespawnCount;
                //add the subscene to the collection. This will be synchronized to the clients
                subsceneCollection.Add(new PrespawnSceneLoaded
                {
                    SubSceneHash = subScenesWithGhosts[i].SubSceneHash,
                    BaselineHash = subScenesWithGhosts[i].BaselinesHash,
                    FirstGhostId = startId,
                    PrespawnCount = subScenesWithGhosts[i].PrespawnCount
                });

                //Mark scenes as initialized and add tracking.
                var sceneSectionData = default(SceneSectionData);
#if UNITY_EDITOR
                if (EntityManager.HasComponent<LiveLinkPrespawnSectionReference>(subSceneEntities[i]))
                {
                    var sceneSectionRef = EntityManager.GetComponentData<LiveLinkPrespawnSectionReference>(subSceneEntities[i]);
                    sceneSectionData.SceneGUID = sceneSectionRef.SceneGUID;
                    sceneSectionData.SubSectionIndex = sceneSectionRef.Section;
                }
                else
#endif
                    sceneSectionData = EntityManager.GetComponentData<SceneSectionData>(subSceneEntities[i]);

                entityCommandBuffer.AddComponent<PrespawnsSceneInitialized>(subSceneEntities[i]);
                entityCommandBuffer.AddComponent(subSceneEntities[i], new SubSceneWithGhostStateComponent
                {
                    SubSceneHash = subScenesWithGhosts[i].SubSceneHash,
                    FirstGhostId = startId,
                    PrespawnCount = subScenesWithGhosts[i].PrespawnCount,
                    SceneGUID = sceneSectionData.SceneGUID,
                    SectionIndex = sceneSectionData.SubSectionIndex
                });
            }
            m_Prespawns.ResetFilter();
            //Wait for all ghost ids jobs assignments completed and populate the spawned ghost map
            var netDebug = m_NetDebugSystem.NetDebug;
            var ghostMap = m_GhostSendSystem.SpawnedGhostEntityMap;
            Dependency = Job.WithName("ServerAddPrespawn").WithDisposeOnCompletion(spawnedGhosts).WithCode(() =>
            {
                for (int i = 0; i < spawnedGhosts.Length; ++i)
                {
                    if (spawnedGhosts[i].ghost.ghostId == 0)
                    {
                        netDebug.LogError($"Prespawn ghost id not assigned.");
                        return;
                    }
                    var newGhost = spawnedGhosts[i];
                    if (!ghostMap.TryAdd(newGhost.ghost, newGhost.entity))
                    {
                        netDebug.LogError($"GhostID {newGhost.ghost.ghostId} already present in the spawned ghost entity map.");
                        //Force a reassignment.
                        ghostMap[newGhost.ghost] = newGhost.entity;
                    }
                }
            }).Schedule(JobHandle.CombineDependencies(Dependency, m_GhostSendSystem.LastGhostMapWriter));
            m_GhostSendSystem.LastGhostMapWriter = Dependency;
            m_Barrier.AddJobHandleForProducer(Dependency);
        }

        /// <summary>
        /// Return the start ghost id for the subscene. Id ranges are re-used by the same subscene if it is loaded again
        /// </summary>
        //TODO: the allocation may become a little more advanced by re-using ids later
        private int AllocatePrespawnGhostRange(ulong subSceneHash, int prespawnCount)
        {
            var allocatedRanges = EntityManager.GetBuffer<PrespawnGhostIdRange>(m_GhostIdAllocator);
            for (int r = 0; r < allocatedRanges.Length; ++r)
            {
                if (allocatedRanges[r].SubSceneHash == subSceneHash)
                {
                    //This is an error or an hash collision.
                    if (allocatedRanges[r].Reserved != 0)
                        throw new System.InvalidOperationException($"prespawn ids range already present for subscene with hash {subSceneHash}");

                    m_NetDebugSystem.NetDebug.DebugLog($"reusing prespawn ids range from {allocatedRanges[r].FirstGhostId} to {allocatedRanges[r].FirstGhostId + prespawnCount} for subscene with hash {subSceneHash}");
                    allocatedRanges[r] = new PrespawnGhostIdRange
                    {
                        SubSceneHash = subSceneHash,
                        FirstGhostId = allocatedRanges[r].FirstGhostId,
                        Count = (short)prespawnCount,
                        Reserved = 1
                    };
                    return allocatedRanges[r].FirstGhostId;
                }
            }

            var nextGhostId = 1;
            if (allocatedRanges.Length > 0)
                nextGhostId = allocatedRanges[allocatedRanges.Length - 1].FirstGhostId +
                              allocatedRanges[allocatedRanges.Length - 1].Count;

            var newRange = new PrespawnGhostIdRange
            {
                SubSceneHash = subSceneHash,
                FirstGhostId = nextGhostId,
                Count = (short)prespawnCount,
                Reserved = 1
            };
            allocatedRanges.Add(newRange);
            LogAllocatedIdRange(newRange);
            //Update the prespawn allocated ids
            m_GhostSendSystem.SetAllocatedPrespawnGhostId(nextGhostId + prespawnCount);
            return newRange.FirstGhostId;
        }

        [Conditional("NETCODE_DEBUG")]
        private void LogAllocatedIdRange(PrespawnGhostIdRange rangeAlloc)
        {
            m_NetDebugSystem.NetDebug.DebugLog($"Assigned id-range [{rangeAlloc.FirstGhostId}-{rangeAlloc.FirstGhostId + rangeAlloc.Count}] to scene section with hash {NetDebug.PrintHex(rangeAlloc.SubSceneHash)}");
        }

        [Conditional("NETCODE_DEBUG")]
        void LogAssignPrespawnGhostIds(in SubSceneWithPrespawnGhosts subScenesWithGhosts)
        {
            m_NetDebugSystem.NetDebug.DebugLog(FixedString.Format("Assinging prespawn ghost ids for scene Hash:{0} Count:{1}",
                NetDebug.PrintHex(subScenesWithGhosts.SubSceneHash), subScenesWithGhosts.PrespawnCount));
        }
    }
}

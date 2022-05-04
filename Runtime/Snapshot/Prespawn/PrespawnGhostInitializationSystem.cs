#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Scenes;

namespace Unity.NetCode
{
    /// <summary>
    /// InitializePrespawnGhostSystem systems is responsbile to prepare and initialize all sub-scenes pre-spawned ghosts
    /// The initialization process is quite involved and neeed multiple steps:
    /// - perform component stripping based on the ghost prefab metadata (MAJOR STRUCTURAL CHANGES)
    /// - kickoff baseline serialization
    /// - compute and assingn the compound baseline hash to each subscene
    ///
    /// The process start by finding the subscenes subset that has all the ghost archetype serializer ready.
    /// A component stripping, serialization and baseline assignement jobs is started for each subscene in parallel.
    /// </summary>
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    partial class PrespawnGhostInitializationSystem : SystemBase
    {
        private EntityQuery m_PrespawnBaselines;
        private EntityQuery m_UninitializedScenes;
        private EntityQuery m_Prespawns;
        private NetDebugSystem m_NetDebugSystem;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private SceneSystem m_SceneSystem;

        private Entity m_SubSceneListPrefab;
        public Entity SubSceneListPrefab => m_SubSceneListPrefab;

        protected override void OnCreate()
        {
            m_Barrier = World.GetExistingSystem<BeginSimulationEntityCommandBufferSystem>();
            m_PrespawnBaselines = EntityManager.CreateEntityQuery(
                new EntityQueryDesc
                {
                    All = new []
                    {
                        ComponentType.ReadWrite<PrespawnGhostBaseline>(),
                        ComponentType.ReadOnly<SubSceneGhostComponentHash>()
                    },
                    Options = EntityQueryOptions.IncludeDisabled
                });
            m_Prespawns = EntityManager.CreateEntityQuery(
                new EntityQueryDesc
                {
                    All = new []
                    {
                        ComponentType.ReadWrite<PreSpawnedGhostIndex>(),
                        ComponentType.ReadOnly<SubSceneGhostComponentHash>(),
                        ComponentType.ReadOnly<GhostTypeComponent>()
                    },
                    Options = EntityQueryOptions.IncludeDisabled
                });
            m_UninitializedScenes = GetEntityQuery(ComponentType.ReadOnly<SubSceneWithPrespawnGhosts>(),
                ComponentType.Exclude<SubScenePrespawnBaselineResolved>());
            m_NetDebugSystem = World.GetExistingSystem<NetDebugSystem>();
            m_SceneSystem = World.GetExistingSystem<SceneSystem>();

            RequireSingletonForUpdate<GhostCollection>();
            RequireForUpdate(m_UninitializedScenes);
        }

        protected override void OnDestroy()
        {
            m_PrespawnBaselines.Dispose();
            m_Prespawns.Dispose();
            if(m_SubSceneListPrefab != Entity.Null)
                PrespawnHelper.DisposeSceneListPrefab(m_SubSceneListPrefab, EntityManager);
        }

        protected override void OnUpdate()
        {
            //This need to be delayed here to avoid creating this entity if not required (so no prespawn presents)
            if (m_SubSceneListPrefab == Entity.Null)
            {
                m_SubSceneListPrefab = PrespawnHelper.CreatePrespawnSceneListGhostPrefab(World, EntityManager);
                RequireForUpdate(m_Prespawns);
                return;
            }
            var collectionEntity = GetSingletonEntity<GhostCollection>();
            var GhostPrefabTypes = EntityManager.GetBuffer<GhostCollectionPrefab>(collectionEntity);
            //No data loaded yet. This condition can be true for both client and server.
            //Server in particular can be in this state until at least one connection enter the in-game state.
            //Client can hit this until the receive the prefab to process from the Server.
            if(GhostPrefabTypes.Length == 0)
                return;

            var processedPrefabs = new NativeParallelHashMap<GhostTypeComponent, Entity>(256, Allocator.TempJob);
            var subSceneWithPrespawnGhosts = m_UninitializedScenes.ToComponentDataArray<SubSceneWithPrespawnGhosts>(Allocator.Temp);
            var subScenesSections = m_UninitializedScenes.ToEntityArray(Allocator.Temp);
            var readySections = new NativeList<int>(subScenesSections.Length, Allocator.Temp);

            //Populate a map for faster retrival and used also by component stripping job
            for (int i = 0; i < GhostPrefabTypes.Length; ++i)
            {
                if(GhostPrefabTypes[i].GhostPrefab != Entity.Null)
                    processedPrefabs.Add(GhostPrefabTypes[i].GhostType, GhostPrefabTypes[i].GhostPrefab);
            }

            //Find out all the scenes that have all their prespawn ghost type resolved by the ghost collection.
            //(so we have the serializer ready)
            for (int i = 0; i < subScenesSections.Length; ++i)
            {
                if(!m_SceneSystem.IsSectionLoaded(subScenesSections[i]))
                    continue;

                //For large number would make sense to schedule a job for that
                var sharedFilter = new SubSceneGhostComponentHash {Value = subSceneWithPrespawnGhosts[i].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                var ghostTypes = m_Prespawns.ToComponentDataArray<GhostTypeComponent>(Allocator.Temp);
                bool allArchetypeProcessed = true;
                for(int t=0;t<ghostTypes.Length && allArchetypeProcessed;++t)
                    allArchetypeProcessed &= processedPrefabs.ContainsKey(ghostTypes[t]);
                if(allArchetypeProcessed)
                    readySections.Add(i);
            }
            m_Prespawns.ResetFilter();

            //If not scene has resolved the ghost prefab, or has been loaded early exit
            if (readySections.Length == 0)
            {
                processedPrefabs.Dispose();
                return;
            }

            //Remove the disable components. Is faster this way than using command buffers because this
            //will affect the whole chunk at once
            for (int readyScene = 0; readyScene < readySections.Length; ++readyScene)
            {
                var sceneIndex = readySections[readyScene];
                var sharedFilter = new SubSceneGhostComponentHash {Value = subSceneWithPrespawnGhosts[sceneIndex].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                EntityManager.RemoveComponent<Disabled>(m_Prespawns);
            }
            //kickoff strip components jobs on all the prefabs for each subscene
            var jobs = new NativeList<JobHandle>(readySections.Length, Allocator.Temp);
            for (int readyScene = 0; readyScene < readySections.Length; ++readyScene)
            {
                var sceneIndex = readySections[readyScene];
                var sharedFilter = new SubSceneGhostComponentHash {Value = subSceneWithPrespawnGhosts[sceneIndex].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                //Strip components this can be a large chunks of major structural changes and it is scheduled
                //at the beginning of the next simulation update
                var ecb = m_Barrier.CreateCommandBuffer();
                LogStrippingPrespawn(subSceneWithPrespawnGhosts[sceneIndex]);
                var stripPrespawnGhostJob = new PrespawnGhostStripComponentsJob
                {
                    metaDataFromEntity = GetComponentDataFromEntity<GhostPrefabMetaDataComponent>(true),
                    linkedEntityTypeHandle = GetBufferTypeHandle<LinkedEntityGroup>(true),
                    ghostTypeHandle = GetComponentTypeHandle<GhostTypeComponent>(true),
                    prefabFromType = processedPrefabs,
                    commandBuffer = ecb.AsParallelWriter(),
                    netDebug = m_NetDebugSystem.NetDebug,
                    server = World.GetExistingSystem<GhostSendSystem>() != null
                };
                jobs.Add(stripPrespawnGhostJob.ScheduleParallel(m_Prespawns, Dependency));
            }
            Dependency = processedPrefabs.Dispose(JobHandle.CombineDependencies(jobs));
            m_Prespawns.ResetFilter();

            //In case the prespawn baselines are not present just mark everything as resolved
            if (m_PrespawnBaselines.IsEmptyIgnoreFilter)
            {
                for (int readyScene = 0; readyScene < readySections.Length; ++readyScene)
                {
                    var sceneIndex = readySections[readyScene];
                    var subScene = subScenesSections[sceneIndex];
                    EntityManager.AddComponent<SubScenePrespawnBaselineResolved>(subScene);
                }
                m_Barrier.AddJobHandleForProducer(Dependency);
                return;
            }

            //Serialize the baseline and add the resolved tag.
            var serializerJob = new PrespawnGhostSerializer
            {
                GhostComponentCollectionFromEntity = GetBufferFromEntity<GhostComponentSerializer.State>(true),
                GhostTypeCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefabSerializer>(true),
                GhostComponentIndexFromEntity = GetBufferFromEntity<GhostCollectionComponentIndex>(true),
                GhostCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefab>(true),
                ghostTypeComponentType = GetComponentTypeHandle<GhostTypeComponent>(true),
                prespawnBaseline = GetBufferTypeHandle<PrespawnGhostBaseline>(),
                entityType = GetEntityTypeHandle(),
                childEntityLookup = GetStorageInfoFromEntity(),
                linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(true),
                ghostFromEntity = GetComponentDataFromEntity<GhostComponent>(true),
                GhostCollectionSingleton = collectionEntity
            };
            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(collectionEntity);
            DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref serializerJob.ghostChunkComponentTypes);

            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            for (int readyScene = 0; readyScene < readySections.Length; ++readyScene)
            {
                var sceneIndex = readySections[readyScene];
                LogSerializingBaselines(subSceneWithPrespawnGhosts[sceneIndex]);
                var subScene = subScenesSections[sceneIndex];
                var subSceneWithGhost = subSceneWithPrespawnGhosts[sceneIndex];
                var sharedFilter = new SubSceneGhostComponentHash {Value = subSceneWithGhost.SubSceneHash};
                m_PrespawnBaselines.SetSharedComponentFilter(sharedFilter);
                // Serialize the baselines and store the baseline hashes
                var baselinesHashes = new NativeArray<ulong>(subSceneWithGhost.PrespawnCount, Allocator.TempJob);
                serializerJob.baselineHashes = baselinesHashes;
                var serializeJobHandle = serializerJob.ScheduleParallelByRef(m_PrespawnBaselines, Dependency);
                // Calculate the aggregate baseline hash for all the ghosts in the scene
                var subSceneWithGhostFromEntity = GetComponentDataFromEntity<SubSceneWithPrespawnGhosts>();
                Dependency = Job
                    .WithDisposeOnCompletion(baselinesHashes)
                    .WithCode(() =>
                    {
                        //Sort to maintain consistent order
                        baselinesHashes.Sort();
                        ulong baselineHash;
                        unsafe
                        {
                            baselineHash = Unity.Core.XXHash.Hash64((byte*)baselinesHashes.GetUnsafeReadOnlyPtr(),
                                baselinesHashes.Length * sizeof(ulong));
                        }
                        subSceneWithGhost.BaselinesHash = baselineHash;
                        subSceneWithGhostFromEntity[subScene] = subSceneWithGhost;
                    }).Schedule(serializeJobHandle);
                //mark as resolved
                commandBuffer.AddComponent<SubScenePrespawnBaselineResolved>(subScene);
            }
            m_Barrier.AddJobHandleForProducer(Dependency);
            //Playback immediately the resolved scenes
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
        }

        [Conditional("NETCODE_DEBUG")]
        private void LogStrippingPrespawn(in SubSceneWithPrespawnGhosts subSceneWithPrespawnGhosts)
        {
            m_NetDebugSystem.NetDebug.DebugLog(FixedString.Format("Initializing prespawn scene Hash:{0} Count:{1}",
                NetDebug.PrintHex(subSceneWithPrespawnGhosts.SubSceneHash),
                subSceneWithPrespawnGhosts.PrespawnCount));
        }
        [Conditional("NETCODE_DEBUG")]
        private void LogSerializingBaselines(in SubSceneWithPrespawnGhosts subSceneWithPrespawnGhosts)
        {
            m_NetDebugSystem.NetDebug.DebugLog(FixedString.Format("Serializing baselines for prespawn scene Hash:{0} Count:{1}",
                NetDebug.PrintHex(subSceneWithPrespawnGhosts.SubSceneHash),
                subSceneWithPrespawnGhosts.PrespawnCount));
        }
    }
}

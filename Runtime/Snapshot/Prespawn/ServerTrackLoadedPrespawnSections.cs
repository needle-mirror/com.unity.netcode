using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scenes;

namespace Unity.NetCode
{
    /// <summary>
    /// The ServerTrackLoadedPrespawnSections is responsible for tracking when an initialized prespawn sections is unloaded
    /// in order to release any allocated data and freeing ghost id ranges.
    /// </summary>
    [UpdateInWorld(TargetWorld.Server)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(ServerPopulatePrespawnedGhostsSystem))]
    public partial class ServerTrackLoadedPrespawnSections : SystemBase
    {
        private EntityQuery m_LoadedSubscenes;
        private EntityQuery m_Prespawns;
        private NetDebugSystem m_NetDebugSystem;
        private EntityQuery m_DestroyedSubscenes;
        private SceneSystem m_SceneSystem;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private GhostSendSystem m_GhostSendSystem;
        private EntityQuery m_AllPrespawnScenes;

        protected override void OnCreate()
        {
            m_LoadedSubscenes = GetEntityQuery(ComponentType.ReadOnly<PrespawnsSceneInitialized>());
            m_DestroyedSubscenes = GetEntityQuery(
                ComponentType.ReadOnly<SubSceneWithGhostStateComponent>(),
                ComponentType.Exclude<PrespawnsSceneInitialized>());
            m_Prespawns = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                ComponentType.ReadOnly<SubSceneGhostComponentHash>());
            m_AllPrespawnScenes = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubSceneWithPrespawnGhosts>());
            m_SceneSystem = World.GetExistingSystem<SceneSystem>();
            m_Barrier = World.GetExistingSystem<BeginSimulationEntityCommandBufferSystem>();
            m_GhostSendSystem = World.GetExistingSystem<GhostSendSystem>();
            m_NetDebugSystem = World.GetExistingSystem<NetDebugSystem>();
            RequireSingletonForUpdate<GhostCollection>();
        }

        protected override void OnDestroy()
        {
            m_Prespawns.Dispose();
            m_AllPrespawnScenes.Dispose();
        }

        protected override void OnUpdate()
        {
            var entityCommandBuffer = m_Barrier.CreateCommandBuffer();
            var unloadedSections = new NativeList<Entity>(16, Allocator.Temp);
            using(var destroyedEntities = m_DestroyedSubscenes.ToEntityArray(Allocator.Temp))
                unloadedSections.AddRange(destroyedEntities);

            //Add all unloaded but not destroyed scenes to the list
            using (var loadedSceneEntities = m_LoadedSubscenes.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < loadedSceneEntities.Length; ++i)
                {
                    if (!m_SceneSystem.IsSectionLoaded(loadedSceneEntities[i]))
                    {
                        unloadedSections.Add(loadedSceneEntities[i]);
                    }
                }
            }

            if (unloadedSections.Length == 0)
                return;

            //Only process scenes for wich all prefabs has been already destroyed
            var subsceneCollection = EntityManager.GetBuffer<PrespawnSceneLoaded>(GetSingletonEntity<PrespawnSceneLoaded>());
            var allocatedRanges = EntityManager.GetBuffer<PrespawnGhostIdRange>(GetSingletonEntity<PrespawnGhostIdRange>());
            var unloadedGhostRange = new NativeList<int2>(Allocator.TempJob);
            for(int i=0;i<unloadedSections.Length;++i)
            {
                var stateComponent = EntityManager.GetComponentData<SubSceneWithGhostStateComponent>(unloadedSections[i]);
                m_Prespawns.SetSharedComponentFilter(new SubSceneGhostComponentHash { Value = stateComponent.SubSceneHash });

                //If there are still some ghosts present, don't remove the scene from the scene list yet
                //NOTE:
                //This check can only detect if the ghosts has been despawn. The entity however may be
                //still pending for ack and tracked by the GhostSystemComponent.
                if (!m_Prespawns.IsEmpty)
                    continue;

                //Lookup and remove the scene from the collection
                int idx = 0;
                for (; idx < subsceneCollection.Length; ++idx)
                {
                    if (subsceneCollection[idx].SubSceneHash == stateComponent.SubSceneHash)
                        break;
                }

                if (idx != subsceneCollection.Length)
                {
                    subsceneCollection.RemoveAtSwapBack(idx);
                }
                else
                {
                    m_NetDebugSystem.NetDebug.LogError($"Scene with hash {stateComponent.SubSceneHash} not found in active subscene list");
                }
                //Release the id range for later reuse. For now we allow reuse the same ghost ids for the same scene
                //for sake of simplicity
                unloadedGhostRange.Add(new int2(stateComponent.FirstGhostId, stateComponent.PrespawnCount));
                for (int rangeIdx = 0; i < allocatedRanges.Length; ++rangeIdx)
                {
                    if (allocatedRanges[rangeIdx].Reserved != 0 &&
                        allocatedRanges[rangeIdx].SubSceneHash == stateComponent.SubSceneHash)
                    {
                        allocatedRanges[rangeIdx] = new PrespawnGhostIdRange
                        {
                            SubSceneHash = allocatedRanges[rangeIdx].SubSceneHash,
                            FirstGhostId = allocatedRanges[rangeIdx].FirstGhostId,
                            Count = allocatedRanges[rangeIdx].Count,
                            Reserved = 0
                        };
                        break;
                    }
                }
                entityCommandBuffer.RemoveComponent<PrespawnsSceneInitialized>(unloadedSections[i]);
                entityCommandBuffer.RemoveComponent<SubScenePrespawnBaselineResolved>(unloadedSections[i]);
                entityCommandBuffer.RemoveComponent<SubSceneWithGhostStateComponent>(unloadedSections[i]);
            }

            if (unloadedGhostRange.Length == 0)
            {
                m_Barrier.AddJobHandleForProducer(Dependency);
                unloadedGhostRange.Dispose();
                return;
            }
            //Schedule a cleanup job for the despawn list in case there are prespawn present
            //Once the range has been release (Reserved == 0) the ghost witch belong to that range
            //are not added to the queue in the GhostSendSystem.
            var despawns = m_GhostSendSystem.DestroyedPrespawns;
            Dependency = Job
                .WithDisposeOnCompletion(unloadedGhostRange)
                .WithCode(() =>
                {
                    for (int i = 0; i < unloadedGhostRange.Length; ++i)
                    {
                        var firstId = unloadedGhostRange[i].x;
                        for (int idx = 0; idx < unloadedGhostRange[i].y; ++idx)
                        {
                            var ghostId = PrespawnHelper.MakePrespawGhostId(firstId + idx);
                            var found = despawns.IndexOf(ghostId);
                            if (found != -1)
                                despawns.RemoveAtSwapBack(found);
                        }
                    }
                }).Schedule(JobHandle.CombineDependencies(Dependency, m_GhostSendSystem.LastGhostMapWriter));
            m_GhostSendSystem.LastGhostMapWriter = Dependency;

            //If no prespawn scenes present, destroy the prespawn scene list
            if(subsceneCollection.Length == 0 && m_AllPrespawnScenes.IsEmpty)
                entityCommandBuffer.DestroyEntity(GetSingletonEntity<PrespawnSceneLoaded>());

            m_Barrier.AddJobHandleForProducer(Dependency);
        }
    }
}

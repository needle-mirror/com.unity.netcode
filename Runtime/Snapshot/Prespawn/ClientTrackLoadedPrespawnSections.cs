using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Scenes;

namespace Unity.NetCode
{
    /// <summary>
    /// The ClientTrackLoadedPrespawnSections is responsible for tracking when a scene section is unloaded and
    /// removing the pre-spawned ghosts from the client ghosts maps
    /// </summary>
    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(PrespawnGhostInitializationSystem))]
    public partial class ClientTrackLoadedPrespawnSections : SystemBase
    {
        private EntityQuery m_InitializedSceneSections;
        private EntityQuery m_DestroyedSubscenes;
        private EntityQuery m_Prespawns;
        private GhostReceiveSystem m_GhostReceiveSystem;
        private SceneSystem m_SceneSystem;

        protected override void OnCreate()
        {
            m_InitializedSceneSections = GetEntityQuery(
                ComponentType.ReadOnly<SubSceneWithPrespawnGhosts>(),
                ComponentType.ReadWrite<SubSceneWithGhostStateComponent>());
            m_DestroyedSubscenes = GetEntityQuery(
                ComponentType.ReadOnly<SubSceneWithGhostStateComponent>(),
                ComponentType.Exclude<SubSceneWithPrespawnGhosts>());
            m_Prespawns = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                ComponentType.ReadOnly<SubSceneGhostComponentHash>());
            m_GhostReceiveSystem = World.GetExistingSystem<GhostReceiveSystem>();
            m_SceneSystem = World.GetExistingSystem<SceneSystem>();
            RequireSingletonForUpdate<GhostCollection>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadOnly<SubSceneWithGhostStateComponent>()));
        }

        protected override void OnDestroy()
        {
            m_Prespawns.Dispose();
        }

        protected override void OnUpdate()
        {
            var unloadedScenes = new NativeList<Entity>(16, Allocator.Temp);
            using(var destroyedEntities = m_DestroyedSubscenes.ToEntityArray(Allocator.Temp))
                unloadedScenes.AddRange(destroyedEntities);
            var initializedSections = m_InitializedSceneSections.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < initializedSections.Length; ++i)
            {
                if (!m_SceneSystem.IsSectionLoaded(initializedSections[i]))
                    unloadedScenes.Add(initializedSections[i]);
            }

            if(unloadedScenes.Length == 0)
                return;

            //Only process scenes for wich all prefabs has been already destroyed
            var ghostsToRemove = new NativeList<SpawnedGhost>(128, Allocator.TempJob);
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
            for(int i=0;i<unloadedScenes.Length;++i)
            {
                var stateComponent = GetComponent<SubSceneWithGhostStateComponent>(unloadedScenes[i]);
                m_Prespawns.SetSharedComponentFilter(new SubSceneGhostComponentHash { Value = stateComponent.SubSceneHash });
                if (m_Prespawns.IsEmpty)
                {
                    var firstId = PrespawnHelper.PrespawnGhostIdBase + stateComponent.FirstGhostId;
                    for (int p = 0; p < stateComponent.PrespawnCount; ++p)
                    {
                        ghostsToRemove.Add(new SpawnedGhost
                        {
                            ghostId = (int) (firstId + p),
                            spawnTick = 0
                        });
                    }

                    entityCommandBuffer.RemoveComponent<PrespawnsSceneInitialized>(unloadedScenes[i]);
                    entityCommandBuffer.RemoveComponent<SubScenePrespawnBaselineResolved>(unloadedScenes[i]);
                    entityCommandBuffer.RemoveComponent<SubSceneWithGhostStateComponent>(unloadedScenes[i]);
                }
            }
            entityCommandBuffer.Playback(EntityManager);

            if (ghostsToRemove.Length == 0)
            {
                ghostsToRemove.Dispose();
                return;
            }

            //Remove the ghosts from the spawn maps
            var spawnedGhostEntityMap = m_GhostReceiveSystem.SpawnedGhostEntityMap;
            var ghostEntityMap = m_GhostReceiveSystem.GhostEntityMap;
            Dependency = Job
                .WithDisposeOnCompletion(ghostsToRemove)
                .WithCode(() =>
                {
                    for(int i=0;i<ghostsToRemove.Length;++i)
                    {
                        spawnedGhostEntityMap.Remove(ghostsToRemove[i]);
                        ghostEntityMap.Remove(ghostsToRemove[i].ghostId);
                    }
                }).Schedule(JobHandle.CombineDependencies(Dependency, m_GhostReceiveSystem.LastGhostMapWriter));

            m_GhostReceiveSystem.LastGhostMapWriter = Dependency;
        }
    }
}

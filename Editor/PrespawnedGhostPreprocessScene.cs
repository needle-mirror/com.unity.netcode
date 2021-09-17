using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Process in the editor any sub-scene open for edit that contains pre-spawned ghosts.
    /// This is a work-around for a limitation in the conversion workdflow that prevent custom component added to
    /// sceen section entity when a sub-scene is open for edit.
    /// To overcome that, the SubSceneWithPrespawnGhosts is added at runtime here and a LiveLinkPrespawnSectionReference
    /// is also added ot the scene section enity to provide some misisng information about the section is referring to.
    /// </summary>
    [UpdateInGroup(typeof(ClientAndServerInitializationSystemGroup))]
    public partial class PrespawnedGhostPreprocessScene : SystemBase
    {
        struct PrespawnSceneExtracted : IComponentData
        {
        }
        private EntityQuery prespawnToPreprocess;
        private EntityQuery subsceneToProcess;
        private SceneSystem sceneSystem;

        protected override void OnCreate()
        {
            prespawnToPreprocess = GetEntityQuery(new EntityQueryDesc
            {
                All = new []
                {
                    new ComponentType(typeof(PreSpawnedGhostIndex)),
                    new ComponentType(typeof(SubSceneGhostComponentHash)),
                    new ComponentType(typeof(SceneTag))
                },
                Options = EntityQueryOptions.IncludeDisabled
            });
            subsceneToProcess = GetEntityQuery(
                ComponentType.ReadOnly<SceneEntityReference>(),
                ComponentType.Exclude<PrespawnSceneExtracted>(),
                ComponentType.Exclude<SceneSectionData>(),
                ComponentType.Exclude<SubSceneWithPrespawnGhosts>());
            sceneSystem = World.GetExistingSystem<SceneSystem>();
            RequireForUpdate(prespawnToPreprocess);
            RequireForUpdate(subsceneToProcess);
        }


        protected override void OnUpdate()
        {
            //this is only valid in the editor
            var prespawnGhostHashType = GetSharedComponentTypeHandle<SubSceneGhostComponentHash>();
            var sceneSectionType = GetSharedComponentTypeHandle<SceneSection>();
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            Entities
                .WithoutBurst()
                .WithAll<DisableSceneResolveAndLoad>()
                .WithAll<SceneEntityReference>()
                .WithNone<SceneSectionData>()
                .WithNone<PrespawnSceneExtracted>()
                .ForEach((Entity sectionEntity) =>
            {
                if(!sceneSystem.IsSectionLoaded(sectionEntity))
                    return;

                prespawnToPreprocess.SetSharedComponentFilter(new SceneTag{SceneEntity = sectionEntity});
                var count = prespawnToPreprocess.CalculateEntityCount();
                if (count > 0)
                {
                    using var chunks = prespawnToPreprocess.CreateArchetypeChunkArray(Allocator.Temp);
                    var prespawnGhostHash = chunks[0].GetSharedComponentData(prespawnGhostHashType, EntityManager);
                    var sceneSection = chunks[0].GetSharedComponentData(sceneSectionType, EntityManager);
                    commandBuffer.AddComponent(sectionEntity, new SubSceneWithPrespawnGhosts
                    {
                        SubSceneHash = prespawnGhostHash.Value,
                        BaselinesHash = 0,
                        PrespawnCount = count
                    });
                    //Add this component to allow retrieve the section index and scene guid. This information are necessary
                    //to correctly add the SceneSection component to the pre-spawned ghosts when they are re-spawned
                    //FIXME: investigate if using the SceneTag may be sufficient to guaratee that re-spawned prespawned ghosts
                    //are deleted when scenes are unloaded. We can the remove this component and further simplify other things
                    commandBuffer.AddComponent(sectionEntity, new LiveLinkPrespawnSectionReference
                    {
                        SceneGUID = sceneSection.SceneGUID,
                        Section = sceneSection.Section
                    });
                }
                commandBuffer.AddComponent<PrespawnSceneExtracted>(sectionEntity);
            }).Run();
            commandBuffer.Playback(EntityManager);
            prespawnToPreprocess.ResetFilter();
        }
    }
}

using Unity.Collections;
using Unity.Entities;
using Unity.Profiling;

namespace Unity.NetCode.Tests
{
#if UNITY_EDITOR
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostCollectionSystem))]
    internal partial class GhostCollectionSystemProxy : SystemBase
    {
        static readonly ProfilerMarker k_Update = new ProfilerMarker("GhostCollectionSystem_OnUpdate");
        static readonly ProfilerMarker k_CompleteTrackedJobs = new ProfilerMarker("GhostCollectionSystem_CompleteAllTrackedJobs");

        protected override void OnUpdate()
        {
            EntityManager.CompleteAllTrackedJobs();

            var systemHandle = World.GetExistingSystem<GhostCollectionSystem>();
            var unmanagedSystem = World.Unmanaged.GetExistingSystemState<GhostCollectionSystem>();
            unmanagedSystem.Enabled = false;

            k_CompleteTrackedJobs.Begin();
            k_Update.Begin();
            systemHandle.Update(World.Unmanaged);
            k_Update.End();
            EntityManager.CompleteAllTrackedJobs();
            k_CompleteTrackedJobs.End();
        }
    }
#endif
}

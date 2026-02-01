using Unity.Collections;
using Unity.Entities;
using Unity.PerformanceTesting;
using Unity.Profiling;

namespace Unity.NetCode.Tests
{
#if UNITY_EDITOR
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(GhostCollectionSystem))]
    [UpdateAfter(typeof(NetDebugSystem))]
    [UpdateBefore(typeof(GhostReceiveSystem))]
    internal partial class GhostReceiveSystemProxy : SystemBase
    {
        static readonly ProfilerMarker k_Update = new ProfilerMarker("GhostReceiveSystem_OnUpdate");
        static readonly ProfilerMarker k_CompleteTrackedJobs = new ProfilerMarker("GhostReceiveSystem_CompleteAllTrackedJobs");

        protected override void OnUpdate()
        {
            EntityManager.CompleteAllTrackedJobs();

            var systemHandle = World.GetExistingSystem<GhostReceiveSystem>();
            var unmanagedSystem = World.Unmanaged.GetExistingSystemState<GhostReceiveSystem>();
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

using Unity.Collections;
using Unity.Entities;
using Unity.Profiling;

namespace Unity.NetCode.Tests
{
#if UNITY_EDITOR
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostReceiveSystem))]
    [UpdateBefore(typeof(GhostSpawnClassificationSystemGroup))]
    [UpdateBefore(typeof(GhostInputSystemGroup))]
    [UpdateBefore(typeof(GhostUpdateSystem))]
    internal partial class GhostUpdateSystemProxy : SystemBase
    {
        static readonly ProfilerMarker k_Update = new ProfilerMarker("GhostUpdateSystem_OnUpdate");
        static readonly ProfilerMarker k_CompleteTrackedJobs = new ProfilerMarker("GhostUpdateSystem_CompleteAllTrackedJobs");

        protected override void OnUpdate()
        {
            EntityManager.CompleteAllTrackedJobs();

            var systemHandle = World.GetExistingSystem<GhostUpdateSystem>();
            if (systemHandle == SystemHandle.Null)
            {
                Assertions.Assert.IsTrue(World.IsThinClient());
                return;
            }
            var unmanagedSystem = World.Unmanaged.GetExistingSystemState<GhostUpdateSystem>();
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

using Unity.Collections;
using Unity.Entities;
using Unity.Profiling;

namespace Unity.NetCode.Tests
{
#if UNITY_EDITOR
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    internal partial class NetworkStreamReceiveSystemProxy : ComponentSystemBase
    {
        static readonly ProfilerMarker k_Update = new ProfilerMarker("NetworkStreamReceiveSystem_OnUpdate");
        static readonly ProfilerMarker k_CompleteTrackedJobs = new ProfilerMarker("NetworkStreamReceiveSystem_CompleteAllTrackedJobs");

        public override void Update()
        {
            EntityManager.CompleteAllTrackedJobs();

            var systemHandle = World.GetExistingSystem<NetworkStreamReceiveSystem>();
            var unmanagedSystem = World.Unmanaged.GetExistingSystemState<NetworkStreamReceiveSystem>();
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

using Unity.Profiling.Editor;
using System;
using Unity.Profiling;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// The profiler module for the client world in Netcode for Entities.
    /// </summary>
    [Serializable]
    [ProfilerModuleMetadata("Client World", IconPath = "Packages/com.unity.netcode/EditorIcons/GhostAuthoring.png")]
    class ClientWorldProfiler : ProfilerModule
    {
        public ClientWorldProfiler()
            : base(new[]
            {
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.GhostSnapshotsCounterNameClient, ProfilerCategory.Network),
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.GhostInstancesCounterNameClient, ProfilerCategory.Network),
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.JitterCounterName, ProfilerCategory.Network),
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.RTTCounterName, ProfilerCategory.Network),
            }, ProfilerModuleChartType.Line) { } // TODO: Make it a bar chart once it's available

        public override ProfilerModuleViewController CreateDetailsViewController()
            => new NetcodeForEntitiesProfilerModuleViewController(ProfilerWindow, NetworkRole.Client);
    }
}

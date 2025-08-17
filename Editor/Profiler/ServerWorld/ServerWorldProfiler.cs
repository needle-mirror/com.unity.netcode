using Unity.Profiling.Editor;
using System;
using Unity.Profiling;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// The profiler module for the server world in Netcode for Entities.
    /// </summary>
    [ProfilerModuleMetadata("Server World", IconPath = "Packages/com.unity.netcode/EditorIcons/GhostAuthoring.png")]
    class ServerWorldProfiler : ProfilerModule
    {
        public ServerWorldProfiler()
            : base(new[]
            {
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.GhostSnapshotsCounterNameServer, ProfilerCategory.Network),
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.GhostInstancesCounterNameServer, ProfilerCategory.Network)
            }, ProfilerModuleChartType.Line) { } // TODO: Make it a bar chart once it's available

        public override ProfilerModuleViewController CreateDetailsViewController()
            => new NetcodeForEntitiesProfilerModuleViewController(ProfilerWindow, NetworkRole.Server);
    }
}

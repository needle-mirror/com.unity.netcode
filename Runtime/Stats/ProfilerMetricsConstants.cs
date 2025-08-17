#if UNITY_EDITOR || NETCODE_DEBUG

using System;

namespace Unity.NetCode
{
    static class ProfilerMetricsConstants
    {
        internal static readonly Guid ServerGuid = new("bb2f5e1e-84f3-49e8-95db-d3cf7050f234");
        internal static readonly Guid ClientGuid = new("15b7eaa7-0e58-4121-8833-aeddeaf751a0");

        internal const string GhostInstancesCounterNameServer = "Ghost Instances (Server)";
        internal const string GhostInstancesCounterNameClient = "Ghost Instances (Client)";
        internal const string GhostSnapshotsCounterNameServer = "Ghost Snapshots (Server)";
        internal const string GhostSnapshotsCounterNameClient = "Ghost Snapshots (Client)";
        internal const string JitterCounterName = "Jitter";
        internal const string RTTCounterName = "RTT";
        internal const string SnapshotAgeMinCounterName = "Snapshot Age Min";
        internal const string SnapshotAgeMaxCounterName = "Snapshot Age Max";

        internal const int NetworkMetricsTag = 0;
        internal const int GhostNamesTag = 1;
        internal const int GhostMetricsTag = 2;
        internal const int PredictionErrorNamesTag = 3;
        internal const int PredictionErrorMetricsTag = 4;
        internal const int ProfilerMetricsTag = 5;
        internal const int UncompressedSizesPerTypeTag = 6;
        internal const int SerializedGhostStatsSnapshotTag = 7;
        internal const int PrefabSerializersTag = 8;
        internal const int SerializerStatesTag = 9;
        internal const int ComponentIndicesTag = 10;
        internal const int CommandStatsTag = 11;
    }
}
#endif

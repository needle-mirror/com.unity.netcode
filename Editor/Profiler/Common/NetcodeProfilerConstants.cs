using System.Collections.Generic;

namespace Unity.NetCode.Editor
{
    static class NetcodeProfilerConstants
    {
        internal static readonly int s_CompressionEfficiencyWarningThresholdPercentage = 70;
        internal static readonly float s_MaxMessageSizeWarningThreshold = 0.9f;

        internal const string nameKey = "name";
        internal const string sizeKey = "size";
        internal const string percentOfSnapshotKey = "percentOfSnapshot";
        internal const string instanceCountKey = "instanceCount";
        internal const string compressionKey = "compression";
        internal const string avgSizePerEntityKey = "avgSizePerEntity";
        internal static Dictionary<string, string> s_ColumnKeysToTitles = new()
        {
            { nameKey, "Name" },
            { sizeKey, "Size in bits (bytes)" },
            { percentOfSnapshotKey, "% of snapshot size" },
            { instanceCountKey, "Instance count" },
            { compressionKey, "Compression efficiency" },
            { avgSizePerEntityKey, "Avg size / instance" },
        };

        internal static readonly string s_ProfilerDocsLink = "https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/testing/network-profiler.html";

        internal static readonly string s_SnapshotOverheadTooltip = "Ghost snapshot overhead.\nMetadata not directly linked to ghost data but still part of a snapshot's tick, such as spawns, despawns, and prefab lists. Refer to the Netcode for Entities Profiler documentation for more information.";
        internal static readonly string s_GhostPrefabOverheadTooltip = "Ghost prefab overhead. \nMetadata to help deserialize per-ghost data, such as ghost IDs, spawn ticks, and baselines. Refer to the Netcode for Entities Profiler documentation for more information.";

        // TreeView column header tooltips
        internal static readonly string s_NameTooltip = "Name of the ghost prefab or component.";
        internal static readonly string s_SizeTooltip = "Size of this item in the snapshot.";
        internal static readonly string s_PercentOfSnapshotTooltip = "Percentage of the total snapshot size taken up by this item.";
        internal static readonly string s_InstanceCountTooltip = "Number of instances of this item in the snapshot.";
        internal static readonly string s_AvgSizePerInstanceTooltip = "Average size per instance of this item in the snapshot.";
        internal static readonly string s_CompressionEfficiencyTooltip = "Compression efficiency is 1-(uncompressed size/compressed size). Ideally, you want the compression efficiency % to be as high as possible.";

        // Warnings
        internal static readonly string s_CompressionEfficiencyWarning = "This item has poor compression efficiency.";
        internal static string GetMaxMessageSizeWarning(int size)
        {
            return $"This value is reaching the max message size of {size} Bytes.";
        }
    }
}

using System;
using UnityEngine.Analytics;

namespace Unity.NetCode.Editor.Analytics
{
#if UNITY_2023_2_OR_NEWER
    // Schema: com.unity3d.data.schemas.editor.analytics.n4eToolsPlayModeLogCommandStats_v1
    // Taxonomy: editor.analytics.n4eToolsPlayModeLogCommandStats.v1
    [AnalyticInfo(eventName: "n4eToolsPlayModeLogCommandStats", vendorKey: "unity.netcode", version: 1, maxEventsPerHour: 1000)]
    internal struct PlayModeLogCommandStatsAnalytic : IAnalytic
    {
        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            data = null;
            error = null;
            return true;
        }
    }
#else
    internal struct PlayModeLogCommandStatsAnalytic{}
#endif
}

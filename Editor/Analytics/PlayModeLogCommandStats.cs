using System;
using UnityEngine.Analytics;

namespace Unity.NetCode.Editor.Analytics
{
#if UNITY_2023_2_OR_NEWER
    [AnalyticInfo(eventName: "n4eToolsPlayModeLogCommandStats", vendorKey: "unity.netcode", version: 1, maxEventsPerHour: 1000)]
    internal struct PlayModeLogCommandStatsAnalytic : IAnalytic
    {
        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            data = null;
            error = null;
            return false;
        }
    }
#else
    internal struct PlayModeLogCommandStatsAnalytic{}
#endif
}

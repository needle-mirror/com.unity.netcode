using System;
using UnityEngine.Analytics;

namespace Unity.NetCode.Editor.Analytics
{
    [Serializable]
#if UNITY_2023_2_OR_NEWER
    internal struct PlayModeLagSpikeTriggeredData : IAnalytic.IData
#else
    internal struct PlayModeLagSpikeTriggeredData
#endif
    {
        public int lengthMs;
    }

#if UNITY_2023_2_OR_NEWER
    // Schema: com.unity3d.data.schemas.editor.analytics.n4eToolsPlayModeLagSpikeTriggered_v1
    // Taxonomy: editor.analytics.n4eToolsPlayModeLagSpikeTriggered.v1
    [AnalyticInfo(eventName: "n4eToolsPlayModeLagSpikeTriggered", vendorKey: "unity.netcode", version:1, maxEventsPerHour: 1000)]
    internal class PlayModeLagSpikeTriggeredAnalytic : IAnalytic
#else
    internal class PlayModeLagSpikeTriggeredAnalytic
#endif
    {
        public PlayModeLagSpikeTriggeredAnalytic(int lengthMs)
        {
            m_Data = new PlayModeLagSpikeTriggeredData()
            {
                lengthMs = lengthMs
            };
        }

#if UNITY_2023_2_OR_NEWER
        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            error = null;
            data = m_Data;
            return data != null;
        }
#endif

        private PlayModeLagSpikeTriggeredData m_Data;
    }
}

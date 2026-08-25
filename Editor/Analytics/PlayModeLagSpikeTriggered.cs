using System;
using UnityEngine.Analytics;

namespace Unity.NetCode.Editor.Analytics
{
    [Serializable]
    internal struct PlayModeLagSpikeTriggeredData : IAnalytic.IData
    {
        public int lengthMs;
    }

    // Schema: com.unity3d.data.schemas.editor.analytics.n4eToolsPlayModeLagSpikeTriggered_v1
    // Taxonomy: editor.analytics.n4eToolsPlayModeLagSpikeTriggered.v1
    [AnalyticInfo(eventName: "n4eToolsPlayModeLagSpikeTriggered", vendorKey: "unity.netcode", version:1, maxEventsPerHour: 1000)]
    internal class PlayModeLagSpikeTriggeredAnalytic : IAnalytic
    {
        public PlayModeLagSpikeTriggeredAnalytic(int lengthMs)
        {
            m_Data = new PlayModeLagSpikeTriggeredData()
            {
                lengthMs = lengthMs
            };
        }

        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            error = null;
            data = m_Data;
            return data != null;
        }

        private PlayModeLagSpikeTriggeredData m_Data;
    }
}

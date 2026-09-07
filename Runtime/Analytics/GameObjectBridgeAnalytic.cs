#if UNITY_EDITOR
using System;
using UnityEngine.Analytics;

namespace Unity.Netcode.Analytics
{
    [Serializable]
    internal class GameObjectBridgeData : IAnalytic.IData
    {
        // Marks that the GameObject bridge is active for this session.
        // This struct is the home for future GameObject-bridge usage metrics; bump the analytic
        // version and update the server-side schema whenever fields are added here.
        public bool GameObjectsUsed;
        public bool SingleWorldHostUsed;
    }

    // Schema: com.unity3d.data.schemas.editor.analytics.n4eGameObjectBridge_v2
    // Taxonomy: editor.analytics.n4eGameObjectBridge.v2
    [AnalyticInfo(eventName: "n4eGameObjectBridge", vendorKey: "unity.netcode", version: 2, maxEventsPerHour: 100)]
    internal class GameObjectBridgeAnalytic : IAnalytic
    {
        public GameObjectBridgeAnalytic(GameObjectBridgeData data)
        {
            m_Data = data;
        }

        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            error = null;
            data = m_Data;
            return data != null;
        }

        private GameObjectBridgeData m_Data;
    }
}
#endif

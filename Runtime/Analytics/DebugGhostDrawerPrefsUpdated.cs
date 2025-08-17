using System;
using UnityEngine.Analytics;

namespace Unity.NetCode.Analytics
{
    [Serializable]
#if UNITY_2023_2_OR_NEWER
    internal class DebugGhostDrawerPreferencesUpdatedData : IAnalytic.IData, IEquatable<DebugGhostDrawerPreferencesUpdatedData>
#else
    internal class DebugGhostDrawerPreferencesUpdatedData : IEquatable<DebugGhostDrawerPreferencesUpdatedData>
#endif
    {
        public string name;
        public bool enabled;
        public bool detailVisible;

        public bool Equals(DebugGhostDrawerPreferencesUpdatedData other)
        {
            if (other is null)
                return false;
            return name == other.name && enabled == other.enabled && detailVisible == other.detailVisible;
        }
    }

#if UNITY_2023_2_OR_NEWER
    // Schema: com.unity3d.data.schemas.editor.analytics.n4eToolsDebugGhostDrawerPrefsUpdated_v1
    // Taxonomy: editor.analytics.n4eToolsDebugGhostDrawerPrefsUpdated.v1
    [AnalyticInfo(eventName: "n4eToolsDebugGhostDrawerPrefsUpdated", vendorKey: "unity.netcode", version:1, maxEventsPerHour: 1000)]
    internal class DebugGhostDrawerPreferencesUpdatedAnalytic : IAnalytic
#else
    internal class DebugGhostDrawerPreferencesUpdatedAnalytic
#endif
    {
        public DebugGhostDrawerPreferencesUpdatedAnalytic(DebugGhostDrawerPreferencesUpdatedData data)
        {
            m_Data = data;
            // Only report our DebugGhostDrawer names, not the custom user ones because it could be PI.
            var ourDrawerNames = new[] { "Bounding Boxes", "Importance Visualizer" };
            if (!Array.Exists(ourDrawerNames, n => n == m_Data.name))
                m_Data.name = "Custom";
        }

#if UNITY_2023_2_OR_NEWER
        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            error = null;
            data = m_Data;
            return data != null;
        }
#endif

        private DebugGhostDrawerPreferencesUpdatedData m_Data;
    }
}

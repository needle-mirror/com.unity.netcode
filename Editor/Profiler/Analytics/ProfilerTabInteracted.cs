using System;
using UnityEngine.Analytics;

namespace Unity.NetCode.Editor.Analytics
{
    [Serializable]
    internal struct ProfilerTabInteractedData : IAnalytic.IData
    {
        public string moduleName;
        public string tabName;
        public string elementTypeName;
        public string elementName;
    }
    // Schema: com.unity3d.data.schemas.editor.analytics.n4e_profiler_tabInteracted_v1
    // Taxonomy: editor.analytics.n4e_profiler_tabInteracted.v1
    [AnalyticInfo(eventName: "n4e_profiler_tabInteracted", vendorKey: "unity.netcode", version:1)]
    internal class ProfilerTabInteractedAnalytic : IAnalytic
    {
        ProfilerTabInteractedData m_Data;

        public ProfilerTabInteractedAnalytic(string moduleName, string tabName, string elementTypeName, string elementName)
        {
            m_Data = new ProfilerTabInteractedData
            {
                moduleName = moduleName,
                tabName = tabName,
                elementTypeName = elementTypeName,
                elementName = elementName
            };
        }

        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            error = null;
            data = m_Data;
            return true;
        }
    }
}

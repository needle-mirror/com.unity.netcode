using System;
using Unity.NetCode.Editor.Analytics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// The base class for Netcode profiler tabs.
    /// </summary>
    class NetcodeProfilerTab : Tab
    {
        readonly VisualElement m_MainView;
        protected VisualElement m_NoDataInfoLabels;
        protected NetworkRole m_NetworkRole;
        string m_TabName;

        internal NetcodeProfilerTab(string tabName, NetworkRole networkRole) : base(tabName)
        {
            m_TabName = tabName;
            m_NetworkRole = networkRole;
            var scrollView = new ScrollView();
            m_MainView = new VisualElement();
            m_MainView.AddToClassList("mainview-container");
            var labelTrimmed = tabName.Trim().Replace(" ", "");
            m_MainView.viewDataKey = $"{m_NetworkRole.ToString()}-{labelTrimmed}-MainView";
            scrollView.viewDataKey = $"{m_NetworkRole.ToString()}-{labelTrimmed}-MainViewScrollView";
            scrollView.Add(m_MainView);
            base.Add(scrollView);
            viewDataKey = $"{m_NetworkRole.ToString()}-{labelTrimmed}-NetcodeProfilerTab";

            var packetDirection = ProfilerUtils.GetPacketDirection(networkRole);
            m_NoDataInfoLabels = UIFactory.CreateNoDataInfoLabel(packetDirection, NetcodeProfilerConstants.s_ProfilerDocsLink);
            Add(m_NoDataInfoLabels);

            RegisterCallback<ClickEvent>(OnTabInteracted);
        }

        void OnTabInteracted(ClickEvent evt)
        {
            var moduleName = m_NetworkRole == NetworkRole.Client ? "Client World" : "Server World";
            var elementName = ((VisualElement)evt.target).name;
            if (!string.IsNullOrEmpty(elementName))
            {
                var tabInteractedAnalytic = new ProfilerTabInteractedAnalytic(moduleName, m_TabName, evt.target.GetType().Name, elementName);
                EditorAnalytics.SendAnalytic(tabInteractedAnalytic);
            }
        }

        internal new void Add(VisualElement element)
        {
            m_MainView.Add(element);
        }

        internal void AddMetricsHeader(MetricsHeader metricsHeader)
        {
            Insert(0, metricsHeader);
        }

        internal virtual void Dispose()
        {

        }
    }
}

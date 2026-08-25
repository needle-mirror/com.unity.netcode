using System;
using System.Collections.Generic;
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
        protected VisualElement m_InfoTextContainer;
        protected TabInfoTextManager m_InfoTextManager;
        protected NetworkRole m_NetworkRole;
        string m_TabName;
        readonly List<VisualElement> m_DataElements;
        readonly InfoTextContext m_CachedInfoTextContext;

        internal NetcodeProfilerTab(string tabName, NetworkRole networkRole) : base(tabName)
        {
            m_TabName = tabName;
            m_NetworkRole = networkRole;
            m_DataElements = new List<VisualElement>();
            m_CachedInfoTextContext = new InfoTextContext { NetworkRole = networkRole };

            var scrollView = new ScrollView();
            m_MainView = new VisualElement();
            m_MainView.AddToClassList("mainview-container");
            var labelTrimmed = tabName.Trim().Replace(" ", "");
            m_MainView.viewDataKey = $"{m_NetworkRole.ToString()}-{labelTrimmed}-MainView";
            scrollView.viewDataKey = $"{m_NetworkRole.ToString()}-{labelTrimmed}-MainViewScrollView";
            scrollView.Add(m_MainView);
            base.Add(scrollView);
            viewDataKey = $"{m_NetworkRole.ToString()}-{labelTrimmed}-NetcodeProfilerTab";

            // Create info text container and manager
            m_InfoTextContainer = new VisualElement();
            m_InfoTextContainer.style.display = DisplayStyle.None;
            Add(m_InfoTextContainer);

            m_InfoTextManager = new TabInfoTextManager(m_InfoTextContainer);
            m_InfoTextManager.InfoTextVisibilityChanged += OnInfoTextVisibilityChanged;
            RegisterInfoTextProviders();

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

        /// <summary>
        /// Virtual method for subclasses to register their info text providers.
        /// Default implementation registers host mode and generic no data providers.
        /// Providers are evaluated in registration order - first matching provider is shown.
        /// </summary>
        protected virtual void RegisterInfoTextProviders()
        {
            var packetDirection = ProfilerUtils.GetPacketDirection(m_NetworkRole);

            // Register providers in order from most specific to most general
            if (m_NetworkRole == NetworkRole.Server)
            {
                // Host mode with no clients connected
                m_InfoTextManager.RegisterProvider(new HostModeNoClientsInfoTextProvider(m_NetworkRole));
                // Server module when editor is acting as a client
                m_InfoTextManager.RegisterProvider(new ServerModuleOnClientInfoTextProvider(m_NetworkRole));
                // Server build with no clients connected
                m_InfoTextManager.RegisterProvider(new ServerModuleNoClientsInfoTextProvider(m_NetworkRole));
            }

            if (m_NetworkRole == NetworkRole.Client)
            {
                // Client module in host mode
                m_InfoTextManager.RegisterProvider(new ClientHostModeInfoTextProvider(m_NetworkRole));
                // Client module when profiling a server build
                m_InfoTextManager.RegisterProvider(new ClientModuleOnServerInfoTextProvider());
            }

            // Generic fallback - no data this frame (registered last)
            m_InfoTextManager.RegisterProvider(new NoDataInfoTextProvider(packetDirection));
        }

        /// <summary>
        /// Helper method for subclasses to update info text based on current frame data.
        /// Reuses cached InfoTextContext to avoid allocations during profiler updates.
        /// </summary>
        /// <param name="frameData">The current frame's profiler data</param>
        protected void UpdateInfoText(NetcodeFrameData frameData)
        {
            m_CachedInfoTextContext.FrameData = frameData;
            m_InfoTextManager.Update(m_CachedInfoTextContext);
        }

        /// <summary>
        /// Register VisualElements that should be hidden when info text is displayed.
        /// Call this method during tab initialization to specify which data elements
        /// should be hidden when an info message is shown (e.g., "No data available").
        /// </summary>
        /// <param name="elements">VisualElements to hide when info text is shown</param>
        protected void RegisterDataElements(params VisualElement[] elements)
        {
            m_DataElements.AddRange(elements);
        }

        /// <summary>
        /// Handles info text visibility changes and toggles data element visibility.
        /// When info text is shown, registered data elements are hidden.
        /// When info text is hidden, registered data elements are shown.
        /// </summary>
        /// <param name="infoTextVisible">True if info text is visible, false otherwise</param>
        void OnInfoTextVisibilityChanged(bool infoTextVisible)
        {
            var dataElementsDisplay = infoTextVisible ? DisplayStyle.None : DisplayStyle.Flex;
            foreach (var element in m_DataElements)
            {
                if (element != null)
                {
                    element.style.display = dataElementsDisplay;
                }
            }
        }

        internal virtual void Dispose()
        {
            if (m_InfoTextManager != null)
            {
                m_InfoTextManager.InfoTextVisibilityChanged -= OnInfoTextVisibilityChanged;
            }
        }
    }
}

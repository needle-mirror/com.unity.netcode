using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Manages info text providers for a profiler tab.
    /// Evaluates providers in registration order and displays the first matching one.
    /// </summary>
    class TabInfoTextManager
    {
        readonly List<IInfoTextProvider> m_Providers;
        readonly VisualElement m_Container;
        IInfoTextProvider m_CurrentProvider;

        /// <summary>
        /// Gets the currently active provider (for testing purposes).
        /// </summary>
        internal IInfoTextProvider CurrentProvider => m_CurrentProvider;

        /// <summary>
        /// Event fired when info text visibility changes.
        /// True = info text is visible, False = info text is hidden
        /// </summary>
        public event Action<bool> InfoTextVisibilityChanged;

        /// <summary>
        /// Create a new TabInfoTextManager
        /// </summary>
        /// <param name="container">The container element where info text will be displayed</param>
        public TabInfoTextManager(VisualElement container)
        {
            m_Container = container;
            m_Providers = new List<IInfoTextProvider>();
        }

        /// <summary>
        /// Register a new info text provider.
        /// Providers are evaluated in the order they are registered.
        /// Register more specific providers before generic fallback providers.
        /// </summary>
        /// <param name="provider">The provider to register</param>
        public void RegisterProvider(IInfoTextProvider provider)
        {
            m_Providers.Add(provider);
        }

        /// <summary>
        /// Update the info text display based on the current context.
        /// Evaluates all providers in registration order and displays the first match.
        /// </summary>
        /// <param name="context">Context for evaluating provider conditions</param>
        public void Update(InfoTextContext context)
        {
            // Find first matching provider
            IInfoTextProvider matchingProvider = null;
            foreach (var provider in m_Providers)
            {
                if (provider.ShouldShow(context))
                {
                    matchingProvider = provider;
                    break;
                }
            }

            // Track previous visibility state
            var wasVisible = m_Container.style.display == DisplayStyle.Flex;
            var isVisible = matchingProvider != null;

            // Show/hide based on match
            if (isVisible)
            {
                // Only update UI if provider changed to avoid unnecessary redraws
                if (m_CurrentProvider != matchingProvider)
                {
                    m_Container.Clear();
                    var infoElement = matchingProvider.CreateInfoText();
                    m_Container.Add(infoElement);
                    m_CurrentProvider = matchingProvider;
                }
                m_Container.style.display = DisplayStyle.Flex;
            }
            else
            {
                m_Container.style.display = DisplayStyle.None;
                m_CurrentProvider = null;
            }

            // Fire event if visibility changed
            if (wasVisible != isVisible)
            {
                InfoTextVisibilityChanged?.Invoke(isVisible);
            }
        }
    }
}
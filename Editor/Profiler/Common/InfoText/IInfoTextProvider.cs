using UnityEngine.UIElements;

namespace Unity.Netcode.Editor
{
    /// <summary>
    /// Interface for info text providers that display contextual information in profiler tabs.
    /// Providers are evaluated in registration order, and the first matching provider is displayed.
    /// </summary>
    interface IInfoTextProvider
    {
        /// <summary>
        /// Check if this provider's condition is met and should be displayed.
        /// </summary>
        /// <param name="context">Context containing frame data, network role, and World reference</param>
        /// <returns>True if this provider should be shown, false otherwise</returns>
        bool ShouldShow(InfoTextContext context);

        /// <summary>
        /// Create the info text visual element to display.
        /// </summary>
        /// <returns>A VisualElement containing the info text UI</returns>
        VisualElement CreateInfoText();
    }
}

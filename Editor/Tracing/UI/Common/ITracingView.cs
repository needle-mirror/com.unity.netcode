using System.Threading.Tasks;
using Unity.Netcode.Tracing;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor.Tracing.UI
{
    /// <summary>
    /// Base interface for all tracing views providing lifecycle management.
    /// All views must implement Create, Clear, and Dispose for proper UI lifecycle.
    /// </summary>
    internal interface ITracingView
    {
        /// <summary>
        /// Creates and returns the root visual element for this view.
        /// </summary>
        /// <returns>The root VisualElement containing the view's UI hierarchy.</returns>
        VisualElement Create();

        /// <summary>
        /// Clears all content from the view, resetting it to an empty state.
        /// </summary>
        void Clear();

        /// <summary>
        /// Disposes of resources and performs cleanup when the view is no longer needed.
        /// </summary>
        void Dispose();
    }

    /// <summary>
    /// Interface for views that automatically update when new tracing data becomes available.
    /// These views don't need user interaction to trigger updates.
    /// </summary>
    internal interface IDataObserver : ISelectionObserver
    {
        /// <summary>
        /// Called automatically when new tracing data is available.
        /// </summary>
        /// <param name="data">The tracing data to display.</param>
        Task OnDataAvailable(TracingData data);
    }

    /// <summary>
    /// Interface for views that update in response to user selections in other views.
    /// These views receive data when a selection changes elsewhere in the UI.
    /// </summary>
    internal interface ISelectionObserver : ITracingView
    {

        /// <summary>
        /// Called when the selected frame or tick changes. Update your UI to show details for the new frame selection.
        /// </summary>
        /// <param name="tracingSelectionChange"></param>
        Task OnSelectedTickOrFrameChanged(TracingSelectionChange tracingSelectionChange);
    }

}

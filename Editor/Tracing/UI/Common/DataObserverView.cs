using System.Threading.Tasks;
using Unity.Netcode.Tracing;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor.Tracing.UI
{
    /// <summary>
    /// Base class for views that automatically update when tracing data is available.
    /// Extend this class for views that need to react to data changes without user interaction.
    /// </summary>
    internal abstract class DataObserverView : IDataObserver
    {
        protected VisualElement m_Root = new();

        protected TracingData m_Data;

        /// <summary>
        /// USS class name for this view type. Used for styling.
        /// </summary>
        protected abstract string UssClassName { get; }

        /// <summary>
        /// Creates the view's visual element hierarchy.
        /// </summary>
        public abstract VisualElement Create();

        /// <summary>
        /// Called when new tracing data is available. Update your UI here.
        /// </summary>
        /// <param name="data">The latest tracing data.</param>
        public abstract Task OnDataAvailable(TracingData data);

        /// <summary>
        /// Clears the view's content. Default implementation clears the root element.
        /// Override to provide custom clearing logic.
        /// </summary>
        public virtual void Clear()
        {
            m_Root.Clear();
        }

        /// <summary>
        /// Disposes resources. Always call base.Dispose() if you override.
        /// </summary>
        public abstract void Dispose();

        public virtual async Task OnSelectedTickOrFrameChanged(TracingSelectionChange tracingSelectionChange)
        {
            await OnSelectedFrameChanged(tracingSelectionChange.frameID);
            await OnSelectedTickChanged(tracingSelectionChange.tickID);
        }

        /// <summary>
        /// Handles selected frame change from external sources.
        /// <param name="frameID">The newly selected frame ID.</param>
        /// </summary>
        protected internal abstract Task OnSelectedFrameChanged(FrameID frameID);

        /// <summary>
        /// Handles selected tick change from external sources.
        /// <param name="tickID">The newly selected tick ID.</param>
        /// </summary>
        protected internal abstract Task OnSelectedTickChanged(TickID tickID);

    }
}

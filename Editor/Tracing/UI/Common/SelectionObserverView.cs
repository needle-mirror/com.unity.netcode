using Unity.NetCode.Tracing;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI
{
    /// <summary>
    /// Base class for views that update based on selections in other views.
    /// Extend this class for detail/inspector views that show information about selected items.
    /// </summary>
    internal abstract class SelectionObserverView : ISelectionObserver
    {
        protected VisualElement m_Root = new();

        /// <summary>
        /// USS class name for this view type. Used for styling.
        /// </summary>
        protected abstract string UssClassName { get; }

        /// <summary>
        /// Creates the view's visual element hierarchy.
        /// </summary>
        public abstract VisualElement Create();

        /// <summary>
        /// Called when a selection changes. Update your UI to show details for the selection.
        /// </summary>
        /// <param name="data">The tracing data context for the selection.</param>
        public abstract void OnSelectionChanged(TracingData data);

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

        public abstract Task OnSelectedTickOrFrameChanged(TracingSelectionChange tracingSelectionChange);

    }
}

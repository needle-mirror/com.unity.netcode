using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI
{
    /// <summary>
    /// Base class for static views that don't need tracing data updates.
    /// Extend this class for UI elements that are self-contained and don't react to data changes.
    /// Examples: Toolbars, configuration panels, help text.
    /// </summary>
    internal abstract class StaticView : ITracingView
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
    }
}
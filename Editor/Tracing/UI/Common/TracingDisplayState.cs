using System;

namespace Unity.NetCode.Editor.Tracing.UI
{
    /// <summary>
    /// Whether the tracing tool is currently presenting a recorded trace (timeline, tick inspector and the rest are populated).
    /// </summary>
    static class TracingDisplayState
    {
        public static bool IsShowingTraces { get; private set; }

        /// <summary>Raised whenever <see cref="IsShowingTraces"/> flips.</summary>
        public static event Action Changed;

        public static void SetShowingTraces(bool showing)
        {
            if (IsShowingTraces == showing)
                return;
            IsShowingTraces = showing;
            Changed?.Invoke();
        }
    }
}

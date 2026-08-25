namespace Unity.NetCode.Editor.Tracing.UI
{
    /// <summary>
    /// Range and stepping of the replay speed multiplier. A single speed is shared by all
    /// <see cref="ReplayRenderingFrequency"/> options; changing the option does not change the speed.
    /// </summary>
    internal static class ReplaySpeed
    {
        public const float Min = 0.5f;
        public const float Max = 3f;
        public const float Step = 0.5f;
        public const float Default = 1f;
    }

    /// <summary>Specifies how frequently the replay should render during playback.</summary>
    internal enum ReplayRenderingFrequency {
        /// <summary>
        /// Replay update once per frame.
        /// </summary>
        OncePerFrame,
        /// <summary>
        /// Replay update once per tick.
        /// </summary>
        OncePerTick,
        /// <summary>
        /// Replay update once per system.
        /// </summary>
        OncePerSystem,
    }
}

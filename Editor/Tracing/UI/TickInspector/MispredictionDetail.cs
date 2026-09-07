using System;
using Unity.Entities;

namespace Unity.Netcode.Editor.Tracing.UI.TickInspector
{
    /// <summary>
    /// Client/server component values before/after the traced system ran. Backs a MispredictionTable row.
    /// </summary>
    sealed class MispredictionDetail
    {
        public Type ComponentType;

        public IComponentData ClientBefore;
        public IComponentData ClientAfter;
        public IComponentData ServerBefore;
        public IComponentData ServerAfter;

        public float FuzzyDiffThreshold;
    }
}

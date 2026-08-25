using System;
using Unity.NetCode.Tracing;

namespace Unity.NetCode.Editor.Tracing.UI
{
    internal struct TracingSelectionChange : IEquatable<TracingSelectionChange>
    {
        public TickID tickID;
        public FrameID frameID;

        public bool Equals(TracingSelectionChange other)
        {
            return tickID.Equals(other.tickID) && frameID.Equals(other.frameID);
        }

        public override bool Equals(object obj)
        {
            return obj is TracingSelectionChange other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(tickID, frameID);
        }

        public static bool operator ==(TracingSelectionChange left, TracingSelectionChange right) => left.Equals(right);
        public static bool operator !=(TracingSelectionChange left, TracingSelectionChange right) => !left.Equals(right);
    }
}

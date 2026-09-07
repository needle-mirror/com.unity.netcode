using System;
using System.Collections.Generic;
using Unity.Collections;

namespace Unity.Netcode.Tracing
{
    internal struct FrameID : IEquatable<FrameID>, IComparer<FrameID>
    {
        public int value;

        public bool Equals(FrameID other)
        {
            return value.Equals(other.value);
        }

        public override bool Equals(object obj)
        {
            return obj is FrameID other && Equals(other);
        }

        public override int GetHashCode()
        {
            return value.GetHashCode();
        }

        public override string ToString()
        {
            return $"Frame[{value}]";
        }

        public int Compare(FrameID x, FrameID y)
        {
            return x.value.CompareTo(y.value);
        }
    }

    internal struct FrameData : IDisposable
    {
        public NativeList<TickID> TickIDs;
        public NativeHashMap<TickID, TickData> PerTickData;
        public float m_CurrentDeltaTimeSeconds;
        public DiffInfo DiffInfo;

        public FrameData(float deltaTime)
        {
            m_CurrentDeltaTimeSeconds = deltaTime;
            PerTickData = new(0, Allocator.Persistent);
            TickIDs = new(0, Allocator.Persistent);
            DiffInfo = default;
        }

        public void Dispose()
        {
            foreach (var pair in PerTickData)
            {
                pair.Value.Dispose();
            }
            PerTickData.Dispose();
            TickIDs.Dispose();
            DiffInfo.Dispose();
        }
    }
}

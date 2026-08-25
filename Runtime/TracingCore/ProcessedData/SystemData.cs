using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode.Tracing
{
    internal struct SystemID : IEquatable<SystemID>, IComparer<SystemID>
    {
        public SystemTypeIndex value;
        public ulong executionOrder;
        public TracePosition tracePosition; // the position relative to the system (before? after?)

        public SystemID(SystemTypeIndex sys, TracePosition tracePosition)
        {
            value = sys;
            executionOrder = default;
            this.tracePosition = tracePosition;
        }

        public bool Equals(SystemID other)
        {
            return value == other.value && tracePosition == other.tracePosition;
        }

        public bool Equivalent(SystemID other)
        {
            return value == other.value;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(value, tracePosition);
        }

        public int Compare(SystemID x, SystemID y)
        {
            return x.executionOrder.CompareTo(y.executionOrder);
        }

        public override string ToString()
        {
            return $"{this.tracePosition} {TypeManager.GetSystemName(value)}";
        }
    }

    internal struct SystemData : IDisposable
    {
        public ProcessedTracingStateData MainGameTracingState; // most cases, we'll have a single state per system. No need to allocate most of the time.

        public TracePosition TracePosition;
        public DiffInfo DiffInfo;
        public SystemID AssociatedSystem;
        public WorldID.WorldType AssociatedSystemWorld; // the world where to find the associated system
        public TraceType Family; // TODO something better than family? just do type index comparison? TODO remove this completely?
        public SystemData(TracePosition tracePosition, SystemID associatedSystem, WorldID.WorldType associatedSystemWorld, TraceType traceType)
        {
            DiffInfo = default;
            TracePosition = tracePosition;
            AssociatedSystem = associatedSystem;
            Family = traceType;
            AssociatedSystemWorld = associatedSystemWorld;
            MainGameTracingState = default;
        }

        public void Dispose()
        {
            MainGameTracingState.Dispose();
            DiffInfo.Dispose();

        }

        public bool ProcessDiff(ref SystemData authoritativeValue, SystemTypeIndex owningSystem = default, ValueChangeSet valueChanges = default)
        {
            if (MainGameTracingState.ProcessDiff(ref authoritativeValue.MainGameTracingState, owningSystem, valueChanges))
            {
                DiffInfo.Add(MainGameTracingState.DiffInfo);
                authoritativeValue.DiffInfo.Add(authoritativeValue.MainGameTracingState.DiffInfo);
            }

            return DiffInfo.HasDiff;
        }
    }
}

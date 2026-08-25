using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.StateSave;

namespace Unity.NetCode.Tracing
{
    internal struct EntityComponentKey : IEquatable<EntityComponentKey>
    {
        public SavedEntityID Entity;
        public TypeIndex Type;

        public EntityComponentKey(SavedEntityID entity, TypeIndex type)
        {
            Entity = entity;
            Type = type;
        }

        public bool Equals(EntityComponentKey other) => Entity.Equals(other.Entity) && Type == other.Type;
        public override bool Equals(object obj) => obj is EntityComponentKey other && Equals(other);
        public override int GetHashCode() => (Entity.GetHashCode() * 397) ^ Type.GetHashCode();
    }

    // Key of the per-tick "this system changed this component's value" set (see TickData.CollectValueChanges).
    internal struct SystemComponentKey : IEquatable<SystemComponentKey>
    {
        public SystemTypeIndex System;
        public SavedEntityID Entity;
        public TypeIndex Type;

        public SystemComponentKey(SystemTypeIndex system, SavedEntityID entity, TypeIndex type)
        {
            System = system;
            Entity = entity;
            Type = type;
        }

        public bool Equals(SystemComponentKey other) => System == other.System && Entity.Equals(other.Entity) && Type == other.Type;
        public override bool Equals(object obj) => obj is SystemComponentKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(System, Entity, Type);
    }

    // Key of the ValueChangeSet.PerEntity index: "this system changed some value on this entity".
    internal struct SystemEntityKey : IEquatable<SystemEntityKey>
    {
        public SystemTypeIndex System;
        public SavedEntityID Entity;

        public SystemEntityKey(SystemTypeIndex system, SavedEntityID entity)
        {
            System = system;
            Entity = entity;
        }

        public bool Equals(SystemEntityKey other) => System == other.System && Entity.Equals(other.Entity);
        public override bool Equals(object obj) => obj is SystemEntityKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(System, Entity);
    }

    internal struct ValueChangeSet : IDisposable
    {
        public NativeHashSet<SystemComponentKey> PerComponent;
        public NativeHashSet<SystemEntityKey> PerEntity;
        public NativeHashSet<SystemTypeIndex> PerSystem;

        public bool IsCreated => PerComponent.IsCreated;

        public ValueChangeSet(int initialCapacity, Allocator allocator)
        {
            PerComponent = new(initialCapacity, allocator);
            PerEntity = new(initialCapacity, allocator);
            PerSystem = new(initialCapacity, allocator);
        }

        public void Add(SystemTypeIndex system, SavedEntityID entity, TypeIndex type)
        {
            PerComponent.Add(new SystemComponentKey(system, entity, type));
            PerEntity.Add(new SystemEntityKey(system, entity));
            PerSystem.Add(system);
        }

        public void Dispose()
        {
            PerComponent.Dispose();
            PerEntity.Dispose();
            PerSystem.Dispose();
        }
    }

    internal enum TraceType
    {
        Default,
        NetcodeGhostUpdateVsSendComparison, // should check for the other side's version of the associated system for diff
    }

    // when was this trace recorded relative to the target system it's tracing.
    internal  enum TracePosition
    {
        undefined,
        before, // before and after should be mirrored. There should be an after trace for every before trace
        after,
        netcode, // special case for netcode specific traces
    }


    /// <summary>
    /// One deduplicated "what diffed" record: the reasons observed for a (system, ghost, component) path
    /// plus whether the value actually changed during the tick. Stored per scope (tick/frame/world) so
    /// views can filter by reason/target/value-change at any level without recomputing the diff.
    /// </summary>
    internal struct DiffAggregate : IEquatable<DiffAggregate>
    {
        public const int InvalidGhostId = -1;

        public SystemTypeIndex System; // default when the diff isn't tied to a system
        public int GhostId; // InvalidGhostId when the diff isn't tied to a ghost
        public TypeIndex Component; // default when the diff isn't tied to a component
        public DiffInfo.DiffReasons Reasons;
        public bool HasValueChanged;

        public bool Equals(DiffAggregate other) =>
            System == other.System && GhostId == other.GhostId && Component == other.Component
            && Reasons == other.Reasons && HasValueChanged == other.HasValueChanged;

        public override bool Equals(object obj) => obj is DiffAggregate other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(System, GhostId, Component, (int)Reasons, HasValueChanged);

        public override string ToString() =>
            $"[{Reasons}|changed:{HasValueChanged}|sys:{System}|ghost:{GhostId}|comp:{Component}]";
    }

    // Reason flags plus the largest ComponentData diff amount
    internal struct DiffReasonInfo
    {
        public DiffInfo.DiffReasons Reasons;
        public float DataDiffAmount;
    }

    /// <summary>
    /// Stores the result of the diff processing between client and server.
    /// An instance of diffInfo is stored per frame, tick and system.
    /// </summary>
    internal struct DiffInfo : IDisposable
    {
        // "Missing" means the server has something the client doesn't; "Extra" means the client has something the server doesn't.
        [Flags]
        public enum DiffReasons : ushort
        {
            Undefined = 0,
            ComponentData = 1, // the component data value doesn't match between the client and the server
            MissingComponent = 2, // a component is present for that ghost on the server but not on the client
            ExtraComponent = 4, // a component is present for that ghost on the client but not on the server
            MissingGhost = 8, // a ghost is present on the server but not on the client
            ExtraGhost = 16, // a ghost is present on the client but not on the server
            MissingSystem = 32, // a system ran on the server but not on the client
            ExtraSystem = 64, // a system ran on the client but not on the server
            SystemOrder = 128, // the tracked systems ran in a different order on the client and the server
            BatchedTick = 256, // the tick is missing from the server because the server simulated it as part of a batch
            DeltaTime = 512, // the client tick's delta time differs from the matching server tick's
        }

        // Every enum value; useful as the "nothing filtered out" mask.
        public const DiffReasons AllDiffReasons = (DiffReasons)ushort.MaxValue;

        public DiffReasons DiffReasonFlags;

        /// <summary>
        /// Reasons owned by this scope's own row
        /// <see cref="m_ComponentReasons"/>).
        /// </summary>
        public DiffReasons RowReasonFlags;

        NativeList<SavedEntityID> m_ServerMissingObjects;
        NativeList<SavedEntityID> m_ClientMissingObjects;

        // The source of truth for "does this scope have a diff" with the maximum data diff amount in that aggregate.
        internal NativeHashMap<DiffAggregate, float> m_Aggregates;

        // Per-ghost / per-component reasons. Populated per-system by ProcessDiff; not merged by Add().
        NativeHashMap<SavedEntityID, DiffReasonInfo> m_ObjectReasons;
        NativeHashMap<EntityComponentKey, DiffReasonInfo> m_ComponentReasons;

        public bool HasDiff => m_Aggregates.IsCreated && !m_Aggregates.IsEmpty;

        /// Read-only view of the deduplicated diff records for this scope (key) with their largest
        /// ComponentData diff amount (value). Not created when there is no diff.
        public NativeHashMap<DiffAggregate, float>.ReadOnly AggregatesRO => m_Aggregates.AsReadOnly();

        public void Add(DiffInfo other)
        {
            UnionWith(other);
            if (other.m_ServerMissingObjects.IsCreated)
                MissingObjectsFromServer.AddRange(other.m_ServerMissingObjects.AsArray());
            if (other.m_ClientMissingObjects.IsCreated)
                MissingObjectsFromClient.AddRange(other.m_ClientMissingObjects.AsArray());
        }

        // Records one diff occurrence. Duplicated occurrences of the same aggregate collapse into one entry.
        public void AddDiff(SystemTypeIndex system, int ghostId, TypeIndex component, DiffReasons reasons, bool hasValueChanged, float dataDiffAmount = 0f)
        {
            AddDiff(new DiffAggregate
            {
                System = system,
                GhostId = ghostId,
                Component = component,
                Reasons = reasons,
                HasValueChanged = hasValueChanged,
            }, dataDiffAmount);
        }

        // Records a diff occurrence not tied to any system, ghost or component (e.g. DeltaTime).
        public void AddDiff(DiffReasons reasons)
        {
            AddDiff(default, DiffAggregate.InvalidGhostId, default, reasons, false);
            RowReasonFlags |= reasons;
        }

        public void AddDiff(DiffAggregate aggregate, float dataDiffAmount = 0f)
        {
            if (!m_Aggregates.IsCreated)
                m_Aggregates = new NativeHashMap<DiffAggregate, float>(8, Allocator.Persistent);
            m_Aggregates.TryGetValue(aggregate, out var existingAmount);
            m_Aggregates[aggregate] = math.max(existingAmount, dataDiffAmount);
            DiffReasonFlags |= aggregate.Reasons;
        }

        // Rolls another scope's aggregates into this one (system -> tick -> frame -> world), deduplicated, keeping the largest amount per entry.
        public void UnionWith(in DiffInfo other)
        {
            if (!other.m_Aggregates.IsCreated || other.m_Aggregates.IsEmpty)
                return;
            if (!m_Aggregates.IsCreated)
                m_Aggregates = new NativeHashMap<DiffAggregate, float>(other.m_Aggregates.Count, Allocator.Persistent);
            foreach (var kvp in other.m_Aggregates)
            {
                m_Aggregates.TryGetValue(kvp.Key, out var existingAmount);
                m_Aggregates[kvp.Key] = math.max(existingAmount, kvp.Value);
            }
            DiffReasonFlags |= other.DiffReasonFlags;
        }

        public void AddObjectReason(SavedEntityID entity, DiffReasons reason, float dataDiffAmount = 0f)
        {
            if (!m_ObjectReasons.IsCreated)
                m_ObjectReasons = new NativeHashMap<SavedEntityID, DiffReasonInfo>(2, Allocator.Persistent);
            m_ObjectReasons.TryGetValue(entity, out var existing);
            m_ObjectReasons[entity] = new DiffReasonInfo
            {
                Reasons = existing.Reasons | reason,
                DataDiffAmount = math.max(existing.DataDiffAmount, dataDiffAmount),
            };
        }

        public void AddComponentReason(SavedEntityID entity, TypeIndex type, DiffReasons reason, float dataDiffAmount = 0f)
        {
            if (!m_ComponentReasons.IsCreated)
                m_ComponentReasons = new NativeHashMap<EntityComponentKey, DiffReasonInfo>(2, Allocator.Persistent);
            var key = new EntityComponentKey(entity, type);
            m_ComponentReasons.TryGetValue(key, out var existing);
            m_ComponentReasons[key] = new DiffReasonInfo
            {
                Reasons = existing.Reasons | reason,
                DataDiffAmount = math.max(existing.DataDiffAmount, dataDiffAmount),
            };
        }

        public DiffReasonInfo GetObjectReasonInfo(SavedEntityID entity)
        {
            if (m_ObjectReasons.IsCreated && m_ObjectReasons.TryGetValue(entity, out var info))
                return info;
            return default;
        }

        public DiffReasonInfo GetComponentReasonInfo(SavedEntityID entity, TypeIndex type)
        {
            if (m_ComponentReasons.IsCreated && m_ComponentReasons.TryGetValue(new EntityComponentKey(entity, type), out var info))
                return info;
            return default;
        }

        // client objects missing from the server
        public NativeList<SavedEntityID> MissingObjectsFromServer {
            get
            {
                if (!m_ServerMissingObjects.IsCreated)
                    m_ServerMissingObjects = new NativeList<SavedEntityID>(2, Allocator.Persistent); // Only use memory if needed, else it remains uninitialized
                return m_ServerMissingObjects;
            }
        }
        // server objects missing from the client
        public NativeList<SavedEntityID> MissingObjectsFromClient {
            get
            {
                if (!m_ClientMissingObjects.IsCreated)
                    m_ClientMissingObjects = new NativeList<SavedEntityID>(2, Allocator.Persistent); // Only use memory if needed, else it remains uninitialized
                return m_ClientMissingObjects;
            }
        }

        public void Dispose()
        {
            if (m_ClientMissingObjects.IsCreated)
                m_ClientMissingObjects.Dispose();
            if (m_ServerMissingObjects.IsCreated)
                m_ServerMissingObjects.Dispose();
            if (m_Aggregates.IsCreated)
                m_Aggregates.Dispose();
            if (m_ObjectReasons.IsCreated)
                m_ObjectReasons.Dispose();
            if (m_ComponentReasons.IsCreated)
                m_ComponentReasons.Dispose();
        }

        public override string ToString()
        {
            string val = $"HasDiff:{HasDiff}";
            if (HasDiff)
            {
                var clientCount = m_ClientMissingObjects.IsCreated ? m_ClientMissingObjects.Count : 0;
                var serverCount = m_ServerMissingObjects.IsCreated ? m_ServerMissingObjects.Count : 0;
                val += $" reason:{DiffReasonFlags} | aggregates:{m_Aggregates.Count} | missing count client:{clientCount},server:{serverCount}";
            }
            return val;
        }
    }
}

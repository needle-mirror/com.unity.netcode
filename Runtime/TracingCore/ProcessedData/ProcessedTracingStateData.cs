using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Netcode.LowLevel.StateSave;
using UnityEngine.Assertions;

namespace Unity.Netcode.Tracing
{
    internal struct StateID : IEquatable<StateID>
    {
        public int ID;
        public const int NetcodeID = -1;
        public static readonly StateID DefaultNetcode = new StateID() { ID = NetcodeID };

        // useful for adding traces inside a system. By default, there is one state per system position. If you want to add more than one for
        // debugging purposes, assign your own custom ID when adding traces
        public StateID(int customID)
        {
            ID = customID;
        }

        public bool Equals(StateID other)
        {
            return ID == other.ID;
        }

        public override bool Equals(object obj)
        {
            return obj is StateID other && Equals(other);
        }

        public override int GetHashCode()
        {
            return ID;
        }

        public override string ToString()
        {
            return $"{ID}{(ID == NetcodeID ? "(DefaultNetcode)" : "")}";
        }
    }

    internal struct ProcessedTracingStateData : IDisposable
    {
        public RawTracingStateSave m_RawStateSave;
        public DiffInfo DiffInfo;
        public bool Initialized;

        public ProcessedTracingStateData(RawTracingStateSave rawStateSave)
        {
            DiffInfo = default;
            Initialized = true;
            m_RawStateSave = rawStateSave;
        }

        public void Dispose()
        {
            Assert.IsTrue(Initialized, "Initialized");
            DiffInfo.Dispose();
            Initialized = false;
        }

        /// <summary>
        /// Diffs this (client) state against the authoritative (server) state, recording per-object and
        /// per-component reasons plus <see cref="DiffAggregate"/>s stamped with <paramref name="owningSystem"/>
        /// and the tick-wide <paramref name="valueChanges"/> set (see <see cref="TickData.CollectValueChanges"/>).
        /// </summary>
        public unsafe bool ProcessDiff(ref ProcessedTracingStateData authoritativeValue, SystemTypeIndex owningSystem = default, ValueChangeSet valueChanges = default)
        {
            WorldStateSave authoritativeStateSave = authoritativeValue.m_RawStateSave.stateSave;
            WorldStateSave currentStateSave = this.m_RawStateSave.stateSave;
            // One scratch buffer for every comparison; stackalloc inside the loops would grow the stack.
            var diffAmountsByUnit = stackalloc float[TypeDiffer.MaxFieldBits];
            var entities = authoritativeStateSave.GetAllEntities(Allocator.Temp);
            foreach (var objectID in entities)
            {
                if (!currentStateSave.Exists(objectID))
                {
                    AddObjectDiff(ref authoritativeValue, objectID, DiffInfo.DiffReasons.MissingGhost, owningSystem, valueChanges);
                    DiffInfo.MissingObjectsFromClient.Add(objectID);
                    authoritativeValue.DiffInfo.MissingObjectsFromClient.Add(objectID);
                    continue;
                }

                using var currentTypes = currentStateSave.GetComponentTypes(objectID);
                using var authoritativeTypes = authoritativeStateSave.GetComponentTypes(objectID);

                foreach (var type in currentTypes)
                {
                    if (!ContainsType(authoritativeTypes, type))
                        AddComponentDiff(ref authoritativeValue, objectID, type.TypeIndex, DiffInfo.DiffReasons.ExtraComponent, owningSystem, valueChanges);
                }

                for (int i = 0; i < authoritativeTypes.Length; i++)
                {
                    var type = authoritativeTypes[i];
                    if (!ContainsType(currentTypes, type))
                    {
                        AddComponentDiff(ref authoritativeValue, objectID, type.TypeIndex, DiffInfo.DiffReasons.MissingComponent, owningSystem, valueChanges);
                        continue;
                    }

                    if (!currentStateSave.TryGetComponentData(objectID, type, out var leftBytes))
                        continue; // present on both sides but nothing to value-diff (zero-sized components).
                    authoritativeStateSave.TryGetComponentData(objectID, type, out var rightBytes);
                    AllTypeDiffers.Instance.TryAddTypeDiffer(type); // necessary since state save itself can add types to the save (like ghost instance when using the indexing save strategy).
                    UnsafeUtility.MemClear(diffAmountsByUnit, TypeDiffer.MaxFieldBits * sizeof(float));
                    var fieldMask = AllTypeDiffers.Instance.AllDiffers[type].ProcessDiffPerField(leftBytes, rightBytes, diffAmountsByUnit);
                    if (fieldMask != 0)
                    {
                        var dataDiffAmount = MaxUnitAmount(fieldMask, diffAmountsByUnit);
                        AddComponentDiff(ref authoritativeValue, objectID, type.TypeIndex, DiffInfo.DiffReasons.ComponentData, owningSystem, valueChanges, dataDiffAmount);
                    }
                }
            }
            entities.Dispose();

            entities = currentStateSave.GetAllEntities(Allocator.Temp);
            foreach (var clientObjectID in entities)
            {
                if (!authoritativeStateSave.Exists(clientObjectID))
                {
                    AddObjectDiff(ref authoritativeValue, clientObjectID, DiffInfo.DiffReasons.ExtraGhost, owningSystem, valueChanges);
                    DiffInfo.MissingObjectsFromServer.Add(clientObjectID);
                    authoritativeValue.DiffInfo.MissingObjectsFromServer.Add(clientObjectID);
                }
            }

            entities.Dispose();

            return DiffInfo.HasDiff;
        }

        static unsafe float MaxUnitAmount(ulong mask, float* diffAmountsByUnit)
        {
            var maxAmount = 0f;
            for (var bit = 0; bit < TypeDiffer.MaxFieldBits; bit++)
            {
                if ((mask & (1UL << bit)) != 0 && diffAmountsByUnit[bit] > maxAmount)
                    maxAmount = diffAmountsByUnit[bit];
            }

            return maxAmount;
        }

        public unsafe void CollectValueChanges(ref ProcessedTracingStateData other, SystemTypeIndex system, ValueChangeSet results)
        {
            WorldStateSave afterSave = this.m_RawStateSave.stateSave;
            WorldStateSave beforeSave = other.m_RawStateSave.stateSave;

            using var afterEntities = afterSave.GetAllEntities(Allocator.Temp);
            foreach (var objectID in afterEntities)
            {
                using var afterTypes = afterSave.GetComponentTypes(objectID);
                if (!beforeSave.Exists(objectID))
                {
                    // entity appeared during the system: every component is a value change.
                    foreach (var type in afterTypes)
                        results.Add(system, objectID, type.TypeIndex);
                    continue;
                }

                using var beforeTypes = beforeSave.GetComponentTypes(objectID);
                foreach (var type in afterTypes)
                {
                    // Presence via the type list: TryGetComponentData is also false for zero-sized
                    // components that are present, and a static tag is not a value change.
                    if (!ContainsType(beforeTypes, type))
                    {
                        results.Add(system, objectID, type.TypeIndex);
                        continue;
                    }

                    if (!beforeSave.TryGetComponentData(objectID, type, out var beforeBytes)
                        || !afterSave.TryGetComponentData(objectID, type, out var afterBytes))
                        continue; // zero-sized: present on both sides, nothing to compare.

                    AllTypeDiffers.Instance.TryAddTypeDiffer(type);
                    if (TypeDiffer.IsDiffAmount(AllTypeDiffers.Instance.AllDiffers[type].ProcessDiffAmount(beforeBytes, afterBytes)))
                        results.Add(system, objectID, type.TypeIndex);
                }
            }

            // Entities/components that vanished during the system also count as value changes.
            using var beforeEntities = beforeSave.GetAllEntities(Allocator.Temp);
            foreach (var objectID in beforeEntities)
            {
                if (!afterSave.Exists(objectID))
                {
                    using var beforeTypes = beforeSave.GetComponentTypes(objectID);
                    foreach (var type in beforeTypes)
                        results.Add(system, objectID, type.TypeIndex);
                    continue;
                }

                using var beforeOnlyTypes = beforeSave.GetComponentTypes(objectID);
                using var afterTypes = afterSave.GetComponentTypes(objectID);
                foreach (var type in beforeOnlyTypes)
                {
                    if (!ContainsType(afterTypes, type))
                        results.Add(system, objectID, type.TypeIndex);
                }
            }
        }

        // Access mode can differ between the two saves' type lists, so compare type indices only.
        static bool ContainsType(NativeArray<ComponentType> types, ComponentType type)
        {
            foreach (var candidate in types)
            {
                if (candidate.TypeIndex == type.TypeIndex)
                    return true;
            }

            return false;
        }

        // Records the reason on both sides of the comparison; the flag itself carries the direction.
        void AddObjectDiff(ref ProcessedTracingStateData authoritativeValue, SavedEntityID objectID, DiffInfo.DiffReasons reason, SystemTypeIndex owningSystem, ValueChangeSet valueChanges)
        {
            DiffInfo.AddObjectReason(objectID, reason);
            authoritativeValue.DiffInfo.AddObjectReason(objectID, reason);
            var aggregate = new DiffAggregate
            {
                System = owningSystem,
                GhostId = objectID.value.ghostId,
                Component = default,
                Reasons = reason,
                // A ghost that (de)spawned during the owning system counts as a value change (see
                // CollectValueChanges), so "Changed values only" keeps the mismatch it caused.
                HasValueChanged = valueChanges.IsCreated && valueChanges.PerEntity.Contains(new SystemEntityKey(owningSystem, objectID)),
            };
            DiffInfo.AddDiff(aggregate);
            authoritativeValue.DiffInfo.AddDiff(aggregate);
        }

        void AddComponentDiff(ref ProcessedTracingStateData authoritativeValue, SavedEntityID objectID, TypeIndex type, DiffInfo.DiffReasons reason, SystemTypeIndex owningSystem, ValueChangeSet valueChanges, float dataDiffAmount = 0f)
        {
            DiffInfo.AddComponentReason(objectID, type, reason, dataDiffAmount);
            authoritativeValue.DiffInfo.AddComponentReason(objectID, type, reason, dataDiffAmount);
            var aggregate = new DiffAggregate
            {
                System = owningSystem,
                GhostId = objectID.value.ghostId,
                Component = type,
                Reasons = reason,
                HasValueChanged = valueChanges.IsCreated && valueChanges.PerComponent.Contains(new SystemComponentKey(owningSystem, objectID, type)),
            };
            DiffInfo.AddDiff(aggregate, dataDiffAmount);
            authoritativeValue.DiffInfo.AddDiff(aggregate, dataDiffAmount);
        }
    }
}

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Netcode.Tracing;
using Unity.Netcode.LowLevel.StateSave;

namespace Unity.Netcode
{
    internal static class WorldDataManagedExtensions
    {
        // Sentinel ParentExecutionOrder value for top-level (root) systems.
        // Using -1 keeps the sentinel safely out of the per-tick counter range (which starts at 1 via
        // pre-increment in TracingDataSingleton.ProcessRawTrace) even if that counter is later changed.
        public const int NoParentExecutionOrder = -1;

        public static ManagedTickData GetServerTickData(this WorldData worldData, TickID tickID)
        {
            return GetManagedTickData(worldData.PerTickData[tickID], worldData.GhostNames);
        }

        public static ManagedTickData GetClientTickData(this WorldData worldData, FrameID frameID, TickID tickID)
        {
            return GetManagedTickData(worldData.PerFrameData[frameID].PerTickData[tickID], worldData.GhostNames);
        }


        static readonly char[] k_GenericArgDelimiters = { '<', '[' };

        // The tool's own per-tick anchors read better under their role than their type name.
        static string GetDisplaySystemName(SystemTypeIndex system, string shortName)
        {
            if (system == TypeManager.GetSystemTypeIndex<TraceEndPredictionSystem>())
                return "End Of Prediction";
            if (system == TypeManager.GetSystemTypeIndex<TraceEndFrameSystem>())
                return "End Of Frame";
            return shortName;
        }

        static string GetShortSystemName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return string.Empty;

            // Strip generic arguments early. Cecil format uses '<...>'; reflection format uses
            // '[[FullName, Assembly, Version=...]]' — the latter contains dots that would otherwise
            // confuse LastIndexOf('.') below and produce garbage like "0.0]]".
            var genericArgs = fullName.IndexOfAny(k_GenericArgDelimiters);
            var baseName = genericArgs >= 0 ? fullName.Substring(0, genericArgs) : fullName;

            var lastDot = baseName.LastIndexOf('.');
            if (lastDot >= 0)
                baseName = baseName.Substring(lastDot + 1);

            var backtick = baseName.IndexOf('`');
            if (backtick >= 0)
                baseName = baseName.Substring(0, backtick);

            return baseName;
        }

        static unsafe ManagedTickData GetManagedTickData(TickData tickData, NativeHashMap<SavedEntityID, FixedString64Bytes> ghostNames)
        {
            using var systemDataKeys = tickData.PerSystemData.GetKeyArray(Allocator.Temp);

            // Collapse the per-trace-position (before/netcode/after) keys for a system into one
            // ManagedSystemData: merge component values, OR diffs, and widen the execution-order range.
            var bySystem = new Dictionary<SystemTypeIndex, ManagedSystemData>();
            foreach (var systemID in systemDataKeys)
            {
                var rawSystemData = tickData.PerSystemData[systemID];
                var rawStateSave = rawSystemData.MainGameTracingState.m_RawStateSave;
                var systemValue = rawStateSave.system.value;

                // Use the key's executionOrder (the per-tick counter); rawStateSave.system's is still 0.
                var executionOrder = (int)systemID.executionOrder;

                if (!bySystem.TryGetValue(systemValue, out var systemData))
                {
                    var systemType = TypeManager.GetSystemType(systemValue);
                    // Prefer the Type's FullName (reflection format); GetSystemName returns Cecil format under
                    // ILPP and won't match downstream Type.FullName comparisons.
                    var fullName = systemType?.FullName ?? TypeManager.GetSystemName(systemValue).Value;
                    systemData = new ManagedSystemData
                    {
                        SystemType = systemType,
                        SystemName = fullName,
                        SystemNameShort = GetDisplaySystemName(systemValue, GetShortSystemName(fullName)),
                        ExecutionOrder = executionOrder,
                        MaxExecutionOrder = executionOrder,
                        IsSystemGroup = systemValue.IsGroup,
                        GhostComponents = new Dictionary<int, GhostComponentData>(),
                        DiffReasonFlags = rawSystemData.DiffInfo.DiffReasonFlags,
                        DiffAggregates = new Dictionary<DiffAggregate, float>(),
                    };
                }

                systemData.ExecutionOrder = Math.Min(systemData.ExecutionOrder, executionOrder);
                systemData.MaxExecutionOrder = Math.Max(systemData.MaxExecutionOrder, executionOrder);
                systemData.HasDiff |= rawSystemData.DiffInfo.HasDiff;
                systemData.DiffReasonFlags |= rawSystemData.DiffInfo.DiffReasonFlags;
                systemData.RowDiffReasonFlags |= rawSystemData.DiffInfo.RowReasonFlags;
                if (rawSystemData.DiffInfo.AggregatesRO.IsCreated)
                {
                    foreach (var kvp in rawSystemData.DiffInfo.AggregatesRO)
                        AddAggregate(systemData.DiffAggregates, kvp.Key, kvp.Value);
                }

                // Accurate per-ghost / per-component reasons (vs. the system-wide aggregate above).
                var stateDiff = rawSystemData.MainGameTracingState.DiffInfo;
                using var ghostKeys = rawStateSave.stateSave.GetAllEntities(Allocator.Temp);
                foreach (var ghostID in ghostKeys)
                {
                    var ghostKey = ghostID.value.ghostId;
                    if (!systemData.GhostComponents.TryGetValue(ghostKey, out var ghostData))
                    {
                        var ghostName = ghostNames.TryGetValue(ghostID, out var nameStr) && !nameStr.IsEmpty
                            ? nameStr.ToString()
                            : ghostID.ToString();
                        ghostData = new GhostComponentData
                        {
                            EntityId = ghostID,
                            GhostName = ghostName,
                            Components = new Dictionary<Type, TracedComponent>(),
                        };
                    }

                    var ghostReason = stateDiff.GetObjectReasonInfo(ghostID);
                    ghostData.DiffReasonFlags |= ghostReason.Reasons;
                    ghostData.RowDiffReasonFlags |= ghostReason.Reasons;
                    if (ghostReason.Reasons != DiffInfo.DiffReasons.Undefined)
                        ghostData.HasDiff = true;

                    using var componentTypes = rawStateSave.stateSave.GetComponentTypes(ghostID);
                    foreach (var componentType in componentTypes)
                    {
                        var managedType = componentType.GetManagedType();
                        if (!ghostData.Components.TryGetValue(managedType, out var component))
                            component = new TracedComponent();

                        var componentReason = stateDiff.GetComponentReasonInfo(ghostID, componentType.TypeIndex);
                        component.DiffReasonFlags |= componentReason.Reasons;
                        component.RowDiffReasonFlags |= componentReason.Reasons;
                        component.DataDiffAmount = Math.Max(component.DataDiffAmount, componentReason.DataDiffAmount);
                        if (componentReason.Reasons != DiffInfo.DiffReasons.Undefined)
                            component.HasDiff = true;
                        if (component.HasDiff)
                            ghostData.HasDiff = true;

                        IComponentData instance = null;
                        if (rawStateSave.stateSave.TryGetComponentData(ghostID, componentType, out var instanceBytePtr))
                        {
                            instance = (IComponentData)TypeManager.ConstructComponentFromBuffer(componentType.TypeIndex, instanceBytePtr);
                        }
                        switch(rawStateSave.system.tracePosition)
                        {
                            case TracePosition.before:
                                component.BeforeValue = instance;
                                break;
                            case TracePosition.netcode:
                                component.NetcodeValue = instance;
                                break;
                            case TracePosition.after:
                                component.AfterValue = instance;
                                break;
                        }
                        ghostData.Components[managedType] = component;
                    }

                    systemData.GhostComponents[ghostKey] = ghostData;
                }

                bySystem[systemValue] = systemData;
            }

            var systems = new List<ManagedSystemData>(bySystem.Values);
            systems.Sort((a, b) => a.ExecutionOrder.CompareTo(b.ExecutionOrder));
            AssignParentExecutionOrders(systems);

            return new ManagedTickData
            {
                SystemsByExecutionOrder = systems,
                HasDiff = tickData.DiffInfo.HasDiff,
                DiffReasons = tickData.DiffInfo.DiffReasonFlags,
                TickRowDiffReasons = tickData.DiffInfo.RowReasonFlags,
                DeltaTime = tickData.CurrentDeltaTimeSeconds

            };
        }

        static void AddAggregate(Dictionary<DiffAggregate, float> aggregates, DiffAggregate key, float dataDiffAmount)
        {
            aggregates.TryGetValue(key, out var existingAmount);
            aggregates[key] = Math.Max(existingAmount, dataDiffAmount);
        }

        // Nest each system under the innermost group whose execution-order range contains it.
        internal static void AssignParentExecutionOrders(List<ManagedSystemData> systems)
        {
            for (var i = 0; i < systems.Count; i++)
            {
                var s = systems[i];
                var parentExec = NoParentExecutionOrder;
                var innermostSize = int.MaxValue;
                foreach (var g in systems)
                {
                    if (!g.IsSystemGroup || g.ExecutionOrder == s.ExecutionOrder)
                        continue;
                    // s is contained when its whole range sits within g's range.
                    if (s.ExecutionOrder < g.ExecutionOrder || s.MaxExecutionOrder > g.MaxExecutionOrder)
                        continue;
                    var size = g.MaxExecutionOrder - g.ExecutionOrder;
                    if (size >= innermostSize)
                        continue;
                    innermostSize = size;
                    parentExec = g.ExecutionOrder;
                }
                s.ParentExecutionOrder = parentExec;
                systems[i] = s;
            }
        }
    }

    struct TracedComponent
    {
        public IComponentData BeforeValue;
        public IComponentData NetcodeValue;
        public IComponentData AfterValue;
        public bool HasDiff;
        public DiffInfo.DiffReasons DiffReasonFlags;
        public bool FromServerWorld;

        // DiffReasons owned by this component alone
        public DiffInfo.DiffReasons RowDiffReasonFlags;

        // Largest recorded unit diff amount, for the view-side fuzzy threshold.
        public float DataDiffAmount;

        public override string ToString()
        {
            string val = $"HasDiff:{HasDiff}";
            val += $"before:{BeforeValue},after:{AfterValue},special:{NetcodeValue}";
            return val;
        }
    }

    struct GhostComponentData
    {
        // Full ghost id (incl. spawn tick) for live-entity/prefab selection; the dictionary keys on ghostId alone.
        public SavedEntityID EntityId;
        public string GhostName;
        public Dictionary<Type, TracedComponent> Components;
        public bool HasDiff;
        public DiffInfo.DiffReasons DiffReasonFlags;

        // Reasons owned by the ghost itself (missing/extra ghost)
        public DiffInfo.DiffReasons RowDiffReasonFlags;

        // True when this row is a server-only (missing) ghost shown under a client system
        public bool FromServerWorld;

        public override string ToString()
        {
            string val = $"GhostName{GhostName},HasDiff:{HasDiff}\n";
            if (Components != null)
            {
                foreach (var componentKvPair in Components)
                {
                    val += $"\t\ttype:{componentKvPair.Key},TracedComp:{componentKvPair.Value.ToString()}\n";
                }
            }

            return val;
        }
    }

    struct ManagedSystemData
    {
        public Type SystemType; // null if the system Type can't be resolved (SystemName is then the only id).
        public string SystemName;
        public string SystemNameShort;

        // Per-tick execution-order range across trace positions; a group's range brackets its children.
        // ExecutionOrder is also the tree item id.
        public int ExecutionOrder;
        public int MaxExecutionOrder;

        public Dictionary<int, GhostComponentData> GhostComponents;
        public bool HasDiff;
        public DiffInfo.DiffReasons DiffReasonFlags;

        // Reasons owned by the system row itself
        public DiffInfo.DiffReasons RowDiffReasonFlags;

        // All aggregates recorded on this system's traces (with their largest ComponentData amount), so
        // views can re-evaluate row highlights against the active filters without reprocessing.
        public Dictionary<DiffAggregate, float> DiffAggregates;
        public bool IsSystemGroup;

        // ExecutionOrder of the innermost containing system group, or NoParentExecutionOrder for top-level
        // systems. See WorldDataManagedExtensions.AssignParentExecutionOrders.
        public int ParentExecutionOrder;

        // True when this row is a server-only (missing) system shown among the client rows
        public bool FromServerWorld;

        public override string ToString()
        {
            string val = $"Sys:{SystemName},order:{ExecutionOrder},HasDiff:{HasDiff}";
            val += $"Reason:{DiffReasonFlags}\n";
            if (GhostComponents != null)
            {
                foreach (var kvPair in GhostComponents)
                {
                    val += $"\tkey:{kvPair.Key},data{kvPair.Value.ToString()}\n";
                }
            }

            return val;
        }
    }

    struct ManagedTickData
    {
        public List<ManagedSystemData> SystemsByExecutionOrder;
        public bool HasDiff;
        public DiffInfo.DiffReasons DiffReasons;

        // Reasons owned by the tick row itself
        public DiffInfo.DiffReasons TickRowDiffReasons;

        public float DeltaTime;

        public override string ToString()
        {
            string val = $"HasDiff:{HasDiff}";
            val += $"|reason:{DiffReasons} dt:{DeltaTime}";
            if (SystemsByExecutionOrder != null)
            {
                foreach (var sys in SystemsByExecutionOrder)
                {
                    val += sys.ToString() + "\n";
                }
            }

            return val;
        }
    }
}

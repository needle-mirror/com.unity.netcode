using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode.Tracing
{
    internal struct TickID : IEquatable<TickID>, IComparer<TickID>
    {
        public NetworkTick value;

        public bool Equals(TickID other)
        {
            return value.Equals(other.value);
        }

        public int Compare(TickID x, TickID y)
        {
            return x.value.TicksSince(y.value);
        }

        public override bool Equals(object obj)
        {
            return obj is TickID other && Equals(other);
        }

        public override int GetHashCode()
        {
            return value.GetHashCode();
        }

        public override string ToString()
        {
            return value.IsValid ? $"tick[{value.TickIndexForValidTick}]" : "tick[invalid]";
        }
    }

    internal struct TickData : IDisposable
    {
        public NativeList<SystemID> SystemIds;
        public NativeHashMap<SystemID, SystemData> PerSystemData;
        public float CurrentDeltaTimeSeconds;
        public NetworkTime NetworkTime;
        public TraceType TraceType;
        public DiffInfo DiffInfo;
        public bool IsCreated => PerSystemData.IsCreated;

        public TickData(float dt, NetworkTime networkTime, TraceType traceType)
        {
            PerSystemData = new(0, Allocator.Persistent);
            SystemIds = new(0, Allocator.Persistent);
            CurrentDeltaTimeSeconds = dt;
            DiffInfo = default;
            NetworkTime = networkTime;
            TraceType = traceType;
        }

        public void Dispose()
        {
            foreach (var systemKvp in PerSystemData)
            {
                systemKvp.Value.Dispose();
            }
            PerSystemData.Dispose();
            SystemIds.Dispose();
            DiffInfo.Dispose();
        }

        public bool ProcessDiff(TickData serverTickData, TracingConfig config)
        {
            var clientTickData = this;
            if (clientTickData.TraceType == TraceType.Default && // ghost update system runs while the DT and network time is not set yet, so partial tick information is invalid as that point
                clientTickData.NetworkTime.IsPartialTick && config.IgnorePartialTicks)
                return false;

            // Which (system, entity, component) values either side actually changed this tick; stamped into the aggregates.
            using var valueChanges = new ValueChangeSet(16, Allocator.Temp);
            CollectValueChanges(clientTickData, valueChanges);
            CollectValueChanges(serverTickData, valueChanges);

            if (clientTickData.TraceType == TraceType.Default && !config.IgnoreDTDiffs && CurrentDeltaTimeSeconds != serverTickData.CurrentDeltaTimeSeconds && !NetworkTime.IsPartialTick)
            {
                DiffInfo.AddDiff(DiffInfo.DiffReasons.DeltaTime);
            }

            if (clientTickData.TraceType == TraceType.Default && serverTickData.NetworkTime.SimulationStepBatchSize > 1)
            {
                DiffInfo.AddDiff(DiffInfo.DiffReasons.BatchedTick);
            }

            using var serverSystemsCovered = new NativeHashSet<SystemID>(20, Allocator.Temp);
            using var serverSystemIDs = serverTickData.PerSystemData.GetKeyArray(Allocator.Temp);
            // is the system client only (then should diff with previous system)
            // is the system server only (then should diff with previous system)
            // is it a special netcode system where we override the diff (ex: ghost send and ghost update systems)
            // does the shared system have a diff with its server version
            foreach (var systemIDToTest in serverSystemIDs)
            {
                var serverSystemData = serverTickData.PerSystemData[systemIDToTest];
                serverSystemsCovered.Add(systemIDToTest);
                SystemData clientSystemDataToTest;
                SystemID clientSystemIDToTest;

                if (clientTickData.PerSystemData.ContainsKey(systemIDToTest))
                {
                    clientSystemDataToTest = clientTickData.PerSystemData[systemIDToTest];
                    clientSystemIDToTest = systemIDToTest;
                }
                else
                {
                    if (serverSystemData.Family == TraceType.NetcodeGhostUpdateVsSendComparison)
                    {
                        if (!clientTickData.PerSystemData.ContainsKey(serverSystemData.AssociatedSystem))
                            // client tick runs multiple times, but only once with GhostUpdate, so it's expected to only have one tick that would have a diff with ghost send
                            continue;

                        clientSystemDataToTest = clientTickData.PerSystemData[serverSystemData.AssociatedSystem];
                        clientSystemIDToTest = serverSystemData.AssociatedSystem;
                        serverSystemsCovered.Add(serverSystemData.AssociatedSystem);
                    }
                    else
                    {
                        // couldn't find system in client, checking if that system did any relevant changes vs the previous system
                        // Netcode-position traces are the tool's own anchors — one-sided by design, never flagged missing.
                        if (serverSystemData.TracePosition != TracePosition.netcode)
                        {
                            var missingSystemAggregate = new DiffAggregate
                            {
                                System = systemIDToTest.value,
                                GhostId = DiffAggregate.InvalidGhostId,
                                Component = default,
                                Reasons = DiffInfo.DiffReasons.MissingSystem,
                                HasValueChanged = valueChanges.PerSystem.Contains(systemIDToTest.value),
                            };
                            serverSystemData.DiffInfo.AddDiff(missingSystemAggregate);
                            // The missing system's own row carries the reason.
                            serverSystemData.DiffInfo.RowReasonFlags |= DiffInfo.DiffReasons.MissingSystem;
                            DiffInfo.AddDiff(missingSystemAggregate);
                        }

                        if (!serverSystemData.AssociatedSystem.Equals(default))
                        {
                            SystemData previousSystemRun = serverTickData.PerSystemData[serverSystemData.AssociatedSystem];
                            if (serverSystemData.ProcessDiff(ref previousSystemRun, systemIDToTest.value, valueChanges))
                                serverTickData.PerSystemData[serverSystemData.AssociatedSystem] = previousSystemRun;
                        }

                        serverTickData.PerSystemData[systemIDToTest] = serverSystemData;

                        continue;
                    }
                }

                if (clientSystemDataToTest.ProcessDiff(ref serverSystemData, clientSystemIDToTest.value, valueChanges))
                {
                    clientTickData.PerSystemData[clientSystemIDToTest] = clientSystemDataToTest;
                    // Bubble the system's aggregates up so the tick (and frames/worlds above) can be filtered by them.
                    DiffInfo.UnionWith(clientSystemDataToTest.DiffInfo);
                    serverTickData.PerSystemData[systemIDToTest] = serverSystemData;
                }
            }

            using var currentSystemsIDs = clientTickData.PerSystemData.GetKeyArray(Allocator.Temp);
            foreach (var systemID in currentSystemsIDs)
            {
                if (serverSystemsCovered.Contains(systemID)) continue;

                var currentSystem = clientTickData.PerSystemData[systemID];
                var extraSystemAggregate = new DiffAggregate
                {
                    System = systemID.value,
                    GhostId = DiffAggregate.InvalidGhostId,
                    Component = default,
                    Reasons = DiffInfo.DiffReasons.ExtraSystem,
                    HasValueChanged = valueChanges.PerSystem.Contains(systemID.value),
                };
                // Netcode-position traces are the tool's own anchors — one-sided by design, never flagged extra.
                if (currentSystem.TracePosition != TracePosition.netcode)
                {
                    currentSystem.DiffInfo.AddDiff(extraSystemAggregate);
                    // The extra system's own row carries the reason.
                    currentSystem.DiffInfo.RowReasonFlags |= DiffInfo.DiffReasons.ExtraSystem;
                    DiffInfo.AddDiff(extraSystemAggregate);
                }
                if (!currentSystem.AssociatedSystem.Equals(default) && !NetworkTime.IsPartialTick) // no previous system, so no diff
                {
                    if (!currentSystemsIDs.Contains(currentSystem.AssociatedSystem))
                    {
                        if (currentSystem.TracePosition != TracePosition.netcode)
                            DiffInfo.AddDiff(extraSystemAggregate);
                        // store back before skipping, or the system's aggregates (native set) leak
                        clientTickData.PerSystemData[systemID] = currentSystem;
                        continue;
                    }

                    SystemData previousSystem = clientTickData.PerSystemData[currentSystem.AssociatedSystem];

                    if (currentSystem.ProcessDiff(ref previousSystem, systemID.value, valueChanges))
                    {
                        clientTickData.PerSystemData[currentSystem.AssociatedSystem] = previousSystem;
                        DiffInfo.UnionWith(currentSystem.DiffInfo);
                    }
                }

                clientTickData.PerSystemData[systemID] = currentSystem;
            }

            ProcessSystemOrderDiff(serverTickData);

            return DiffInfo.HasDiff;
        }

        static void CollectValueChanges(in TickData tickData, ValueChangeSet results)
        {
            using var systemIDs = tickData.PerSystemData.GetKeyArray(Allocator.Temp);
            foreach (var systemID in systemIDs)
            {
                if (systemID.tracePosition != TracePosition.after)
                    continue;
                var systemData = tickData.PerSystemData[systemID];
                if (systemData.AssociatedSystem.Equals(default) || systemData.AssociatedSystem.tracePosition != TracePosition.before)
                    continue;
                if (!tickData.PerSystemData.TryGetValue(systemData.AssociatedSystem, out var beforeData))
                    continue;
                var afterState = systemData.MainGameTracingState;
                var beforeState = beforeData.MainGameTracingState;
                afterState.CollectValueChanges(ref beforeState, systemID.value, results);
            }
        }

        public void ProcessSystemOrderDiff(TickData serverTickData)
        {
            using var clientSharedOrder = new NativeList<SystemID>(SystemIds.Length, Allocator.Temp);
            foreach (var systemID in SystemIds)
            {
                if (serverTickData.PerSystemData.ContainsKey(systemID))
                    clientSharedOrder.Add(systemID);
            }

            using var serverSharedOrder = new NativeList<SystemID>(serverTickData.SystemIds.Length, Allocator.Temp);
            foreach (var systemID in serverTickData.SystemIds)
            {
                if (PerSystemData.ContainsKey(systemID))
                    serverSharedOrder.Add(systemID);
            }

            // Both lists hold the same set (the intersection), so they have the same length;
            // Math.Min only guards against malformed traces.
            var sharedCount = Math.Min(clientSharedOrder.Length, serverSharedOrder.Length);
            for (var i = 0; i < sharedCount; i++)
            {
                if (clientSharedOrder[i].Equals(serverSharedOrder[i]))
                    continue;
                DiffInfo.AddDiff(clientSharedOrder[i].value, DiffAggregate.InvalidGhostId, default, DiffInfo.DiffReasons.SystemOrder, false);
                MarkSystemOrderDiff(PerSystemData, clientSharedOrder[i]);
                MarkSystemOrderDiff(serverTickData.PerSystemData, serverSharedOrder[i]);
            }
        }

        static void MarkSystemOrderDiff(NativeHashMap<SystemID, SystemData> perSystemData, SystemID systemID)
        {
            var systemData = perSystemData[systemID];
            systemData.DiffInfo.AddDiff(systemID.value, DiffAggregate.InvalidGhostId, default, DiffInfo.DiffReasons.SystemOrder, false);
            // The reordered system's own row carries the reason.
            systemData.DiffInfo.RowReasonFlags |= DiffInfo.DiffReasons.SystemOrder;
            perSystemData[systemID] = systemData;
        }
    }
}

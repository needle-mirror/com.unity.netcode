using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Entities;
using Unity.Netcode.LowLevel.StateSave;

namespace Unity.Netcode.Tracing
{
    internal struct WorldID : IEquatable<WorldID>, IComparer<WorldID>
    {
        public enum WorldType
        {
            Undefined,
            Client,
            Server
        }

        public ulong value;

        public WorldID(WorldUnmanaged world)
        {
            value = world.SequenceNumber;
        }

        public bool Equals(WorldID other)
        {
            return value == other.value;
        }

        public int Compare(WorldID x, WorldID y)
        {
            return x.value.CompareTo(y.value);
        }

        public override int GetHashCode()
        {
            return value.GetHashCode();
        }

        public override string ToString()
        {
            string toRet = $"world[{value}]";
            toRet = ToWorld().Name;
            return toRet;
        }

        public World ToWorld()
        {
            foreach (var world in World.All)
            {
                if (world.SequenceNumber == this.value)
                {
                    return world;
                }
            }

            return null;
        }
    }

    internal struct WorldData : IDisposable
    {
        public NativeList<FrameID> FrameIDs;
        public NativeHashMap<FrameID, FrameData> PerFrameData;
        public NativeList<TickID> TickIDs;
        public NativeHashMap<TickID, TickData> PerTickData;
        public NativeHashMap<SavedEntityID, FixedString64Bytes> GhostNames;
        public DiffInfo DiffInfo;
        public bool IsCreated;

        public void Dispose()
        {
            if (!IsCreated)
                return;
            if (PerFrameData.IsCreated)
            {
                FrameIDs.Dispose();
                foreach (var pair in PerFrameData)
                {
                    pair.Value.Dispose();
                }

                PerFrameData.Clear();
                PerFrameData.Dispose();
            }

            if (PerTickData.IsCreated)
            {
                TickIDs.Dispose();
                foreach (var pair in PerTickData)
                {
                    pair.Value.Dispose();
                }

                PerTickData.Clear();
                PerTickData.Dispose();
            }

            DiffInfo.Dispose();
            IsCreated = false;
        }

        public WorldData(Allocator allocator)
        {
            FrameIDs = new(0, allocator);
            TickIDs = new(0, allocator);
            PerFrameData = new(0, allocator);
            PerTickData = new(0, allocator);

            GhostNames = default;
            DiffInfo = default;
            IsCreated = true;
        }

        public WorldData(int initialCapacity, NativeHashMap<SavedEntityID, FixedString64Bytes> ghostNames,
            Allocator allocator)
        {
            FrameIDs = new(initialCapacity, allocator);
            TickIDs = new(initialCapacity, allocator);
            PerFrameData = new(initialCapacity, allocator);
            PerTickData = new(initialCapacity, allocator);
            GhostNames = ghostNames;

            DiffInfo = default;
            IsCreated = true;
        }

        /// <summary>
        /// Diff each authoritative server tick against the matching client ticks, one server tick at a time.
        /// The caller owns frame budgeting and can yield/resume between ticks.
        /// Yields the fraction of diff work done so far in ]0,1] so the caller can report progress.
        /// </summary>
        public static IEnumerable<float> ProcessDiff(TracingDataAccess.ProcessedWorldsData processedWorldsData, CancellationToken ct)
        {
            bool foundTick = false;
            // Allocator needs to be persistent since processing can take more than four frames
            using var framesKeys = processedWorldsData.ClientWorldData.PerFrameData.GetKeyArray(Allocator.Persistent);
            var totalWork = Math.Max(1L, (long)processedWorldsData.ServerWorldData.PerTickData.Count * framesKeys.Length + framesKeys.Length);
            var processedWork = 0L;
            foreach (var authoritativeTickKvp in processedWorldsData.ServerWorldData.PerTickData)
            {
                if (ct.IsCancellationRequested)
                    break;

                foreach (var frameKey in framesKeys)
                {
                    var frameToTest = processedWorldsData.ClientWorldData.PerFrameData[frameKey];
                    if (!frameToTest.PerTickData.ContainsKey(authoritativeTickKvp.Key))
                        continue;

                    foundTick = true;
                    var tickToTest = frameToTest.PerTickData[authoritativeTickKvp.Key];
                    if (tickToTest.ProcessDiff(authoritativeTickKvp.Value))
                    {
                        frameToTest.PerTickData[authoritativeTickKvp.Key] = tickToTest;
                        // Bubble the tick's aggregates up so frames and worlds can be filtered without re-walking children.
                        frameToTest.DiffInfo.UnionWith(tickToTest.DiffInfo);

                        processedWorldsData.ClientWorldData.PerFrameData[frameKey] = frameToTest;
                        processedWorldsData.ClientWorldData.DiffInfo.UnionWith(tickToTest.DiffInfo);
                    }
                }

                processedWork += framesKeys.Length;
                yield return (float)processedWork / totalWork;
            }


            // Mark client ticks the server never simulated individually because a batch ticks
            using var batchCoveredTicks = new NativeHashSet<TickID>(16, Allocator.Persistent);
            foreach (var serverTickKvp in processedWorldsData.ServerWorldData.PerTickData)
            {
                var batchSize = serverTickKvp.Value.NetworkTime.SimulationStepBatchSize;
                for (var i = 1u; i < batchSize; i++)
                {
                    var coveredTick = serverTickKvp.Key.value;
                    coveredTick.Subtract(i);
                    batchCoveredTicks.Add(new TickID { value = coveredTick });
                }
            }

            foreach (var frameKey in framesKeys)
            {
                if (ct.IsCancellationRequested)
                    break;

                var frameToTest = processedWorldsData.ClientWorldData.PerFrameData[frameKey];
                var frameHasBatchDiff = false;
                var frameHasPartialTick = false;
                foreach (var tickID in frameToTest.TickIDs)
                {
                    var tickToTest = frameToTest.PerTickData[tickID];
                    if (tickToTest.TraceType != TraceType.Default)
                        continue;

                    // A partial tick re-predicts with only a fraction of the delta time (and e.g. physics
                    // doesn't step at all), so mismatches with the server's full tick are expected. Flag
                    // every partial tick so views can call that out and filter on it.
                    if (tickToTest.NetworkTime.IsPartialTick)
                    {
                        tickToTest.DiffInfo.AddDiff(DiffInfo.DiffReasons.PartialTick);
                        frameToTest.PerTickData[tickID] = tickToTest;
                        frameHasPartialTick = true;
                    }

                    if (!batchCoveredTicks.Contains(tickID)
                        || processedWorldsData.ServerWorldData.PerTickData.ContainsKey(tickID))
                        continue;

                    tickToTest.DiffInfo.AddDiff(DiffInfo.DiffReasons.BatchedTick);
                    frameToTest.PerTickData[tickID] = tickToTest;
                    frameHasBatchDiff = true;
                }

                // The precise reasons propagate to the frame and world roll-ups so views can filter on them.
                if (frameHasBatchDiff)
                {
                    frameToTest.DiffInfo.AddDiff(DiffInfo.DiffReasons.BatchedTick);
                    processedWorldsData.ClientWorldData.DiffInfo.AddDiff(DiffInfo.DiffReasons.BatchedTick);
                }
                if (frameHasPartialTick)
                {
                    frameToTest.DiffInfo.AddDiff(DiffInfo.DiffReasons.PartialTick);
                    processedWorldsData.ClientWorldData.DiffInfo.AddDiff(DiffInfo.DiffReasons.PartialTick);
                }
                if (frameHasBatchDiff || frameHasPartialTick)
                    processedWorldsData.ClientWorldData.PerFrameData[frameKey] = frameToTest;

                processedWork++;
                yield return (float)processedWork / totalWork;
            }

            // No client tick ever matched a server tick: flag the world with an unclassified aggregate so HasDiff still reports the anomaly.
            if (!foundTick)
                processedWorldsData.ClientWorldData.DiffInfo.AddDiff(DiffInfo.DiffReasons.Undefined);
        }
    }

    /// <summary>
    /// Wrap client and server world data.
    /// </summary>
    struct TracingData
    {
        public WorldData ClientWorldData;
        public WorldData ServerWorldData;
    }
}

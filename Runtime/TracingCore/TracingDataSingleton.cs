using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Entities;
using Unity.Netcode.LowLevel.StateSave;
using Unity.Profiling;
using Unity.Assertions;

namespace Unity.Netcode.Tracing
{
    // Tracing backend internal class,<see cref="TracingDataAccess"/> for public API.
    // These world singletons are added to the client and server to enable tracing. They hold the raw and processed trace data.
    internal struct TracingDataSingleton : IComponentData, IDisposable
    {
        public WorldData ProcessedWorldData;
        public UnprocessedTraces UnprocessedTraces;
        public NativeHashMap<SavedEntityID, FixedString64Bytes> GhostNames;
        public bool IsCreated;
        internal readonly WorldID.WorldType m_WorldType;
        internal readonly WorldID m_WorldID;

        public TracingDataSingleton(WorldUnmanaged world, Allocator allocator)
        {
            ProcessedWorldData = default;
            UnprocessedTraces = new UnprocessedTraces(allocator);
            m_WorldType = world.IsClient() ? WorldID.WorldType.Client : WorldID.WorldType.Server;
            m_WorldID = new WorldID(world);
            GhostNames = new NativeHashMap<SavedEntityID, FixedString64Bytes>(0, allocator);
            IsCreated = true;
        }

        /// <see cref="StateSave.DisposeForWorld"/>
        public void DisposeForWorld()
        {
            if (!IsCreated)
                return;
            UnprocessedTraces.DisposeForWorld();
        }

        public void Dispose()
        {
            if(!IsCreated)
                return;
            IsCreated = false;
            UnprocessedTraces.EndOfFrameComplete();
            UnprocessedTraces.Dispose();
            DisposeProcessedWorldData();
            if(GhostNames.IsCreated)
                GhostNames.Dispose();
        }

        static readonly ProfilerMarker s_ProcessRawTraceMarker = new ProfilerMarker("ProcessRawTraceData");

        internal void DisposeProcessedWorldData()
        {
            if (ProcessedWorldData.IsCreated)
            {
                ProcessedWorldData.Dispose();
            }
        }

        /// <summary>
        /// Create and process traces one tick at a time. The caller owns frame budgeting and can yield/resume between ticks.
        /// Yields the fraction of raw traces processed so far in ]0,1] so the caller can report progress.
        /// </summary>
        public IEnumerable<float> ProcessRawTraceData(TracingDataAccess.ProcessedWorldsData processedWorldsData, CancellationToken ct)
        {
            using var marker = s_ProcessRawTraceMarker.Auto();

            RawTracingStateSave previousSystemRawData = UnprocessedTraces.FirstElement();
            if (!previousSystemRawData.Initialized)
                yield break;

            var processedWorldData = new WorldData(10, GhostNames, Allocator.Persistent);
            if(m_WorldType == WorldID.WorldType.Client)
                processedWorldsData.ClientWorldData = processedWorldData;
            else
                processedWorldsData.ServerWorldData = processedWorldData;

            ulong counter = 0;
            var totalTraces = Math.Max(1, UnprocessedTraces.Count); // should be > 0, otherwise FirstElement() is not initialized
            var processedTraces = 0;

            using var it = UnprocessedTraces.GetEnumerator();
            TickID currentTick = default;
            bool hasCurrentTick = false;
            while (it.MoveNext())
            {
                if (ct.IsCancellationRequested)
                    break;

                var rawData = it.Current;
                Assert.IsTrue(rawData.Initialized); // sanity check

                if (!hasCurrentTick)
                {
                    currentTick = rawData.tick;
                    hasCurrentTick = true;
                }
                else if (!rawData.tick.Equals(currentTick))
                {
                    yield return (float)processedTraces / totalTraces;
                    currentTick = rawData.tick;
                }

                ProcessRawTrace(ref processedWorldData, ref previousSystemRawData, ref counter, rawData);
                processedTraces++;
            }

            if (hasCurrentTick)
                yield return (float)processedTraces / totalTraces;
        }

        void ProcessRawTrace(ref WorldData processedWorldData, ref RawTracingStateSave previousSystemRawData, ref ulong counter, RawTracingStateSave rawData)
        {
            var traceType = TraceType.Default;
            if (rawData.system.value == TypeManager.GetSystemTypeIndex<GhostSendSystem>())
                traceType = TraceType.NetcodeGhostUpdateVsSendComparison;

            if (!processedWorldData.PerFrameData.TryGetValue(rawData.frame, out var frameData))
            {
                frameData = new FrameData(rawData.frameDeltaTime);
                processedWorldData.PerFrameData.Add(rawData.frame, frameData);
                if (!processedWorldData.FrameIDs.Contains(rawData.frame))
                    processedWorldData.FrameIDs.Add(rawData.frame);
            }

            TickData tickData = default;
            // servers have only one instance of a tick, so indexing by tick instead of by frame.
            if ((m_WorldType == WorldID.WorldType.Client) && !frameData.PerTickData.TryGetValue(rawData.tick, out tickData))
            {
                tickData = new TickData(rawData.tickDeltaTime, rawData.networkTime, traceType);
                frameData.PerTickData.Add(rawData.tick, tickData);
                if (!frameData.TickIDs.Contains(rawData.tick))
                    frameData.TickIDs.Add(rawData.tick);
            }
            else if (m_WorldType == WorldID.WorldType.Server && !processedWorldData.PerTickData.TryGetValue(rawData.tick, out tickData))
            {
                // for servers, there should only be one iteration of a tick, so we're allowing to access them directly from the world in addition to grouping them by frame
                tickData = new TickData(rawData.tickDeltaTime, rawData.networkTime, traceType);
                processedWorldData.PerTickData.Add(rawData.tick, tickData);
                if (!processedWorldData.TickIDs.Contains(rawData.tick))
                    processedWorldData.TickIDs.Add(rawData.tick);
            }

            if (!tickData.PerSystemData.TryGetValue(rawData.system, out var systemData))
            {
                SystemID systemPair = default; // The system that should be used to compare.
                if (rawData.tick.Equals(previousSystemRawData.tick) && rawData.system.tracePosition == TracePosition.after && previousSystemRawData.system.tracePosition == TracePosition.before && previousSystemRawData.system.Equivalent(rawData.system))
                {
                    // for client/server only systems, we try to see if that system did any simulation changes that could influence determinism
                    systemPair = previousSystemRawData.system;
                }

                var worldType = m_WorldType;
                if (rawData.SystemPairOverride.value != default)
                {
                    systemPair = rawData.SystemPairOverride;
                    worldType = rawData.SystemPairOverrideWorld;
                }

                systemData = new SystemData(rawData.system.tracePosition, systemPair, worldType, traceType);
                var systemId = rawData.system;
                systemId.executionOrder = ++counter;

                tickData.PerSystemData.Add(systemId, systemData);
                if (!tickData.SystemIds.Contains(systemId))
                    tickData.SystemIds.Add(systemId);
            }

            var tracingStateData = new ProcessedTracingStateData(rawData);
            systemData.MainGameTracingState = tracingStateData;
            tickData.PerSystemData[rawData.system] = systemData;

            previousSystemRawData = rawData;
        }
    }
}

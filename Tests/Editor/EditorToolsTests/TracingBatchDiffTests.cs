using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.NetcodeTime;
using Unity.Netcode.Tracing;

namespace Tests.Editor
{
    /// <summary>
    /// Unit tests for the tick-level reason flagging in the trace diff processing
    /// (<see cref="WorldData.ProcessDiff"/> / <see cref="TickData.ProcessDiff"/>):
    /// client ticks the server batched away (no individual server trace) and client ticks whose matched
    /// server tick ran batched are both flagged with <see cref="DiffInfo.DiffReasons.BatchedTick"/>,
    /// and every partial client tick is flagged with <see cref="DiffInfo.DiffReasons.PartialTick"/>.
    /// </summary>
    class TracingBatchDiffTests
    {
        const float k_TickDt = 1f / 60f;

        TracingDataAccess.ProcessedWorldsData m_Data;

        [TearDown]
        public void TearDown()
        {
            if (m_Data == null)
                return;
            if (m_Data.ClientWorldData.IsCreated)
                m_Data.ClientWorldData.Dispose();
            if (m_Data.ServerWorldData.IsCreated)
                m_Data.ServerWorldData.Dispose();
            m_Data = null;
        }

        static NetworkTime MakeTime(uint tick, int batchSize, float fraction = 1f)
        {
            return new NetworkTime
            {
                ServerTick = new NetworkTick(tick),
                ServerTickFraction = fraction,
                SimulationStepBatchSize = batchSize,
            };
        }

        static TickID Tick(uint index) => new TickID { value = new NetworkTick(index) };

        // Client frame with ticks [firstTick..lastTick], each simulated as a single per-tick step.
        // Server has simulated only 'serverTick' covering 'serverBatchSize' ticks in one step.
        TracingDataAccess.ProcessedWorldsData MakeData(int frame, uint firstTick, uint lastTick, uint serverTick, int serverBatchSize)
        {
            var clientWorldData = new WorldData(Allocator.Persistent);
            var frameID = new FrameID { value = frame };
            var frameData = new FrameData(k_TickDt);
            for (var t = firstTick; t <= lastTick; t++)
            {
                var tickData = new TickData(k_TickDt, MakeTime(t, 1), TraceType.Default);
                frameData.TickIDs.Add(Tick(t));
                frameData.PerTickData.Add(Tick(t), tickData);
            }
            clientWorldData.FrameIDs.Add(frameID);
            clientWorldData.PerFrameData.Add(frameID, frameData);

            var serverWorldData = new WorldData(Allocator.Persistent);
            var serverTickData = new TickData(k_TickDt * serverBatchSize, MakeTime(serverTick, serverBatchSize), TraceType.Default);
            serverWorldData.TickIDs.Add(Tick(serverTick));
            serverWorldData.PerTickData.Add(Tick(serverTick), serverTickData);

            return new TracingDataAccess.ProcessedWorldsData
            {
                ClientWorldData = clientWorldData,
                ServerWorldData = serverWorldData,
            };
        }

        // Appends a partial re-prediction of 'tick' to the client frame.
        void AppendPartialClientTick(int frame, uint tick, float fraction)
        {
            var frameID = new FrameID { value = frame };
            var frameData = m_Data.ClientWorldData.PerFrameData[frameID];
            var tickData = new TickData(k_TickDt * fraction, MakeTime(tick, 1, fraction), TraceType.Default);
            frameData.TickIDs.Add(Tick(tick));
            frameData.PerTickData.Add(Tick(tick), tickData);
            m_Data.ClientWorldData.PerFrameData[frameID] = frameData;
        }

        void RunProcessDiff()
        {
            // Drain the budget-sliced enumerator fully.
            foreach (var unused in WorldData.ProcessDiff(m_Data, default))
            {
            }
        }

        DiffInfo TickDiff(int frame, uint tick)
        {
            var frameData = m_Data.ClientWorldData.PerFrameData[new FrameID { value = frame }];
            return frameData.PerTickData[Tick(tick)].DiffInfo;
        }

        [Test]
        public void BatchedAwayTicks_AreFlaggedWithBatchedTick()
        {
            // Client simulated 5,6,7,8; server ran one step at tick 8 covering 4 ticks (5,6,7 batched away).
            m_Data = MakeData(frame: 1, firstTick: 5, lastTick: 8, serverTick: 8, serverBatchSize: 4);

            RunProcessDiff();

            foreach (var t in new uint[] { 5, 6, 7 })
            {
                var diff = TickDiff(1, t);
                Assert.That(diff.HasDiff, Is.True, $"tick {t} should be flagged (batched away by server tick 8)");
                Assert.That(diff.DiffReasonFlags & DiffInfo.DiffReasons.BatchedTick, Is.EqualTo(DiffInfo.DiffReasons.BatchedTick), $"tick {t} should carry the BatchedTick reason");
            }

            // The precise reason propagates to the frame roll-up.
            var frameDiff = m_Data.ClientWorldData.PerFrameData[new FrameID { value = 1 }].DiffInfo;
            Assert.That(frameDiff.HasDiff, Is.True);
            Assert.That(frameDiff.DiffReasonFlags & DiffInfo.DiffReasons.BatchedTick, Is.EqualTo(DiffInfo.DiffReasons.BatchedTick));
        }

        [Test]
        public void MatchedTick_WithBatchedServerStep_CarriesBatchedTickReason()
        {
            m_Data = MakeData(frame: 1, firstTick: 5, lastTick: 8, serverTick: 8, serverBatchSize: 4);

            RunProcessDiff();

            var diff = TickDiff(1, 8);
            Assert.That(diff.HasDiff, Is.True);
            Assert.That(diff.DiffReasonFlags & DiffInfo.DiffReasons.BatchedTick, Is.EqualTo(DiffInfo.DiffReasons.BatchedTick),
                "the tick the server actually simulated should also carry BatchedTick, since it ran with a batched delta time");
        }

        [Test]
        public void TicksOutsideAnyBatchWindow_AreNotFlagged()
        {
            // Client also simulated ticks 9 and 10 (after the last server tick): missing server data there is
            // end-of-trace truncation, not batching, and must not be flagged.
            m_Data = MakeData(frame: 1, firstTick: 5, lastTick: 10, serverTick: 8, serverBatchSize: 4);

            RunProcessDiff();

            foreach (var t in new uint[] { 9, 10 })
            {
                var diff = TickDiff(1, t);
                Assert.That(diff.DiffReasonFlags & DiffInfo.DiffReasons.BatchedTick, Is.EqualTo(DiffInfo.DiffReasons.Undefined),
                    $"tick {t} is beyond the recorded server ticks and should not be marked as batched");
            }
        }

        [Test]
        public void PartialTicks_AreFlaggedWithPartialTick_AndFullTicksAreNot()
        {
            // Client simulated full tick 8 plus a partial re-prediction of tick 9 the server never traced.
            m_Data = MakeData(frame: 1, firstTick: 8, lastTick: 8, serverTick: 8, serverBatchSize: 1);
            AppendPartialClientTick(frame: 1, tick: 9, fraction: 0.5f);

            RunProcessDiff();

            var partialDiff = TickDiff(1, 9);
            Assert.That(partialDiff.HasDiff, Is.True, "a partial tick should always be flagged");
            Assert.That(partialDiff.DiffReasonFlags & DiffInfo.DiffReasons.PartialTick, Is.EqualTo(DiffInfo.DiffReasons.PartialTick),
                "a partial tick should carry the PartialTick reason");

            var fullDiff = TickDiff(1, 8);
            Assert.That(fullDiff.DiffReasonFlags & DiffInfo.DiffReasons.PartialTick, Is.EqualTo(DiffInfo.DiffReasons.Undefined),
                "a full tick should not carry the PartialTick reason");

            // The precise reason propagates to the frame roll-up.
            var frameDiff = m_Data.ClientWorldData.PerFrameData[new FrameID { value = 1 }].DiffInfo;
            Assert.That(frameDiff.DiffReasonFlags & DiffInfo.DiffReasons.PartialTick, Is.EqualTo(DiffInfo.DiffReasons.PartialTick));
        }

        [Test]
        public void NonBatchedServer_FlagsNothing()
        {
            // Server simulated tick 8 as a single step: no coverage window, nothing marked.
            m_Data = MakeData(frame: 1, firstTick: 8, lastTick: 8, serverTick: 8, serverBatchSize: 1);

            RunProcessDiff();

            var diff = TickDiff(1, 8);
            Assert.That(diff.DiffReasonFlags & DiffInfo.DiffReasons.BatchedTick, Is.EqualTo(DiffInfo.DiffReasons.Undefined));
        }
    }
}

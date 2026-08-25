using NUnit.Framework;
using Unity.Collections;
using Unity.NetCode;
using Unity.NetCode.Tracing;

namespace Tests.Editor
{
    /// <summary>
    /// Unit tests for the server-tick-batching detection in the trace diff processing
    /// (<see cref="WorldData.ProcessDiff"/> / <see cref="TickData.ProcessDiff"/>):
    /// client ticks the server batched away (no individual server trace) and client ticks whose matched
    /// server tick ran batched are both flagged with <see cref="DiffInfo.DiffReasons.BatchedTick"/>.
    /// </summary>
    class TracingBatchDiffTests
    {
        const float k_TickDt = 1f / 60f;

        TracingDataAccess.ProcessedWorldsData m_Data;
        TracingConfig m_Config;

        [SetUp]
        public void SetUp()
        {
            m_Config = default;
            m_Config.ResetToDefault();
        }

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
        TracingDataAccess.ProcessedWorldsData MakeData(int frame, uint firstTick, uint lastTick, uint serverTick, int serverBatchSize, float clientFractionForLastTick = 1f)
        {
            var clientWorldData = new WorldData(Allocator.Persistent);
            var frameID = new FrameID { value = frame };
            var frameData = new FrameData(k_TickDt);
            for (var t = firstTick; t <= lastTick; t++)
            {
                var fraction = t == lastTick ? clientFractionForLastTick : 1f;
                var tickData = new TickData(k_TickDt * fraction, MakeTime(t, 1, fraction), TraceType.Default);
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

        void RunProcessDiff()
        {
            // Drain the budget-sliced enumerator fully.
            foreach (var unused in WorldData.ProcessDiff(m_Data, m_Config, default))
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
            m_Config.IgnorePartialTicks = false;

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
            m_Config.IgnorePartialTicks = false;

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
            m_Config.IgnorePartialTicks = false;

            RunProcessDiff();

            foreach (var t in new uint[] { 9, 10 })
            {
                var diff = TickDiff(1, t);
                Assert.That(diff.DiffReasonFlags & DiffInfo.DiffReasons.BatchedTick, Is.EqualTo(DiffInfo.DiffReasons.Undefined),
                    $"tick {t} is beyond the recorded server ticks and should not be marked as batched");
            }
        }

        [Test]
        public void NonBatchedServer_FlagsNothing()
        {
            // Server simulated tick 8 as a single step: no coverage window, nothing marked.
            m_Data = MakeData(frame: 1, firstTick: 8, lastTick: 8, serverTick: 8, serverBatchSize: 1);
            m_Config.IgnorePartialTicks = false;

            RunProcessDiff();

            var diff = TickDiff(1, 8);
            Assert.That(diff.DiffReasonFlags & DiffInfo.DiffReasons.BatchedTick, Is.EqualTo(DiffInfo.DiffReasons.Undefined));
        }

        [Test]
        public void BatchedAwayPartialTick_RespectsIgnorePartialTicks()
        {
            // Last client tick (7) is partial and falls inside the batch window of server tick 8.
            m_Data = MakeData(frame: 1, firstTick: 5, lastTick: 7, serverTick: 8, serverBatchSize: 4, clientFractionForLastTick: 0.5f);
            m_Config.IgnorePartialTicks = true;

            RunProcessDiff();

            var diff = TickDiff(1, 7);
            Assert.That(diff.DiffReasonFlags & DiffInfo.DiffReasons.BatchedTick, Is.EqualTo(DiffInfo.DiffReasons.Undefined),
                "partial ticks should not be marked while IgnorePartialTicks is enabled");

            m_Config.IgnorePartialTicks = false;
        }
    }
}

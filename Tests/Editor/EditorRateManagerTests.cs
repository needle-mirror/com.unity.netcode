using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.NetCode.Tests;
using UnityEngine;
using UnityEngine.TestTools;
using static Unity.NetCode.ClientServerTickRate.FrameRateMode;

namespace Tests.Editor
{
    [DisableAutoCreation]
    internal abstract partial class BaseCallbackSystem : SystemBase
    {
        public delegate void OnUpdateDelegate(World world);
        public OnUpdateDelegate OnUpdateCallback;

        protected override void OnUpdate()
        {
            OnUpdateCallback?.Invoke(this.World);
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    internal partial class BeforePredictionSystem : BaseCallbackSystem
    {
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    internal partial class AfterPredictionSystem : BaseCallbackSystem
    {

    }

    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class UpdateInPredictionSystem : BaseCallbackSystem
    {

    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(UpdateNetworkTimeSystem))]
    internal partial class BeforeSimulationSystemGroup : BaseCallbackSystem
    {

    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    internal partial class AfterSimulationSystemGroup : BaseCallbackSystem
    {

    }

    [Category(NetcodeTestCategories.Foundational)]
    internal class RateManagerTests
    {
        [Test]
        public void TestElapsedTimeNonNegativeAtStart()
        {
            const float tickDt = 1f / 60f;
            using var testWorld = new NetCodeTestWorld(useGlobalConfig: true, initialElapsedTime: 0);
            NetCodeConfig.Global.ClientServerTickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.BusyWait;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepBatchSize = 4;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepsPerFrame = 4;

            testWorld.Bootstrap(includeNetCodeSystems: true);
            testWorld.CreateWorlds(server: true, numClients: 1, tickWorldAfterCreation: false);

            bool didRun = false;
            testWorld.ServerWorld.GetExistingSystemManaged<UpdateInPredictionSystem>().OnUpdateCallback += world =>
            {
                didRun = true;
                Assert.That(world.Time.ElapsedTime, Is.GreaterThanOrEqualTo(0), "time should always be positive");
            };
            testWorld.Tick(tickDt * 4f); // large dt where multiple ticks run, to see if each one has a non-negative elapsedTime
            Assert.IsTrue(didRun, "didRun");
        }

        [Test]
        public void RateManagerTest([Values(BusyWait, Sleep)] ClientServerTickRate.FrameRateMode frameRateMode, [Values] NetCodeConfig.HostWorldMode hostMode, [Values(1, 4)] int maxBatchSize, [Values(1, 4)] int maxStepsPerFrame)
        {
            // Setup
            if (hostMode == NetCodeConfig.HostWorldMode.SingleWorld && frameRateMode == Sleep)
            {
                Assert.Ignore("Not implemented for now, ignoring");
            }

            var useSingleWorld = hostMode == NetCodeConfig.HostWorldMode.SingleWorld;
            using var testWorld = new NetCodeTestWorld(useGlobalConfig: true);
            NetCodeConfig.Global.ClientServerTickRate.TargetFrameRateMode = frameRateMode;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepBatchSize = maxBatchSize;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepsPerFrame = maxStepsPerFrame;
            testWorld.Bootstrap(includeNetCodeSystems: true,
                typeof(BeforePredictionSystem),
                typeof(AfterPredictionSystem),
                typeof(UpdateInPredictionSystem)
            );

            testWorld.CreateWorlds(server: !useSingleWorld, numClients: useSingleWorld ? 0 : 1, numHostWorlds: useSingleWorld ? 1 : 0);
            testWorld.Connect(enableGhostReplication: true);

            int beforePredictionCount = 0;
            int duringPredictionCount = 0;
            int afterPredictionCount = 0;
            int initializationCount = 0;

            // test setup with execute methods
            TimeData beforeTime = default;
            NetworkTime beforeNetTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<BeforePredictionSystem>().OnUpdateCallback += world =>
            {
                beforePredictionCount++;
                beforeTime = world.Time;
                beforeNetTime = testWorld.GetNetworkTime(world);
                Assert.That(testWorld.GetNetworkTime(world).IsInPredictionLoop, Is.Not.True, "network time flag fail, before prediction");
            };
            TimeData duringTime = default;
            NetworkTime duringNetTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<UpdateInPredictionSystem>().OnUpdateCallback += world =>
            {
                duringPredictionCount++;
                duringTime = world.Time;
                duringNetTime = testWorld.GetNetworkTime(world);
                Assert.That(testWorld.GetNetworkTime(world).IsInPredictionLoop, Is.True, "network time flag fail, during prediction");
            };
            TimeData afterTime = default;
            NetworkTime afterNetTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<AfterPredictionSystem>().OnUpdateCallback += world =>
            {
                afterPredictionCount++;
                afterTime = world.Time;
                afterNetTime = testWorld.GetNetworkTime(world);
                Assert.That(testWorld.GetNetworkTime(world).IsInPredictionLoop, Is.Not.True, "network time flag fail, after prediction");
            };
            TimeData initializationTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<BeforeSimulationSystemGroup>().OnUpdateCallback += world =>
            {
                initializationCount++;
                initializationTime = world.Time;
                Assert.That(testWorld.GetNetworkTime(world).IsInPredictionLoop, Is.Not.True, "network time flag fail, in initialization group");
            };

            // frame is 1/4 of a tick. Expect 4 frame to 1 tick ratio. So 3 frames, then 1 frame with a tick, then 3 frames, then 1 frame with a tick
            const float kTickDt = 1f / 60f;
            var frameCountPerTick = 4;
            if (frameRateMode == Sleep)
            {
                // Rate manager automatically adjusts Application.targetFrameRate on DGS, making frame rate and tick rate 1:1.
                // We're mocking this here
                frameCountPerTick = 1;
            }

            var frameDt = 1f / frameCountPerTick * kTickDt;

            int frameCount = 8;
            float expectedTick = 2;

            void ResetTime()
            {
                beforeTime = default;
                afterTime = default;
                duringTime = default;
                initializationTime = default;
                beforeNetTime = default;
                afterNetTime = default;
                duringNetTime = default;
            }

            // Test with small frame DT (ticks should skip frames when appropriate)
            {
                if (useSingleWorld)
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        testWorld.Tick(frameDt);
                        Assert.That(beforeTime.DeltaTime, Is.EqualTo(frameDt), $"beforeTime, iteration {i}. We need deltaTime to be the frame time so things like interpolation can run with the appropriate deltaTimes");
                        Assert.That(afterTime.DeltaTime, Is.EqualTo(frameDt), $"afterTime, iteration {i}. We need deltaTime to be the frame time so things like interpolation can run with the appropriate deltaTimes");
                        var expectedTickCountSum = math.floor((i + 1f) / frameCountPerTick);
                        var tickCountThisFrame = (i + 1f) % frameCountPerTick == 0 ? 1 : 0;
                        Assert.That(duringPredictionCount, Is.EqualTo(expectedTickCountSum), $"wrong tick count for duringCount, iteration {i}");
                        Assert.That(beforePredictionCount, Is.EqualTo(i + 1), $"wrong tick count for beforeCount, iteration {i}");
                        Assert.That(afterPredictionCount, Is.EqualTo(i + 1), $"wrong tick count for afterCount, iteration {i}");
                        Assert.That(beforeNetTime.NumPredictedTicksExpected, Is.EqualTo(tickCountThisFrame));
                        if (tickCountThisFrame > 0) // no prediction tick happened, so that test value is undefined here
                            Assert.That(duringNetTime.NumPredictedTicksExpected, Is.EqualTo(tickCountThisFrame));
                        Assert.That(afterNetTime.NumPredictedTicksExpected, Is.EqualTo(tickCountThisFrame));
                        Assert.That(beforeNetTime.IsOffFrame, Is.EqualTo(tickCountThisFrame == 0));
                        if (tickCountThisFrame > 0) // no prediction tick happened, so that test value is undefined here
                            Assert.That(duringNetTime.IsOffFrame, Is.EqualTo(tickCountThisFrame == 0));
                        Assert.That(afterNetTime.IsOffFrame, Is.EqualTo(tickCountThisFrame == 0));
                        if (i % frameCountPerTick == frameCountPerTick - 1)
                        {
                            // last loop. ex: frame 0, 1, 2, 3 (tick at i==3), 4, 5, 6, 7 (tick at i==7)
                            Assert.That(duringTime.DeltaTime, Is.EqualTo(kTickDt), $"duringTime, iteration {i}");
                            Assert.That(duringTime.ElapsedTime, Is.LessThanOrEqualTo(initializationTime.ElapsedTime), "elapsed time, prediction should always follow, but be behind elapsed time outside the prediction group");
                            Assert.That(duringTime.ElapsedTime, Is.LessThanOrEqualTo(afterTime.ElapsedTime), "elapsed time, prediction should always follow, but be behind elapsed time outside the prediction group");
                            Assert.That(duringTime.ElapsedTime, Is.GreaterThan(0));
                        }
                        else
                        {
                            Assert.That(duringTime.DeltaTime, Is.EqualTo(0), $"duringTime should be 0 outside of ticks, iteration {i}");
                        }

                        ResetTime(); // so we don't corrupt results of next for loop iterations
                    }
                }
                else // binary world
                {
                    void ValidateZeroCountAndDT()
                    {
                        Assert.That(beforePredictionCount, Is.EqualTo(0), $"small dt, beforeCount, validating nothing ran");
                        Assert.That(afterPredictionCount, Is.EqualTo(0), $"small dt, afterCount, validating nothing ran");
                        Assert.That(duringPredictionCount, Is.EqualTo(0), $"small dt, duringCount, validating nothing ran");
                        Assert.That(beforeTime.DeltaTime, Is.EqualTo(0), $"beforeTime nothing, server, validating nothing ran");
                        Assert.That(afterTime.DeltaTime, Is.EqualTo(0), $"afterTime nothing, server, validating nothing ran");
                        Assert.That(duringTime.DeltaTime, Is.EqualTo(0), $"duringTime nothing, server, validating nothing ran");
                        Assert.That(initializationTime.DeltaTime, Is.EqualTo(frameDt), $"initialization group dt, validating everything is normal");
                        Assert.That(duringTime.ElapsedTime, Is.LessThanOrEqualTo(initializationTime.ElapsedTime), "elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
                        Assert.That(beforeNetTime.NumPredictedTicksExpected, Is.EqualTo(0));
                        Assert.That(duringNetTime.NumPredictedTicksExpected, Is.EqualTo(0));
                        Assert.That(afterNetTime.NumPredictedTicksExpected, Is.EqualTo(0));
                        ResetTime();
                    }

                    // Application.targetFrameRate should be adjusted automatically, skipping in-between frames when in sleep mode
                    // In busy wait mode, we skip the whole SimulationSystemGroup if there's no tick to run (keeping current behaviour)
                    if (frameRateMode == BusyWait)
                    {
                        testWorld.Tick(frameDt);
                        ValidateZeroCountAndDT();
                        testWorld.Tick(frameDt);
                        ValidateZeroCountAndDT();
                        testWorld.Tick(frameDt);
                        ValidateZeroCountAndDT();
                    }

                    // frameDt can vary depending on if we're in busy wait or sleep mode. We're mocking frameDt in sleep mode since we also adjust Application.targetFrameRate
                    testWorld.Tick(frameDt);
                    Assert.That(beforeTime.DeltaTime, Is.EqualTo(kTickDt), $"beforeTime, server");
                    Assert.That(afterTime.DeltaTime, Is.EqualTo(kTickDt), $"afterTime, server");
                    Assert.That(duringTime.DeltaTime, Is.EqualTo(kTickDt), $"duringTime, server");
                    Assert.That(initializationTime.DeltaTime, Is.EqualTo(frameDt), $"initialization group dt, validating everything is normal");
                    Assert.That(duringTime.ElapsedTime, Is.LessThanOrEqualTo(initializationTime.ElapsedTime), "elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
                    Assert.That(duringTime.ElapsedTime, Is.GreaterThan(0));
                    Assert.That(beforeNetTime.NumPredictedTicksExpected, Is.EqualTo(1));
                    Assert.That(duringNetTime.NumPredictedTicksExpected, Is.EqualTo(1));
                    Assert.That(afterNetTime.NumPredictedTicksExpected, Is.EqualTo(1));
                    ResetTime();
                    testWorld.TickMultiple(frameCountPerTick, frameDt);
                }

                Assert.That(beforePredictionCount, Is.EqualTo(useSingleWorld ? frameCount : expectedTick), "small dt, beforeCount");
                Assert.That(afterPredictionCount, Is.EqualTo(useSingleWorld ? frameCount : expectedTick), "small dt, afterCount");
                Assert.That(duringPredictionCount, Is.EqualTo(expectedTick), "small dt, duringCount");

                beforePredictionCount = 0;
                afterPredictionCount = 0;
                duringPredictionCount = 0;

                ResetTime();
            }

            // Test with large frame DT (should batch ticks or run multiple ticks when appropriate)
            {
                // if max batch steps is 4 and max batch size is 1, so with a dt of 2x, we should expect 2x steps
                testWorld.Tick(2f * kTickDt);
                int expectedPredictionCount = maxStepsPerFrame > 1 ? 2 : 1;
                float expectedTickDt = kTickDt;
                if (maxBatchSize > 1 && maxStepsPerFrame == 1)

                    // netcode prefers running more ticks than batching ticks if that's allowed, it's more accurate. The only way to get a batched tick is if max steps is not high enough and batch size is
                    expectedTickDt = 2f * kTickDt;
                var expectedFrameDt = 2f * kTickDt; // we have frame time and tick time. Simulation group is at the frame level, prediction group is at the tick level
                var expectedFrameCount = 1f;

                if (!useSingleWorld)
                {
                    // TODO-2.0 this is a hack until we fix DGS behaviour for batching
                    // on DGS, frame time is pushed at the simulation group level. on host, frame time is pushed at the prediction group level
                    // this is to avoid breaking changes, but we should fix this in a N4E 2.0?
                    // batch count behaviour should be the same though. Just PushTime that's different. Tests don't seem to break, but I think that's because we don't test
                    // batching too much...
                    expectedFrameDt = expectedTickDt;
                }

                Assert.That(beforePredictionCount, Is.EqualTo(hostMode == NetCodeConfig.HostWorldMode.BinaryWorlds ? expectedPredictionCount : expectedFrameCount), "big dt, beforeCount");
                Assert.That(duringPredictionCount, Is.EqualTo(expectedPredictionCount), "big dt, duringCount, expecting multiple prediction iterations");
                Assert.That(afterPredictionCount, Is.EqualTo(hostMode == NetCodeConfig.HostWorldMode.BinaryWorlds ? expectedPredictionCount : expectedFrameCount), "big dt, afterCount");
                Assert.That(beforeTime.DeltaTime, Is.EqualTo(hostMode == NetCodeConfig.HostWorldMode.BinaryWorlds ? expectedTickDt : expectedFrameDt), "batched dt, before");
                Assert.That(duringTime.DeltaTime, Is.EqualTo(expectedTickDt), "batched dt, during");
                Assert.That(afterTime.DeltaTime, Is.EqualTo(hostMode == NetCodeConfig.HostWorldMode.BinaryWorlds ? expectedTickDt : expectedFrameDt), "batched dt, after");
                ResetTime();
            }

            // stabilize
            for (int i = 0; i < 100; i++)
            {
                testWorld.Tick(kTickDt);
            }

            var epsillon = 0.0001f;

            // Make sure elapsedTime updates correctly with varying frame rates over time
            {
                if (maxBatchSize == 1 && maxStepsPerFrame == 1)
                {
                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(kTickDt);
                        Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - kTickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group, iteration {i}");
                        ResetTime();
                    }

                    // harder to catch up with both max to 1, validating we still catch up when back on small frame dt
                    var bigDt = 2 * kTickDt;
                    var smallDt = 0.5f * kTickDt;

                    // let it fall behind
                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(bigDt);
                    }

                    // let it catchup
                    for (int i = 0; i < 200; i++)
                    {
                        testWorld.Tick(smallDt);
                    }

                    Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - kTickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
                }
                else
                {
                    // validate there's no divergence over multiple ticks
                    var batchDt = 3f * kTickDt; // smaller than the maxCount = 4 setting, so we should not fall behind
                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(kTickDt);
                        Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - kTickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group, iteration {i}");
                        ResetTime();
                    }

                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(batchDt);
                        Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - batchDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group, iteration {i}");
                        ResetTime();
                    }

                    // let it fall behind
                    batchDt = 6 * kTickDt; // larger than the maxCount = 4 setting, so we fall behind
                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(batchDt);
                        Assert.That(duringTime.ElapsedTime, Is.LessThan(initializationTime.ElapsedTime));
                    }

                    // make sure we can catch up
                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(kTickDt);
                    }

                    Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - kTickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
                }
            }
        }

        [Test]
        public void TestCanDetectIfServerWillUpdate([Values(Sleep, BusyWait)] ClientServerTickRate.FrameRateMode mode, [Values] bool singleWorldHost)
        {
            if (mode == Sleep && singleWorldHost) Assert.Ignore("TODO-release not supported right now");
            using var testWorld = new NetCodeTestWorld(useGlobalConfig: true);
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepBatchSize = 1;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepsPerFrame = 1; // this is already default, but making sure tests assumptions don't break for sanity
            NetCodeConfig.Global.ClientServerTickRate.TargetFrameRateMode = mode;

            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(BeforeSimulationSystemGroup), typeof(AfterSimulationSystemGroup));
            testWorld.CreateWorlds(server: !singleWorldHost, numHostWorlds: singleWorldHost ? 1 : 0, numClients: 1);
            testWorld.Connect();
            testWorld.GoInGame();

            // bool willUpdate = false;
            bool expectedWillUpdate = false;

            void ValidateWillUpdate(bool isBefore)
            {
                // check if server simulation group is going to run or not
                var networkTime = testWorld.GetSingleton<NetworkTime>(testWorld.ServerWorld);
                bool willUpdate;
                if (singleWorldHost)
                {
                    var hostRateManager = testWorld.ServerWorld.GetExistingSystemManaged<SimulationSystemGroup>().RateManager as NetcodeHostRateManager;
                    willUpdate = hostRateManager.WillUpdateInternal();
                }
                else
                {
                    var serverRateManager = testWorld.ServerWorld.GetExistingSystemManaged<SimulationSystemGroup>().RateManager as NetcodeServerRateManager;
#pragma warning disable CS0618 // Type or member is obsolete
                    willUpdate = serverRateManager.WillUpdate();
#pragma warning restore CS0618 // Type or member is obsolete
                }
                Assert.AreEqual(expectedWillUpdate, isBefore ? willUpdate : !willUpdate);
                Assert.AreEqual(expectedWillUpdate, !networkTime.IsOffFrame);
            }
            testWorld.ServerWorld.GetExistingSystemManaged<BeforeSimulationSystemGroup>().OnUpdateCallback += world =>
            {
                ValidateWillUpdate(isBefore: true);
            };
            testWorld.ServerWorld.GetExistingSystemManaged<AfterSimulationSystemGroup>().OnUpdateCallback += world =>
            {
                ValidateWillUpdate(isBefore: false);
            };
            if (mode == Sleep)
            {
                expectedWillUpdate = true;
                testWorld.Tick();
                LogAssert.Expect(LogType.Warning, "Testing if will update when TargetFrameRateMode is set to Sleep. This will always return true.");
            }
            else
            {
                // we're in busy wait mode, so should skip one frame out of two.
                var halfDt = 0.5f / 60f;
                expectedWillUpdate = false;
                testWorld.Tick(halfDt);
                expectedWillUpdate = true;
                testWorld.Tick(halfDt);
                expectedWillUpdate = false;
                testWorld.Tick(halfDt);
                expectedWillUpdate = true;
                testWorld.Tick(halfDt);
            }
        }
    }
}

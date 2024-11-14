using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using Unity.NetCode;
using Unity.NetCode.Tests;
using UnityEngine;
using UnityEngine.TestTools;
using static Unity.NetCode.ClientServerTickRate.FrameRateMode;

namespace Tests.Editor
{
    [DisableAutoCreation]
    public abstract partial class BaseCallbackSystem : SystemBase
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
    public partial class BeforePredictionSystem : BaseCallbackSystem
    {

    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    public partial class AfterPredictionSystem : BaseCallbackSystem
    {

    }

    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    public partial class UpdateInPredictionSystem : BaseCallbackSystem
    {

    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class BeforeSimulationSystemGroup : BaseCallbackSystem
    {

    }

    public class RateManagerTests
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
        public void RateManagerTest([Values(BusyWait, Sleep)] ClientServerTickRate.FrameRateMode frameRateMode, [Values(1, 4)] int maxBatchSize, [Values(1, 4)] int maxStepsPerFrame)
        {
            using var testWorld = new NetCodeTestWorld(useGlobalConfig: true);
            NetCodeConfig.Global.ClientServerTickRate.TargetFrameRateMode = frameRateMode;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepBatchSize = maxBatchSize;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepsPerFrame = maxStepsPerFrame;
            testWorld.Bootstrap(includeNetCodeSystems: true,
                typeof(BeforePredictionSystem),
                typeof(AfterPredictionSystem),
                typeof(UpdateInPredictionSystem)
                );

            testWorld.CreateWorlds(server: true, numClients: 1);
            testWorld.Connect();
            testWorld.GoInGame();

            int beforeCount = 0;
            int duringCount = 0;
            int afterCount = 0;
            int initializationCount = 0;

            // test setup with execute methods
            TimeData beforeTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<BeforePredictionSystem>().OnUpdateCallback += world =>
            {
                beforeCount++;
                beforeTime = world.Time;
                Assert.That(testWorld.GetNetworkTime(world).IsInPredictionLoop, Is.Not.True, "network time flag fail, before prediction");
            };
            TimeData duringTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<UpdateInPredictionSystem>().OnUpdateCallback += world =>
            {
                duringCount++;
                duringTime = world.Time;
                Assert.That(testWorld.GetNetworkTime(world).IsInPredictionLoop, Is.True, "network time flag fail, during prediction");
            };
            TimeData afterTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<AfterPredictionSystem>().OnUpdateCallback += world =>
            {
                afterCount++;
                afterTime = world.Time;
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
            var tickDt = 1f / 60f;
            var frameCountPerTick = 4;
            float frameDt;
            if (frameRateMode == Sleep)
            {
                // Rate manager automatically adjusts Application.targetFrameRate on DGS, making frame rate and tick rate 1:1.
                // We're mocking this here
                frameCountPerTick = 1;
            }
            frameDt = 1f / frameCountPerTick * tickDt;

            float expectedTick = 2;

            void ResetTime()
            {
                beforeTime = default;
                afterTime = default;
                duringTime = default;
                initializationTime = default;
            }

            {
                void ValidateZeroCountAndDT()
                {
                    Assert.That(beforeCount, Is.EqualTo(0), $"small dt, beforeCount, validating nothing ran");
                    Assert.That(afterCount, Is.EqualTo(0), $"small dt, afterCount, validating nothing ran");
                    Assert.That(duringCount, Is.EqualTo(0), $"small dt, duringCount, validating nothing ran");
                    Assert.That(beforeTime.DeltaTime, Is.EqualTo(0), $"beforeTime nothing, server, validating nothing ran");
                    Assert.That(afterTime.DeltaTime, Is.EqualTo(0), $"afterTime nothing, server, validating nothing ran");
                    Assert.That(duringTime.DeltaTime, Is.EqualTo(0), $"duringTime nothing, server, validating nothing ran");
                    Assert.That(initializationTime.DeltaTime, Is.EqualTo(frameDt), $"initialization group dt, validating everything is normal");
                    Assert.That(duringTime.ElapsedTime, Is.LessThanOrEqualTo(initializationTime.ElapsedTime), "elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
                    ResetTime();
                }

                // Application.targetFrameRate should be adjusted automatically, skipping in-between frames when in sleep mode
                // In busy wait mode, we skip the whole SimulationSystemGroup if there's no tick to run
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
                Assert.That(beforeTime.DeltaTime, Is.EqualTo(tickDt), $"beforeTime, server");
                Assert.That(afterTime.DeltaTime, Is.EqualTo(tickDt), $"afterTime, server");
                Assert.That(duringTime.DeltaTime, Is.EqualTo(tickDt), $"duringTime, server");
                Assert.That(initializationTime.DeltaTime, Is.EqualTo(frameDt), $"initialization group dt, validating everything is normal");
                Assert.That(duringTime.ElapsedTime, Is.LessThanOrEqualTo(initializationTime.ElapsedTime), "elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
                Assert.That(duringTime.ElapsedTime, Is.GreaterThan(0));
                ResetTime();
                for (int i = 0; i < frameCountPerTick; i++)
                {
                    testWorld.Tick(frameDt);
                }
            }

            Assert.That(beforeCount, Is.EqualTo(expectedTick), "small dt, beforeCount");
            Assert.That(afterCount, Is.EqualTo(expectedTick), "small dt, afterCount");
            Assert.That(duringCount, Is.EqualTo(expectedTick), "small dt, duringCount");

            beforeCount = 0;
            afterCount = 0;
            duringCount = 0;

            ResetTime();
            // if max batch steps is 4 and max batch size is 1, so with a dt of 2x, we should expect 2x steps
            testWorld.Tick(2f * tickDt);
            int expectedCount = maxStepsPerFrame > 1 ? 2 : 1;
            float expectedDt = tickDt;
            if (maxBatchSize > 1 && maxStepsPerFrame == 1)
                // netcode prefers running more ticks than batching ticks if that's allowed, it's more accurate. The only way to get a batched tick is if max steps is not high enough and batch size is
                expectedDt = 2f * tickDt;


            Assert.That(beforeCount, Is.EqualTo(expectedCount), "big dt, beforeCount");
            Assert.That(duringCount, Is.EqualTo(expectedCount), "big dt, duringCount, expecting multiple prediction iterations");
            Assert.That(afterCount, Is.EqualTo(expectedCount), "big dt, afterCount");
            Assert.That(beforeTime.DeltaTime, Is.EqualTo(expectedDt), "batched dt, before");
            Assert.That(duringTime.DeltaTime, Is.EqualTo(expectedDt), "batched dt, during");
            Assert.That(afterTime.DeltaTime, Is.EqualTo(expectedDt), "batched dt, after");
            ResetTime();

            // stabilize
            for (int i = 0; i < 100; i++)
            {
                testWorld.Tick(tickDt);
            }

            var epsillon = 0.0001f;

            if (maxBatchSize == 1 && maxStepsPerFrame == 1)
            {
                for (int i = 0; i < 100; i++)
                {
                    testWorld.Tick(tickDt);
                    Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - tickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group, iteration {i}");
                    ResetTime();
                }

                // harder to catch up with both max to 1, validating we still catch up when back on small frame dt
                var bigDt = 2 * tickDt;
                var smallDt = 0.5f * tickDt;
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
                Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - tickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
            }
            else
            {
                // validate there's no divergence over multiple ticks
                var batchDt = 3f * tickDt; // smaller than the maxCount = 4 setting, so we should not fall behind
                for (int i = 0; i < 100; i++)
                {
                    testWorld.Tick(tickDt);
                    Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - tickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group, iteration {i}");
                    ResetTime();
                }

                for (int i = 0; i < 100; i++)
                {
                    testWorld.Tick(batchDt);
                    Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - batchDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group, iteration {i}");
                    ResetTime();
                }

                // let it fall behind
                batchDt = 6 * tickDt; // larger than the maxCount = 4 setting, so we fall behind
                for (int i = 0; i < 100; i++)
                {
                    testWorld.Tick(batchDt);
                    Assert.That(duringTime.ElapsedTime, Is.LessThan(initializationTime.ElapsedTime));
                }

                // make sure we can catch up
                for (int i = 0; i < 100; i++)
                {
                    testWorld.Tick(tickDt);
                }

                Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - tickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
            }
        }

        [Test]
        public void TestCanDetectIfServerWillUpdate([Values(Sleep, BusyWait)] ClientServerTickRate.FrameRateMode mode)
        {
            using var testWorld = new NetCodeTestWorld(useGlobalConfig: true);
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepBatchSize = 1;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepsPerFrame = 1; // this is already default, but making sure tests assumptions don't break for sanity
            NetCodeConfig.Global.ClientServerTickRate.TargetFrameRateMode = mode;

            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(BeforeSimulationSystemGroup));
            testWorld.CreateWorlds(server: true, numClients: 1);
            testWorld.Connect();
            testWorld.GoInGame();

            bool willUpdate = false;
            testWorld.ServerWorld.GetExistingSystemManaged<BeforeSimulationSystemGroup>().OnUpdateCallback += world =>
            {
                // check if server simulation group is going to run or not
                var serverRateManager = testWorld.ServerWorld.GetExistingSystemManaged<SimulationSystemGroup>().RateManager as NetcodeServerRateManager;
                willUpdate = serverRateManager.WillUpdate();
            };

            if (mode == Sleep)
            {
                testWorld.Tick();
                LogAssert.Expect(LogType.Warning, "Testing if will update when TargetFrameRateMode is set to Sleep. This will always return true.");
            }
            else
            {
                // we're in busy wait mode, so should skip one frame out of two.
                var halfDt = 0.5f / 60f;
                testWorld.Tick(halfDt);
                Assert.That(willUpdate, Is.False);
                testWorld.Tick(halfDt);
                Assert.That(willUpdate, Is.True);
                testWorld.Tick(halfDt);
                Assert.That(willUpdate, Is.False);
                testWorld.Tick(halfDt);
                Assert.That(willUpdate, Is.True);
            }
        }
    }
}

#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.TestTools;
using Random = UnityEngine.Random;

namespace Unity.NetCode.Tests
{
    //FIXME this will break serialization. It is non handled and must be documented
    [GhostEnabledBit]
    struct BufferWithReplicatedEnableBits: IBufferElementData, IEnableableComponent
    {
        public byte value;
    }

    //Added to the ISystem state entity, track the number of time a system update has been called
    struct SystemExecutionCounter : IComponentData
    {
        public int value;
    }

    class PredictionTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            //Transform is replicated, Owner is replicated (components with sizes)
            baker.AddComponent(entity, new GhostOwner());
            //Buffer with enable bits, replicated
            //TODO: missing: Buffer with enable bits, no replicated fields. This break serialization
            //baker.AddBuffer<BufferWithReplicatedEnableBits>().ResizeUninitialized(3);
            baker.AddBuffer<EnableableBuffer>(entity).ResizeUninitialized(3);
            //Empty enable flags
            baker.AddComponent<EnableableFlagComponent>(entity);
            //Non empty enable flags
            baker.AddComponent(entity, new ReplicatedEnableableComponentWithNonReplicatedField{value = 9999});
        }
    }

    struct CountSimulationFromSpawnTick : IComponentData
    {
        public int Value;
    }

    class GhostWithRollbackConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new CountSimulationFromSpawnTick{Value = 0});
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class PredictionTestPredictionSystem : SystemBase
    {
        public static bool s_IsEnabled;
        protected override void OnUpdate()
        {
            if (!s_IsEnabled)
                return;
            var deltaTime = SystemAPI.Time.DeltaTime;

            Entities.WithAll<Simulate, GhostInstance>().ForEach((ref LocalTransform trans) => {
                // Make sure we advance by one unit per tick, makes it easier to debug the values
                trans.Position.x += deltaTime * 60.0f;
            }).ScheduleParallel();
        }
    }
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostUpdateSystem))]
    [UpdateBefore(typeof(GhostReceiveSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class InvalidateAllGhostDataBeforeUpdate : SystemBase
    {
        protected override void OnCreate()
        {
            EntityManager.AddComponent<SystemExecutionCounter>(SystemHandle);
        }

        protected override void OnUpdate()
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var tick = networkTime.ServerTick;
            if(!tick.IsValid)
                return;
            //Do not invalidate full ticks. The backup is not restored in that case
            if(!networkTime.IsPartialTick)
                return;
            Entities
                .WithoutBurst()
                .WithAll<GhostInstance>().ForEach((
                    Entity ent,
                    ref LocalTransform trans,
                    ref DynamicBuffer<EnableableBuffer> buffer,
                    //ref DynamicBuffer<BufferWithReplicatedEnableBits> nonReplicatedBuffer,
                    ref ReplicatedEnableableComponentWithNonReplicatedField comp) =>
            {
                for (int el = 0; el < buffer.Length; ++el)
                    buffer[el] = new EnableableBuffer { value = 100*(int)tick.SerializedData };

                // for (int el = 0; el < nonReplicatedBuffer.Length; ++el)
                //     nonReplicatedBuffer[el] = new BufferWithReplicatedEnableBits { value = (byte)tick.SerializedData };

                trans.Position = new float3(-10 * tick.SerializedData, -10 * tick.SerializedData, -10 * tick.SerializedData);
                trans.Scale = -10f*tick.SerializedData;
                comp.value = -10*(int)tick.SerializedData;
                EntityManager.SetComponentEnabled<ReplicatedEnableableComponentWithNonReplicatedField>(ent, false);
                EntityManager.SetComponentEnabled<EnableableFlagComponent>(ent, false);
            }).Run();
            var counter = SystemAPI.GetComponentRW<SystemExecutionCounter>(SystemHandle);
            ++counter.ValueRW.value;
        }
    }
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostUpdateSystem))]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class CheckRestoreFromBackupIsCorrect : SystemBase
    {
        protected override void OnCreate()
        {
            EntityManager.AddComponent<SystemExecutionCounter>(SystemHandle);
        }

        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            if(!tick.IsValid)
                return;
            Entities
                .WithoutBurst()
                .WithAll<Simulate, GhostInstance>().ForEach((
                    Entity ent,
                    ref LocalTransform trans,
                    ref DynamicBuffer<EnableableBuffer> buffer,
                    ref ReplicatedEnableableComponentWithNonReplicatedField comp) =>
                {
                    Assert.IsTrue(trans.Position.x > 0f);
                    Assert.IsTrue(trans.Position.y > 0f);
                    Assert.IsTrue(trans.Position.z > 0f);
                    Assert.IsTrue(math.abs(1f - trans.Scale) < 1e-4f);

                    //enable bits must be replicated
                    Assert.IsTrue(EntityManager.IsComponentEnabled<ReplicatedEnableableComponentWithNonReplicatedField>(ent));
                    Assert.IsTrue(EntityManager.IsComponentEnabled<EnableableFlagComponent>(ent));
                    //This component is not replicated. As such its values is never restored.
                    Assert.AreEqual(-10*(int)tick.SerializedData, comp.value);
                    for (int el = 0; el < buffer.Length; ++el)
                         Assert.AreEqual(1000 * (el+1), buffer[el].value);
                }).Run();
            var counter = SystemAPI.GetComponentRW<SystemExecutionCounter>(SystemHandle);
            ++counter.ValueRW.value;
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    partial struct CheckElapsedTime : ISystem
    {
        private double SinceFirstUpdate;
        private double LastElapsedTime;
        public void OnUpdate(ref SystemState state)
        {
            var timestep = state.World.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
            var time = SystemAPI.Time;
            if (SinceFirstUpdate == 0.0)
            {
                SinceFirstUpdate = time.ElapsedTime;
            }
            Assert.GreaterOrEqual(time.ElapsedTime, LastElapsedTime);
            //the elapsed time must be always an integral multiple of the time step
            Assert.LessOrEqual(math.fmod(time.ElapsedTime, timestep), 1e-6);
            //the relative elapsed time since last update should also be equal to the timestep. If the timestep is changed
            //before the last update, this may be not true
            var totalElapsedSinceFirstUpdate = math.fmod(time.ElapsedTime - SinceFirstUpdate,  timestep);
            var elapsedTimeSinceLastUpdate = math.fmod(time.ElapsedTime - LastElapsedTime,  timestep);
            Assert.LessOrEqual(elapsedTimeSinceLastUpdate, 1e-6);
            Assert.LessOrEqual(totalElapsedSinceFirstUpdate, 1e-6);
            LastElapsedTime = time.ElapsedTime;
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class CheckSkipFrameSystem : SystemBase
    {
        public struct Count : IComponentData
        {
            public NetworkTick LastProcessedServerTick;
            public int lastFrame;
            public int SkippedFrames;
        }

        protected override void OnCreate()
        {
            EntityManager.CreateSingleton(new Count());
        }

        protected override void OnUpdate()
        {
            ref var c = ref SystemAPI.GetSingletonRW<Count>().ValueRW;
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            if (UnityEngine.Time.frameCount > c.lastFrame
                && c.lastFrame != 0
                && (UnityEngine.Time.frameCount - c.lastFrame) > 1)
            {
                ++c.SkippedFrames;
                UnityEngine.Debug.Log($"[{UnityEngine.Time.frameCount}] CheckSkipFrameSystem missed a Unity frame. Current frame {UnityEngine.Time.frameCount} last processed frame {c.lastFrame} - tick {tick}");
            }
            c.lastFrame = UnityEngine.Time.frameCount;
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    partial struct CountNumberOfRollbacksSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var time = SystemAPI.GetSingleton<NetworkTime>();
            foreach (var (data, instance) in SystemAPI.Query<RefRW<CountSimulationFromSpawnTick>, RefRO<GhostInstance>>().WithAll<Simulate>())
            {
                var spawnTick = instance.ValueRO.spawnTick;
                //don't check prediction spawned ghosts not initialized yet
                if(!spawnTick.IsValid)
                    return;
                if (!time.IsPartialTick && time.ServerTick.TicksSince(spawnTick) == 1)
                {
                    data.ValueRW.Value++;
                }
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    partial struct CheckGhostsAlwaysResumedFromLastPredictionBackupTick : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            //don't need to map
            foreach (var rollback in SystemAPI.Query<RefRW<CountSimulationFromSpawnTick>>().WithAll<GhostInstance>().WithAll<Simulate>())
            {
                ++rollback.ValueRW.Value;
            }
        }
    }

    internal class StructuralChangesConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            var b1 = baker.AddBuffer<EnableableBuffer_0>(entity);
            for (int i = 0; i < 3; ++i)
                b1.Add(new EnableableBuffer_0{value = 10+i});
            var b2 = baker.AddBuffer<EnableableBuffer_1>(entity);
            for (int i = 0; i < 3; ++i)
                b2.Add(new EnableableBuffer_1{value = 20+i});
            var b3 = baker.AddBuffer<EnableableBuffer_2>(entity);
            for (int i = 0; i < 3; ++i)
                b3.Add(new EnableableBuffer_2{value = 30+i});
            baker.AddComponent<EnableableComponent_0>(entity, new EnableableComponent_0{value = 1000});
            baker.AddComponent<EnableableComponent_1>(entity, new EnableableComponent_1{value = 2000});
            baker.AddComponent<EnableableComponent_3>(entity, new EnableableComponent_3{value = 3000});
            baker.AddComponent<Data>(entity, new Data{Value = 100});
            baker.AddComponent<CountSimulationFromSpawnTick>(entity);
        }
    }

    internal partial class PredictionTests
    {
        [Category(NetcodeTestCategories.Foundational)]
        [Category(NetcodeTestCategories.Smoke)]
        [TestCase((uint)0x229321)]
        [TestCase((uint)100)]
        [TestCase((uint)0x7FFF011F)]
        [TestCase((uint)0x7FFFFF00)]
        [TestCase((uint)0x7FFFFFF0)]
        [TestCase((uint)0x7FFFF1F0)]
        public void PredictionTickEvolveCorrectly(uint serverTickData)
        {
            var serverTick = new NetworkTick(serverTickData);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(PredictionTestPredictionSystem));
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.SetServerTick(serverTick);
                testWorld.Connect();
                testWorld.GoInGame();
                var serverEnt = testWorld.SpawnOnServer(0);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                for(int i=0;i<256;++i)
                    testWorld.Tick();
            }
        }

        [Test]
        public void PartialPredictionTicksAreRolledBack()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(PredictionTestPredictionSystem));
                PredictionTestPredictionSystem.s_IsEnabled = true;

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<EnableableBuffer>(serverEnt);
                for (int i = 0; i < buffer.Length; ++i)
                    buffer[i] = new EnableableBuffer { value = 1000 * (i + 1) };
                // var nonReplicatedBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<BufferWithReplicatedEnableBits>(serverEnt);
                // for (int i = 0; i < nonReplicatedBuffer.Length; ++i)
                //     nonReplicatedBuffer[i] = new BufferWithReplicatedEnableBits { value = (byte)(10 * (i + 1)) };

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                var prevServer = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt).Position;
                var prevClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt).Position;

                for (int i = 0; i < 64; ++i)
                {
                    testWorld.Tick(1.0f / 60.0f / 4f);

                    var curServer = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt);
                    var curClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt);
                    testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs();
                    // Server does not do fractional ticks so it will not advance the position every frame
                    Assert.GreaterOrEqual(curServer.Position.x, prevServer.x);
                    testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                    // Client does fractional ticks and position should be always increasing
                    Assert.Greater(curClient.Position.x, prevClient.x);
                    prevServer = curServer.Position;
                    prevClient = curClient.Position;
                }
                // Stop updating, let it run for a while and check that they ended on the same value
                PredictionTestPredictionSystem.s_IsEnabled = false;
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                prevServer = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt).Position;
                prevClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt).Position;
                Assert.IsTrue(math.distance(prevServer, prevClient) < 0.01);
            }
        }

        [Test]
        [Description("Expect that MaxPredictAheadTimeMS a) caps the number of prediction ticks performed & b) adds some forced input latency.")]
        public void MaxPredictAheadTimeMS_Works()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;

            const int one60HzTickMs = 17;
            testWorld.DriverSimulatedDelay = 150 - one60HzTickMs; // EACH WAY! Thus, adjusted to be a EstimatedRTT of ~300ms.
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 3);
            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, serverEnt);

            var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            clientTickRate.MaxPredictAheadTimeMS = 200;
            clientTickRate.ForcedInputLatencyTicks = 0;
            testWorld.ClientWorlds[0].EntityManager.CreateSingleton(clientTickRate);

            clientTickRate.MaxPredictAheadTimeMS = 200;
            clientTickRate.ForcedInputLatencyTicks = 6; // 100ms.
            testWorld.ClientWorlds[1].EntityManager.CreateSingleton(clientTickRate);

            clientTickRate.MaxPredictAheadTimeMS = 500;
            clientTickRate.ForcedInputLatencyTicks = 3; // 50ms.
            testWorld.ClientWorlds[2].EntityManager.CreateSingleton(clientTickRate);

            testWorld.Connect(maxSteps:64);
            testWorld.GoInGame();

            for (int i = 0; i < 512; ++i)
                testWorld.Tick();

            var client0NetTime = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]);
            var client0SnapshotAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]);
            var client1NetTime = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[1]);
            var client1SnapshotAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[1]);
            var client2NetTime = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[2]);
            var client2SnapshotAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[2]);
            Debug.Log($"Client0: {client0NetTime} {(int)client0SnapshotAck.EstimatedRTT}ms \nClient1:{client1NetTime} {(int)client1SnapshotAck.EstimatedRTT}ms\nClient2:{client2NetTime} {(int)client2SnapshotAck.EstimatedRTT}ms");
            Assert.That(client0SnapshotAck.EstimatedRTT, Is.EqualTo(300).Within(20));
            Assert.That(client1SnapshotAck.EstimatedRTT, Is.EqualTo(300).Within(20));
            Assert.That(client2SnapshotAck.EstimatedRTT, Is.EqualTo(300).Within(20));
            Assert.That(client0NetTime.EffectiveInputLatencyTicks, Is.EqualTo(9).Within(2)); // ( ~18 ticks (~300ms) + 2 ticks for TargetCommandSlack - clamped at 12 ticks (200ms) = ~8 ticks.
            Assert.That(client1NetTime.EffectiveInputLatencyTicks, Is.EqualTo(9).Within(2)); // Same as above, as ForcedInputLatency is essentially ignored here, as its lower than 8 ticks, which is being forced by MaxPredictAheadMS.
            Assert.That(client2NetTime.EffectiveInputLatencyTicks, Is.EqualTo(3).Within(2)); // Expect it to be the same as its config value.
            Assert.That(client0NetTime.InputTargetTick.TicksSince(client0NetTime.ServerTick), Is.EqualTo(client0NetTime.EffectiveInputLatencyTicks));
            Assert.That(client1NetTime.InputTargetTick.TicksSince(client1NetTime.ServerTick), Is.EqualTo(client1NetTime.EffectiveInputLatencyTicks));
            Assert.That(client2NetTime.InputTargetTick.TicksSince(client2NetTime.ServerTick), Is.EqualTo(client2NetTime.EffectiveInputLatencyTicks));
            Assert.That(client0NetTime.PredictedTickIndex, Is.GreaterThan(12)); // ~200ms + 1 for partial tick.
            Assert.That(client1NetTime.PredictedTickIndex, Is.GreaterThan(12)); // ~200ms + 1 for partial tick.
            Assert.That(client2NetTime.PredictedTickIndex, Is.GreaterThan((6*3)-3)); // ~300ms - 3 ticks for ForcedInputLatency.
        }

        [TestCase(1)]
        [TestCase(20)]
        [TestCase(30)]
        [TestCase(40)]
        public void HistoryBufferIsRollbackCorrectly(int ghostCount)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(PredictionTestPredictionSystem),
                    typeof(InvalidateAllGhostDataBeforeUpdate),
                    typeof(CheckRestoreFromBackupIsCorrect));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                for (int i = 0; i < ghostCount; ++i)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                    var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<EnableableBuffer>(serverEnt);
                    for (int el = 0; el < buffer.Length; ++el)
                        buffer[el] = new EnableableBuffer { value = 1000 * (el+ 1) };
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, LocalTransform.FromPosition(new float3(0f, 10f, 100f)));
                    // var nonReplicatedBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<BufferWithReplicatedEnableBits>(serverEnt);
                    // for (int el = 0; el < nonReplicatedBuffer.Length; ++el)
                    //     nonReplicatedBuffer[el] = new BufferWithReplicatedEnableBits { value = (byte)(10 * (el + 1)) };
                }
                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                PredictionTestPredictionSystem.s_IsEnabled = true;
                for (int i = 0; i < 64; ++i)
                {
                    testWorld.Tick(1.0f / 60.0f / 4f);
                }
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                PredictionTestPredictionSystem.s_IsEnabled = false;
                var counter1 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SystemExecutionCounter>(
                        testWorld.ClientWorlds[0].GetExistingSystem<InvalidateAllGhostDataBeforeUpdate>());
                var counter2 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SystemExecutionCounter>(
                    testWorld.ClientWorlds[0].GetExistingSystem<InvalidateAllGhostDataBeforeUpdate>());
                Assert.Greater(counter1.value, 0);
                Assert.Greater(counter2.value, 0);
                Assert.AreEqual(counter1.value, counter2.value);
            }
        }

        [Category(NetcodeTestCategories.Foundational)]
        [TestCase(90)]
        [TestCase(82)]
        [TestCase(45)]
        public void NetcodeClientPredictionRateManager_WillWarnWhenMismatchSimulationTickRate(int fixedStepRate)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().InternalRateManager.Timestep = 1f/fixedStepRate;
                testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().InternalRateManager.Timestep = 1f/fixedStepRate;

                // Connect and make sure the connection could be established
                testWorld.Connect();
                //Expect 2, one for server, one for the client
                LogAssert.Expect(LogType.Warning, $"The PredictedFixedStepSimulationSystemGroup.TimeStep is {1f/fixedStepRate}ms ({fixedStepRate}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {1f/60f}ms ({60f}FPS).\n" +
                                                  "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                  "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");

                LogAssert.Expect(LogType.Warning, $"The PredictedFixedStepSimulationSystemGroup.TimeStep is {1f/fixedStepRate}ms ({fixedStepRate}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {1f/60f}ms ({60f}FPS).\n" +
                                                  "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                  "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");

                //Check that the simulation tick rate are the same
                var clientRate = testWorld.GetSingleton<ClientServerTickRate>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(60, clientRate.SimulationTickRate);
                Assert.AreEqual(1, clientRate.PredictedFixedStepSimulationTickRatio);
                var serverTimeStep = testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
                var clientTimestep = testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
                Assert.That(serverTimeStep, Is.EqualTo(clientRate.SimulationFixedTimeStep));
                Assert.That(clientTimestep, Is.EqualTo(clientRate.SimulationFixedTimeStep));

                //Also check that if the value is overriden, it is still correctly set to the right value
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().InternalRateManager.Timestep = 1f/fixedStepRate;
                    testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().InternalRateManager.Timestep = 1f/fixedStepRate;
                    testWorld.Tick();
                    serverTimeStep = testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
                    clientTimestep = testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
                    LogAssert.Expect(LogType.Warning, $"The PredictedFixedStepSimulationSystemGroup.TimeStep is {1f/fixedStepRate}ms ({fixedStepRate}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {1f/60f}ms ({60f}FPS).\n" +
                                                      "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                      "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");
                    LogAssert.Expect(LogType.Warning, $"The PredictedFixedStepSimulationSystemGroup.TimeStep is {1f/fixedStepRate}ms ({fixedStepRate}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {1f/60f}ms ({60f}FPS).\n" +
                                                      "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                      "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");
                    Assert.That(clientTimestep, Is.EqualTo(clientRate.SimulationFixedTimeStep));
                    Assert.That(serverTimeStep, Is.EqualTo(clientRate.SimulationFixedTimeStep));
                }
            }
        }

        [Category(NetcodeTestCategories.Foundational)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void PredictedFixedStepSimulation_ElapsedTimeReportedCorrectly(int ratio)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CheckElapsedTime));
                testWorld.CreateWorlds(true, 1);

                //tick the world before connecting or finalizing the setup to mimic the fact the values has been changed by users
                //after the world creation later on.
                for(int i=0;i<10;++i)
                    testWorld.Tick();
                var tickRate = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(ClientServerTickRate));
                testWorld.ServerWorld.EntityManager.SetComponentData(tickRate, new ClientServerTickRate
                {
                    PredictedFixedStepSimulationTickRatio = ratio
                });
                testWorld.Connect();
                //Check that the simulation tick rate are the same
                var clientRate = testWorld.GetSingleton<ClientServerTickRate>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(60, clientRate.SimulationTickRate);
                Assert.AreEqual(ratio, clientRate.PredictedFixedStepSimulationTickRatio);
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                }
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(1f / 30f);
                }
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(1f / 45f);
                }
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(1f / 117f);
                }
            }
        }

        [TestCase(1)]
        [TestCase(100)]
        public void HistoryBufferIsPreservedOnStructuralChanges(int ghostCount)
        {
            void CheckPredicitionStepsAndStartTick(NativeArray<Entity> entities, NetCodeTestWorld testWorld, NetworkTick currentPartialTick,
                NetworkTick lastBackupTick)
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var predictionCount = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountSimulationFromSpawnTick>(entities[i]).Value;
                    var predictedGhost = testWorld.ClientWorlds[0].EntityManager.GetComponentData<PredictedGhost>(entities[i]);
                    if (predictedGhost.AppliedTick == predictedGhost.PredictionStartTick)
                    {
                        Assert.AreEqual(currentPartialTick.TicksSince(predictedGhost.AppliedTick), predictionCount);
                    }
                    else
                    {
                        Assert.AreEqual(lastBackupTick, predictedGhost.PredictionStartTick);
                        Assert.AreEqual(currentPartialTick.TicksSince(lastBackupTick), predictionCount);
                    }

                    //reset here the start tick, so next partial we will track and reset counters
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new CountSimulationFromSpawnTick());
                }
            }

            void CheckValues(NativeArray<Entity> entities, NetCodeTestWorld testWorld, NativeArray<int> expecteDataValue)
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Assert.AreEqual(1000, testWorld.ClientWorlds[0].EntityManager.GetComponentData<EnableableComponent_0>(entities[i]).value);
                    Assert.AreEqual(2000, testWorld.ClientWorlds[0].EntityManager.GetComponentData<EnableableComponent_1>(entities[i]).value);
                    Assert.AreEqual(3000, testWorld.ClientWorlds[0].EntityManager.GetComponentData<EnableableComponent_3>(entities[i]).value);
                    if (testWorld.ClientWorlds[0].EntityManager.HasComponent<Data>(entities[i]))
                        Assert.AreEqual(expecteDataValue[i], testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(entities[i]).Value);
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_0>(entities[i]);
                        Assert.AreEqual(3, b.Length);
                        Assert.AreEqual(10, b[0].value);
                        Assert.AreEqual(11, b[1].value);
                        Assert.AreEqual(12, b[2].value);
                    }
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_1>(entities[i]);
                        Assert.AreEqual(3, b.Length);
                        Assert.AreEqual(20, b[0].value);
                        Assert.AreEqual(21, b[1].value);
                        Assert.AreEqual(22, b[2].value);
                    }
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_2>(entities[i]);
                        Assert.AreEqual(3, b.Length);
                        Assert.AreEqual(30, b[0].value);
                        Assert.AreEqual(31, b[1].value);
                        Assert.AreEqual(32, b[2].value);
                    }
                }
            }

            void InvalidateValues(NativeArray<Entity> entities, NetCodeTestWorld testWorld)
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new EnableableComponent_0(){value = 0});
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new EnableableComponent_1(){value = 0});
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new EnableableComponent_3(){value = 0});
                    if (testWorld.ClientWorlds[0].EntityManager.HasComponent<Data>(entities[i]))
                    {
                       testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new Data(){Value = 0});
                    }
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_0>(entities[i]);
                        b.ElementAt(0).value = 0;
                        b.ElementAt(1).value = 0;
                        b.ElementAt(2).value = 0;
                    }
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_1>(entities[i]);
                        b.ElementAt(0).value = 0;
                        b.ElementAt(1).value = 0;
                        b.ElementAt(2).value = 0;
                    }
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_2>(entities[i]);
                        b.ElementAt(0).value = 0;
                        b.ElementAt(1).value = 0;
                        b.ElementAt(2).value = 0;
                    }
                }
            }

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(PredictionTestPredictionSystem),
                    typeof(CheckGhostsAlwaysResumedFromLastPredictionBackupTick));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new StructuralChangesConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                ghostConfig.RollbackPredictionOnStructuralChanges = false;
                var ghostChild = new GameObject();
                ghostChild.transform.parent = ghostGameObject.transform;
                ghostChild.AddComponent<TestNetCodeAuthoring>().Converter = new StructuralChangesConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();
                //sync clocks
                for(int i=0;i<16;++i)
                    testWorld.Tick();
                //spawn
                for (int i = 0; i < ghostCount; ++i)
                {
                    testWorld.SpawnOnServer(ghostGameObject);
                }

                testWorld.ClientWorlds[0].Unmanaged.GetExistingSystemState<CheckGhostsAlwaysResumedFromLastPredictionBackupTick>().Enabled = false;
                //sync everything
                for(int i=0;i<64;++i)
                    testWorld.Tick();

                //Run one last time with a delta time such that we end up exactly on a full tick
                var time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                testWorld.TickClientWorld((1 - time.ServerTickFraction)/60f);

                time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                Assert.IsFalse(time.IsPartialTick, $"time.IsPartialTick, server tick fraction is {time.ServerTickFraction}");

                var ghosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
                var entities = ghosts.ToEntityArray(Allocator.Temp);
                var dataValues = new NativeArray<int>(entities.Length, Allocator.Temp);
                for (int i = 0; i < dataValues.Length; ++i)
                    dataValues[i] = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(entities[i]).Value;
                Assert.AreEqual(ghostCount, entities.Length);
                CheckValues(entities, testWorld, dataValues);

                //run partial ticks and verify max 1 prediction step is done
                testWorld.ClientWorlds[0].Unmanaged.GetExistingSystemState<CheckGhostsAlwaysResumedFromLastPredictionBackupTick>().Enabled = true;
                var lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
                Assert.IsTrue(lastBackupTick.IsValid);
                //there is no partial tick restore in this tick because the last tick was a full tick. The continuation goes without actually
                //restoring from the backup.
                //TODO: would be nice to distinguish
                testWorld.TickClientWorld(1f/240f);
                var currentPartialTick = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTick;
                CheckPredicitionStepsAndStartTick(entities, testWorld, currentPartialTick, lastBackupTick);
                //Now I can invalidate and check restore work properly
                InvalidateValues(entities, testWorld);
                testWorld.TickClientWorld(1f/240f);
                //run partial ticks and verify max 1 prediction step is done`
                Assert.AreEqual(testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value, lastBackupTick);
                CheckPredicitionStepsAndStartTick(entities, testWorld, currentPartialTick, lastBackupTick);
                CheckValues(entities, testWorld, dataValues);
                //change half of the entities. Backup should be still the same and so all values should be restored as they were at backup time
                for (int i = 0; i < entities.Length; i+=2)
                    testWorld.ClientWorlds[0].EntityManager.RemoveComponent<Data>(entities[i]);
                InvalidateValues(entities, testWorld);
                //What happen in this tick ? The client will receive a new snapshot from the server and rollback-prediction will occur,
                //causing a new backup being made for the same tick on the client. What the Data backup contains? because the component has been
                //removed, the value should be 0 on certain entities
                testWorld.TickServerWorld();
                testWorld.TickClientWorld(1f/240f);
                //run partial ticks and verify max 1 prediction step is done`
                Assert.AreEqual(testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value, lastBackupTick);
                CheckPredicitionStepsAndStartTick(entities, testWorld, currentPartialTick, lastBackupTick);
                CheckValues(entities, testWorld, dataValues);
                //Add 1/4 of the entities back to the previous chunk. For these entities the data value will be 0 now.
                for (int i = 0; i < entities.Length; i += 4)
                {
                    testWorld.ClientWorlds[0].EntityManager.AddComponent<Data>(entities[i]);
                    dataValues[i] = 0;
                }
                InvalidateValues(entities, testWorld);
                //What happen here: the client will now re-add the component and it state will be restored from the backup, that does not contain
                //the correct authoritative (or predicted) data (that is 100) but instead 0.
                //Should this be considered a bug?
                //In the original implementation, because a rollback to that last received snapshot occur (because of the structural change)
                //this will sync the component to a correct state.
                //But because now the recovery is able to find the backup, until we don't receive new data from the server, that value is stale.
                //This does not occur if the structural change does not affect replicated components. That is probably the most common scenario.
                //How do we solve this?
                testWorld.TickClientWorld(1f/240f);
                Assert.AreEqual(currentPartialTick.TickIndexForValidTick, testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTick.TickIndexForValidTick);
                //A new backup has been made
                Assert.AreNotEqual(lastBackupTick, testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value);
                CheckPredicitionStepsAndStartTick(entities, testWorld, currentPartialTick, lastBackupTick);
                CheckValues(entities, testWorld, dataValues);
            }
        }

        internal struct TestCommand : IInputComponentData
        {
            public int Value;
        }

        [Test(Description = "Tests that we have 0 margin for commands when using IPC")]
        public void MarginIsZeroWithIPC()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.UseFakeSocketConnection = 0;
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new StructuralChangesConverter();
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoring.DefaultGhostMode = GhostMode.Predicted;
                authoring.HasOwner = true;
                testWorld.CreateGhostCollection(ghostGameObject);
                testWorld.CreateWorlds(true, 1);
                var entity = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(entity, new GhostOwner()
                {
                    NetworkId = 1
                });
                testWorld.Connect();
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                testWorld.GoInGame();

                for (int i = 0; i < 2048; ++i)
                {
                    testWorld.Tick();

                    // Check that the margin is zero
                    var serverTime = testWorld.GetNetworkTime(testWorld.ServerWorld);
                    var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                    var serverAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ServerWorld);
                    Debug.Log($"[{i}] ST:{serverTime.ServerTick}, LastReceivedSnapshotByRemote:{serverAck.LastReceivedSnapshotByRemote}, LastReceivedSnapshotByLocal:{serverAck.LastReceivedSnapshotByLocal}, MostRecentFullCommandTick:{serverAck.MostRecentFullCommandTick}!");
                    if (serverAck.LastReceivedSnapshotByRemote.IsValid)
                        Assert.IsTrue(!serverTime.ServerTick.IsNewerThan(serverAck.LastReceivedSnapshotByLocal));
                    if (serverAck.MostRecentFullCommandTick.IsValid)
                        Assert.AreEqual(serverTime.ServerTick, serverAck.MostRecentFullCommandTick);
                }
            }
        }

        [Test(Description = "Tests that the client stay ahead of the server and never skip a prediction tick, even in presence of partial ticks and lower send rate.")]
        public void ClientNeverSkipAPredictionTick()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                //enable using IPC connection
                testWorld.UseFakeSocketConnection = 0;
                testWorld.Bootstrap(true, typeof(CheckSkipFrameSystem));
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new StructuralChangesConverter();
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoring.DefaultGhostMode = GhostMode.Predicted;
                authoring.HasOwner = true;
                testWorld.CreateGhostCollection(ghostGameObject);
                testWorld.CreateWorlds(true, 1);
                var entity = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(entity, new GhostOwner()
                {
                    NetworkId = 1
                });
                testWorld.ServerWorld.EntityManager.CreateSingleton(new ClientServerTickRate
                {
                    SimulationTickRate = 30,
                });
                testWorld.Connect(0.271f/60f, 64);
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                testWorld.GoInGame();
                var rnd = new Unity.Mathematics.Random(0x4000);
                for (int i = 0; i < 2048; ++i)
                {
                    var dt = rnd.NextFloat(0.1f, 0.4f) / 60f;
                    testWorld.Tick(dt);
                    var serverTime = testWorld.GetNetworkTime(testWorld.ServerWorld);
                    var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                    //check that when the server tick change, the client tick is already ahead so that the server always
                    //receive the right full tick.
                    if (clientTime.ServerTick.IsValid)
                    {
                        Assert.IsTrue(clientTime.ServerTick.IsNewerThan(serverTime.ServerTick), $"Expected client tick {clientTime.ServerTick}.{clientTime.ServerTickFraction} to be always ahead of the server to ensure full command tick update arrive in time, but server tick was already {serverTime.ServerTick}");
                    }
                }
                var count = testWorld.GetSingleton<CheckSkipFrameSystem.Count>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(0, count.SkippedFrames, "Expect client does not skip any partial prediction or ticks.");
            }
        }

        [Test]
        public void NetworkTimeSingleton_CorrectValuesInsidePredictionLoop()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(AssertNetworkTimeSingletonValuesCorrectInsidePredictionLoopSystem));
            testWorld.DriverSimulatedDelay = 40;
            testWorld.DriverSimulatedJitter = 20;
            testWorld.DriverSimulatedDrop = 20; // Interval, so 5%, or every 20th packet.
            var ghostGameObject = new GameObject();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            const float FrameTime = 1.0f / 60.0f;
            testWorld.Connect(FrameTime, 128);
            testWorld.GoInGame();
            // Spawn a new entity on the server. Server will start send snapshots now.
            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, serverEnt);

            // Tick for a while, using an extremely wobbly client step.
            AssertNetworkTimeSingletonValuesCorrectInsidePredictionLoopSystem.Reset();
            var rand = Unity.Mathematics.Random.CreateFromIndex(10350135);
            for (int i = 0; i < 100; i++)
            {
                testWorld.Tick(rand.NextFloat(FrameTime * 0.5f, FrameTime * 1.5f));
            }

            AssertNetworkTimeSingletonValuesCorrectInsidePredictionLoopSystem.Validate();
        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        internal partial struct AssertNetworkTimeSingletonValuesCorrectInsidePredictionLoopSystem : ISystem
        {
            public static bool HasHadPartialTickOnClient;
            public static bool HasHadFullTickOnClient;
            public static bool HasHadFinalPredictionTickOnClient;
            public static bool HasHadFinalPredictionTickOnServer;

            private NetworkTick m_LatestFullServerTick;
            private NetworkTick m_LastServerTickOnServer;
            private int m_LastFinalTickIndex;
            private double m_LastElapsedNetworkTimeOnServer;

            public static void Reset()
            {
                HasHadPartialTickOnClient = false;
                HasHadFullTickOnClient = false;
                HasHadFinalPredictionTickOnClient = false;
                HasHadFinalPredictionTickOnServer = false;
            }

            public static void Validate()
            {
                Assert.IsTrue(HasHadPartialTickOnClient);
                Assert.IsTrue(HasHadFullTickOnClient);
                Assert.IsTrue(HasHadFinalPredictionTickOnClient);
                Assert.IsTrue(HasHadFinalPredictionTickOnServer);
            }

            public void OnUpdate(ref SystemState state)
            {
                Assert.IsFalse(state.WorldUnmanaged.IsThinClient());
                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                SystemAPI.GetSingleton<NetDebug>().Log($"[{state.WorldUnmanaged.Name}] [TestTick:{NetCodeTestWorld.TickIndex}] ServerTick:{networkTime.ServerTick.ToFixedString()} (fraction:{(int) (100 * networkTime.ServerTickFraction)}%), SimulationStepBatchSize:{networkTime.SimulationStepBatchSize}, ElapsedNT:{networkTime.ElapsedNetworkTime}\n<color=green>{networkTime.Flags}</color>");
                Assert.IsTrue(networkTime.IsInPredictionLoop);

                if (!networkTime.ServerTick.IsValid) return;

                SystemAPI.TryGetSingleton<ClientServerTickRate>(out var clientServerTickRate);
                clientServerTickRate.ResolveDefaults();

                if (state.WorldUnmanaged.IsClient())
                    AssertForClient(networkTime, SystemAPI.Time);
                else
                    AssertForServer(networkTime, clientServerTickRate, SystemAPI.Time);
                AssertForBoth(networkTime);
            }

            private void AssertForClient(NetworkTime networkTime, TimeData timeData)
            {
                Assert.IsFalse(networkTime.IsCatchUpTick, "IsCatchUpTick");
                Assert.NotZero(networkTime.ElapsedNetworkTime, "ElapsedNetworkTime");
                Assert.NotZero(timeData.DeltaTime, "DeltaTime");
                Assert.NotZero(timeData.ElapsedTime, "ElapsedTime");
                Assert.That(networkTime.PredictedTickIndex, Is.AtLeast(1), "PredictedTickIndex");
                Assert.That(networkTime.NumPredictedTicksExpected, Is.AtLeast(1), "NumPredictedTicksExpected");

                Assert.IsTrue(networkTime.ServerTick.IsNewerThan(networkTime.InterpolationTick), "ST.IsNewerThan(IT)");
                if (networkTime.IsPartialTick)
                {
                    HasHadPartialTickOnClient = true;
                    Assert.IsFalse(networkTime.IsFirstTimeFullyPredictingTick, "IsFirstTimeFullyPredictingTick when IsPartialTick");
                    Assert.NotZero(networkTime.ServerTickFraction, "ServerTickFraction when IsPartialTick");
                }
                else
                {
                    HasHadFullTickOnClient = true;
                    Assert.That(networkTime.ServerTickFraction, Is.EqualTo(1), "ServerTickFraction");

                    if (networkTime.IsFirstTimeFullyPredictingTick)
                    {
                        if (m_LatestFullServerTick.IsValid) Assert.That(networkTime.ServerTick.TicksSince(m_LatestFullServerTick), Is.GreaterThanOrEqualTo(0), "networkTime.ServerTick.TicksSince(m_LatestFullServerTick)");
                        m_LatestFullServerTick = networkTime.ServerTick;
                    }
                }

                if (networkTime.IsFinalPredictionTick) HasHadFinalPredictionTickOnClient = true;
                if (networkTime.IsFinalFullPredictionTick) Assert.IsFalse(networkTime.IsPartialTick, "IsPartialTick");

                Assert.NotZero(networkTime.ServerTickFraction, "ServerTickFraction");
            }

            private void AssertForServer(NetworkTime networkTime, ClientServerTickRate clientServerTickRate, TimeData timeData)
            {
                Assert.IsFalse(networkTime.IsPartialTick, "IsPartialTick");
                Assert.IsTrue(networkTime.IsFirstTimeFullyPredictingTick, "IsFirstTimeFullyPredictingTick");
                Assert.AreEqual(networkTime.IsFinalFullPredictionTick, networkTime.IsFinalPredictionTick, "IsFinalPredictionTick");
                Assert.That(networkTime.SimulationStepBatchSize, Is.AtLeast(1), "SimulationStepBatchSize");
                Assert.That(networkTime.ServerTickFraction, Is.EqualTo(1), "ServerTickFraction");
                Assert.That(networkTime.PredictedTickIndex, Is.EqualTo(0), "PredictedTickIndex");
                Assert.That(networkTime.NumPredictedTicksExpected, Is.EqualTo(1), "PredictedTickIndex");

                Assert.IsFalse(networkTime.IsCatchUpTick, "Server is not being death-spiral stressed in this test.");
                if (networkTime.IsFinalPredictionTick) HasHadFinalPredictionTickOnServer = true;

                if (m_LastServerTickOnServer.IsValid)
                    Assert.That(networkTime.ServerTick.TicksSince(m_LastServerTickOnServer), Is.EqualTo(networkTime.SimulationStepBatchSize), "ServerTick.TicksSince(m_LastServerTickOnServer)");
                m_LastServerTickOnServer = networkTime.ServerTick;

                if (m_LastElapsedNetworkTimeOnServer != default)
                {
                    var deltaTime = networkTime.ElapsedNetworkTime - m_LastElapsedNetworkTimeOnServer;
                    Assert.That(deltaTime, Is.EqualTo(clientServerTickRate.SimulationFixedTimeStep * networkTime.SimulationStepBatchSize), "dt == SimulationStepBatchSize");
                    Assert.That(deltaTime, Is.EqualTo(timeData.DeltaTime), "dt == timeData.DeltaTime");
                    Assert.That(networkTime.ElapsedNetworkTime, Is.EqualTo(timeData.ElapsedTime), "ElapsedNetworkTime == timeData.ElapsedTime");
                }

                m_LastElapsedNetworkTimeOnServer = networkTime.ElapsedNetworkTime;
            }

            private void AssertForBoth(NetworkTime networkTime)
            {
                Assert.IsTrue(networkTime.InputTargetTick.IsValid, "InputTargetTick.IsValid");
                Assert.IsTrue(networkTime.ServerTick.IsValid, "ServerTick.IsValid");

                // NetCodeTestWorld.TickIndex is a test-compatible proxy for the outer loop tick.
                // I.e. Time.frameCount.
                // So we can use this to validate that we're not ticking
                // the outer loop when networkTime.IsFinalPredictionTick is false.
                if (networkTime.IsFinalPredictionTick)
                {
                    m_LastFinalTickIndex = NetCodeTestWorld.TickIndex;
                }
                else if (m_LastFinalTickIndex != default)
                {
                    Assert.That(NetCodeTestWorld.TickIndex - m_LastFinalTickIndex, Is.EqualTo(1), "TickIndex - m_LastFinalTickIndex");
                }

                Assert.Zero(networkTime.EffectiveInputLatencyTicks, "EffectiveInputLatencyTicks");
            }
        }

        [DisableAutoCreation]
        internal partial struct AssertNetworkTimeSingletonValuesCorrectOutsidePredictionLoopSystem : ISystem
        {
            public void OnUpdate(ref SystemState state)
            {
                Assert.IsFalse(state.WorldUnmanaged.IsThinClient());
                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                Assert.IsFalse(networkTime.IsInPredictionLoop, "A system created without an UpdateInGroup should NOT automatically be added to the prediction loop.");

                if (!networkTime.ServerTick.IsValid) return;

                Assert.IsTrue(networkTime.InputTargetTick.IsValid);
                Assert.IsTrue(networkTime.ServerTick.IsValid);

                SystemAPI.TryGetSingleton<ClientServerTickRate>(out var clientServerTickRate);
                clientServerTickRate.ResolveDefaults();

                Assert.NotZero(SystemAPI.Time.DeltaTime);
                Assert.NotZero(SystemAPI.Time.ElapsedTime);

                Assert.IsFalse(networkTime.IsCatchUpTick);
                Assert.IsFalse(networkTime.IsFinalPredictionTick);
                Assert.IsFalse(networkTime.IsFirstTimeFullyPredictingTick);
                Assert.IsFalse(networkTime.IsFirstPredictionTick);
                Assert.IsFalse(networkTime.IsFinalFullPredictionTick);
                Assert.NotZero(networkTime.ElapsedNetworkTime);
                Assert.That(networkTime.SimulationStepBatchSize, Is.GreaterThanOrEqualTo(1));
                Assert.That(networkTime.PredictedTickIndex, Is.InRange(0, 10));
                Assert.That(networkTime.NumPredictedTicksExpected, Is.EqualTo(networkTime.PredictedTickIndex));
                Assert.Zero(networkTime.EffectiveInputLatencyTicks);
            }
        }
    }
}

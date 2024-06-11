using NUnit.Framework;
using Unity.Collections;
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
    public class PredictionTestConverter : TestNetCodeAuthoring.IConverter
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
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial class PredictionTestPredictionSystem : SystemBase
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
    public partial class InvalidateAllGhostDataBeforeUpdate : SystemBase
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
    public partial class CheckRestoreFromBackupIsCorrect : SystemBase
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
        private double ElapsedTime;
        public void OnUpdate(ref SystemState state)
        {
            var timestep = state.World.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
            var time = SystemAPI.Time;
            if (ElapsedTime == 0.0)
            {
                ElapsedTime = time.ElapsedTime;
            }
            var totalElapsed = math.fmod(time.ElapsedTime - ElapsedTime,  timestep);
            //the elapsed time must be always an integral multiple of the time step
            Assert.LessOrEqual(totalElapsed, 1e-6);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    partial struct CheckNumberOfRollbacks : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var time = SystemAPI.GetSingleton<NetworkTime>();
            foreach (var (data, instance) in SystemAPI.Query<RefRW<PredictionTests.CountRollback>, RefRO<GhostInstance>>().WithAll<Simulate>())
            {
                var spawnTick = instance.ValueRO.spawnTick;
                //The first tick after the spawn is what we are predicting
                spawnTick.Increment();
                if (!time.IsPartialTick && time.ServerTick == spawnTick)
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
            foreach (var rollback in SystemAPI.Query<RefRW<PredictionTests.CountRollback>>().WithAll<GhostInstance>().WithAll<Simulate>())
            {
                ++rollback.ValueRW.Value;
            }
        }
    }

    public class StructuralChangesConverter : TestNetCodeAuthoring.IConverter
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
            baker.AddComponent<PredictionTests.CountRollback>(entity);
        }
    }


    struct GhostSpawner : IComponentData
    {
        public Entity ghost;
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial class PredictSpawnGhost : SystemBase
    {
        public NetworkTick spawnTick;
        protected override void OnCreate()
        {
            RequireForUpdate<GhostSpawner>();
        }

        protected override void OnUpdate()
        {
            var spawner = SystemAPI.GetSingleton<GhostSpawner>();
            var serverTick = SystemAPI.GetSingleton<NetworkTime>();
            if (serverTick.IsFirstTimeFullyPredictingTick && spawnTick == serverTick.ServerTick)
            {
                var predictedEntity = EntityManager.Instantiate(spawner.ghost);
                EntityManager.SetComponentData(predictedEntity, new Data{Value = 100});
            }
        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial class PredictSpawnGhostUpdate : SystemBase
    {
        protected override void OnUpdate()
        {
            foreach(var data in SystemAPI.Query<RefRW<Data>>().WithAll<Simulate>())
            {
                ++data.ValueRW.Value;
            }
        }
    }


    public class PredictionTests
    {
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

        [TestCase(90)]
        [TestCase(82)]
        [TestCase(45)]
        public void NetcodeClientPredictionRateManager_WillWarnWhenMismatchSimulationTickRate(int fixedStepRate)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().RateManager.Timestep = 1f/fixedStepRate;
                testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().RateManager.Timestep = 1f/fixedStepRate;

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
                Assert.That(serverTimeStep, Is.EqualTo(1f / clientRate.SimulationTickRate));
                Assert.That(clientTimestep, Is.EqualTo(1f / clientRate.SimulationTickRate));

                //Also check that if the value is overriden, it is still correctly set to the right value
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().RateManager.Timestep = 1f/fixedStepRate;
                    testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().RateManager.Timestep = 1f/fixedStepRate;
                    testWorld.Tick();
                    serverTimeStep = testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
                    clientTimestep = testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
                    LogAssert.Expect(LogType.Warning, $"The PredictedFixedStepSimulationSystemGroup.TimeStep is {1f/fixedStepRate}ms ({fixedStepRate}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {1f/60f}ms ({60f}FPS).\n" +
                                                      "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                      "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");
                    LogAssert.Expect(LogType.Warning, $"The PredictedFixedStepSimulationSystemGroup.TimeStep is {1f/fixedStepRate}ms ({fixedStepRate}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {1f/60f}ms ({60f}FPS).\n" +
                                                      "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                      "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");
                    Assert.That(clientTimestep, Is.EqualTo(1f / clientRate.SimulationTickRate));
                    Assert.That(serverTimeStep, Is.EqualTo(1f / clientRate.SimulationTickRate));
                }
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void PredictedFixedStepSimulation_ElapsedTimeReportedCorrectly(int ratio)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CheckElapsedTime));
                testWorld.CreateWorlds(true, 1);
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

        internal struct CountRollback : IComponentData
        {
            public int Value;
        }
        public class GhostWithRollbackConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent(entity, new CountRollback{Value = 0});
            }
        }

        public enum PredictedSpawnRollbackOptions
        {
            RollbackToSpawnTick,
            DontRollbackToSpawnTick
        }
        public enum KeepHistoryBufferOptions
        {
            UseHistoryBufferOnStructuralChanges,
            RollbackOnStructuralChanges
        }

        [Test]
        public void PredictSpawnGhost_OutsidePrediction_RollbackAndHistoryBackup([Values]PredictedSpawnRollbackOptions rollback,
            [Values]KeepHistoryBufferOptions rollbackOnStructuralChanges)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CheckNumberOfRollbacks));

                // Predicted ghost
                var predictedGhost = new GameObject("PredictedGO1");
                predictedGhost.AddComponent<TestNetCodeAuthoring>().Converter = new GhostWithRollbackConverter();
                var ghostConfig = predictedGhost.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                ghostConfig.RollbackPredictedSpawnedGhostState = rollback == PredictedSpawnRollbackOptions.RollbackToSpawnTick;
                ghostConfig.RollbackPredictionOnStructuralChanges = rollbackOnStructuralChanges == KeepHistoryBufferOptions.RollbackOnStructuralChanges;

                var predictedGhost2 = new GameObject("PredictedGO2");
                predictedGhost2.AddComponent<TestNetCodeAuthoring>().Converter = new GhostWithRollbackConverter();
                var ghostConfig2 = predictedGhost2.AddComponent<GhostAuthoringComponent>();
                ghostConfig2.DefaultGhostMode = GhostMode.Predicted;
                ghostConfig2.SupportedGhostModes = GhostModeMask.Predicted;

                Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhost, predictedGhost2));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                //server spawn a predicted ghost such that every update predicted spawned ghost should rollback
                testWorld.SpawnOnServer(1);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // client predict spawning two predicted ghost: one with update, one without.
                var prefabs = testWorld.GetSingletonBuffer<NetCodeTestPrefab>(testWorld.ClientWorlds[0]);
                var ghostWithRollback = testWorld.ClientWorlds[0].EntityManager.Instantiate(prefabs[0].Value);
                var tickOnClient = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTick;
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                    if (testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick == tickOnClient)
                    {
                        testWorld.SpawnOnServer(0);
                    }
                }
                var rollbackData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountRollback>(ghostWithRollback);
                if(rollback == PredictedSpawnRollbackOptions.RollbackToSpawnTick)
                    Assert.Greater(rollbackData.Value, 2);
                else if(rollbackOnStructuralChanges == KeepHistoryBufferOptions.RollbackOnStructuralChanges)
                    Assert.AreEqual(2, rollbackData.Value);
                else
                    Assert.AreEqual(1, rollbackData.Value);
            }
        }

        [Test(Description = "The test verify both the misprediction behavior and its mitigation, in case a predicted spawned ghost is " +
                            "instantiated immediately in the prediction loop. When the prefab is configured to preserve the history buffer on structural changes, " +
                            "the misprediction disappears")]
        public void PredictSpawnGhost_InsidePrediction_CanMispredict([Values]KeepHistoryBufferOptions rollbackOnStructuralChange)
        {
            static void SetupSpawner(NetCodeTestWorld testWorld, World world)
            {
                var spawner = world.EntityManager.CreateEntity(typeof(GhostSpawner));
                world.EntityManager.SetComponentData(spawner, new GhostSpawner
                {
                    ghost = testWorld.GetSingletonBuffer<NetCodeTestPrefab>(world)[1].Value
                });
            }
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(PredictSpawnGhost), typeof(PredictSpawnGhostUpdate));

                var prefabs = new GameObject[2];
                for (int i = 0; i < 2; ++i)
                {
                    // Predicted ghost. We create two types of the same
                    var predictedGhostGO = new GameObject($"PredictedGO-{i}");
                    predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
                    var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
                    ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                    ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                    ghostConfig.RollbackPredictionOnStructuralChanges = rollbackOnStructuralChange == KeepHistoryBufferOptions.RollbackOnStructuralChanges;

                    // One child nested on predicted ghost
                    var predictedGhostGOChild = new GameObject("PredictedGO-Child");
                    predictedGhostGOChild.AddComponent<TestNetCodeAuthoring>().Converter = new ChildDataConverter();
                    predictedGhostGOChild.transform.parent = predictedGhostGO.transform;
                    prefabs[i] = predictedGhostGO;
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(prefabs));

                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();

                testWorld.SpawnOnServer(0);

                testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().Enabled = false;

                SetupSpawner(testWorld, testWorld.ServerWorld);
                SetupSpawner(testWorld, testWorld.ClientWorlds[0]);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                var spawnTick = time.ServerTick;
                if(time.IsPartialTick)
                    spawnTick.Decrement();
                spawnTick.Add(1);
                testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;
                testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().Enabled = true;

                var predictedSpawnRequests = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PredictedGhostSpawnRequest));
                testWorld.Tick(0.75f*1f/60f);
                //Client will spawn the entity now. We will do a full tick + 1 partial tick. There will be a new backup
                //for the spawnTick, that is when the entity is spawned. The predicted spawned ghost is not initialized yet.
                testWorld.Tick(0.75f*1f/60f);
                Assert.IsFalse(predictedSpawnRequests.IsEmpty);
                var predictedSpawnEntity = predictedSpawnRequests.GetSingletonEntity();
                Assert.AreEqual(102, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
                var lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
                Assert.AreEqual(spawnTick.TickIndexForValidTick, lastBackupTick.TickIndexForValidTick);
                //Client initialize the spawned object (remove PredictedGhostRequest). At this point the ghost will contains in the
                //snapshot buffer a value that is not correct (it is the one for a partial tick, not the spawn one). I consider this
                //almost a bug. For now we will preserve the current behaviour but need to be changed.
                //However, because the backup exist, the value of data component is preserved correctly.
                testWorld.Tick();
                lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
                //Next frame is removed (still in the command buffer)
                Assert.IsFalse(predictedSpawnRequests.IsEmpty);
                //We have a new backup now, it will contains 102
                Assert.AreEqual(spawnTick.TickIndexForValidTick + 1, lastBackupTick.TickIndexForValidTick);
                if(rollbackOnStructuralChange == KeepHistoryBufferOptions.RollbackOnStructuralChanges)
                    Assert.AreEqual(104, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
                else
                    Assert.AreEqual(103, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
                testWorld.Tick(0.25f/60f);
                lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
                Assert.AreEqual(spawnTick.TickIndexForValidTick + 1, lastBackupTick.TickIndexForValidTick);
                Assert.IsTrue(predictedSpawnRequests.IsEmpty);
                time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                Assert.IsTrue(time.IsPartialTick);
                if (rollbackOnStructuralChange == KeepHistoryBufferOptions.RollbackOnStructuralChanges)
                {
                    Assert.AreEqual(2, time.PredictedTickIndex);
                    Assert.AreNotEqual(103, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
                    Assert.AreEqual(104, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
                }
                else
                {
                    Assert.AreEqual(1, time.PredictedTickIndex);
                    Assert.AreEqual(103, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
                }
                testWorld.Tick();
                //How many prediction tick we did? We received from the server a new ghost update, so at least the delta in respect
                //the last received and the current client tick
                time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                var lastReceivedTick = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]).LastReceivedSnapshotByLocal;
                var expectedPredictionTicks = time.ServerTick.TicksSince(lastReceivedTick);
                Assert.AreEqual(expectedPredictionTicks, time.PredictedTickIndex);
                if (rollbackOnStructuralChange == KeepHistoryBufferOptions.RollbackOnStructuralChanges)
                {
                    Assert.AreNotEqual(104, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
                    Assert.AreEqual(105, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
                }
                else
                {
                    Assert.AreEqual(104, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
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
                    var predictionCount = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountRollback>(entities[i]).Value;
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
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new CountRollback());
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
                testWorld.Tick(time.ServerTickFraction/60f);

                time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                Assert.IsFalse(time.IsPartialTick);

                var ghosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
                var entities = ghosts.ToEntityArray(Allocator.Temp);
                var dataValues = new NativeArray<int>(entities.Length, Allocator.Temp);
                for (int i = 0; i < dataValues.Length; ++i)
                    dataValues[i] = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(entities[0]).Value;
                Assert.AreEqual(ghostCount, entities.Length);
                CheckValues(entities, testWorld, dataValues);

                //run partial ticks and verify max 1 prediction step is done
                testWorld.ClientWorlds[0].Unmanaged.GetExistingSystemState<CheckGhostsAlwaysResumedFromLastPredictionBackupTick>().Enabled = true;
                var lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
                Assert.IsTrue(lastBackupTick.IsValid);
                //there is no partial tick restore in this tick because the last tick was a full tick. The continuation goes without actually
                //restoring from the backup.
                //TODO: would be nice to distinguish
                testWorld.Tick(1f/240f);
                var currentPartialTick = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTick;
                CheckPredicitionStepsAndStartTick(entities, testWorld, currentPartialTick, lastBackupTick);
                //Now I can invalidate and check restore work properly
                InvalidateValues(entities, testWorld);
                testWorld.Tick(1f/240f);
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
                testWorld.Tick(1f/240f);
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
                testWorld.Tick(1f/240f);
                Assert.AreEqual(currentPartialTick.TickIndexForValidTick, testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTick.TickIndexForValidTick);
                //A new backup has been made
                Assert.AreNotEqual(lastBackupTick, testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value);
                CheckPredicitionStepsAndStartTick(entities, testWorld, currentPartialTick, lastBackupTick);
                CheckValues(entities, testWorld, dataValues);
            }
        }
    }
}

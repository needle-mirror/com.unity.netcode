#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;
using Random = Unity.Mathematics.Random;

namespace Unity.NetCode.Tests
{
    internal struct InputComponentData : IInputComponentData
    {
        public int Horizontal;
        public int Vertical;
        /// <summary>Unique value, ensuring deserialization on a per-tick basis is 100% correct.</summary>
        public NetworkTick SentinelTick;
        public uint Sentinel;
        public InputEvent Jump;
    }

    internal struct InputRemoteTestComponentData : IInputComponentData
    {
        [GhostField] public int Horizontal;
        [GhostField] public int Vertical;
        [GhostField] public InputEvent Jump;
    }

    [GhostComponent(PrefabType=GhostPrefabType.AllPredicted)]
    internal struct InputComponentDataAllPredicted : IInputComponentData
    {
        public int Horizontal;
        public int Vertical;
        public InputEvent Jump;
    }

    [GhostComponent(PrefabType=GhostPrefabType.AllPredicted)]
    internal struct InputComponentDataAllPredictedWithGhostFields : IInputComponentData
    {
        [GhostField] public int Horizontal;
        [GhostField] public int Vertical;
        [GhostField] public InputEvent Jump;
    }

    [GhostComponent(PrefabType=GhostPrefabType.Server)]
    internal struct InputComponentDataServerOnly : IInputComponentData
    {
        public int Horizontal;
        public int Vertical;
        public InputEvent Jump;
    }

    [GhostComponent(PrefabType = GhostPrefabType.Client, OwnerSendType = SendToOwnerType.SendToOwner, SendTypeOptimization = GhostSendType.OnlyInterpolatedClients, SendDataForChildEntity = false)]
    internal struct InputComponentDataWithGhostComponent : IInputComponentData
    {
        public int Horizontal;
        public int Vertical;
        public InputEvent Jump;
    }

    [GhostComponent(PrefabType = GhostPrefabType.Client, OwnerSendType = SendToOwnerType.SendToOwner, SendTypeOptimization = GhostSendType.OnlyInterpolatedClients, SendDataForChildEntity = false)]
    internal struct InputComponentDataWithGhostComponentAndGhostFields : IInputComponentData
    {
        [GhostField] public int Horizontal;
        [GhostField] public int Vertical;
        [GhostField] public InputEvent Jump;
    }

    internal class InputRemoteTestComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<InputRemoteTestComponentData>(entity);
        }
    }

    internal class InputComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<InputComponentData>(entity);
        }
    }

    internal class InputComponentDataAllPredictedConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<InputComponentDataAllPredicted>(entity);
        }
    }

    internal class InputComponentDataAllPredictedWithGhostFieldsConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<InputComponentDataAllPredictedWithGhostFields>(entity);
        }
    }

    internal class InputComponentDataServerOnlyConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<InputComponentDataServerOnly>(entity);
        }
    }

    internal class InputComponentDataWithGhostComponentConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<InputComponentDataWithGhostComponent>(entity);
        }
    }

    internal class InputComponentDataWithGhostComponentAndGhostFieldsConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<InputComponentDataWithGhostComponentAndGhostFields>(entity);
        }
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    internal partial class GatherInputsSystem : SystemBase
    {
        private const int k_WaitDuration = 5;
        int m_WaitTicks = k_WaitDuration;   // Wait n ticks to ensure we read individual Input sends.
        int m_EventCounter;                 // How many times have we set an input event so far
        public static int TargetEventCount; // How many times total should we trigger events

        protected override void OnCreate()
        {
            RequireForUpdate<InputComponentData>();
            RequireForUpdate<NetworkStreamInGame>();
        }
        protected override void OnUpdate()
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var targetEventCount = TargetEventCount;
            Entities
                .WithoutBurst()
                .WithAll<GhostOwnerIsLocal>()
                .ForEach((ref InputComponentData inputData) =>
                {
                    inputData = default;
                    inputData.Horizontal = 1;
                    inputData.Vertical = 1;
                    inputData.SentinelTick = networkTime.ServerTick;
                    inputData.Sentinel = Unity.Mathematics.Random.CreateFromIndex(networkTime.ServerTick.TickIndexForValidTick).NextUInt();
                    if (m_EventCounter < targetEventCount)
                    {
                        if (m_WaitTicks > 0)
                        {
                            m_WaitTicks--;
                        }
                        else
                        {
                            m_EventCounter++;
                            inputData.Jump.Set();
                            m_WaitTicks = k_WaitDuration;
                        }
                    }
                }).Run();
        }
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    internal partial class GatherInputsRemoteTestSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<InputRemoteTestComponentData>();
            RequireForUpdate<NetworkStreamInGame>();
        }
        protected override void OnUpdate()
        {
            // Inputs are only gathered on the local player, so if any inputs are set on
            // the remote player it's because they were fetched from the buffer (replicated via ghost system)
            Entities
                .WithAll<GhostOwnerIsLocal>()
                .ForEach((ref InputRemoteTestComponentData inputData) =>
                {
                    inputData = default;
                    inputData.Horizontal = 1;
                    inputData.Vertical = 1;
                }).Run();
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class ProcessInputsSystem : SystemBase
    {
        public int EventCounter;
        public long EventCountSumValue;
        public int ArrivedOnTimeCounter;
        public int ArrivedLateCounter;
        public int ExpectedInputDeltaTicks;
        protected override void OnCreate()
        {
            RequireForUpdate<InputComponentData>();
        }
        protected override void OnUpdate()
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var worldName = World.Unmanaged.Name;
            bool client = World.IsClient();
            Entities.WithoutBurst().WithAll<Simulate>().ForEach(
                (ref InputComponentData input) =>
                {
                    if (input.Jump.IsSet)
                    {
                        EventCounter++;
                        EventCountSumValue += input.Jump.Count;
                    }

                    // Validate Sentinel (but only when inputs begin to arrive):
                    if (input.Horizontal != 0)
                    {
                        var deltaTicks = networkTime.ServerTick.TicksSince(input.SentinelTick);
                        var world = (client ? "CLIENT" : "SERVER");
                        var arrivedOnTime = deltaTicks >= 0 && deltaTicks <= ExpectedInputDeltaTicks;
                        if (arrivedOnTime)
                            ArrivedOnTimeCounter++;
                        else
                        {
                            ArrivedLateCounter++;
                            Debug.LogWarning($"Input was raised on client on ServerTick {input.SentinelTick.ToFixedString()}, but was consumed by the {world} {deltaTicks} tick later (on arrival tick {networkTime.ServerTick.ToFixedString()}), which is more than expected [0, {ExpectedInputDeltaTicks}]!");
                            Assert.That(deltaTicks - ExpectedInputDeltaTicks <= 1, "deltaTicks - ExpectedInputDeltaTicks <= 1");
                        }
                        var expectedSentinelValue = Random.CreateFromIndex(input.SentinelTick.TickIndexForValidTick).NextUInt();
                        Assert.AreEqual(expectedSentinelValue, input.Sentinel, $"Input Sentinel value was incorrect for seed value {input.SentinelTick.ToFixedString()} on {world}!");
                        //Debug.Log($" >> ProcessInputsSystem:{worldName}\n\tST:{networkTime.ServerTick}.{(int)(networkTime.ServerTickFraction * 100)} | H:{input.Horizontal} V:{input.Vertical} Jump:{input.Jump} SentinelTick:{input.SentinelTick} Sentinel:{input.Sentinel} deltaTicks:{deltaTicks} of {ExpectedInputDeltaTicks}, arrivedOnTime:{arrivedOnTime}!");
                    }
                }).Run();
        }
    }

    internal class InputComponentDataTest
    {
        public enum ForcedInputLatencyMode : uint
        {
            Disabled = 0u,
            ForcedInputLatency_1 = 1u,
            ForcedInputLatency_2 = 2u,
            ForcedInputLatency_8 = 16u,
        }

        [Test, Description("Ensures that InputComponentData's are correctly handled by netcode locally (i.e. client-side, inside prediction) and remotely (i.e. on the server), and without input delay. Also ensures ForcedInputLatency is correctly applied.")]
        [Ignore("Disabled as there is a GhostOwner field assignment bug in the GhostUpdateSystem on IPC.")]
        public void InputComponentData_IsCorrectlySynchronized_CorrectInputDelay_IPC(
            [Values] ForcedInputLatencyMode forcedInputLatency)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.UseFakeSocketConnection = 0;
            InputComponentData_IsCorrectlySynchronized_CorrectInputDelay_RunTest(true, NetCodeTestLatencyProfile.None, forcedInputLatency, testWorld);
        }

        [Test, Description("Ensures that InputComponentData's are correctly handled by netcode locally (i.e. client-side, inside prediction) and remotely (i.e. on the server), and without input delay. Also ensures ForcedInputLatency is correctly applied.")]
        public void InputComponentData_IsCorrectlySynchronized_CorrectInputDelay_UDP(
            [Values]NetCodeTestLatencyProfile profile,
            [Values]ForcedInputLatencyMode forcedInputLatency)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.SetTestLatencyProfile(profile);
            InputComponentData_IsCorrectlySynchronized_CorrectInputDelay_RunTest(false, profile, forcedInputLatency, testWorld);
        }

        private static void InputComponentData_IsCorrectlySynchronized_CorrectInputDelay_RunTest(bool isIPC, NetCodeTestLatencyProfile profile, ForcedInputLatencyMode forcedInputLatency, NetCodeTestWorld testWorld)
        {
            GatherInputsSystem.TargetEventCount = 0;
            testWorld.Bootstrap(true, typeof(GatherInputsSystem), typeof(ProcessInputsSystem));

            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new InputComponentDataConverter();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
            ghostConfig.HasOwner = true;
            ghostConfig.SupportAutoCommandTarget = true;
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect(maxSteps: 32);
            testWorld.GoInGame();
            var serverInputSystem = testWorld.ServerWorld.GetExistingSystemManaged<ProcessInputsSystem>();
            serverInputSystem.ExpectedInputDeltaTicks = (isIPC ? 1 : (int)NetworkTimeSystem.DefaultClientTickRate.TargetCommandSlack);
            serverInputSystem.ExpectedInputDeltaTicks += (int)forcedInputLatency;
            var clientInputSystem = testWorld.ClientWorlds[0].GetExistingSystemManaged<ProcessInputsSystem>();
            clientInputSystem.ExpectedInputDeltaTicks = (int)forcedInputLatency;

            //.
            var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            clientTickRate.ForcedInputLatencyTicks = (byte)forcedInputLatency;
            testWorld.ClientWorlds[0].EntityManager.CreateSingleton(clientTickRate);

            // Ghost & GhostOwner.
            var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
            var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;
            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId,});

            // Wait for client spawn:
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();
            var clientEnt = testWorld.TryGetSingletonEntity<InputComponentData>(testWorld.ClientWorlds[0]);
            Assert.AreNotEqual(Entity.Null, clientEnt, "Sanity!");

            // Run test:
            GatherInputsSystem.TargetEventCount = 5;
            for (int i = 0; i < 64; ++i)
                testWorld.Tick();

            // Event should only fire equal to TargetEventCount on the server (but can multiple times on client because of prediction loop)
            Assert.AreEqual(GatherInputsSystem.TargetEventCount, serverInputSystem.EventCounter, "serverInputSystem.TargetEventCount");
            Assert.AreEqual(GatherInputsSystem.TargetEventCount, serverInputSystem.EventCountSumValue, "serverInputSystem.TargetEventCount");
            // Client prediction rollback & re-simulation means these counts will be higher.
            Assert.GreaterOrEqual(clientInputSystem.EventCounter, GatherInputsSystem.TargetEventCount, "clientInputSystem.TargetEventCount");
            Assert.GreaterOrEqual(clientInputSystem.EventCountSumValue, GatherInputsSystem.TargetEventCount, "clientInputSystem.TargetEventCount");

            // Assert input polling -> processing tick latency:
            Assert.That(clientInputSystem.ArrivedLateCounter, Is.LessThan(10), "clientInputSystem.ArrivedLateCounter");
            Assert.That(clientInputSystem.ArrivedOnTimeCounter, Is.GreaterThan(clientInputSystem.ArrivedLateCounter), "clientInputSystem.ArrivedOnTimeCounter");
            Assert.That(serverInputSystem.ArrivedLateCounter, Is.LessThan(10), "serverInputSystem.ArrivedLateCounter");
            Assert.That(serverInputSystem.ArrivedOnTimeCounter, Is.GreaterThan(serverInputSystem.ArrivedLateCounter), "serverInputSystem.ArrivedOnTimeCounter");

            // Assert input logging is realistic:
            var networkSnapshotAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ServerWorld);
            var cas = networkSnapshotAck.CommandArrivalStatistics;
            Debug.Log(cas.ToFixedString());
            const int expectedMinPacketsArrived = 7;
            Assert.GreaterOrEqual(cas.NumCommandPacketsArrived, expectedMinPacketsArrived, "Expected command packets to arrive!?");
            Assert.GreaterOrEqual(cas.NumCommandsArrived, expectedMinPacketsArrived * 4, "Expected command packets to be full of inputs!?");
            Assert.GreaterOrEqual(cas.NumRedundantResends, expectedMinPacketsArrived * 2.2f, "Expected most resends to be redundant!?");
            if (profile != NetCodeTestLatencyProfile.PL33)
                Assert.GreaterOrEqual(cas.NumArrivedTooLate, 0f, "Expected no inputs arriving too late!");
            Assert.IsTrue(math.abs(cas.AvgCommandsPerPacket - 4) < double.Epsilon, "Expected the default of 4 commands per packet!");
            Assert.GreaterOrEqual(cas.AvgCommandPayloadSizeInBits, 200, "Expected command bits to be ~200!");

            //.
            var clientNetTime = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]);
            Assert.AreEqual(clientNetTime.EffectiveInputLatencyTicks, (uint)forcedInputLatency, "EffectiveForcedInputLatencyTicks");
        }

        /* Validate that remote predicted input is properly synchronized when input fields are
         * marked as ghost fields. The input buffer should be present on all clients and be
         * filled with the input values of each one as well.
         */
        [Test]
        public void InputComponentData_InputBufferIsRemotePredictedWhenAppropriate()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(GatherInputsRemoteTestSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new InputRemoteTestComponentDataConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.SupportedGhostModes = GhostModeMask.All;
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 2);
                testWorld.Connect();
                testWorld.GoInGame();

                using var serverConnectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                var serverConnectionEntities = serverConnectionQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(2, serverConnectionEntities.Length);
                var serverConnectionEntToClient1 = serverConnectionEntities[0];
                var serverConnectionEntToClient2 = serverConnectionEntities[1];
                var clientConnectionEnt1 = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var clientConnectionEnt2 = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[1]);
                var netId1 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt1).Value;
                var netId2 = testWorld.ClientWorlds[1].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt2).Value;

                var serverEntPlayer1 = testWorld.SpawnOnServer(ghostGameObject);
                var serverEntPlayer2 = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntPlayer1, new GhostOwner {NetworkId = netId1});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntPlayer2, new GhostOwner {NetworkId = netId2});

                // Wait for client spawn
                using EntityQuery clientQuery1 = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<InputRemoteTestComponentData>());
                using EntityQuery clientQuery2 = testWorld.ClientWorlds[1].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<InputRemoteTestComponentData>());
                for (int i = 0; i < 16; ++i)
                {
                    if (clientQuery1.CalculateEntityCount() == 2 && clientQuery2.CalculateEntityCount() == 2) break;
                    testWorld.Tick();
                }

                using var inputsQueryOnClient1 = testWorld.ClientWorlds[0].EntityManager
                    .CreateEntityQuery(ComponentType.ReadOnly<InputRemoteTestComponentData>());
                var playersOnClient1 = inputsQueryOnClient1.ToEntityArray(Allocator.Temp);
                var clientEnt1OwnPlayer = playersOnClient1[0];
                var ghostOwnerOnPlayer1OnClient1 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEnt1OwnPlayer);
                Assert.AreEqual(1, ghostOwnerOnPlayer1OnClient1.NetworkId);

                using var inputsQueryOnClient2 = testWorld.ClientWorlds[1].EntityManager
                    .CreateEntityQuery(ComponentType.ReadOnly<InputRemoteTestComponentData>());
                var playersOnClient2 = inputsQueryOnClient2.ToEntityArray(Allocator.Temp);
                var clientEnt2OwnPlayer = playersOnClient2[1];
                var ghostOwnerOnPlayer2OnClient2 = testWorld.ClientWorlds[1].EntityManager.GetComponentData<GhostOwner>(clientEnt2OwnPlayer);
                Assert.AreEqual(2, ghostOwnerOnPlayer2OnClient2.NetworkId);

                testWorld.ServerWorld.EntityManager.SetComponentData(serverConnectionEntToClient1, new CommandTarget{targetEntity = serverEntPlayer1});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverConnectionEntToClient2, new CommandTarget{targetEntity = serverEntPlayer2});
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientConnectionEnt1, new CommandTarget{targetEntity = clientEnt1OwnPlayer});
                testWorld.ClientWorlds[1].EntityManager.SetComponentData(clientConnectionEnt2, new CommandTarget{targetEntity = clientEnt2OwnPlayer});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // Input buffer must be added to the prefab
                Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasBuffer<InputBufferData<InputRemoteTestComponentData>>(serverEntPlayer1));
                Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasBuffer<InputBufferData<InputRemoteTestComponentData>>(serverEntPlayer2));
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasBuffer<InputBufferData<InputRemoteTestComponentData>>(clientEnt1OwnPlayer));
                Assert.IsTrue(testWorld.ClientWorlds[1].EntityManager.HasBuffer<InputBufferData<InputRemoteTestComponentData>>(clientEnt2OwnPlayer));

                // Validate that client 2 actually has input buffer data (copied to component) from client 1 and reversed
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                testWorld.ClientWorlds[1].EntityManager.CompleteAllTrackedJobs();
                var inputsOnClient1 = inputsQueryOnClient1.ToComponentDataArray<InputRemoteTestComponentData>(Allocator.Temp);
                Assert.AreEqual(2, inputsOnClient1.Length);
                Assert.AreEqual(1, inputsOnClient1[0].Horizontal);
                Assert.AreEqual(1, inputsOnClient1[0].Vertical);
                Assert.AreEqual(1, inputsOnClient1[1].Horizontal);
                Assert.AreEqual(1, inputsOnClient1[1].Vertical);

                var inputsOnClient2 = inputsQueryOnClient2.ToComponentDataArray<InputRemoteTestComponentData>(Allocator.Temp);
                Assert.AreEqual(2, inputsOnClient2.Length);
                Assert.AreEqual(1, inputsOnClient2[0].Horizontal);
                Assert.AreEqual(1, inputsOnClient2[0].Vertical);
                Assert.AreEqual(1, inputsOnClient2[1].Horizontal);
                Assert.AreEqual(1, inputsOnClient2[1].Vertical);
            }
        }

        [Test]
        public void InputComponentData_WithDefaultGhostComponents()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var gameObject0 = new GameObject();
                gameObject0.AddComponent<GhostAuthoringComponent>();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new InputRemoteTestComponentDataConverter();
                gameObject0.name = $"{nameof(InputRemoteTestComponentData)}Test";

                var gameObject1 = new GameObject();
                gameObject1.AddComponent<GhostAuthoringComponent>();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new InputComponentDataConverter();
                gameObject1.name = $"{nameof(InputComponentData)}Test";

                Assert.IsTrue(testWorld.CreateGhostCollection(
                    gameObject0, gameObject1));

                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                testWorld.SpawnOnServer(gameObject0);
                testWorld.SpawnOnServer(gameObject1);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InputComponentData>(), 1);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InputRemoteTestComponentData>(), 1);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InputBufferData<InputComponentData>>(), 1);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InputBufferData<InputRemoteTestComponentData>>(), 1);

                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InputComponentData>(), 1);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InputRemoteTestComponentData>(), 1);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InputBufferData<InputComponentData>>(), 1);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InputBufferData<InputRemoteTestComponentData>>(), 1);
            }
        }

        [Test]
        public void InputComponentData_WithAllPredictedGhost()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var gameObject0 = new GameObject();
                var ghostAuthoringComponent = gameObject0.AddComponent<GhostAuthoringComponent>();
                ghostAuthoringComponent.HasOwner = true;
                ghostAuthoringComponent.SupportAutoCommandTarget = true;
                ghostAuthoringComponent.SupportedGhostModes = GhostModeMask.Predicted;
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new InputComponentDataAllPredictedConverter();
                gameObject0.name = $"{nameof(InputComponentDataAllPredicted)}Test";

                var gameObject1 = new GameObject();
                ghostAuthoringComponent = gameObject1.AddComponent<GhostAuthoringComponent>();
                ghostAuthoringComponent.HasOwner = true;
                ghostAuthoringComponent.SupportAutoCommandTarget = true;
                ghostAuthoringComponent.SupportedGhostModes = GhostModeMask.Predicted;
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new InputComponentDataAllPredictedWithGhostFieldsConverter();
                gameObject1.name = $"{nameof(InputComponentDataAllPredictedWithGhostFields)}Test";

                Assert.IsTrue(testWorld.CreateGhostCollection(
                    gameObject0, gameObject1));

                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var predictedEnt = testWorld.SpawnOnServer(gameObject0);
                testWorld.ServerWorld.EntityManager.SetComponentData(predictedEnt, new GhostOwner {NetworkId = netId});
                predictedEnt = testWorld.SpawnOnServer(gameObject1);
                testWorld.ServerWorld.EntityManager.SetComponentData(predictedEnt, new GhostOwner {NetworkId = netId});

                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InputComponentDataAllPredictedWithGhostFields>(), 1);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InputComponentDataAllPredicted>(), 1);

                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InputComponentDataAllPredictedWithGhostFields>(), 1);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InputComponentDataAllPredicted>(), 1);
            }
        }

        /* Test
         *   - IInputComponent with GhostComponent attribute
         *   - IInputComponent with GhostFields and GhostComponent attribute
         * In all cases the ghost component config (like prefab type) from the input component
         * should copy over to the generated input buffer.
         */
        [Test]
        public void InputComponentData_BufferCopiesGhostComponentConfigFromInputComponent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var gameObject0 = new GameObject();
                gameObject0.AddComponent<GhostAuthoringComponent>();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new InputComponentDataWithGhostComponentConverter();
                gameObject0.name = $"{nameof(InputComponentDataWithGhostComponent)}Test";

                var gameObject1 = new GameObject();
                gameObject1.AddComponent<GhostAuthoringComponent>();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new InputComponentDataWithGhostComponentAndGhostFieldsConverter();
                gameObject1.name = $"{nameof(InputComponentDataWithGhostComponentAndGhostFields)}Test";

                Assert.IsTrue(testWorld.CreateGhostCollection(
                    gameObject0, gameObject1));

                testWorld.CreateWorlds(true, 1);

                // We can only validate the remote predicted component in the ghost component collection
                // as the normal component does not get a buffer which is a ghost component (to be
                // synced in snapshots) but is just handled as a command is
                using var collectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostComponentSerializerCollectionData>());
                var collectionData = collectionQuery.GetSingleton<GhostComponentSerializerCollectionData>();
                GhostComponentSerializer.State inputBufferWithGhostFieldsSerializerState = default;
                var inputBufferType = ComponentType.ReadWrite<InputBufferData<InputComponentDataWithGhostComponent>>();
                var inputBufferWithFieldsType = ComponentType.ReadWrite<InputBufferData<InputComponentDataWithGhostComponentAndGhostFields>>();
                foreach (var state in collectionData.Serializers)
                {
                    if (state.ComponentType.CompareTo(inputBufferWithFieldsType) == 0)
                        inputBufferWithGhostFieldsSerializerState = state;
                }

                // There should be empty variant configs for all the other types (input components and buffer without ghost fields)
                // where we'll find the prefab type registered
                var foundVariantForInputBuffer = false;
                var foundVariantForInputComponent = false;
                var foundVariantForInputComponentWithFields = false;
                foreach (var nonSerializedStrategies in collectionData.SerializationStrategies)
                {
                    if (nonSerializedStrategies.IsSerialized != 0)
                        continue;

                    if (nonSerializedStrategies.Component.CompareTo(inputBufferType) == 0)
                    {
                        foundVariantForInputBuffer = true;
                        Assert.AreEqual(GhostPrefabType.Client, nonSerializedStrategies.PrefabType);
                    }
                    if (nonSerializedStrategies.Component.CompareTo(ComponentType.ReadWrite<InputComponentDataWithGhostComponent>()) == 0)
                    {
                        foundVariantForInputComponent = true;
                        Assert.AreEqual(GhostPrefabType.Client, nonSerializedStrategies.PrefabType);
                    }
                    if (nonSerializedStrategies.Component.CompareTo(ComponentType.ReadWrite<InputComponentDataWithGhostComponentAndGhostFields>()) == 0)
                    {
                        foundVariantForInputComponentWithFields = true;
                        Assert.AreEqual(GhostPrefabType.Client, nonSerializedStrategies.PrefabType);
                    }
                }
                Assert.IsTrue(foundVariantForInputBuffer);
                Assert.IsTrue(foundVariantForInputComponent);
                Assert.IsTrue(foundVariantForInputComponentWithFields);

                Assert.AreEqual(GhostPrefabType.Client, inputBufferWithGhostFieldsSerializerState.PrefabType);
                Assert.AreEqual((int)GhostSendType.OnlyInterpolatedClients, (int)inputBufferWithGhostFieldsSerializerState.SendMask);
                // TODO: Fix test-case using new info that component data on child entities is NOT sent by default. Assert.AreEqual(0, inputBufferWithGhostFieldsSerializerState.SendForChildEntities);
                // SendToOwnerType will be forced to SendToNonOwner (was set to SendToOwner)
                Assert.AreEqual(SendToOwnerType.SendToNonOwner, inputBufferWithGhostFieldsSerializerState.SendToOwner);

                // A spawn on server should result in no input or buffer components as it was configured
                // to be client only via the ghost component attributes on the input component struct
                testWorld.SpawnOnServer(gameObject0);
                testWorld.SpawnOnServer(gameObject1);

                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InputComponentDataWithGhostComponent>(), 0);
                CheckComponent(testWorld.ServerWorld, inputBufferType, 0);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InputComponentDataWithGhostComponentAndGhostFields>(), 0);
                CheckComponent(testWorld.ServerWorld, inputBufferWithFieldsType, 0);

                testWorld.Connect();
                testWorld.GoInGame();
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InputComponentDataWithGhostComponent>(), 1);
                CheckComponent(testWorld.ClientWorlds[0], inputBufferType, 1);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InputComponentDataWithGhostComponentAndGhostFields>(), 1);
                CheckComponent(testWorld.ClientWorlds[0], inputBufferWithFieldsType, 1);
            }
        }

        void CheckComponent(World w, ComponentType testType, int expectedCount)
        {
            using var query = w.EntityManager.CreateEntityQuery(testType);
            using (var ghosts = query.ToEntityArray(Allocator.Temp))
            {
                var compCount = ghosts.Length;
                Assert.AreEqual(expectedCount, compCount);
            }
        }
    }
}

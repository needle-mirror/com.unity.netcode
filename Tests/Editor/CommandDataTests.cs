using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.Transforms;
using UnityEngine.Networking.PlayerConnection;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    public struct CommandDataTestsTickInput : ICommandData
    {
        public uint Tick { get; set; }
        public int Value;
    }
    public struct CommandDataTestsTickInput2 : ICommandData
    {
        public uint Tick { get; set; }
        public int Value;
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    public partial class CommandDataTestsTickInputSystem : SystemBase
    {
        private ClientSimulationSystemGroup clientSim;
        protected override void OnCreate()
        {
            clientSim = World.GetExistingSystem<ClientSimulationSystemGroup>();
            RequireSingletonForUpdate<NetworkStreamInGame>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadWrite<CommandDataTestsTickInput>()));
        }
        protected override void OnUpdate()
        {
            var tick = clientSim.ServerTick;
            Entities.ForEach((DynamicBuffer<CommandDataTestsTickInput> inputBuffer) => {
                inputBuffer.AddCommandData(new CommandDataTestsTickInput
                {
                    Tick = tick,
                    Value = 1
                });
            }).Run();
        }
    }
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    public partial class CommandDataTestsTickInput2System : SystemBase
    {
        private ClientSimulationSystemGroup clientSim;
        protected override void OnCreate()
        {
            clientSim = World.GetExistingSystem<ClientSimulationSystemGroup>();
            RequireSingletonForUpdate<NetworkStreamInGame>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadWrite<CommandDataTestsTickInput2>()));
        }
        protected override void OnUpdate()
        {
            var tick = clientSim.ServerTick;
            Entities.ForEach((DynamicBuffer<CommandDataTestsTickInput2> inputBuffer) => {
                inputBuffer.AddCommandData(new CommandDataTestsTickInput2
                {
                    Tick = tick,
                    Value = 2
                });
            }).Run();
        }
    }
    public class CommandDataTests
    {
        private const float deltaTime = 1.0f / 60.0f;

        [Test]
        public void MissingCommandTargetUpdatesAckAndCommandAge()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 4));
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var serverAck = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkSnapshotAckComponent>(serverConnectionEnt);
                var clientAck = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkSnapshotAckComponent>(clientConnectionEnt);

                Assert.Less(testWorld.ServerWorld.GetExistingSystem<ServerSimulationSystemGroup>().ServerTick - serverAck.LastReceivedSnapshotByRemote, 4);
                Assert.Less(clientAck.ServerCommandAge / 256.0f, -1.5f);
                Assert.Greater(clientAck.ServerCommandAge / 256.0f, -2.5f);
            }
        }
        [Test]
        public void ConnectionCommandTargetComponentSendsDataForSingleBuffer()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 4));
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkIdComponent>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);

                testWorld.ServerWorld.EntityManager.SetComponentData(serverConnectionEnt, new CommandTargetComponent{targetEntity = serverEnt});
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientConnectionEnt, new CommandTargetComponent{targetEntity = clientEnt});

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(deltaTime);

                clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
            }
        }
        [Test]
        public void ConnectionCommandTargetComponentSendsDataForMultipleBuffers()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 4));
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkIdComponent>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);
                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput2>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput2>(clientEnt);

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                var clientBuffer2 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput2>(clientEnt);
                var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput2>(serverEnt);
                Assert.AreEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
                Assert.AreEqual(0, serverBuffer2.Length);
                Assert.AreNotEqual(0, clientBuffer2.Length);

                testWorld.ServerWorld.EntityManager.SetComponentData(serverConnectionEnt, new CommandTargetComponent{targetEntity = serverEnt});
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientConnectionEnt, new CommandTargetComponent{targetEntity = clientEnt});

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(deltaTime);

                clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                clientBuffer2 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput2>(clientEnt);
                serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput2>(serverEnt);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
                Assert.AreNotEqual(0, serverBuffer2.Length);
                Assert.AreNotEqual(0, clientBuffer2.Length);

                var serverTick = testWorld.ServerWorld.GetExistingSystem<ServerSimulationSystemGroup>().ServerTick;
                Assert.IsTrue(serverBuffer.GetDataAtTick(serverTick, out var sin1));
                Assert.IsTrue(serverBuffer2.GetDataAtTick(serverTick, out var sin2));
                Assert.AreEqual(1, sin1.Value);
                Assert.AreEqual(2, sin2.Value);
            }
        }
        [Test]
        public void AutoCommandTargetSendsDataForSingleBuffer()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = GhostAuthoringComponent.GhostMode.OwnerPredicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 4));
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkIdComponent>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
            }
        }
        [Test]
        public void AutoCommandTargetSendsDataForMultipleBuffers()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = GhostAuthoringComponent.GhostMode.OwnerPredicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 4));
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkIdComponent>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);
                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput2>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput2>(clientEnt);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                var clientBuffer2 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput2>(clientEnt);
                var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput2>(serverEnt);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
                Assert.AreNotEqual(0, serverBuffer2.Length);
                Assert.AreNotEqual(0, clientBuffer2.Length);

                var serverTick = testWorld.ServerWorld.GetExistingSystem<ServerSimulationSystemGroup>().ServerTick;
                Assert.IsTrue(serverBuffer.GetDataAtTick(serverTick, out var sin1));
                Assert.IsTrue(serverBuffer2.GetDataAtTick(serverTick, out var sin2));
                Assert.AreEqual(1, sin1.Value);
                Assert.AreEqual(2, sin2.Value);
            }
        }
        [Test]
        public void MultipleAutoCommandTargetSendsData()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = GhostAuthoringComponent.GhostMode.OwnerPredicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 4));
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkIdComponent>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                var serverEnt2 = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent {NetworkId = netId});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt2, new GhostOwnerComponent {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostOwnerComponent));
                var clientEnts = query.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(2, clientEnts.Length);
                Assert.AreNotEqual(Entity.Null, clientEnts[0]);
                Assert.AreNotEqual(Entity.Null, clientEnts[1]);
                if (testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostComponent>(clientEnts[0]).ghostId != testWorld.ServerWorld.EntityManager.GetComponentData<GhostComponent>(serverEnt).ghostId)
                {
                    // swap 0 and 1
                    var temp = clientEnts[0];
                    clientEnts[0] = clientEnts[1];
                    clientEnts[1] = temp;
                }

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnts[0]);
                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput2>(serverEnt2);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput2>(clientEnts[1]);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnts[0]);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                var clientBuffer2 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput2>(clientEnts[1]);
                var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput2>(serverEnt2);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
                Assert.AreNotEqual(0, serverBuffer2.Length);
                Assert.AreNotEqual(0, clientBuffer2.Length);

                var serverTick = testWorld.ServerWorld.GetExistingSystem<ServerSimulationSystemGroup>().ServerTick;
                Assert.IsTrue(serverBuffer.GetDataAtTick(serverTick, out var sin1));
                Assert.IsTrue(serverBuffer2.GetDataAtTick(serverTick, out var sin2));
                Assert.AreEqual(1, sin1.Value);
                Assert.AreEqual(2, sin2.Value);
            }
        }
        [Test]
        public void ConnectionCommandTargetAndAutoCommandTargetSendsDataAtTheSameTime()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = GhostAuthoringComponent.GhostMode.OwnerPredicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 4));
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkIdComponent>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent {NetworkId = netId});
                var serverEnt2 = testWorld.ServerWorld.EntityManager.CreateEntity();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                var clientEnt2 = testWorld.ClientWorlds[0].EntityManager.CreateEntity();

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);
                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput2>(serverEnt2);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput2>(clientEnt2);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverConnectionEnt, new CommandTargetComponent{targetEntity = serverEnt2});
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientConnectionEnt, new CommandTargetComponent{targetEntity = clientEnt2});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                var clientBuffer2 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput2>(clientEnt2);
                var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput2>(serverEnt2);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
                Assert.AreNotEqual(0, serverBuffer2.Length);
                Assert.AreNotEqual(0, clientBuffer2.Length);

                var serverTick = testWorld.ServerWorld.GetExistingSystem<ServerSimulationSystemGroup>().ServerTick;
                Assert.IsTrue(serverBuffer.GetDataAtTick(serverTick, out var sin1));
                Assert.IsTrue(serverBuffer2.GetDataAtTick(serverTick, out var sin2));
                Assert.AreEqual(1, sin1.Value);
                Assert.AreEqual(2, sin2.Value);
            }
        }
        [Test]
        public void AutoCommandTargetDoesNotSendWhenDisabled()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = GhostAuthoringComponent.GhostMode.OwnerPredicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 4));
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkIdComponent>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent {NetworkId = netId});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new AutoCommandTarget {Enabled = false});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
            }
        }
        [Test]
        public void AutoCommandTargetDoesNotSendWhenNotPredicted()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = GhostAuthoringComponent.GhostMode.Interpolated;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 4));
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkIdComponent>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
            }
        }
        [Test]
        public void AutoCommandTargetDoesNotSendWhenNotOwned()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = GhostAuthoringComponent.GhostMode.Predicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 4));
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkIdComponent>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent {NetworkId = netId + 1});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
            }
        }
    }
}

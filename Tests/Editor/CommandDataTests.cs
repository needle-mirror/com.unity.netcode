#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal struct CommandDataTestsTickInput : ICommandData
    {
        public NetworkTick Tick { get; set; }
        public int Value;
    }
    internal struct CommandDataTestsTickInput2 : ICommandData
    {
        public NetworkTick Tick { get; set; }
        public int Value;
    }
    internal struct CommandDataTestsTickInputDouble : ICommandData
    {
        public NetworkTick Tick { get; set; }
        public double Value;
    }
    internal struct CommandDataTestsTickInputLarge : ICommandData
    {
        public NetworkTick Tick { get; set; }

        public FixedString128Bytes Value;
        public FixedString128Bytes Value1;
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class CommandDataTestsTickInputSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamInGame>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadWrite<CommandDataTestsTickInput>()));
        }
        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<NetworkTime>().InputTargetTick;
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
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class CommandDataTestsTickInput2System : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamInGame>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadWrite<CommandDataTestsTickInput2>()));
        }
        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<NetworkTime>().InputTargetTick;
            Entities.ForEach((DynamicBuffer<CommandDataTestsTickInput2> inputBuffer) => {
                inputBuffer.AddCommandData(new CommandDataTestsTickInput2
                {
                    Tick = tick,
                    Value = 2
                });
            }).Run();
        }
    }
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class CommandDataTestsTickInputLargeSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamInGame>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadWrite<CommandDataTestsTickInputLarge>()));
        }
        protected override void OnUpdate()
        {
            FixedString128Bytes longString = "";

            for (int i = 0; i < FixedString128Bytes.UTF8MaxLengthInBytes; ++i)
            {
                if ( i == FixedString128Bytes.UTF8MaxLengthInBytes - 1)
                {
                    longString += "\0";
                }
                else
                {
                    longString += "a";
                }
            }
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            Entities.ForEach((DynamicBuffer<CommandDataTestsTickInputLarge> inputBuffer) => {
                inputBuffer.AddCommandData(new CommandDataTestsTickInputLarge
                {
                    Tick = tick,
                    Value = longString,
                    Value1 = longString
                });
            }).Run();
        }
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class CommandDataTestsTickInputIncreaseSystem : SystemBase
    {
        public double m_Value = 0;
        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamInGame>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadWrite<CommandDataTestsTickInputDouble>()));
        }
        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            foreach (var inputBuffer in SystemAPI.Query<DynamicBuffer<CommandDataTestsTickInputDouble>>())
            {
                m_Value += 3.14159;
                inputBuffer.AddCommandData(new CommandDataTestsTickInputDouble
                {
                    Tick = tick,
                    Value = m_Value
                });
            }
        }
    }

    internal class CommandDataTests
    {
        [Test]
        public void MissingCommandTargetUpdatesAckAndCommandAge([Values]GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverMaxMessageSize = 120;
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = false;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var serverAck = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkSnapshotAck>(serverConnectionEnt);
                var clientAck = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkSnapshotAck>(clientConnectionEnt);

                Assert.Less(testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick.TicksSince(serverAck.LastReceivedSnapshotByRemote), 4);
                var driverType = testWorld.GetSingleton<NetworkStreamDriver>(testWorld.ClientWorlds[0]).DriverStore.GetDriverType(NetworkDriverStore.FirstDriverId);
                if (driverType == TransportType.Socket)
                {
                    Assert.Less(clientAck.ServerCommandAge / 256.0f, -1.6f);
                    Assert.Greater(clientAck.ServerCommandAge / 256.0f, -2.6f);
                }
                else
                {
                    Assert.Less(clientAck.ServerCommandAge / 256.0f, -.25f);
                    Assert.Greater(clientAck.ServerCommandAge / 256.0f, -0.75f);
                }
            }
        }
        [Test]
        public void ConnectionCommandTargetComponentSendsDataForSingleBuffer([Values]GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = false;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);

                testWorld.ServerWorld.EntityManager.SetComponentData(serverConnectionEnt, new CommandTarget{targetEntity = serverEnt});
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientConnectionEnt, new CommandTarget{targetEntity = clientEnt});

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
            }
        }
        [Test]
        public void ConnectionCommandTargetComponentSendsDataForMultipleBuffers([Values]GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = false;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);
                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput2>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput2>(clientEnt);

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                var clientBuffer2 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput2>(clientEnt);
                var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput2>(serverEnt);
                Assert.AreEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
                Assert.AreEqual(0, serverBuffer2.Length);
                Assert.AreNotEqual(0, clientBuffer2.Length);

                testWorld.ServerWorld.EntityManager.SetComponentData(serverConnectionEnt, new CommandTarget{targetEntity = serverEnt});
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientConnectionEnt, new CommandTarget{targetEntity = clientEnt});

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                clientBuffer2 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput2>(clientEnt);
                serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput2>(serverEnt);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
                Assert.AreNotEqual(0, serverBuffer2.Length);
                Assert.AreNotEqual(0, clientBuffer2.Length);

                var serverTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                Assert.IsTrue(serverBuffer.GetDataAtTick(serverTick, out var sin1));
                Assert.IsTrue(serverBuffer2.GetDataAtTick(serverTick, out var sin2));
                Assert.AreEqual(1, sin1.Value);
                Assert.AreEqual(2, sin2.Value);
            }
        }
        [Test]
        public void AutoCommandTargetSendsDataForSingleBuffer([Values]GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = ghostMode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
            }
        }
        [Test]
        public void AutoCommandTargetSendsDataForMultipleBuffers([Values]GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = ghostMode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);
                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput2>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput2>(clientEnt);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                var clientBuffer2 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput2>(clientEnt);
                var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput2>(serverEnt);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
                Assert.AreNotEqual(0, serverBuffer2.Length);
                Assert.AreNotEqual(0, clientBuffer2.Length);

                var serverTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                Assert.IsTrue(serverBuffer.GetDataAtTick(serverTick, out var sin1));
                Assert.IsTrue(serverBuffer2.GetDataAtTick(serverTick, out var sin2));
                Assert.AreEqual(1, sin1.Value);
                Assert.AreEqual(2, sin2.Value);
            }
        }
        [Test]
        public void MultipleAutoCommandTargetSendsData([Values]GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = ghostMode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                var serverEnt2 = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt2, new GhostOwner {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostOwner));
                var clientEnts = query.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(2, clientEnts.Length);
                Assert.AreNotEqual(Entity.Null, clientEnts[0]);
                Assert.AreNotEqual(Entity.Null, clientEnts[1]);
                if (testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(clientEnts[0]).ghostId != testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt).ghostId)
                {
                    // swap 0 and 1
                    (clientEnts[0], clientEnts[1]) = (clientEnts[1], clientEnts[0]);
                }

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnts[0]);
                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput2>(serverEnt2);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput2>(clientEnts[1]);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnts[0]);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                var clientBuffer2 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput2>(clientEnts[1]);
                var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput2>(serverEnt2);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
                Assert.AreNotEqual(0, serverBuffer2.Length);
                Assert.AreNotEqual(0, clientBuffer2.Length);

                var serverTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                Assert.IsTrue(serverBuffer.GetDataAtTick(serverTick, out var sin1));
                Assert.IsTrue(serverBuffer2.GetDataAtTick(serverTick, out var sin2));
                Assert.AreEqual(1, sin1.Value);
                Assert.AreEqual(2, sin2.Value);
            }
        }
        [Test]
        public void ConnectionCommandTargetAndAutoCommandTargetSendsDataAtTheSameTime([Values]GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = ghostMode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId});
                var serverEnt2 = testWorld.ServerWorld.EntityManager.CreateEntity();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                var clientEnt2 = testWorld.ClientWorlds[0].EntityManager.CreateEntity();

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);
                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput2>(serverEnt2);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput2>(clientEnt2);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverConnectionEnt, new CommandTarget{targetEntity = serverEnt2});
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientConnectionEnt, new CommandTarget{targetEntity = clientEnt2});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                var clientBuffer2 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput2>(clientEnt2);
                var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput2>(serverEnt2);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
                Assert.AreNotEqual(0, serverBuffer2.Length);
                Assert.AreNotEqual(0, clientBuffer2.Length);

                var serverTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                Assert.IsTrue(serverBuffer.GetDataAtTick(serverTick, out var sin1));
                Assert.IsTrue(serverBuffer2.GetDataAtTick(serverTick, out var sin2));
                Assert.AreEqual(1, sin1.Value);
                Assert.AreEqual(2, sin2.Value);
            }
        }
        [Test]
        public void AutoCommandTargetDoesNotSendWhenDisabled([Values]GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = ghostMode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new AutoCommandTarget {Enabled = false});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
            }
        }
        [Test]
        public void AutoCommandTargetDoesNotSendWhenNotOwned([Values]GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = ghostMode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId + 1});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnt);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnt);
                Assert.AreEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
            }
        }

        [Test]
        public void CommandDataSendsWhenLargerThanMaxMessageSize([Values] GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverMaxMessageSize = 548;

                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputLargeSystem));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = ghostMode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = netId });

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInputLarge>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInputLarge>(clientEnt);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<CommandDataTestsTickInputLarge>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInputLarge>(serverEnt);
                Assert.AreNotEqual(0, serverBuffer.Length);
                Assert.AreNotEqual(0, clientBuffer.Length);
            }
        }

        [Test(Description = "Ensures that when a client sends a partial command, the server will update the command data with the new value")]
        public void ServerOverridesUpdatedCommandData()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputIncreaseSystem));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId});

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInputDouble>(serverEnt);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandDataTestsTickInputDouble>(clientEnt);

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // Ensure we have control over time, this is needed otherwise the NetworkTimeSystem will slow down the client
                var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
                clientTickRate.PredictionTimeScaleMin = 0.999f;
                clientTickRate.PredictionTimeScaleMax = 1.001f;
                testWorld.ClientWorlds[0].EntityManager.CreateSingleton(clientTickRate);

                // Tick so we're on a full tick on the client
                var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                testWorld.TickClientWorld((1 - clientTime.ServerTickFraction) / 60f);
                clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                Assert.IsFalse(clientTime.IsPartialTick);

                // Tick half a tick
                // This will send the command to the server
                testWorld.TickClientWorld(1/120f);
                clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                testWorld.GetSingletonBuffer<CommandDataTestsTickInputDouble>(testWorld.ClientWorlds[0]).GetDataAtTick(clientTime.ServerTick, out var clientFirstSendCommand);

                // Tick half a tick
                // This will not send a command, but will update the value for the tick
                testWorld.TickClientWorld(1/120f);
                // Will be same ServerTick
                testWorld.GetSingletonBuffer<CommandDataTestsTickInputDouble>(testWorld.ClientWorlds[0]).GetDataAtTick(clientTime.ServerTick, out var clientUpdatedCommand);
                Assert.AreNotEqual(clientFirstSendCommand, clientUpdatedCommand);

                // Tick so the server receives the command, and the client sends the next command with the updated value
                testWorld.Tick();
                testWorld.GetSingletonBuffer<CommandDataTestsTickInputDouble>(testWorld.ServerWorld).GetDataAtTick(clientTime.ServerTick, out var serverFirstCommand); // Use client ServerTick since it's the same tick we want
                Assert.AreEqual(clientFirstSendCommand.Tick, serverFirstCommand.Tick); // Ensure the tick is correct since GetDataAtTick will return the closest tick
                Assert.AreEqual(clientFirstSendCommand.Value, serverFirstCommand.Value);

                // Tick so the server receives the updated command
                testWorld.Tick();
                testWorld.GetSingletonBuffer<CommandDataTestsTickInputDouble>(testWorld.ServerWorld).GetDataAtTick(clientTime.ServerTick, out var serverUpdatedCommand); // Use client ServerTick since it's the same tick we want
                Assert.AreEqual(clientUpdatedCommand.Tick, serverUpdatedCommand.Tick); // Ensure the tick is correct since GetDataAtTick will return the closest tick
                Assert.AreEqual(clientUpdatedCommand.Value, serverUpdatedCommand.Value);
            }
        }
    }
}

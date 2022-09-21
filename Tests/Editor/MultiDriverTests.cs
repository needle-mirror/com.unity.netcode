using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    public class MultiDriverTests
    {
        [Test]
        public void ConnectWithMultipleInterfaces()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CheckConnectionSystem));
                testWorld.UseMultipleDrivers = 1;
                testWorld.CreateWorlds(true, 2);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                foreach (var world in testWorld.ClientWorlds)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(world).ValueRW.Connect(world.EntityManager, ep);

                var serverDriverStore = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkStreamDriver>(testWorld.TryGetSingletonEntity<NetworkStreamDriver>(testWorld.ServerWorld)).DriverStore;
                var client0DriverStore = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkStreamDriver>(testWorld.TryGetSingletonEntity<NetworkStreamDriver>(testWorld.ClientWorlds[0])).DriverStore;
                var client1DriverStore = testWorld.ClientWorlds[1].EntityManager.GetComponentData<NetworkStreamDriver>(testWorld.TryGetSingletonEntity<NetworkStreamDriver>(testWorld.ClientWorlds[1])).DriverStore;
                Assert.AreEqual(2, serverDriverStore.DriversCount);
                Assert.AreEqual(TransportType.IPC, serverDriverStore.GetDriverType(NetworkDriverStore.FirstDriverId));
                Assert.AreEqual(TransportType.Socket, serverDriverStore.GetDriverType(NetworkDriverStore.FirstDriverId+1));

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(16f/1000f);

                Assert.AreEqual(2, testWorld.ServerWorld.GetExistingSystemManaged<CheckConnectionSystem>().numConnected);
                foreach (var world in testWorld.ClientWorlds)
                    Assert.AreEqual(1, world.GetExistingSystemManaged<CheckConnectionSystem>().numConnected);

                testWorld.GoInGame();
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(16f/1000f);

                Assert.AreEqual(2, testWorld.ServerWorld.GetExistingSystemManaged<CheckConnectionSystem>().numConnected);
                Assert.AreEqual(2, testWorld.ServerWorld.GetExistingSystemManaged<CheckConnectionSystem>().numInGame);
                foreach (var world in testWorld.ClientWorlds)
                {
                    Assert.AreEqual(1, world.GetExistingSystemManaged<CheckConnectionSystem>().numConnected);
                    Assert.AreEqual(1, world.GetExistingSystemManaged<CheckConnectionSystem>().numInGame);
                }
            }
        }

        [Test]
        public void RpcAreSentAndReceiveByAllClients()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(ServerRpcBroadcastSendSystem),
                    typeof(ClientRcpSendSystem),
                    typeof(ServerRpcReceiveSystem),
                    typeof(ClientRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.UseMultipleDrivers = 1;
                testWorld.CreateWorlds(true, 2);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                foreach (var world in testWorld.ClientWorlds)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(world).ValueRW.Connect(world.EntityManager, ep);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(16f/1000f);

                ServerRpcReceiveSystem.ReceivedCount = 0;
                ServerRpcBroadcastSendSystem.SendCount = 5;
                ClientRcpSendSystem.SendCount = 5 * testWorld.ClientWorlds.Length;

                testWorld.GoInGame();
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick(16f/1000f);

                Assert.AreEqual(5 * testWorld.ClientWorlds.Length, ServerRpcReceiveSystem.ReceivedCount);
                foreach (var world in testWorld.ClientWorlds)
                    Assert.AreEqual(5, world.GetExistingSystemManaged<ClientRpcReceiveSystem>().ReceivedCount);
            }
        }

        [Test]
        public void CommandsFromAllClientsAreReceived()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CommandDataTestsTickInputSystem), typeof(CommandDataTestsTickInput2System));

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.UseMultipleDrivers = 1;
                testWorld.CreateWorlds(true, 2);
                Assert.IsTrue(testWorld.Connect(1f/60f, 8));
                testWorld.GoInGame();

                var clientConnectionEnt = new[]
                {
                    testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[0]),
                    testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[1])
                };
                var serverEnts = new Entity[2];
                var clientEnts = new Entity[2];
                for (int i = 0; i < 2; ++i)
                {
                    serverEnts[i] = testWorld.SpawnOnServer(0);
                    testWorld.ServerWorld.EntityManager.AddComponent<CommandDataTestsTickInput>(serverEnts[i]);
                    var netId = testWorld.ClientWorlds[i].EntityManager.GetComponentData<NetworkIdComponent>(clientConnectionEnt[i]);
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnts[i], new GhostOwnerComponent {NetworkId = netId.Value});
                }
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(1f/60f);

                for (int i = 0; i < 2; ++i)
                {
                    var ghostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ClientWorlds[i]);
                    var ghostEntityMap = testWorld.ClientWorlds[i].EntityManager.GetComponentData<SpawnedGhostEntityMap>(ghostMapSingleton).ClientGhostEntityMap;
                    var ghostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostComponent>(serverEnts[i]).ghostId;
                    Assert.AreEqual(2, ghostEntityMap.Count());
                    clientEnts[i] = ghostEntityMap[ghostId];
                    Assert.AreNotEqual(Entity.Null, clientEnts[i]);
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<CommandDataTestsTickInput>(clientEnts[i]);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(1f/60f);

                for (int i = 0; i < 2; ++i)
                {
                    var clientBuffer = testWorld.ClientWorlds[i].EntityManager.GetBuffer<CommandDataTestsTickInput>(clientEnts[i]);
                    var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<CommandDataTestsTickInput>(serverEnts[i]);
                    Assert.AreNotEqual(0, serverBuffer.Length, $"client {i}");
                    Assert.AreNotEqual(0, clientBuffer.Length, $"client {i}");
                }
            }
        }
    }
}

using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class CheckConnectionSystem : SystemBase
    {
        public static int IsConnected;
        protected override void OnUpdate()
        {
            if (HasSingleton<NetworkStreamConnection>())
            {
                if (World.GetExistingSystem<ServerSimulationSystemGroup>() != null)
                    IsConnected |= 2;
                else
                    IsConnected |= 1;
            }
        }
    }
    public class ConnectionTests
    {
        [Test]
        public void ConnectSingleClient()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CheckConnectionSystem));
                testWorld.CreateWorlds(true, 1);

                CheckConnectionSystem.IsConnected = 0;

                var ep = NetworkEndPoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.ServerWorld.GetExistingSystem<NetworkStreamReceiveSystem>().Listen(ep);
                testWorld.ClientWorlds[0].GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);

                for (int i = 0; i < 16 && CheckConnectionSystem.IsConnected != 3; ++i)
                    testWorld.Tick(16f/1000f);

                Assert.AreEqual(3, CheckConnectionSystem.IsConnected);
            }
        }
    }

    public class VersionTests
    {
        [Test]
        public void SameVersion_ConnectSuccessfully()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                var serverVersion = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(NetworkProtocolVersion));
                testWorld.ServerWorld.EntityManager.SetComponentData(serverVersion, new NetworkProtocolVersion
                {
                    NetCodeVersion = 1,
                    GameVersion = 0,
                    RpcCollectionVersion = 1,
                    ComponentCollectionVersion = 1
                });
                var clientVersion = testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(NetworkProtocolVersion));
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientVersion, new NetworkProtocolVersion
                {
                    NetCodeVersion = 1,
                    GameVersion = 0,
                    RpcCollectionVersion = 1,
                    ComponentCollectionVersion = 1
                });

                var ep = NetworkEndPoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.ServerWorld.GetExistingSystem<NetworkStreamReceiveSystem>().Listen(ep);
                testWorld.ClientWorlds[0].GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(16f/1000f);

                var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(1, query.CalculateEntityCount());
            }
        }

        [Test]
        public void DifferentVersions_AreDisconnnected()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                var serverVersion = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(NetworkProtocolVersion));
                testWorld.ServerWorld.EntityManager.SetComponentData(serverVersion, new NetworkProtocolVersion
                {
                    NetCodeVersion = 1,
                    GameVersion = 0,
                    RpcCollectionVersion = 1,
                    ComponentCollectionVersion = 1
                });
                var ep = NetworkEndPoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.ServerWorld.GetExistingSystem<NetworkStreamReceiveSystem>().Listen(ep);

                //Different NetCodeVersion
                var clientVersion = testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(NetworkProtocolVersion));
                var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientVersion, new NetworkProtocolVersion
                {
                    NetCodeVersion = 2,
                    GameVersion = 0,
                    RpcCollectionVersion = 1,
                    ComponentCollectionVersion = 1
                });
                testWorld.ClientWorlds[0].GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);

                //one per client and server
                LogAssert.Expect(LogType.Error, "RpcSystem received bad protocol version from connection 0");
                LogAssert.Expect(LogType.Error, "RpcSystem received bad protocol version from connection 0");
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(16f/1000f);

                Assert.AreEqual(0, query.CalculateEntityCount());
                //Different GameVersion
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientVersion, new NetworkProtocolVersion
                {
                    NetCodeVersion = 1,
                    GameVersion = 1,
                    RpcCollectionVersion = 1,
                    ComponentCollectionVersion = 1
                });
                testWorld.ClientWorlds[0].GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);

                //one per client and server
                LogAssert.Expect(LogType.Error, "RpcSystem received bad protocol version from connection 0");
                LogAssert.Expect(LogType.Error, "RpcSystem received bad protocol version from connection 0");
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(16f/1000f);

                Assert.AreEqual(0, query.CalculateEntityCount());
                //Different Rpcs
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientVersion, new NetworkProtocolVersion
                {
                    NetCodeVersion = 1,
                    GameVersion = 0,
                    RpcCollectionVersion = 2,
                    ComponentCollectionVersion = 1
                });
                testWorld.ClientWorlds[0].GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);

                //one per client and server
                LogAssert.Expect(LogType.Error, "RpcSystem received bad protocol version from connection 0");
                LogAssert.Expect(LogType.Error, "RpcSystem received bad protocol version from connection 0");
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(16f/1000f);

                Assert.AreEqual(0, query.CalculateEntityCount());

                //Different Ghost
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientVersion, new NetworkProtocolVersion
                {
                    NetCodeVersion = 1,
                    GameVersion = 0,
                    RpcCollectionVersion = 1,
                    ComponentCollectionVersion = 2
                });
                testWorld.ClientWorlds[0].GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);

                //one per client and server
                LogAssert.Expect(LogType.Error, "RpcSystem received bad protocol version from connection 0");
                LogAssert.Expect(LogType.Error, "RpcSystem received bad protocol version from connection 0");
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(16f/1000f);

                Assert.AreEqual(0, query.CalculateEntityCount());
            }
        }
        public class TestConverter : TestNetCodeAuthoring.IConverter
        {
            public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
            {
                dstManager.AddComponentData(entity, new GhostOwnerComponent());
                dstManager.AddComponentData(entity, new GhostGenTestTypeFlat());
            }
        }
        [Test]
        public void GhostCollectionGenerateSameHashOnClientAndServer()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghost1 = new GameObject();
                ghost1.AddComponent<TestNetCodeAuthoring>().Converter = new TestConverter();
                ghost1.AddComponent<GhostAuthoringComponent>().DefaultGhostMode = GhostAuthoringComponent.GhostMode.Predicted;
                var ghost2 = new GameObject();
                ghost2.AddComponent<TestNetCodeAuthoring>().Converter = new TestConverter();
                ghost2.AddComponent<GhostAuthoringComponent>().DefaultGhostMode = GhostAuthoringComponent.GhostMode.Interpolated;

                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection(ghost1, ghost2);

                testWorld.CreateWorlds(true, 1);
                float frameTime = 1.0f / 60.0f;
                var serverCollectionSystem = testWorld.ServerWorld.GetExistingSystem<GhostCollectionSystem>();
                var clientCollectionSystem = testWorld.ClientWorlds[0].GetExistingSystem<GhostCollectionSystem>();
                //First tick: compute on both client and server the ghost collection hash
                testWorld.Tick(frameTime);
                Assert.AreEqual(serverCollectionSystem.CalculateComponentCollectionHash(), clientCollectionSystem.CalculateComponentCollectionHash());

                // compare the list of loaded prefabs
                var serverCollectionSingleton = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var clientCollectionSingleton = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, serverCollectionSingleton);
                Assert.AreNotEqual(Entity.Null, clientCollectionSingleton);
                var serverCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefab>(serverCollectionSingleton);
                var clientCollection = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostCollectionPrefab>(clientCollectionSingleton);
                Assert.AreEqual(serverCollection.Length, clientCollection.Length);
                for (int i = 0; i < serverCollection.Length; ++i)
                {
                    Assert.AreEqual(serverCollection[i].GhostType, clientCollection[i].GhostType);
                    Assert.AreEqual(serverCollection[i].Hash, clientCollection[i].Hash);
                }

                //Check that and server can connect (same component hash)
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                testWorld.GoInGame();
                for(int i=0;i<10;++i)
                    testWorld.Tick(frameTime);

                Assert.IsTrue(testWorld.ClientWorlds[0].GetExistingSystem<NetworkStreamReceiveSystem>()
                    .HasSingleton<NetworkIdComponent>());
            }
        }
    }
}
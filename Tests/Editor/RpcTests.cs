using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Collections;

namespace Unity.NetCode.Tests
{
    public class RpcTests
    {
        [Test]
        public void Rpc_UsingBroadcastOnClient_Works()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(ClientRcpSendSystem),
                    typeof(ServerRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1);

                int SendCount = 10;
                ClientRcpSendSystem.SendCount = SendCount;
                ServerRpcReceiveSystem.ReceivedCount = 0;

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                for (int i = 0; i < 33; ++i)
                    testWorld.Tick(16f / 1000f);

                Assert.AreEqual(SendCount, ServerRpcReceiveSystem.ReceivedCount);
            }
        }

        [Test]
        public void Rpc_UsingConnectionEntityOnClient_Works()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(ClientRcpSendSystem),
                    typeof(ServerRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1);

                int SendCount = 10;
                ClientRcpSendSystem.SendCount = SendCount;
                ServerRpcReceiveSystem.ReceivedCount = 0;

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                var remote = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ClientWorlds[0]);
                testWorld.ClientWorlds[0].GetExistingSystem<ClientRcpSendSystem>().Remote = remote;

                for (int i = 0; i < 33; ++i)
                    testWorld.Tick(16f / 1000f);

                Assert.AreEqual(SendCount, ServerRpcReceiveSystem.ReceivedCount);
            }
        }

        [Test]
        public void Rpc_SerializedRpcFlow_Works()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedClientRcpSendSystem),
                    typeof(SerializedServerRpcReceiveSystem),
                    typeof(SerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1);

                int SendCount = 1;
                var SendCmd = new SerializedRpcCommand
                    {intValue = 123456, shortValue = 32154, floatValue = 12345.67f};
                SerializedClientRcpSendSystem.SendCount = SendCount;
                SerializedClientRcpSendSystem.Cmd = SendCmd;

                SerializedServerRpcReceiveSystem.ReceivedCount = 0;

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                for (int i = 0; i < 33; ++i)
                    testWorld.Tick(16f / 1000f);

                Assert.AreEqual(SendCount, SerializedServerRpcReceiveSystem.ReceivedCount);
                Assert.AreEqual(SendCmd, SerializedServerRpcReceiveSystem.ReceivedCmd);
            }
        }

        [Test]
        public void Rpc_ServerBroadcast_Works()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(ServerRpcBroadcastSendSystem),
                    typeof(MultipleClientBroadcastRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 2);

                ServerRpcBroadcastSendSystem.SendCount = 0;
                MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[0] = 0;
                MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[1] = 0;

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                int SendCount = 5;
                ServerRpcBroadcastSendSystem.SendCount = SendCount;

                for (int i = 0; i < 33; ++i)
                    testWorld.Tick(16f / 1000f);

                Assert.AreEqual(SendCount, MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[0]);
                Assert.AreEqual(SendCount, MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[1]);
            }
        }

        [Test]
        public void Rpc_SendingBeforeGettingNetworkId_Throws()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(FlawedClientRcpSendSystem),
                    typeof(ServerRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1);

                int SendCount = 1;
                ServerRpcReceiveSystem.ReceivedCount = 0;
                FlawedClientRcpSendSystem.SendCount = SendCount;

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: Cannot send RPC with no remote connection."));
                for (int i = 0; i < 33; ++i)
                    testWorld.Tick(16f / 1000f);

                Assert.AreEqual(0, ServerRpcReceiveSystem.ReceivedCount);
            }
        }

        [Test]
        public void Rpc_LateCreationOfSystem_Throws()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                Assert.Throws<InvalidOperationException>(()=>{testWorld.ServerWorld.GetOrCreateSystem(typeof(NonSerializedRpcCommandRequestSystem));});
                Assert.Throws<InvalidOperationException>(()=>{testWorld.ClientWorlds[0].GetOrCreateSystem(typeof(NonSerializedRpcCommandRequestSystem));});
            }
        }

        [Test]
        public void Rpc_MalformedPackets_ThrowsAndLogError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverRandomSeed = 0xbadc0de;
                testWorld.DriverFuzzOffset = 1;
                testWorld.DriverFuzzFactor = new int[2];
                testWorld.DriverFuzzFactor[0] = 10;
                testWorld.Bootstrap(true,
                    typeof(MalformedClientRcpSendSystem),
                    typeof(ServerMultipleRpcReceiveSystem),
                    typeof(MultipleClientSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 2);

                int SendCount = 15;
                MalformedClientRcpSendSystem.SendCount[0] = SendCount;
                MalformedClientRcpSendSystem.SendCount[1] = SendCount;

                MalformedClientRcpSendSystem.Cmds[0] = new ClientIdRpcCommand {Id = 0};
                MalformedClientRcpSendSystem.Cmds[1] = new ClientIdRpcCommand {Id = 1};

                ServerMultipleRpcReceiveSystem.ReceivedCount[0] = 0;
                ServerMultipleRpcReceiveSystem.ReceivedCount[1] = 0;

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                LogAssert.Expect(LogType.Error, new Regex("RpcSystem received invalid rpc from connection 1"));
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(16f / 1000f);

                Assert.Less(ServerMultipleRpcReceiveSystem.ReceivedCount[0], SendCount);
                Assert.True(ServerMultipleRpcReceiveSystem.ReceivedCount[1] == SendCount);
            }
        }

        [Test]
        [Ignore("changes in burst 1.3 made this test fail now. The FunctionPointers are are always initialized now")]
        public void Rpc_ClientRegisterRpcCommandWithNullFunctionPtr_Throws()
        {

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(InvalidRpcCommandRequestSystem));
                Assert.Throws<InvalidOperationException>(()=>{testWorld.CreateWorlds(false, 1);});
                Assert.Throws<InvalidOperationException>(()=>{testWorld.CreateWorlds(true, 1);});
            }
        }

        [Test]
        public void Rpc_CanSendMoreThanOnePacketPerFrame()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedClientLargeRcpSendSystem),
                    typeof(SerializedServerLargeRpcReceiveSystem),
                    typeof(SerializedLargeRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1);

                int SendCount = 50;
                var SendCmd = new SerializedLargeRpcCommand
                    {stringValue = new FixedString512("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")};
                SerializedClientLargeRcpSendSystem.SendCount = SendCount;
                SerializedClientLargeRcpSendSystem.Cmd = SendCmd;

                SerializedServerLargeRpcReceiveSystem.ReceivedCount = 0;

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                for (int i = 0; i < 33; ++i)
                    testWorld.Tick(16f / 1000f);

                Assert.AreEqual(SendCount, SerializedServerLargeRpcReceiveSystem.ReceivedCount);
                Assert.AreEqual(SendCmd, SerializedServerLargeRpcReceiveSystem.ReceivedCmd);
            }
        }


        public class GhostConverter : TestNetCodeAuthoring.IConverter
        {
            public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
            {
                dstManager.AddComponentData(entity, new GhostOwnerComponent());
            }
        }

        [Test]
        public void Rpc_CanSendEntityFromClientAndServer()
        {
            void SendRpc(World world, Entity entity)
            {
                var req = world.EntityManager.CreateEntity();
                world.EntityManager.AddComponentData(req, new RpcWithEntity {entity = entity});
                world.EntityManager.AddComponentData(req, new SendRpcCommandRequestComponent {TargetConnection = Entity.Null});
            }

            RpcWithEntity RecvRpc(World world)
            {
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<RpcWithEntity>());
                Assert.AreEqual(1, query.CalculateEntityCount());
                var rpcReceived = query.GetSingleton<RpcWithEntity>();
                world.EntityManager.DestroyEntity(query);
                return rpcReceived;
            }


            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(RpcWithEntityRpcCommandRequestSystem));
                var ghostGameObject = new GameObject("SimpleGhost");
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
                testWorld.CreateGhostCollection(ghostGameObject);
                testWorld.CreateWorlds(true, 1);

                float frameTime = 1.0f / 60.0f;
                Assert.IsTrue(testWorld.Connect(frameTime, 4));
                // Go in-game
                testWorld.GoInGame();

                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                //Wait some frame so it is spawned also on the client
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick(16f / 1000f);

                // Retrieve the client entity
                testWorld.ClientWorlds[0].GetExistingSystem<GhostSimulationSystemGroup>().LastGhostMapWriter.Complete();
                var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostComponent>(serverEntity);
                Assert.IsTrue(testWorld.ClientWorlds[0].GetExistingSystem<GhostSimulationSystemGroup>().SpawnedGhostEntityMap
                    .TryGetValue(new SpawnedGhost {ghostId = ghost.ghostId, spawnTick = ghost.spawnTick}, out var clientEntity));

                //Send the rpc to the server
                SendRpc(testWorld.ClientWorlds[0], clientEntity);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick(16f / 1000f);
                var rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity != Entity.Null);
                Assert.IsTrue(rpcReceived.entity == serverEntity);

                // Server send the rpc to the client
                SendRpc(testWorld.ServerWorld, serverEntity);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick(16f / 1000f);
                rpcReceived = RecvRpc(testWorld.ClientWorlds[0]);
                Assert.IsTrue(rpcReceived.entity != Entity.Null);
                Assert.IsTrue(rpcReceived.entity == clientEntity);

                // Client try to send a client-only entity -> result in a Entity.Null reference
                //Send the rpc to the server
                var clientOnlyEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntity();
                SendRpc(testWorld.ClientWorlds[0], clientOnlyEntity);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick(16f / 1000f);
                rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);

                // Some Edge cases:
                // 1 - Entity has been or going to be despawned on the client. Expected: server will receive an Entity.Null in the rpc
                // 2 - Entity has been despawn on the server but the client. Server will not be able to resolve the entity correctly
                //     in that window, since the ghost mapping is reset

                //Destroy the entity on the server
                testWorld.ServerWorld.EntityManager.DestroyEntity(serverEntity);
                //Let the client try to send an rpc for it (this mimic sort of latency)
                SendRpc(testWorld.ClientWorlds[0], clientEntity);
                //Entity is destroyed on the server (so no GhostComponent). If server try to send an rpc, the entity will be translated to null
                SendRpc(testWorld.ServerWorld, serverEntity);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(16f / 1000f);
                //Server should not be able to resolve the reference
                rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);
                //On the client must but null
                rpcReceived = RecvRpc(testWorld.ClientWorlds[0]);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);
                //If client send the rpc now (the entity should not exists anymore and the mapping should be reset on both client and server now)
                Assert.IsFalse(testWorld.ClientWorlds[0].GetExistingSystem<GhostSimulationSystemGroup>().SpawnedGhostEntityMap
                    .TryGetValue(new SpawnedGhost {ghostId = ghost.ghostId, spawnTick = ghost.spawnTick}, out var _));
                Assert.IsFalse(testWorld.ServerWorld.GetExistingSystem<GhostSimulationSystemGroup>().SpawnedGhostEntityMap
                    .TryGetValue(new SpawnedGhost {ghostId = ghost.ghostId, spawnTick = ghost.spawnTick}, out var _));
                SendRpc(testWorld.ClientWorlds[0], clientEntity);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(16f / 1000f);
                //The received entity must be null
                rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);
            }
        }
    }
}
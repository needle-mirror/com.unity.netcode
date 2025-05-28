using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Burst;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine.Scripting;

namespace Unity.NetCode.Tests
{
    internal class RpcTests
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

                // Connect and make sure the connection could be established
                testWorld.Connect();

                for (int i = 0; i < 12; ++i)
                    testWorld.Tick();

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

                // Connect and make sure the connection could be established
                testWorld.Connect();

                var remote = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ClientWorlds[0]);
                testWorld.ClientWorlds[0].GetExistingSystemManaged<ClientRcpSendSystem>().Remote = remote;

                for (int i = 0; i < 12; ++i)
                    testWorld.Tick();

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
                { intValue = 123456, shortValue = 32154, floatValue = 12345.67f };
                SerializedClientRcpSendSystem.SendCount = SendCount;
                SerializedClientRcpSendSystem.Cmd = SendCmd;

                SerializedServerRpcReceiveSystem.ReceivedCount = 0;

                // Connect and make sure the connection could be established
                testWorld.Connect();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                Assert.AreEqual(SendCount, SerializedServerRpcReceiveSystem.ReceivedCount);
                Assert.AreEqual(SendCmd, SerializedServerRpcReceiveSystem.ReceivedCmd);
            }
        }

        [Test]
        public void Rpc_ServerBroadcast_Works([Values(32, 64)] int windowSize)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverReliablePipelineWindowSize = windowSize;
                testWorld.Bootstrap(true,
                    typeof(ServerRpcBroadcastSendSystem),
                    typeof(MultipleClientBroadcastRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 2);

                ServerRpcBroadcastSendSystem.SendCount = 0;
                MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[0] = 0;
                MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[1] = 0;

                // Connect and make sure the connection could be established
                testWorld.Connect();

                int SendCount = 5;
                ServerRpcBroadcastSendSystem.SendCount = SendCount;

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                Assert.AreEqual(SendCount, MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[0]);
                Assert.AreEqual(SendCount, MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[1]);
            }
        }

        public static readonly SharedStatic<int> VariableSizedResultCnt = SharedStatic<int>.GetOrCreate<VariableSizedRpc>();

        /// <summary>Officially supported in 1.3.x.</summary>
        [Test]
        public void Rpc_VariableSizedCompression_Works([Values] bool useDynamicAssemblyList)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1, false);
                testWorld.SetDynamicAssemblyList(useDynamicAssemblyList);
                testWorld.GetSingletonRW<RpcCollection>(testWorld.ServerWorld).ValueRW.RegisterRpc<VariableSizedRpc, VariableSizedRpc>();
                testWorld.GetSingletonRW<RpcCollection>(testWorld.ClientWorlds[0]).ValueRW.RegisterRpc<VariableSizedRpc, VariableSizedRpc>();
                testWorld.Connect();

                // Send them without Entities:
                const int sendCount = 35;
                var rpcQueue = testWorld.GetSingletonRW<RpcCollection>(testWorld.ClientWorlds[0]).ValueRW.GetRpcQueue<VariableSizedRpc>();
                var outBuf = testWorld.GetSingletonBuffer<OutgoingRpcDataStreamBuffer>(testWorld.ClientWorlds[0]);
                VariableSizedResultCnt.Data = 0;
                for (int i = 0; i < sendCount; i++)
                {
                    rpcQueue.Schedule(outBuf, default, new VariableSizedRpc
                    {
                        Value1 = VariableSizedRpc.Value1Multiplier * i,
                        Value2 = VariableSizedRpc.Value2Multiplier * i,
                        Value3 = VariableSizedRpc.Value3Multiplier * i,
                    });
                }
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Assert happens inside the VariableSizedRpc.Execute.
                Assert.AreEqual(sendCount, VariableSizedResultCnt.Data);
            }
        }

        // TODO: With rpc changes with approval rpcs this no longer is the case
        // [Test]
        // public void Rpc_SendingBeforeGettingNetworkId_LogWarning()
        // {
        //     using (var testWorld = new NetCodeTestWorld())
        //     {
        //         testWorld.Bootstrap(true,
        //             typeof(FlawedClientRcpSendSystem),
        //             typeof(ServerRpcReceiveSystem),
        //             typeof(NonSerializedRpcCommandRequestSystem));
        //         testWorld.CreateWorlds(true, 1);
        //
        //         int SendCount = 1;
        //         ServerRpcReceiveSystem.ReceivedCount = 0;
        //         FlawedClientRcpSendSystem.SendCount = SendCount;
        //
        //         // Connect and make sure the connection could be established
        //         testWorld.Connect();
        //
        //         LogAssert.Expect(LogType.Warning, new Regex("Cannot send RPC with no remote connection."));
        //         for (int i = 0; i < 33; ++i)
        //             testWorld.Tick();
        //
        //         Assert.AreEqual(0, ServerRpcReceiveSystem.ReceivedCount);
        //     }
        // }


        [Test]
        [Ignore("Need significant package hardening to make guarantees about what happens when fuzzing packets!. Tracked as MTT-11334")]
        // TODO - Fuzzy test with ghosts + inputs too.
        // TODO - Fuzzy test gameplay sample to be reasonably sure we don't break the server and/or other clients.
        // TODO - Fuzzy test to ensure the bad client eventually gets DC'd.
        // TODO - Fuzzy test the server to ensure the client is also acceptably tolerant of issues.
        public void Rpc_MalformedPackets_ThrowsAndLogError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverRandomSeed = 0xbadc0de;
                testWorld.DriverFuzzOffset = 1; // TODO - Should be zero.
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
                MalformedClientRcpSendSystem.Cmds[0] = new ClientIdRpcCommand { Id = 0 };
                MalformedClientRcpSendSystem.Cmds[1] = new ClientIdRpcCommand { Id = 1 };

                ServerMultipleRpcReceiveSystem.ReceivedCount[0] = 0;
                ServerMultipleRpcReceiveSystem.ReceivedCount[1] = 0;

                // Note that packet fuzzing can have thousands of implications in our implementation:
                // - Error, warning, and trace logs.
                // - Exceptions in any serialization code dealing with ticks or sizes.
                // - No visible error at all.

                // E.g. After a recent change to the size of the RPC header,
                // this test silently succeeded, because it fuzzed the packet index to look like a different RPC
                // with an identical serialization layout. Thus, no serialization errors, but we did infer that we were
                // counting it incorrectly.
                LogAssert.ignoreFailingMessages = true;

                // Connect and make sure the connection could be established
                testWorld.Connect();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // TODO - Both of these checks are invalid, because we could have fuzzed the ClientIdRpcCommand.Id
                // from index 0 to 1 (or vice versa), leading to unexpected counts in both of these.
                // We may also throw (or worse) in these kinds of situations due to a lack of hardening.
                Debug.Log($"Received: [0]={ServerMultipleRpcReceiveSystem.ReceivedCount[0]}, [1]={ServerMultipleRpcReceiveSystem.ReceivedCount[0]}");
                //Assert.Less(ServerMultipleRpcReceiveSystem.ReceivedCount[0], SendCount);
                //Assert.AreEqual(SendCount, ServerMultipleRpcReceiveSystem.ReceivedCount[1]);
            }
        }

        [Test]
        public void Rpc_IndividualRpcTooLarge_ThrowsAndLogError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();

                var clientEm = testWorld.ClientWorlds[0].EntityManager;
                var entity = clientEm.CreateEntity(typeof(SerializedTooBigCommand), typeof(SendRpcCommandRequest));
                var serializedTooBigCommand = default(SerializedTooBigCommand);
                unsafe
                {
                    var ptr0 = (int*)&serializedTooBigCommand.bytes0;
                    for (int i = 0; i < sizeof(FixedBytes4094)/4; i++) ptr0[i] = i;
                    var ptr1 = (int*)&serializedTooBigCommand.bytes1;
                    for (int i = 0; i < sizeof(FixedBytes4094)/4; i++) ptr1[i] = i;
                    var ptr2 = (int*)&serializedTooBigCommand.bytes3;
                    for (int i = 0; i < sizeof(FixedBytes126)/4; i++) ptr2[i] = i;
                }
                clientEm.SetComponentData(entity, serializedTooBigCommand);

                LogAssert.Expect(LogType.Exception, new Regex("is too large to serialize into the RpcQueue!"));
                for (int i = 0; i < 24; ++i)
                    testWorld.Tick();
            }
        }

        [Test]
        public void Rpc_IndividualRpcIncorrectDeserialization_ThrowsAndLogError([Values] bool useDynamicAssemblyList, [Values] IncorrectDeserializationCommand.IncorrectMode incorrectDeserializationMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(IncorrectDeserializationCommandRequestSystem));
                testWorld.CreateWorlds(true, 1, false);
                testWorld.SetDynamicAssemblyList(useDynamicAssemblyList);
                testWorld.Connect();

                var clientEm = testWorld.ClientWorlds[0].EntityManager;
                var clientEntity = clientEm.CreateEntity();
                var command = new IncorrectDeserializationCommand
                {
                    bytes = 999,
                    mode = incorrectDeserializationMode,
                };
                Assert.IsTrue(clientEm.AddComponentData(clientEntity, command));
                Assert.IsTrue(clientEm.AddComponent<SendRpcCommandRequest>(clientEntity));

                if (incorrectDeserializationMode == IncorrectDeserializationCommand.IncorrectMode.DeserializeTooManyBytes)
                    LogAssert.Expect(LogType.Error, new Regex(@"Trying to read \d bytes from a stream where only \d are available"));
                LogAssert.Expect(LogType.Error, new Regex(@"\[ServerTest(.*)\](.*)RpcSystem failed to deserialize RPC(.*)as bits read(.*)did not match expected"));
                // Note: When failing to deserialize, the received RPC will still be created!
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ServerWorld), "Expected no connection left!");
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ClientWorlds[0]), "Expected no connection left!");
            }
        }

        [Test]
        public void Rpc_CanSendMoreThanOnePacketPerFrame([Values] bool useDynamicAssemblyList, [Values(2, 100)] int sendCount, [Values(32, 64)] int windowSize)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverReliablePipelineWindowSize = windowSize;
                testWorld.Bootstrap(true,
                    typeof(SerializedClientLargeRcpSendSystem),
                    typeof(SerializedServerLargeRpcReceiveSystem),
                    typeof(SerializedLargeRpcCommandRequestSystem));

                testWorld.CreateWorlds(true, 1, false);
                testWorld.SetDynamicAssemblyList(useDynamicAssemblyList);

                var SendLargeCmd = new SerializedLargeRpcCommand
                { stringValue = new FixedString512Bytes("baaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaavaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaac") };
                var SendSmallCmd = new SerializedSmallRpcCommand { Value = 8 };
                SerializedClientLargeRcpSendSystem.SendCount = sendCount;
                SerializedClientLargeRcpSendSystem.LargeCmd = SendLargeCmd;
                SerializedClientLargeRcpSendSystem.SmallCmd = SendSmallCmd;

                SerializedServerLargeRpcReceiveSystem.ReceivedLargeCount = 0;
                SerializedServerLargeRpcReceiveSystem.ReceivedSmallCount = 0;

                // Connect and make sure the connection could be established
                testWorld.Connect();

                var numTicks = Mathf.Max(2, sendCount * .1f);
                for (int i = 0; i < numTicks; ++i)
                    testWorld.Tick();

                Assert.AreEqual(sendCount, SerializedServerLargeRpcReceiveSystem.ReceivedLargeCount);
                Assert.AreEqual(sendCount, SerializedServerLargeRpcReceiveSystem.ReceivedSmallCount);
                Assert.AreEqual(SendLargeCmd, SerializedServerLargeRpcReceiveSystem.ReceivedLargeCmd);
                Assert.AreEqual(SendSmallCmd, SerializedServerLargeRpcReceiveSystem.ReceivedSmallCmd);
            }
        }

        [Test]
        public void Rpc_IsRemovedWithConnectionDeletion()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1, true);
                testWorld.Connect();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var client = testWorld.ClientWorlds[0];

                // Send RPC from client to server
                var rpcData = new SerializedRpcCommand
                    {intValue = 12345, shortValue = 12345, floatValue = 123.45f};
                var rpcEntity = client.EntityManager.CreateEntity();
                client.EntityManager.AddComponentData(rpcEntity, rpcData);
                client.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // No RPC entity yet on server
                var rpcReqQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ReceiveRpcCommandRequest>(), ComponentType.ReadOnly<SerializedRpcCommand>());
                Assert.AreEqual(0, rpcReqQuery.CalculateEntityCount());

                testWorld.Tick();

                // Server sees RPC now
                Assert.AreEqual(1, rpcReqQuery.CalculateEntityCount());

                // Directly disconnect the client on the server side
                var clientConnectionOnServer = testWorld.GetSingletonRW<NetworkStreamConnection>(testWorld.ServerWorld);
                testWorld.GetSingleton<NetworkStreamDriver>(testWorld.ServerWorld).DriverStore.Disconnect(clientConnectionOnServer.ValueRO);

                var clientConnectionQuery = client.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(1, clientConnectionQuery.CalculateEntityCount());

                testWorld.Tick();

                // RPC has now been cleaned up as the source connection was deleted
                Assert.AreEqual(0, rpcReqQuery.CalculateEntityCount());
            }
        }

        [Test]
        public void Rpc_IsRemovedWithConnectionDeletionInSystem()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(ClientRcpSendSystem),
                    typeof(ServerRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1, true);

                testWorld.Connect();

                ClientRcpSendSystem.SendCount = 1;
                ServerRpcReceiveSystem.ReceivedCount = 0;

                // Client sends RPC inside ClientRcpSendSystem
                testWorld.Tick();
                Assert.AreEqual(0, ClientRcpSendSystem.SendCount);

                // Client triggers disconnect
                // The RPC would be processed on server without cleanup in NetworkGroupCommandBufferSystem.PatchConnectionEvents
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                var clientConnection = testWorld.GetSingletonRW<NetworkStreamConnection>(testWorld.ClientWorlds[0]);
                testWorld.GetSingleton<NetworkStreamDriver>(testWorld.ClientWorlds[0]).DriverStore.Disconnect(clientConnection.ValueRO);

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // Client is disconnected
                var clientConnectionQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(0, clientConnectionQuery.CalculateEntityCount());

                // RPC was never received
                Assert.AreEqual(0, ServerRpcReceiveSystem.ReceivedCount);
            }
        }

        // Where RPCs are received and processed on the server
        internal enum SystemSetup
        {
            UpdateBeforeNetworkECB,
            UpdateAfterNetworkECB,
            UpdateInSimulation
        }

        // Where we'll trigger connect from a system
        internal enum ConnectSetup
        {
            BeforeNetworkECB,
            AfterNetworkECB
        }

        // Where we'll trigger disconnect from a system
        internal enum DisconnectSetup
        {
            BeforeNetworkECB,
            AfterNetworkECB
        }

        // This test will trigger connect/disconnect directly in the test itself while the one below will do these
        // from systems in specific locations.
        // Some expected warnings will print, like
        //   "Attempting to complete a connection with state '1'" - transport called CompleteConnecting on a
        //     connection which was no longer in the Connecting state. We disconnected before connection was completed.
        //   "Cannot send RPC 'Unity.NetCode.Tests.FastReconnectRpc' with no remote connection." - The SendRpcData job
        //     ran when the connection was disconnected. We sent an RPC and immediately disconnected in the same frame.
        [Test]
        public void Rpc_IsCleanedUpWithFastReconnectManual(
            [Values] bool useApproval,
            [Values] SystemSetup systemSetup)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var updateSystem = typeof(ReceiveFastReconnectRpcBefore);
                if (systemSetup == SystemSetup.UpdateAfterNetworkECB)
                    updateSystem = typeof(ReceiveFastReconnectRpcAfter);
                if (systemSetup == SystemSetup.UpdateInSimulation)
                    updateSystem = typeof(ReceiveFastReconnectRpc);

                testWorld.Bootstrap(true,
                    typeof(SendFastReconnectRpc), typeof(SendFastReconnectApprovalRpc), typeof(ReceiveFastReconnectApprovalRpc), updateSystem);

                testWorld.CreateWorlds(true, 1, true);

                // Connect + Disconnect + Connect etc with variable amounts of ticks between and do the connect/disconnect in different places
                var ep = NetworkEndpoint.LoopbackIpv4.WithPort(7979);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int ticksBeforeDisconnecting = 0; ticksBeforeDisconnecting < 8; ticksBeforeDisconnecting++)
                {
                    // There must be at least 1 tick between a disconnect+connect or we'll complain about trying to connect while already connected
                    for (int ticksBeforeReconnecting = 1; ticksBeforeReconnecting < 8; ticksBeforeReconnecting++)
                    {
                        testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                        for (int i = 0; i < ticksBeforeDisconnecting; i++)
                            testWorld.Tick();

                        testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                        var clientConnection = testWorld.GetSingletonRW<NetworkStreamConnection>(testWorld.ClientWorlds[0]);
                        testWorld.GetSingleton<NetworkStreamDriver>(testWorld.ClientWorlds[0]).DriverStore.Disconnect(clientConnection.ValueRO);

                        for (int i = 0; i < ticksBeforeReconnecting; i++)
                            testWorld.Tick();
                    }
                }
            }
        }

        [Test]
        public void Rpc_IsCleanedUpWithFastReconnectInSystems(
            [Values] bool useApproval,
            [Values] SystemSetup systemSetup,
            [Values] ConnectSetup connectSetup,
            [Values] DisconnectSetup disconnectSetup)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var updateSystem = typeof(ReceiveFastReconnectRpcBefore);
                if (systemSetup == SystemSetup.UpdateAfterNetworkECB)
                    updateSystem = typeof(ReceiveFastReconnectRpcAfter);
                if (systemSetup == SystemSetup.UpdateInSimulation)
                    updateSystem = typeof(ReceiveFastReconnectRpc);
                var connectSystem = typeof(FastReconnectRpcConnectAfterSystem);
                if (connectSetup == ConnectSetup.BeforeNetworkECB)
                    connectSystem = typeof(FastReconnectRpcConnectBeforeSystem);
                var disconnectSystem = typeof(FastReconnectRpcDisconnectAfterSystem);
                if (disconnectSetup == DisconnectSetup.BeforeNetworkECB)
                    disconnectSystem = typeof(FastReconnectRpcDisconnectBeforeSystem);

                testWorld.Bootstrap(true,
                    typeof(SendFastReconnectRpc), typeof(SendFastReconnectApprovalRpc), typeof(ReceiveFastReconnectApprovalRpc),
                    updateSystem, connectSystem, disconnectSystem);

                // Don't even tick once after world creation, so we can also test our instant connect flows.
                testWorld.CreateWorlds(true, 1, false);

                var ep = NetworkEndpoint.LoopbackIpv4.WithPort(7979);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);

                for (int ticksBeforeReconnecting = 1; ticksBeforeReconnecting < 3; ticksBeforeReconnecting++)
                {
                    for (int ticksBeforeDisconnecting = 0; ticksBeforeDisconnecting < 7; ticksBeforeDisconnecting++)
                    {
                        // Connect immediately:
                        FastReconnectRpcConnectAfterSystem.ConnectNow = true;
                        FastReconnectRpcConnectBeforeSystem.ConnectNow = true;

                        // Wait a specific number of frames, starting from 0.
                        // Reasoning: You can call Disconnect on the same frame you call Connect.
                        FastReconnectRpcDisconnectAfterSystem.DisconnectDelay = ticksBeforeDisconnecting;
                        FastReconnectRpcDisconnectBeforeSystem.DisconnectDelay = ticksBeforeDisconnecting;

                        // Run those ticks:
                        for (int i = 0; i < ticksBeforeDisconnecting + ticksBeforeReconnecting; i++) testWorld.Tick();
                    }
                }
            }
        }

        [Test]
        public void Rpc_CanPackMultipleRPCs()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedClientLargeRcpSendSystem),
                    typeof(SerializedServerLargeRpcReceiveSystem),
                    typeof(SerializedLargeRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1);

                int SendCount = 500;
                var SendCmd = new SerializedLargeRpcCommand
                { stringValue = new FixedString512Bytes("\0\0\0\0\0\0\0\0\0\0") };
                SerializedClientLargeRcpSendSystem.SendCount = SendCount;
                SerializedClientLargeRcpSendSystem.LargeCmd = SendCmd;

                SerializedServerLargeRpcReceiveSystem.ReceivedLargeCount = 0;

                // Connect and make sure the connection could be established
                testWorld.Connect();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                Assert.AreEqual(SendCount, SerializedServerLargeRpcReceiveSystem.ReceivedLargeCount);
                Assert.AreEqual(SendCmd, SerializedServerLargeRpcReceiveSystem.ReceivedLargeCmd);
            }
        }

        internal class GhostConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent(entity, new GhostOwner());
            }
        }

        [Test]
        public void Rpc_CanSendEntityFromClientAndServer()
        {
            void SendRpc(World world, Entity entity)
            {
                var req = world.EntityManager.CreateEntity();
                world.EntityManager.AddComponentData(req, new RpcWithEntity { entity = entity });
                world.EntityManager.AddComponentData(req, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            }

            RpcWithEntity RecvRpc(World world)
            {
                using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<RpcWithEntity>());
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

                testWorld.Connect();
                // Go in-game
                testWorld.GoInGame();

                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                //Wait some frame so it is spawned also on the client
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                var recvGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ClientWorlds[0]);
                // Retrieve the client entity
                var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntity);
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value
                    .TryGetValue(new SpawnedGhost { ghostId = ghost.ghostId, spawnTick = ghost.spawnTick }, out var clientEntity));

                //Send the rpc to the server
                SendRpc(testWorld.ClientWorlds[0], clientEntity);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                var rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity != Entity.Null);
                Assert.IsTrue(rpcReceived.entity == serverEntity);

                // Server send the rpc to the client
                SendRpc(testWorld.ServerWorld, serverEntity);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                rpcReceived = RecvRpc(testWorld.ClientWorlds[0]);
                Assert.IsTrue(rpcReceived.entity != Entity.Null);
                Assert.IsTrue(rpcReceived.entity == clientEntity);

                // Client try to send a client-only entity -> result in a Entity.Null reference
                //Send the rpc to the server
                var clientOnlyEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntity();
                SendRpc(testWorld.ClientWorlds[0], clientOnlyEntity);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
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
                    testWorld.Tick();
                //Server should not be able to resolve the reference
                rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);
                //On the client must but null
                rpcReceived = RecvRpc(testWorld.ClientWorlds[0]);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);
                var sendGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ServerWorld);
                //If client send the rpc now (the entity should not exists anymore and the mapping should be reset on both client and server now)
                Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value
                    .TryGetValue(new SpawnedGhost { ghostId = ghost.ghostId, spawnTick = ghost.spawnTick }, out var _));
                Assert.IsFalse(testWorld.ServerWorld.EntityManager.GetComponentData<SpawnedGhostEntityMap>(sendGhostMapSingleton).Value
                    .TryGetValue(new SpawnedGhost { ghostId = ghost.ghostId, spawnTick = ghost.spawnTick }, out var _));
                SendRpc(testWorld.ClientWorlds[0], clientEntity);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();
                //The received entity must be null
                rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);
            }
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS && !NETCODE_NDEBUG
        [Test]
        public void Rpc_WarnIfSendingApprovalRpcWithoutApprovalRequired([Values]bool suppressWarning)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1, true);
                testWorld.Connect();
                Debug.Assert(testWorld.TrySuppressNetDebug(true, suppressWarning), "Sanity check");

                var client = testWorld.ClientWorlds[0];
                var rpcEntity = client.EntityManager.CreateEntity();
                client.EntityManager.AddComponent<MyApprovalRpc>(rpcEntity);
                client.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

                if(!suppressWarning)
                    LogAssert.Expect(LogType.Warning, new Regex(@"\[ClientTest0(.*)\] Sending approval RPC '(.*)' to the server but connection approval is disabled"));
                testWorld.Tick();
                LogAssert.NoUnexpectedReceived();
            }
        }

        /* Testing various invalid RPC sending scenarios.
         * Without connection approval:
         *   - Can't send after connection has been started but not yet finished (no NetworkId)
         *   - It's invalid to send an IApprovalRpc when approval is disabled
         * With connection approval:
         *   - Can't send a normal RPC before connection approval has finished (no NetworkId)
         * Both:
         *   - Can't send before any connection has been set up
         *   - Can't send to a target connection which has no outgoing RPC buffer
         */
        [Test]
        public void Rpc_WarnIfSendingBeforeConnectionEstablished([Values]bool useApprovalRpc)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1, true);

                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.RequireConnectionApproval = useApprovalRpc;

                // Send RPC from client to server
                var rpcData = new SerializedRpcCommand
                    {intValue = 12345, shortValue = 12345, floatValue = 123.45f};
                var client = testWorld.ClientWorlds[0];
                var rpcEntity = client.EntityManager.CreateEntity();
                client.EntityManager.AddComponentData(rpcEntity, rpcData);
                client.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

                testWorld.Tick();
                LogAssert.Expect(LogType.Warning, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as not connected"));
                // Start connection setup for next phase of tests
                var ep = NetworkEndpoint.LoopbackIpv4.WithPort(7979);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                var connectionEntity = testWorld.GetSingletonRW<NetworkStreamDriver>(client).ValueRW.Connect(client.EntityManager, ep);

                if (useApprovalRpc)
                {
                    for (int i = 0; i < 2; ++i)
                        testWorld.Tick();

                    // Verify we're now in Handshake state
                    client.EntityManager.CompleteAllTrackedJobs();
                    var clientConnectionOnClient = testWorld.GetSingleton<NetworkStreamConnection>(client);
                    Assert.AreEqual(ConnectionState.State.Handshake, clientConnectionOnClient.CurrentState);

                    // Try sending again before connection approval is finished
                    rpcEntity = client.EntityManager.CreateEntity();
                    client.EntityManager.AddComponentData(rpcEntity, rpcData);
                    client.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

                    LogAssert.Expect(LogType.Error, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as it is not an Approval RPC, and its NetworkConnection(.*) - on Entity(.*) - is in state `Handshake`"));
                    testWorld.Tick();

                    // Now with a target connection instead of broadcast
                    var clientConnectionToServer = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(client);
                    Assert.AreNotEqual(Entity.Null, clientConnectionToServer);
                    rpcEntity = client.EntityManager.CreateEntity();
                    client.EntityManager.AddComponentData(rpcEntity, rpcData);
                    client.EntityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest(){TargetConnection = clientConnectionToServer});

                    LogAssert.Expect(LogType.Error, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as it is not an Approval RPC, and its NetworkConnection(.*) - on Entity(.*) - is in state `Handshake`"));
                    testWorld.Tick();

                    // Disconnect to invalidate the connection entity
                    client.EntityManager.AddComponent<NetworkStreamRequestDisconnect>(connectionEntity);
                    for (int i = 0; i < 4; ++i)
                        testWorld.Tick();
                    testWorld.GetSingletonRW<NetworkStreamDriver>(client).ValueRW.Connect(client.EntityManager, ep);
                }
                else
                {
                    rpcEntity = client.EntityManager.CreateEntity();
                    client.EntityManager.AddComponentData(rpcEntity, rpcData);
                    client.EntityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest(){TargetConnection = connectionEntity});

                    for (int i = 0; i < 5; ++i)
                        testWorld.Tick();

                    // Connection attempt is ongoing but NetworkId not received yet
                    LogAssert.Expect(LogType.Error, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as its NetworkConnection(.*) - on Entity(.*) - is in state `Connecting`"));
                    // Verify the connection did finish
                    Assert.AreNotEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkId>(client));

                    // Disconnect and test again with broadcast RPC
                    client.EntityManager.AddComponent<NetworkStreamRequestDisconnect>(connectionEntity);

                    for (int i = 0; i < 5; ++i)
                        testWorld.Tick();

                    testWorld.GetSingletonRW<NetworkStreamDriver>(client).ValueRW.Connect(client.EntityManager, ep);

                    rpcEntity = client.EntityManager.CreateEntity();
                    client.EntityManager.AddComponentData(rpcEntity, rpcData);
                    client.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

                    for (int i = 0; i < 5; ++i)
                        testWorld.Tick();

                    LogAssert.Expect(LogType.Error, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as its NetworkConnection(.*) - on Entity(.*) - is in state `Connecting`"));
                    Assert.AreNotEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkId>(client));
                }

                // Try to send to an invalid connection entity
                rpcEntity = client.EntityManager.CreateEntity();
                client.EntityManager.AddComponentData(rpcEntity, rpcData);
                client.EntityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest(){TargetConnection = connectionEntity});

                LogAssert.Expect(LogType.Warning, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as its connection entity \(Entity(.*)\) does not have a `NetworkStreamConnection` or `OutgoingRpcDataStreamBuffer` component"));
                testWorld.Tick();
            }
        }

        [Test]
        public void WarnsIfApplicationRunInBackgroundIsFalse()
        {
            var existingRunInBackground = Application.runInBackground;
            try
            {
                using var testWorld = new NetCodeTestWorld();
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);

                Application.runInBackground = false;
                testWorld.Connect();
                // Warning is suppressed by default.
                testWorld.Tick();
                // Un-suppress it.
                Assert.IsTrue(testWorld.TrySuppressNetDebug(false, true), "Failed to un-suppress!");
                // Expect two logs, one per world:
                var regex = new Regex(@"Netcode detected that you don't have Application\.runInBackground enabled.*Project Settings > Player > Resolution and Presentation > Run in Background");
                LogAssert.Expect(LogType.Error, regex);
                LogAssert.Expect(LogType.Error, regex);
                testWorld.Tick();
                // When the client is DC'd, it should not warn.
                testWorld.DisposeServerWorld();
                testWorld.Tick();
            }
            finally
            {
                Application.runInBackground = existingRunInBackground;
            }
        }

        [Test]
        public void Rpc_SendingRPCLargerThanMaxMessageSizeGivesTheCorrectError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverMaxMessageSize = 548;
                testWorld.Bootstrap(true,
                    typeof(VeryLargeRcpSendSystem),
                    typeof(VeryLargeRpcReceiveSystem));
                testWorld.CreateWorlds(true, 1);

                FixedString512Bytes largeString = "";
                for ( int i=0; i<FixedString512Bytes.UTF8MaxLengthInBytes; ++i )
                {
                    if (i == FixedString512Bytes.UTF8MaxLengthInBytes - 1 )
                    {
                        largeString += "\0";
                    }
                    else
                    {
                        largeString += "a";
                    }
                }

                int SendCount = 1;
                var SendCmd = new VeryLargeRPC
                { value = new FixedString512Bytes(largeString),
                value1 = new FixedString512Bytes(largeString)};
                VeryLargeRcpSendSystem.SendCount = SendCount;
                VeryLargeRcpSendSystem.Cmd = SendCmd;

                VeryLargeRpcReceiveSystem.ReceivedCount = 0;

                // Connect and make sure the connection could be established
                testWorld.Connect();

                for (int i = 0; i < 33; ++i)
                    testWorld.Tick();

                var regex = new Regex(@"Reduce the size of this RPC payload!");
                LogAssert.Expect(LogType.Exception, regex);
            }
        }

        [Test]
        public void Rpc_WarnsIfNotConsumedAfter4Frames([Values]bool enabled)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);

                // Create a dud RPC on client and server. Ideally this test would test a full RPC flow, but trying to isolate dependencies:
                var clientWorld = testWorld.ClientWorlds[0];
                var clientNetDebug = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>()).GetSingleton<NetDebug>();
                clientNetDebug.LogLevel = NetDebug.LogLevelType.Warning;
                testWorld.GetSingletonRW<NetDebug>(clientWorld).ValueRW.MaxRpcAgeFrames = (ushort) (enabled ? 4 : 0);
                clientWorld.EntityManager.CreateEntity(ComponentType.ReadWrite<ReceiveRpcCommandRequest>());

                var serverWorld = testWorld.ServerWorld;
                var serverNetDebug = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>()).GetSingleton<NetDebug>();
                serverNetDebug.LogLevel = NetDebug.LogLevelType.Warning;
                testWorld.GetSingletonRW<NetDebug>(serverWorld).ValueRW.MaxRpcAgeFrames = (ushort) (enabled ? 4 : 0);
                serverWorld.EntityManager.CreateEntity(ComponentType.ReadWrite<ReceiveRpcCommandRequest>());

                // 3 ticks before our expected one:
                testWorld.Tick();
                testWorld.Tick();
                testWorld.Tick();

                // Now assert the final tick logs warning on both client and server (server is 1 frame behind):
                var regex = new Regex(@"NetCode RPC Entity\(\d*\:\d*\) has not been consumed or destroyed for '4'");
                if(enabled) LogAssert.Expect(LogType.Warning, regex);
                testWorld.Tick();
                if(enabled) LogAssert.Expect(LogType.Warning, regex);
                testWorld.Tick();
                // Only once!
                testWorld.Tick();
                testWorld.Tick();
            }
        }
#endif
    }
}

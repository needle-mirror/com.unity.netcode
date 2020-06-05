using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;

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

                LogAssert.Expect(LogType.Exception, "InvalidOperationException: Cannot send RPC with no remote connection.");
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
        public void Rpc_MalformedPackets_Throws()
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

                LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: RpcSystem received malformed packets or packets with the wrong version"));
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

    }
}
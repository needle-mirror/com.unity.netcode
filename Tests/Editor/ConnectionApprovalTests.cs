using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    [Category(NetcodeTestCategories.Foundational)]
    internal partial class ConnectionApprovalTests
    {
        internal struct CheckApproval : IApprovalRpcCommand
        {
            public int Payload;
        }

        internal struct NormalRpc : IRpcCommand
        {
            public int Value;
        }

        [Test]
        public void StandardConnectionApprovalFlow()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.RequireConnectionApproval = true;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                var clientQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                var serverEm = testWorld.ServerWorld.EntityManager;
                var clientEm = testWorld.ClientWorlds[0].EntityManager;

                // Client starts in Connecting state (transport is setting up connection)
                Assert.AreEqual(ConnectionState.State.Connecting, clientQuery.GetSingleton<NetworkStreamConnection>().CurrentState);

                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // Server starts in Handshake state (as soon as connection is accepted), client switches to that after sending protocol version on transport connect
                serverEm.CompleteAllTrackedJobs();
                clientEm.CompleteAllTrackedJobs();
                Assert.AreEqual(ConnectionState.State.Handshake, serverQuery.GetSingleton<NetworkStreamConnection>().CurrentState);
                Assert.AreEqual(ConnectionState.State.Handshake, clientQuery.GetSingleton<NetworkStreamConnection>().CurrentState);

                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                clientEm.CompleteAllTrackedJobs();
                serverEm.CompleteAllTrackedJobs();
                Assert.AreEqual(ConnectionState.State.Approval, clientQuery.GetSingleton<NetworkStreamConnection>().CurrentState);
                Assert.AreEqual(ConnectionState.State.Approval, serverQuery.GetSingleton<NetworkStreamConnection>().CurrentState);

                var approvalRpc = clientEm.CreateEntity();
                clientEm.AddComponentData(approvalRpc, new CheckApproval() { Payload = 1234 });
                clientEm.AddComponent<SendRpcCommandRequest>(approvalRpc);

                for (int i = 0; i < 3; ++i)
                    testWorld.Tick();

                var rpcReceiveQuery = serverEm.CreateEntityQuery(ComponentType.ReadOnly<ReceiveRpcCommandRequest>(), ComponentType.ReadOnly<CheckApproval>());
                Assert.AreEqual(1234, rpcReceiveQuery.GetSingleton<CheckApproval>().Payload);
                serverEm.DestroyEntity(rpcReceiveQuery.GetSingletonEntity());
                serverEm.AddComponent<ConnectionApproved>(serverQuery.GetSingletonEntity());

                testWorld.Tick();

                // Client and server go from Approval to Connected
                clientEm.CompleteAllTrackedJobs();
                serverEm.CompleteAllTrackedJobs();
                Assert.AreEqual(ConnectionState.State.Connected, clientQuery.GetSingleton<NetworkStreamConnection>().CurrentState);
                Assert.AreEqual(ConnectionState.State.Connected, serverQuery.GetSingleton<NetworkStreamConnection>().CurrentState);
            }
        }

        [Test]
        public void NonApprovalRpcIsDenied()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.RequireConnectionApproval = true;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                var clientConnectionEntity = testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                var clientQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamConnection>());
                var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                var serverEm = testWorld.ServerWorld.EntityManager;
                var clientEm = testWorld.ClientWorlds[0].EntityManager;

                for (int i = 0; i < 8; ++i) // Only need ~4 ticks, but pass in this many for defensive reasons.
                    testWorld.Tick();

                // Verify both parties are in the Approval state now
                clientEm.CompleteAllTrackedJobs();
                serverEm.CompleteAllTrackedJobs();
                Assert.AreEqual(ConnectionState.State.Approval, clientQuery.GetSingleton<NetworkStreamConnection>().CurrentState);
                Assert.AreEqual(ConnectionState.State.Approval, serverQuery.GetSingleton<NetworkStreamConnection>().CurrentState);

                // Hack the client into thinking he can send normal RPCs now (is connected)
                clientQuery.GetSingletonRW<NetworkStreamConnection>().ValueRW.CurrentState = ConnectionState.State.Connected;
                testWorld.ClientWorlds[0].EntityManager.AddComponent<NetworkId>(clientConnectionEntity);

                // Sending a normal RPC at this point will result in error and disconnection
                var normalRpc = clientEm.CreateEntity();
                clientEm.AddComponentData(normalRpc, new NormalRpc { Value = 1 });
                clientEm.AddComponent<SendRpcCommandRequest>(normalRpc);

                LogAssert.Expect(LogType.Error, new Regex("\\[(.*)\\] RpcSystem received non-approval RPC Rpc\\[\\d+, Unity\\.NetCode\\.Tests\\.ConnectionApprovalTests\\+NormalRpc\\] while in the Approval connection state, from NetworkConnection\\[id0,v1\\]. Make sure you only send non-approval RPCs once the connection is approved. Disconnecting."));

                for (int i = 0; i < 6; ++i)
                    testWorld.Tick();

                NetworkStreamConnection conn;
                Assert.IsTrue(!clientQuery.TryGetSingleton(out conn) || conn.CurrentState == ConnectionState.State.Disconnected, $"Client must be disconnected but was {conn.CurrentState}!");
                Assert.IsTrue(!serverQuery.TryGetSingleton(out conn) || conn.CurrentState == ConnectionState.State.Disconnected, $"Server must be disconnected but was {conn.CurrentState}!");
            }
        }

        [Test]
        public void CannotSetRequireConnectionApprovalAfterStartingDriver()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 0);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.Tick();
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.RequireConnectionApproval = true;
                LogAssert.Expect(LogType.Error, "Attempting to set RequireConnectionApproval while network driver has already been started. This must be done before connecting/listening.");
            }
        }


        [DisableAutoCreation]
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        [RequireMatchingQueriesForUpdate]
        internal partial class SendServerApprovalRpcSystem : SystemBase
        {
            protected override void OnUpdate()
            {
                var serverApprovedConnectionRpcQueue = SystemAPI.GetSingleton<RpcCollection>().GetRpcQueue<ServerApprovedConnection>();
                var serverRequestApprovalAfterHandshakeRpcQueue = SystemAPI.GetSingleton<RpcCollection>().GetRpcQueue<ServerRequestApprovalAfterHandshake>();
                var ghostLookup = SystemAPI.GetComponentLookup<GhostInstance>();
                foreach (var (buffer, entity) in SystemAPI.Query<DynamicBuffer<OutgoingRpcDataStreamBuffer>>().WithEntityAccess())
                {
                    serverRequestApprovalAfterHandshakeRpcQueue.Schedule(buffer, ghostLookup, new ServerRequestApprovalAfterHandshake());
                    serverApprovedConnectionRpcQueue.Schedule(buffer, ghostLookup, new ServerApprovedConnection(){NetworkId = 1, RefreshRequest = default});
                    Enabled = false;
                }
            }
        }

        [Test]
        public void ClientCantSendInternalApprovalRpcToServer()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(SendServerApprovalRpcSystem));
                testWorld.CreateWorlds(true, 1);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.RequireConnectionApproval = true;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                LogAssert.Expect(LogType.Error, new Regex(@"\[Server(.*)\]\[Connection\] Server received internal client-only RPC request 'Unity\.NetCode\.ServerRequestApprovalAfterHandshake' from client"));
                LogAssert.Expect(LogType.Error, new Regex(@"\[Server(.*)\]\[Connection\] Server received internal client-only RPC request 'Unity\.NetCode\.ServerApprovedConnection' from client"));
            }
        }
    }
}

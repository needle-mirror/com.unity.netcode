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

        /// <summary>
        /// System for triggering a disconnect right before the RpcSystem runs
        /// </summary>
        [DisableAutoCreation]
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
        [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
        [UpdateBefore(typeof(RpcSystem))]
        partial class DisconnectSystem : SystemBase
        {
            public static bool TriggerDisconnect;

            protected override void OnUpdate()
            {
                if (TriggerDisconnect)
                {
                    var connectionEntity = SystemAPI.GetSingletonEntity<NetworkStreamConnection>();
                    SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRO.DriverStore.Disconnect(EntityManager.GetComponentData<NetworkStreamConnection>(connectionEntity));
                    Enabled = false;
                }
            }
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

                LogAssert.Expect(LogType.Error, new Regex(@"\[(Server|Host)(.*)\]\[Connection\] Server received internal client-only RPC request 'Unity\.NetCode\.ServerRequestApprovalAfterHandshake' from client"));
                LogAssert.Expect(LogType.Error, new Regex(@"\[(Server|Host)(.*)\]\[Connection\] Server received internal client-only RPC request 'Unity\.NetCode\.ServerApprovedConnection' from client"));
            }
        }

        /// <summary>
        /// There is a special case where a disconnect might happen because of an error during the handshake/approval
        /// process but the RPC involved might never get processed as the connection is disconnected at the same time.
        /// This can be tested by disconnecting right after connecting, then we'll have the RequestProtocolVersionHandshake
        /// RPC (an approval RPC) in the queue and ensure it will get processed.
        /// </summary>
        [Test]
#if NETCODE_NDEBUG
        [Ignore("This tests depends on a debug log level message appearing so cannot run when debug logging is disabled via NETCODE_NDEBUG define")]
#endif
        public void ApprovalRpcsGetProcessedWhenDisconnected([Values(true, false)] bool useHash)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                DisconnectSystem.TriggerDisconnect = false;
                testWorld.Bootstrap(true, typeof(DisconnectSystem));
                testWorld.CreateWorlds(true, 1);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                if (useHash)
                {
                    testWorld.GetSingletonRW<RpcCollection>(testWorld.ServerWorld).ValueRW.DynamicAssemblyList = true;
                    testWorld.GetSingletonRW<RpcCollection>(testWorld.ClientWorlds[0]).ValueRW.DynamicAssemblyList = true;
                }
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.RequireConnectionApproval = true;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                // The message printed in this scenario only appears when in debug logging mode
                testWorld.GetSingletonRW<NetCodeDebugConfig>(testWorld.ServerWorld).ValueRW.LogLevel = NetDebug.LogLevelType.Debug;
                testWorld.GetSingletonRW<NetCodeDebugConfig>(testWorld.ClientWorlds[0]).ValueRW.LogLevel = NetDebug.LogLevelType.Debug;

                // The disconnect needs to happen right before the RpcSystem runs right after connecting, then we'll have
                // the network protocol version RPC in the queue
                testWorld.Tick();
                if (testWorld.ServerWorld.IsClient())
                    testWorld.Tick(); // an extra tick is needed when in single world host mode
                DisconnectSystem.TriggerDisconnect = true;

                // A few other debug messages will print so we'll just watch out for this specific one (ignore the rest)
                // which indicates we're disconnected but processed this pending RPC in the queue
                LogAssert.ignoreFailingMessages = true;
                LogAssert.Expect(LogType.Log, new Regex(@$"\[(.*)\] NetworkConnection\[id0,v1\] in disconnected state but allowing Rpc\[(\d+), Unity.NetCode.RequestProtocolVersionHandshake\] to get processed, as it's an approval RPC\!"));

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // Just verify we end up in a fully disconnected state
                var clientQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamConnection>());
                var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamConnection>());
                Assert.AreEqual(0, clientQuery.CalculateEntityCount());
                Assert.AreEqual(0, serverQuery.CalculateEntityCount());
            }
        }
    }
}

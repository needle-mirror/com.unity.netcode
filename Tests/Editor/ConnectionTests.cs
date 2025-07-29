using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;
using State = Unity.NetCode.ConnectionState.State;
namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class CheckConnectionSystem : SystemBase
    {
        public int numConnected;
        public int numInGame;
        private EntityQuery inGame;
        private EntityQuery connected;
        protected override void OnCreate()
        {
            connected = GetEntityQuery(ComponentType.ReadOnly<NetworkId>());
            inGame = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
        }

        protected override void OnUpdate()
        {
            numConnected = connected.CalculateEntityCount();
            numInGame = inGame.CalculateEntityCount();
        }
    }

    [Category(NetcodeTestCategories.Foundational)]
    internal class ConnectionTests
    {
        internal struct CheckApproval : IApprovalRpcCommand
        {
            public int Payload;
        }

        [Test]
        [Category(NetcodeTestCategories.Smoke)]
        public void ConnectSingleClient()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CheckConnectionSystem));
                testWorld.CreateWorlds(true, 1);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                Assert.AreEqual(1, testWorld.ServerWorld.GetExistingSystemManaged<CheckConnectionSystem>().numConnected);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].GetExistingSystemManaged<CheckConnectionSystem>().numConnected);

                testWorld.GoInGame();
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                Assert.AreEqual(1, testWorld.ServerWorld.GetExistingSystemManaged<CheckConnectionSystem>().numConnected);
                Assert.AreEqual(1, testWorld.ServerWorld.GetExistingSystemManaged<CheckConnectionSystem>().numInGame);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].GetExistingSystemManaged<CheckConnectionSystem>().numConnected);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].GetExistingSystemManaged<CheckConnectionSystem>().numInGame);
            }
        }

        [TestCase(60, 60, 1)]
        [TestCase(40, 20, 2)]
        public void ClientTickRate_ServerAndClientsUseTheSameRateSettings(
            int simulationTickRate, int networkTickRate, int predictedFixedStepRatio)
        {
            using var testWorld = new NetCodeTestWorld();
            var tickRate = new ClientServerTickRate
            {
                SimulationTickRate = simulationTickRate,
                PredictedFixedStepSimulationTickRatio = predictedFixedStepRatio,
                NetworkTickRate = networkTickRate,
                HandshakeApprovalTimeoutMS = 10_000, // Prevent timeout.
            };
            SetupTickRate(tickRate, testWorld);
            //Check that the predicted fixed step rate is also set accordingly.
            LogAssert.NoUnexpectedReceived();
            Assert.AreEqual(tickRate.PredictedFixedStepSimulationTimeStep, testWorld.ServerWorld.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep);
            Assert.AreEqual(tickRate.PredictedFixedStepSimulationTimeStep, testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep);
        }

        static void SetupTickRate(ClientServerTickRate tickRate, NetCodeTestWorld testWorld)
        {
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            testWorld.ServerWorld.EntityManager.CreateSingleton(tickRate);
            tickRate.ResolveDefaults();
            tickRate.Validate();
            // Connect and make sure the connection could be established
            testWorld.Connect();

            //Check that the simulation tick rate are the same
            var serverRate = testWorld.GetSingleton<ClientServerTickRate>(testWorld.ServerWorld);
            var clientRate = testWorld.GetSingleton<ClientServerTickRate>(testWorld.ClientWorlds[0]);
            Assert.AreEqual(tickRate.SimulationTickRate, serverRate.SimulationTickRate);
            Assert.AreEqual(tickRate.SimulationTickRate, clientRate.SimulationTickRate);
            Assert.AreEqual(tickRate.PredictedFixedStepSimulationTickRatio, serverRate.PredictedFixedStepSimulationTickRatio);
            Assert.AreEqual(tickRate.PredictedFixedStepSimulationTickRatio, clientRate.PredictedFixedStepSimulationTickRatio);

            //Do one last step so all the new settings are applied
            testWorld.Tick();
        }

        [Test]
        public void IncorrectlyDisposingAConnectionLogsError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                Test(testWorld, testWorld.ClientWorlds[0]);
            }
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                Test(testWorld, testWorld.ServerWorld);
            }

            static void Test(NetCodeTestWorld testWorld, World worldBeingTested)
            {
                testWorld.Connect();
                var connEntity = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(worldBeingTested);
                Assert.IsTrue(worldBeingTested.EntityManager.Exists(connEntity));
                LogAssert.Expect(LogType.Error, new Regex($@"(has been incorrectly disposed)(.*)({worldBeingTested.Name})"));
                worldBeingTested.EntityManager.DestroyEntity(connEntity);
                testWorld.Tick(); // This tick will raise the error.
                testWorld.Tick(); // This tick should NOT raise it again.
            }
        }

        internal enum ApprovalMode
        {
            NoApproval,
            WithApproval,
        }
        internal enum ConnectionStateMode
        {
            UsingConnectionState,
            NoConnectionState,
        }

        private bool isVerifyingConnState;
        [Test]
        public void ConnectionEventsAreRaised([Values]ApprovalMode approvalMode, [Values]ConnectionStateMode connectionStateMode)
        {
            var isApproval = approvalMode == ApprovalMode.WithApproval;
            isVerifyingConnState = connectionStateMode == ConnectionStateMode.UsingConnectionState;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 3);

                // Manually connect them:
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;

                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.RequireConnectionApproval = isApproval;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                var connectionEntities = new Entity[testWorld.ClientWorlds.Length];
                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                {
                    var clientWorld = testWorld.ClientWorlds[i];
                    if (isVerifyingConnState)
                    {
                        connectionEntities[i] = clientWorld.EntityManager.CreateEntity(typeof(ConnectionState));
                        testWorld.GetSingletonRW<NetworkStreamDriver>(clientWorld).ValueRW.Connect(clientWorld.EntityManager, ep, connectionEntities[i]);
                        // Ensure the ConnectionState is correct even on tick zero.
                        var cs = clientWorld.EntityManager.GetComponentData<ConnectionState>(connectionEntities[i]);
                        Assert.AreEqual(State.Connecting, cs.CurrentState);
                    }
                    else connectionEntities[i] = testWorld.GetSingletonRW<NetworkStreamDriver>(clientWorld).ValueRW.Connect(clientWorld.EntityManager, ep);
                }

                // Tick zero: Connect called! No events at all.
                AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                // Tick one: Client connecting events only:
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 1, testWorld.ClientWorlds);
                WorldHasEventAtIndex(testWorld, testWorld.ClientWorlds, 0, ConnectionState.State.Connecting);

                // Tick 2: Both the client and server should now get the handshake:
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 3, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 1, testWorld.ClientWorlds);

                // Add connection states now:
                if (isVerifyingConnState)
                {
                    using var serverNetworkStreamConnectionsQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    Assert.AreEqual(3, serverNetworkStreamConnectionsQuery.CalculateEntityCount(), "Sanity check: Adding ConnectionState to all 3 server entities.");
                    testWorld.ServerWorld.EntityManager.AddComponent<ConnectionState>(serverNetworkStreamConnectionsQuery);
                }

                isVerifyingConnState = false; // ConnectionState is added THIS FRAME on the server, so won't be correct here!
                ServerHasEventForEachClient(testWorld, ConnectionState.State.Handshake);
                isVerifyingConnState = connectionStateMode == ConnectionStateMode.UsingConnectionState;
                WorldHasEventAtIndex(testWorld, testWorld.ClientWorlds, 0, ConnectionState.State.Handshake);

                // Tick 3: Client is sending the RPC to the server, no events on either.
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                // Tick 4: This is where the flow diverges:
                // - With approval flow - We enter `Approval` state on server and reply.
                // - Without approval flow - We enter Connected` state on the server and reply.
                // In both cases: The server should have events, but not client!
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 3, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                if (isApproval) // DIVERGE!
                {
                    using var serverCheckApprovalQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ReceiveRpcCommandRequest>(), ComponentType.ReadOnly<CheckApproval>());
                    Assert.AreEqual(0, serverCheckApprovalQuery.CalculateEntityCount());

                    // Server should have Approval state.
                    ServerHasEventForEachClient(testWorld, ConnectionState.State.Approval);

                    // Client must wait for `ServerRequestApprovalAfterHandshake` RPC.
                    // Tick 5: Clients now move into Approval!
                    testWorld.Tick();
                    Assert.AreEqual(0, serverCheckApprovalQuery.CalculateEntityCount());
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 1, testWorld.ClientWorlds);
                    WorldHasEventAtIndex(testWorld, testWorld.ClientWorlds, 0, ConnectionState.State.Approval);

                    // Connection approval routine runs - Clients user-code now reacts to this event and send an approval RPC...
                    for (var i = 0; i < testWorld.ClientWorlds.Length; i++)
                    {
                        var world = testWorld.ClientWorlds[i];
                        var approvalRpc = world.EntityManager.CreateEntity();
                        world.EntityManager.AddComponentData(approvalRpc, new CheckApproval() {Payload = 1234});
                        world.EntityManager.AddComponent<SendRpcCommandRequest>(approvalRpc);
                    }

                    // Tick 6: Approval RPC in flight...
                    testWorld.Tick();
                    Assert.AreEqual(0, serverCheckApprovalQuery.CalculateEntityCount());
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                    // Tick 7: Approval RPC arrives, RPC Entity spawn is queued...
                    testWorld.Tick();
                    Assert.AreEqual(0, serverCheckApprovalQuery.CalculateEntityCount());
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                    // Tick 8: Approval RPC is queryable! Server processes it:
                    testWorld.Tick();
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                    // Servers user-code now reacts, adding the `ConnectionApproved`...
                    var rpcEntities = serverCheckApprovalQuery.ToEntityArray(Allocator.Temp);
                    var rpcData = serverCheckApprovalQuery.ToComponentDataArray<ReceiveRpcCommandRequest>(Allocator.Temp);
                    Assert.AreEqual(3, rpcEntities.Length, "Server expecting to have 3 CheckApproval RPCs from clients!");
                    var approvalData = serverCheckApprovalQuery.ToComponentDataArray<CheckApproval>(Allocator.Temp);
                    for (var i = 0; i < rpcData.Length; i++)
                    {
                        Assert.AreEqual(1234, approvalData[i].Payload);
                        testWorld.ServerWorld.EntityManager.DestroyEntity(rpcEntities[i]);
                        testWorld.ServerWorld.EntityManager.AddComponent<ConnectionApproved>(rpcData[i].SourceConnection);
                    }

                    // Tick 9: Now the new approval component will be registered by the server,
                    // leading to server connect, realigning the two flows...
                    testWorld.Tick();
                    Assert.AreEqual(0, serverCheckApprovalQuery.CalculateEntityCount());
                }

                // Server - Connected:
                AssertCorrectEventCount(testWorld, 3, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);
                ServerHasEventForEachClient(testWorld, ConnectionState.State.Connected, true);

                // Next tick: The client should ALSO now receive the `Connected` state:
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 1, testWorld.ClientWorlds);
                WorldHasEventAtIndex(testWorld, testWorld.ClientWorlds, 0, ConnectionState.State.Connected, true);

                // Then we expect quiet thereafter...
                for (int i = 0; i < 3; i++)
                {
                    testWorld.Tick();
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);
                }

                Debug.Log("Connection flow success! ----------------------");

                using var serverNetworkIdQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                var lastClientsConnectionEntity = serverNetworkIdQuery.ToEntityArray(Allocator.Temp)[^1];
                var lastClientWorld = testWorld.ClientWorlds[^1];
                var otherClients = testWorld.ClientWorlds.AsSpan(0, testWorld.ClientWorlds.Length - 1).ToArray();

                // Disconnect the last client, but do it via a server kick, so that we can also test the disconnect reason:
                {
                    var conn = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkStreamConnection>(lastClientsConnectionEntity);
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.DriverStore.Disconnect(conn);
                }

                // Next Tick: Disconnect is applied, event is raised later on the same frame (NetworkGroupCommandBufferSystem)
                // for BOTH server and client.
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 1, testWorld.ServerWorld);
                WorldHasEventAtIndex(testWorld, testWorld.ServerWorld, 0, ConnectionState.State.Disconnected, true, NetworkStreamDisconnectReason.ConnectionClose);
                AssertCorrectEventCount(testWorld, 0, otherClients);
                AssertCorrectEventCount(testWorld, 1, lastClientWorld);
                WorldHasEventAtIndex(testWorld, lastClientWorld, 0, ConnectionState.State.Disconnected, true, NetworkStreamDisconnectReason.ClosedByRemote);

                // Now tick for a few more frames, ensuring there are no errant events:
                for (int i = 0; i < 3; i++)
                {
                    testWorld.Tick();
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 0, otherClients);
                    AssertCorrectEventCount(testWorld, 0, lastClientWorld);
                }
            }
        }

        private static void AssertCorrectEventCount(NetCodeTestWorld testWorld, int numEventsExpectedPerWorld, params World[] worlds)
        {
            foreach (var world in worlds)
            {
                world.EntityManager.CompleteAllTrackedJobs();
                var connectionEventsForTick = testWorld.GetSingleton<NetworkStreamDriver>(world).ConnectionEventsForTick;
                if (numEventsExpectedPerWorld == connectionEventsForTick.Length) continue;

                string all = "";
                for (var i = 0; i < connectionEventsForTick.Length; i++)
                {
                    var evt = connectionEventsForTick[i];
                    all += $"\n\t[{i}]={evt.ToFixedString()}";
                    if (i < numEventsExpectedPerWorld) all += " <-- Expected!";
                    else all += " <-- Surprising!";
                }

                if (connectionEventsForTick.Length > numEventsExpectedPerWorld)
                    Assert.Fail($"Rogue events found! {world.Name} has too MANY events on tick {NetCodeTestWorld.TickIndex}! Expected: {numEventsExpectedPerWorld}, but has: {connectionEventsForTick.Length}\n{all}");
                else Assert.Fail($"{world.Name} has too FEW events on tick {NetCodeTestWorld.TickIndex}! Expected: {numEventsExpectedPerWorld}, but has: {connectionEventsForTick.Length}\n{all}");
            }
        }

        private void ServerHasEventForEachClient(NetCodeTestWorld testWorld, ConnectionState.State expectedState, bool expectedValidNetworkId = false, NetworkStreamDisconnectReason expectedDisconnectReason = default)
        {
            var serverWorld = testWorld.ServerWorld;
            serverWorld.EntityManager.CompleteAllTrackedJobs();
            var connectionEventsForServerWorld = testWorld.GetSingleton<NetworkStreamDriver>(serverWorld).ConnectionEventsForTick;
            for (var i = 0; i < connectionEventsForServerWorld.Length; i++)
            {
                WorldHasEventAtIndex(testWorld, serverWorld, i, expectedState, expectedValidNetworkId, expectedDisconnectReason);
            }
        }

        private void WorldHasEventAtIndex(NetCodeTestWorld testWorld, World[] worlds, int index, ConnectionState.State expectedState, bool expectedValidNetworkId = false, NetworkStreamDisconnectReason expectedDisconnectReason = default)
        {
            foreach (var world in worlds)
                WorldHasEventAtIndex(testWorld, world, index, expectedState, expectedValidNetworkId, expectedDisconnectReason);
        }

        private void WorldHasEventAtIndex(NetCodeTestWorld testWorld, World world, int index, ConnectionState.State expectedState, bool expectedValidNetworkId = false, NetworkStreamDisconnectReason expectedDisconnectReason = default)
        {
            world.EntityManager.CompleteAllTrackedJobs();
            bool expectEntityExists = expectedState != ConnectionState.State.Disconnected;
            bool expectedConnectionIdToBeValid = expectedState is State.Connecting or State.Handshake or State.Approval or State.Connected or State.Disconnected;
            var connectionEvents = testWorld.GetSingleton<NetworkStreamDriver>(world).ConnectionEventsForTick;
            var evt = connectionEvents[index];
            var s = $"[{world.Name}] ConnectionEventsForTick[{index}]={evt.ToFixedString()}\nOn tick {NetCodeTestWorld.TickIndex}\nExpecting: {expectedState}, validNetworkId:{expectedValidNetworkId}";
            Assert.AreEqual(expectedConnectionIdToBeValid, evt.ConnectionId.IsCreated, s + "\nevt.ConnectionId.IsCreated?");
            Assert.AreEqual(expectedState, evt.State, s + "\nevt.State is correct?");

            Assert.AreEqual(expectedDisconnectReason, evt.DisconnectReason, s + "\nevt.DisconnectReason correct?");
            if (expectedValidNetworkId)
            {
                if (expectEntityExists)
                {
                    Assert.IsTrue(world.EntityManager.HasComponent<NetworkId>(evt.ConnectionEntity), s + "\nHasComponent<NetworkId>(evt.ConnectionEntity) == TRUE");
                    var expectedNetworkId = world.EntityManager.GetComponentData<NetworkId>(evt.ConnectionEntity);
                    Assert.AreEqual(expectedNetworkId.Value, evt.Id.Value, s + "\nComponent value == evt.NetworkId?");
                }
                else Assert.AreNotEqual(0, evt.Id.Value, s + "\nevt.NetworkId.Value != 0?");
            }
            else if (expectEntityExists)
            {
                Assert.IsFalse(world.EntityManager.HasComponent<NetworkId>(evt.ConnectionEntity), s + "\nHasComponent<NetworkId>(evt.ConnectionEntity) == FALSE");
            }

            if (expectEntityExists)
                Assert.AreEqual(expectedState, world.EntityManager.GetComponentData<NetworkStreamConnection>(evt.ConnectionEntity).CurrentState, s + "\nNetworkStreamConnection.CurrentState == " + expectedState);

            if (isVerifyingConnState)
            {
                var cs = world.EntityManager.GetComponentData<ConnectionState>(evt.ConnectionEntity);
                Assert.AreEqual(expectedState, cs.CurrentState, s + "\nConnectionState.CurrentState correct?");
                Assert.AreEqual(expectedDisconnectReason, cs.DisconnectReason, s + "\nConnectionState.DisconnectReason correct?");
                if (expectedValidNetworkId && expectEntityExists)
                {
                    var expectedNetworkId = world.EntityManager.GetComponentData<NetworkId>(evt.ConnectionEntity);
                    Assert.AreEqual(expectedNetworkId.Value, cs.NetworkId, s + "\nConnectionState.NetworkId == evt.NetworkId?");
                }
                if (expectedState == State.Disconnected)
                {
                    bool didRemove = world.EntityManager.RemoveComponent<ConnectionState>(evt.ConnectionEntity);
                    Assert.IsTrue(didRemove, s + "\nRemove ConnectionState success?");
                }
            }

            Assert.AreEqual(expectEntityExists, world.EntityManager.Exists(evt.ConnectionEntity), s + "\nevt.ConnectionEntity exists?");
        }

        [Test]
        public void ConnectionUniqueIdsAreCleanedUp()
        {
            var numClients = 5;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, numClients);

                // Connect every client except the last one which we'll connect later
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 0; i < numClients-1; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                testWorld.GoInGame();

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                var firstClientWorld = testWorld.ClientWorlds[0];
                var connectionUniqueId = testWorld.GetSingleton<ConnectionUniqueId>(firstClientWorld);
                var originalClientId = connectionUniqueId.Value;

                // Disconnect and reconnect first client
                var firstClientConnectionQuery = firstClientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
                testWorld.GetSingletonRW<NetworkStreamDriver>(firstClientWorld).ValueRW.DriverStore.Disconnect(firstClientConnectionQuery.GetSingleton<NetworkStreamConnection>());
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                testWorld.GetSingletonRW<NetworkStreamDriver>(firstClientWorld).ValueRW.Connect(firstClientWorld.EntityManager, ep);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Verify the client reported unique ID is used by the server (otherwise would generate a new one), unique ID persists across reconnections
                connectionUniqueId = testWorld.GetSingleton<ConnectionUniqueId>(firstClientWorld);
                Assert.AreEqual(originalClientId, connectionUniqueId.Value);

                // Make the last client duplicate the ID used by first client
                var lastClientWorld = testWorld.ClientWorlds[numClients - 1];
                lastClientWorld.EntityManager.CreateSingleton(new ConnectionUniqueId() { Value = originalClientId });

                testWorld.GetSingletonRW<NetworkStreamDriver>(lastClientWorld).ValueRW.Connect(lastClientWorld.EntityManager, ep);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Server will detect duplicate unique ID and assign a new one
                connectionUniqueId = testWorld.GetSingleton<ConnectionUniqueId>(lastClientWorld);
                Assert.AreNotEqual(originalClientId, connectionUniqueId.Value);
            }
        }

        [Test]
        public void ReconnectedConnectionsAreDetected()
        {
            var numClients = 5;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, numClients);

                // Connect every client except the last one which we'll connect later
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 0; i < numClients-1; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                testWorld.GoInGame();

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Disconnect and reconnect first client
                var firstClientWorld = testWorld.ClientWorlds[0];
                var client0ConnectionQuery = firstClientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
                testWorld.GetSingletonRW<NetworkStreamDriver>(firstClientWorld).ValueRW.DriverStore.Disconnect(client0ConnectionQuery.GetSingleton<NetworkStreamConnection>());
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                testWorld.GetSingletonRW<NetworkStreamDriver>(firstClientWorld).ValueRW.Connect(firstClientWorld.EntityManager, ep);
                testWorld.GoInGame(firstClientWorld);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Verify connections are detected as reconnected on both client and server
                var clientIsReconnectedOnServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkId), typeof(NetworkStreamIsReconnected));
                Assert.IsTrue(clientIsReconnectedOnServerQuery.CalculateEntityCount() == 1);
                var clientIsReconnectedQuery = firstClientWorld.EntityManager.CreateEntityQuery(typeof(NetworkId), typeof(NetworkStreamIsReconnected));
                Assert.IsTrue(clientIsReconnectedQuery.CalculateEntityCount() == 1);

                // Make the last client duplicate the ID used by first client
                var lastClientWorld = testWorld.ClientWorlds[numClients - 1];
                testWorld.GetSingletonRW<NetworkStreamDriver>(lastClientWorld).ValueRW.Connect(lastClientWorld.EntityManager, ep);
                testWorld.GoInGame(lastClientWorld);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Last client should not be detected as reconnected as the unique IDs from the server and the local one
                // on the client did not match.
                clientIsReconnectedQuery = lastClientWorld.EntityManager.CreateEntityQuery(typeof(NetworkId), typeof(NetworkStreamIsReconnected));
                Assert.IsFalse(clientIsReconnectedQuery.CalculateEntityCount() == 1);
            }
        }
    }

    // Without NETCODE_DEBUG, ALL error logs are logged to the console, thus we cannot turn on specific ones to test against.
    // Hard to fix the tests to correctly expect, so simply disabled all of them.
#if !NETCODE_NDEBUG
    internal class VersionTests
    {
        [Test]
        public void SameVersion_ConnectSuccessfully()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                //Don't tick the world after creation. that will generate the default protocol version.
                //We want to use a custom one here
                testWorld.CreateWorlds(true, 1, false);
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

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(1, query.CalculateEntityCount());
            }
        }

        internal enum DifferenceType
        {
            GameVersion,
            NetCodeVersion,
            RpcVersion,
            ComponentVersion,
        }
        [Test]
        public void DifferentVersions_AreDisconnnected([Values]DifferenceType differenceType)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1, true);

                // Setup `RequireStrictProtocolVersionValidation`:
                var clientServerTickRate = new ClientServerTickRate();
                clientServerTickRate.ResolveDefaults();
                testWorld.ServerWorld.EntityManager.CreateSingleton(clientServerTickRate);

                // Get the default protocol version:
                int maxTicks = 3;
                Entity serverProtocolVersionEntity;
                while ((serverProtocolVersionEntity = testWorld.TryGetSingletonEntity<NetworkProtocolVersion>(testWorld.ServerWorld)) == Entity.Null)
                {
                    testWorld.Tick();
                    if(maxTicks-- <= 0) Assert.Fail("Sanity: Expected singleton creation!");
                }
                // Modify it on server:
                var serverProtocolVersion = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkProtocolVersion>(serverProtocolVersionEntity);
                switch (differenceType)
                {
                    case DifferenceType.GameVersion:
                        serverProtocolVersion.GameVersion = 99;
                        break;
                    case DifferenceType.NetCodeVersion:
                        serverProtocolVersion.NetCodeVersion = 98;
                        break;
                    case DifferenceType.RpcVersion:
                        serverProtocolVersion.RpcCollectionVersion = 97;
                        break;
                    case DifferenceType.ComponentVersion:
                        serverProtocolVersion.ComponentCollectionVersion = 96;
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(differenceType), differenceType, null);
                }
                testWorld.ServerWorld.EntityManager.SetComponentData(serverProtocolVersionEntity, serverProtocolVersion);

                // The ordering of the protocol version error messages can be scrambled, so we can't log.expect exact ordering
                LogAssert.ignoreFailingMessages = true;
                LogAssert.Expect(LogType.Error, new Regex(@"\[ClientTest(.*)\] RpcSystem received bad protocol version from NetworkConnection"));
                LogAssert.Expect(LogType.Error, new Regex(@"\[ServerTest(.*)\] RpcSystem received bad protocol version from NetworkConnection"));

                switch (differenceType)
                {
                    case DifferenceType.GameVersion:
                        LogAssert.Expect(LogType.Error, "The Game version mismatched between remote and local. Ensure that you are using the same version of the game on both client and server.");
                        break;
                    case DifferenceType.NetCodeVersion:
                        LogAssert.Expect(LogType.Error, "The NetCode version mismatched between remote and local. Ensure that you are using the same version of Netcode for Entities on both client and server.");
                        break;
                    case DifferenceType.RpcVersion:
                        LogAssert.Expect(LogType.Error, "The RPC Collection mismatched between remote and local. Compare the following list of RPCs against the set produced by the remote, to find which RPCs are misaligned. You can also enable `RpcCollection.DynamicAssemblyList` to relax this requirement (which is recommended during development, see documentation for more details).");
                        break;
                    case DifferenceType.ComponentVersion:
                        LogAssert.Expect(LogType.Error, "The Component Collection mismatched between remote and local. Compare the following list of Components against the set produced by the remote, to find which components are misaligned. You can also enable `RpcCollection.DynamicAssemblyList` to relax this requirement (which is recommended during development, see documentation for more details).");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(differenceType), differenceType, null);
                }

                // Connecting triggers the error, as it occurs during handshake.
                testWorld.Connect(failTestIfConnectionFails: false);

                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ServerWorld), "Expected no connection left!");
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ClientWorlds[0]), "Expected no connection left!");
            }
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public void ProtocolVersionDebugInfoAppearsOnMismatch(bool debugServer)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                // Only print the protocol version debug errors in one world, so the output can be deterministically validated
                // if it's printed in both worlds (client and server) the output can interweave and log checks will fail
                testWorld.EnableLogsOnServer = debugServer;  // WARNING: DISABLE "Force Log Settings" TOOL OR THIS TEST WILL FAIL!
                testWorld.EnableLogsOnClients = !debugServer;
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1, false);

                float dt = 16f / 1000f;
                var entity = testWorld.ClientWorlds[0].EntityManager.CreateEntity(ComponentType.ReadWrite<GameProtocolVersion>());
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(entity, new GameProtocolVersion(){Version = 9000});
                testWorld.Tick(dt);
                testWorld.Tick(dt);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                LogExpectProtocolError(testWorld, testWorld.ServerWorld, debugServer);

                // Allow disconnect to happen
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // Verify client connection is disconnected
                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(0, query.CalculateEntityCount());
            }
        }

        [Test]
        public void DisconnectEventAndRPCVersionErrorProcessedInSameFrame([Values] bool checkServer)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                // Only print the protocol version debug errors in one world, so the output can be deterministically validated
                // if it's printed in both worlds (client and server) the output can interweave and log checks will fail
                testWorld.EnableLogsOnServer = checkServer; // WARNING: DISABLE "Force Log Settings" TOOL OR THIS TEST WILL FAIL!
                testWorld.EnableLogsOnClients = !checkServer;
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1, false);

                float dt = 16f / 1000f;
                testWorld.ClientWorlds[0].EntityManager.CreateSingleton(new GameProtocolVersion(){Version = 9000});
                testWorld.Tick(dt);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                LogExpectProtocolError(testWorld, testWorld.ServerWorld, checkServer);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick(dt);

                // Verify client connection is disconnected
                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(0, query.CalculateEntityCount());
            }
        }

        void LogExpectProtocolError(NetCodeTestWorld testWorld, World world, bool checkServer)
        {
            LogAssert.Expect(LogType.Error, new Regex(@$"\[{(checkServer ? "Server" : "Client")}Test(.*)\] RpcSystem received bad protocol version from NetworkConnection\[id0,v1\]"
                                                      + @$"\nLocal protocol: NPV\[NetCodeVersion:{NetworkProtocolVersion.k_NetCodeVersion}, GameVersion:{(checkServer ? "0" : "9000")}, RpcCollection:(\d+), ComponentCollection:(\d+)\]"
                                                      + @$"\nRemote protocol: NPV\[NetCodeVersion:{NetworkProtocolVersion.k_NetCodeVersion}, GameVersion:{(!checkServer ? "0" : "9000")}, RpcCollection:(\d+), ComponentCollection:(\d+)\]"));
            LogAssert.Expect(LogType.Error, "The Game version mismatched between remote and local. Ensure that you are using the same version of the game on both client and server.");
            var rpcs = testWorld.GetSingleton<RpcCollection>(world).Rpcs;
            Assert.AreNotEqual(0, rpcs.Length, "Sanity.");
            LogAssert.Expect(LogType.Error, "RPC List (for above 'bad protocol version' error): " + rpcs.Length);
            for (int i = 0; i < rpcs.Length; ++i)
                LogAssert.Expect(LogType.Error, new Regex("Unity.NetCode"));
            using var collection = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
            // GhostCollection serializers do not get reset to 0.
            ref var ghostCollection = ref testWorld.GetSingletonRW<GhostComponentSerializerCollectionData>(testWorld.ClientWorlds[0]).ValueRW;
            Assert.AreNotEqual(0, ghostCollection.Serializers.Length, $"Sanity: ghostCollection.Serializers.Length is zero");
            LogAssert.Expect(LogType.Error, $"Component serializer data (for above 'bad protocol version' error): {ghostCollection.Serializers.Length}");
            for (int i = 0; i < ghostCollection.Serializers.Length; ++i)
                LogAssert.Expect(LogType.Error, new Regex(@$"ComponentHash\[{i}\] = Type:"));
        }

        internal class TestConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent(entity, new GhostOwner());
                baker.AddComponent(entity, new GhostGenTestUtils.GhostGenTestType_IComponentData());
                // TODO (flag in review): Add the other types (Input, RPC etc) to this test
            }
        }
        [Test]
        public void GhostCollectionGenerateSameHashOnClientAndServer()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghost1 = new GameObject();
                ghost1.AddComponent<TestNetCodeAuthoring>().Converter = new TestConverter();
                ghost1.AddComponent<GhostAuthoringComponent>().DefaultGhostMode = GhostMode.Predicted;
                var ghost2 = new GameObject();
                ghost2.AddComponent<TestNetCodeAuthoring>().Converter = new TestConverter();
                ghost2.AddComponent<GhostAuthoringComponent>().DefaultGhostMode = GhostMode.Interpolated;

                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection(ghost1, ghost2);

                testWorld.CreateWorlds(true, 1);
                var serverCollectionSingleton = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var clientCollectionSingleton = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ClientWorlds[0]);
                //First tick: compute on both client and server the ghost collection hash
                testWorld.Tick();
                Assert.AreEqual(GhostCollectionSystem.CalculateComponentCollectionHash(testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(serverCollectionSingleton)),
                    GhostCollectionSystem.CalculateComponentCollectionHash(testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostComponentSerializer.State>(clientCollectionSingleton)));

                // compare the list of loaded prefabs
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
                testWorld.Connect();

                testWorld.GoInGame();
                for(int i=0;i<10;++i)
                    testWorld.Tick();

                Assert.IsTrue(testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]) != Entity.Null);
            }
        }

        [Test]
        public void DefaultVariantHashAreCalculatedCorrectly()
        {
            var realHash = GhostVariantsUtility.UncheckedVariantHash(typeof(LocalTransform).FullName, typeof(LocalTransform).FullName);
            Assert.AreEqual(realHash, GhostVariantsUtility.CalculateVariantHashForComponent(ComponentType.ReadWrite<LocalTransform>()));
            var compName = new FixedString512Bytes(typeof(LocalTransform).FullName);
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHash(compName, compName));
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHash(compName, ComponentType.ReadWrite<LocalTransform>()));
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHashNBC(typeof(LocalTransform), ComponentType.ReadWrite<LocalTransform>()));
        }
        [Test]
        public void tVariantHashAreCalculatedCorrectly()
        {
            var realHash = GhostVariantsUtility.UncheckedVariantHash(typeof(TransformDefaultVariant).FullName, typeof(LocalTransform).FullName);
            var compName = new FixedString512Bytes(typeof(LocalTransform).FullName);
            var variantName = new FixedString512Bytes(typeof(TransformDefaultVariant).FullName);
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHash(variantName, compName));
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHash(variantName, ComponentType.ReadWrite<LocalTransform>()));
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHashNBC(typeof(TransformDefaultVariant), ComponentType.ReadWrite<LocalTransform>()));
        }
        [Test]
        public void RuntimeAndCodeGeneratedVariantHashMatch()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                //Grab all the serializers we have and recalculate locally the hash and verify they match.
                //TODO: to have a complete end-to-end test we have a missing piece: we don't have the original variant System.Type.
                //Either we add that in code-gen (as string, for test/debug purpose only) or we need to store somehow the type
                //when we register the serialiser itself. It is not a priority, but great to have.
                //Right now I exposed a a VariantTypeFullHashName in the serialiser that allow at lest to do the most
                //important verification: the hash matches!
                var data = testWorld.GetSingleton<GhostComponentSerializerCollectionData>(testWorld.ServerWorld);
                for (int i = 0; i < data.Serializers.Length; ++i)
                {
                    var variantTypeHash = data.Serializers.ElementAt(i).VariantTypeFullNameHash;
                    var componentType = data.Serializers.ElementAt(i).ComponentType;
                    var variantHash = GhostVariantsUtility.UncheckedVariantHash(variantTypeHash, componentType);
                    Assert.AreEqual(data.Serializers.ElementAt(i).VariantHash, variantHash,
                        $"Expect variant hash for code-generated serializer is identical to the" +
                        $"calculated at runtime for component {componentType.GetManagedType().FullName}." +
                        $"generated: {data.Serializers.ElementAt(i).VariantHash} runtime:{variantHash}");
                }
            }
        }
    }
#endif
}

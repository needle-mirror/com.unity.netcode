using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.Netcode.Tests
{
    internal struct TestMessage : IRpcCommand
    {
        public int value;
    }

    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(RpcSystem))]
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    internal partial class TestMessageReceiver : SystemBase
    {
        public List<Action<TestMessage>> m_Handlers = new();
        EntityQuery m_MessageQuery;
        protected override void OnCreate()
        {
            m_MessageQuery = this.EntityManager.CreateEntityQuery(typeof(TestMessage), typeof(ReceiveRpcCommandRequest));
        }

        protected override void OnUpdate()
        {
            var allMessages = m_MessageQuery.ToComponentDataArray<TestMessage>(Allocator.Temp);
            foreach (var testMessage in allMessages)
            {
                foreach (var handler in m_Handlers)
                {
                    handler.Invoke(testMessage);
                }
            }
            EntityManager.DestroyEntity(m_MessageQuery);
        }
    }

    internal class ConnectionTests
    {
        const ushort k_TestPort = 9999;
        NetworkEndpoint ServerListenEndpoint = NetworkEndpoint.AnyIpv4.WithPort(k_TestPort);
        NetworkEndpoint ClientConnectEndpoint = NetworkEndpoint.LoopbackIpv4.WithPort(k_TestPort);

        void SendMessage(TestMessage message, Connection connection)
        {
            if (connection.GetConnectionState() != ConnectionState.State.Connected)
            {
                Assert.Fail("trying to send message on invalid connection");
            }
            var req = connection.World.EntityManager.CreateEntity(ComponentType.ReadWrite<SendRpcCommandRequest>(), ComponentType.ReadWrite<TestMessage>());
            connection.World.EntityManager.SetComponentData(req, new SendRpcCommandRequest{TargetConnection = connection.ConnectionEntity});
            connection.World.EntityManager.SetComponentData(req, message);
        }

        void ReceiveMessage(Action<TestMessage> callback, Connection connection)
        {
            var sys = connection.World.GetExistingSystemManaged<TestMessageReceiver>();
            if (sys == null)
                Assert.Fail($"forgot to register {nameof(TestMessageReceiver)} in test setup!");
            sys.m_Handlers.Add(callback);
        }

        void StartServer()
        {
            if (NetCodeTestWorld.OverrideUseSingleWorldHost)
                Assert.GreaterOrEqual(Netcode.StartAsHost(ServerListenEndpoint).GetConnectionState(), ConnectionState.State.Connecting);
            else
                Assert.IsTrue(Netcode.Listen(ServerListenEndpoint));
        }

        NetcodeWorld StartAndConnectNewClient(NetCodeTestWorld testWorld)
        {
            testWorld.CreateAdditionalClientWorlds(1, tickWorldAfterCreation: false);
            NetcodeWorld additionalClientWorld = testWorld.ClientWorlds[0];
            additionalClientWorld.Connect(ClientConnectEndpoint);
            return additionalClientWorld;
        }

        [Test(Description = "Basic connection test with high level connection APIs.")]
        [Category(NetcodeTestCategories.Foundational), Category(NetcodeTestCategories.Smoke)]
        public async Task TestSimpleConnection()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest(serverCount: 0, clientCount: 0, singleWorldHostCount: 0, userSystems: typeof(TestMessageReceiver));

            Assert.IsTrue(ClientServerBootstrap.ClientWorld == null, "sanity check failed");
            Assert.IsTrue(ClientServerBootstrap.ServerWorld == null, "sanity check failed");

            StartServer();
            Assert.IsNotNull(testWorld.ServerWorld);

            Assert.IsTrue(testWorld.ServerWorld.IsHost() == NetCodeTestWorld.OverrideUseSingleWorldHost, "sanity check failed");
            Assert.IsTrue(testWorld.ClientWorlds == null || testWorld.ClientWorlds.Length == 0);
            int connectedEvent = 0;
            Netcode.OnConnectionEvent += (connection, @event) =>
            {
                if (@event.State == ConnectionState.State.Connected)
                    connectedEvent++;
            };
            StartAndConnectNewClient(testWorld);

            Assert.AreEqual(1, testWorld.ClientWorlds.Length);
            Assert.IsTrue(testWorld.ServerWorld != testWorld.ClientWorlds[0]);

            await testWorld.TickUntilConnectedAsync(testWorld.ClientWorlds[0]);

            Assert.IsTrue(ClientServerBootstrap.ClientWorld.IsCreated);
            Assert.IsTrue(ClientServerBootstrap.ServerWorld.IsCreated);
            if (!NetCodeTestWorld.OverrideUseSingleWorldHost)
                Assert.AreNotEqual(ClientServerBootstrap.ClientWorld, ClientServerBootstrap.ServerWorld);
            else
                Assert.AreNotEqual(ClientServerBootstrap.ClientWorlds[1], ClientServerBootstrap.ServerWorld);
            Assert.AreEqual(2, connectedEvent, "expected one client OnConnect and one server OnConnect");
            CheckConnected(testWorld, ConnectionState.State.Connected, 1);
            await TestMessageExchange(testWorld.ServerWorld, testWorld.ClientWorlds[0], testWorld);
        }

        static IEnumerable<GameObject> GetGhostObjectsForWorld(NetcodeWorld world)
        {
            var allGhosts = GameObject.FindObjectsByType<GhostObject>().Where(ghost => !ghost.IsPrefab()).ToList();
            foreach (var ghost in allGhosts)
            {
                if (ghost.World == world)
                    yield return ghost.gameObject;
            }
        }

        [Test(Description = "Test spawning logic can happen as expected")]
        public async Task TestSpawnerLogic()
        {
            bool isHost = NetCodeTestWorld.OverrideUseSingleWorldHost;
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest(clientCount: 0, serverCount: 0, singleWorldHostCount: 0, userSystems: typeof(TestMessageReceiver));

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("BasicData");

            var spawnerGo = new GameObject();
            spawnerGo.SetActive(false);
            var spawner = spawnerGo.AddComponent<TestGameObjectSpawner>();
            spawner.prefab = prefab.gameObject;

            StartServer();
            var additionalClientWorld = StartAndConnectNewClient(testWorld);

            spawnerGo.SetActive(true);

            Assert.AreEqual(isHost ? 1 : 0, GetGhostObjectsForWorld(testWorld.ServerWorld).Count());
            await testWorld.TickUntilConnectedAsync(additionalClientWorld);
            await TestMessageExchange(testWorld.ServerWorld, testWorld.ClientWorlds[0], testWorld);
            CheckConnected(testWorld, ConnectionState.State.Connected, 1);

            await testWorld.TickMultipleAsync(30);
            Assert.AreEqual(isHost ? 2 : 1, GetGhostObjectsForWorld(testWorld.ServerWorld).Count());
            Assert.AreEqual(0, GetGhostObjectsForWorld(testWorld.ClientWorlds[0]).Count(), "NetworkStreamInGame isn't expected to be enabled yet");

            Netcode.AllConnections[0].EnableGhostReplication(true);
            additionalClientWorld.LocalConnection.EnableGhostReplication(true);
            await testWorld.TickMultipleAsync(6);

            Assert.AreEqual(isHost ? 2 : 1, GetGhostObjectsForWorld(testWorld.ServerWorld).Count());
            Assert.AreEqual(1, testWorld.ClientWorlds.Length, "sanity check failed");
            Assert.AreEqual(isHost ? 2 : 1, GetGhostObjectsForWorld(testWorld.ClientWorlds[0]).Count(), "NetworkStreamInGame should have been enabled for this world");
        }

        async Task TestMessageExchange(NetcodeWorld serverWorld, NetcodeWorld clientWorld, NetCodeTestWorld testWorld)
        {
            bool didReceive = false;
            ReceiveMessage(message =>
            {
                didReceive = true;
                Assert.IsTrue(message.value == 123);
            }, clientWorld.LocalConnection);
            // find server side connection
            Connection serverSideConnection = default;
            foreach (var connection in serverWorld.AllConnections)
            {
                if (connection.NetworkId == clientWorld.LocalConnection.NetworkId)
                    serverSideConnection = connection;
            }
            if (!serverSideConnection.IsValid())
                Assert.Fail("server side connection invalid");
            SendMessage(new TestMessage() { value = 123 }, serverSideConnection);
            await testWorld.TickMultipleAsync(3);
            Assert.IsTrue(didReceive, "didReceive");
        }

        static void CheckConnected(NetCodeTestWorld testWorld, ConnectionState.State stateToCheck, int expectedConnectionCount)
        {
            Assert.AreEqual(stateToCheck == ConnectionState.State.Connected ? expectedConnectionCount : 0, testWorld.ServerWorld.AllConnections.Count);
            for (int i = 0; i < testWorld.ServerWorld.AllConnections.Count; i++)
            {
                Assert.AreEqual(stateToCheck, testWorld.ServerWorld.AllConnections[i].GetConnectionState());
            }

            foreach (var clientWorld in testWorld.ClientWorlds)
            {
                Assert.AreEqual(stateToCheck, clientWorld.LocalConnection.GetConnectionState());
                var connectionQuery = clientWorld.EntityManager.CreateEntityQuery(typeof(NetworkId));
                Assert.AreEqual(stateToCheck == ConnectionState.State.Connected ? 1 : 0, connectionQuery.CalculateEntityCount());
            }
        }

        [Test(Description = "Test a client getting disconnected by the server can recover")]
        public async Task TestGettingKicked_AndReconnect()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest(clientCount: 0, serverCount: 0, singleWorldHostCount: 0, userSystems: typeof(TestMessageReceiver));

            StartServer();
            var additionalClientWorld = StartAndConnectNewClient(testWorld);
            await testWorld.TickUntilConnectedAsync(testWorld.ClientWorlds[0]);
            await TestMessageExchange(testWorld.ServerWorld, testWorld.ClientWorlds[0], testWorld);
            CheckConnected(testWorld, ConnectionState.State.Connected, 1);

            var connection = testWorld.ServerWorld.AllConnections[0];
            connection.RequestDisconnect();
            Assert.AreEqual(ConnectionState.State.Connected, connection.GetConnectionState(), "This is a deferred disconnect that'll happen in a system later, server side connection should still be marked as connected.");
            Assert.AreEqual(ConnectionState.State.Connected, testWorld.ClientWorlds[0].LocalConnection.GetConnectionState(), "Client shouldn't be aware yet it was disconnected");

            await testWorld.TickMultipleAsync(1);
            Assert.AreEqual(ConnectionState.State.Unknown, connection.GetConnectionState());
            Assert.AreEqual(0, testWorld.ServerWorld.AllConnections.Count);
            Assert.AreEqual(0, Netcode.AllConnections.Count);

            Assert.AreEqual(ConnectionState.State.Connected, testWorld.ClientWorlds[0].LocalConnection.GetConnectionState(), "Client shouldn't be aware yet it was disconnected");
            await testWorld.TickMultipleAsync(1);
            CheckConnected(testWorld, ConnectionState.State.Unknown, 0);
            // Test reconnecting
            additionalClientWorld.Connect(ClientConnectEndpoint);
            await testWorld.TickUntilConnectedAsync(additionalClientWorld);
            CheckConnected(testWorld, ConnectionState.State.Connected, 1);
            await TestMessageExchange(testWorld.ServerWorld, additionalClientWorld, testWorld);
        }

        void CleanupTestWorldAfterDispose(NetCodeTestWorld testWorld)
        {
            List<NetcodeWorld> newClientList = new();
            foreach (var clientWorld in testWorld.ClientWorlds)
            {
                if (clientWorld.ExistsAndIsCreated())
                    newClientList.Add(clientWorld);
            }

            testWorld.m_ClientWorlds = newClientList;

            List<NetcodeWorld> newServerList = new();
            foreach (var serverWorld in testWorld.m_ServerWorlds)
            {
                if (serverWorld.ExistsAndIsCreated())
                    newServerList.Add(serverWorld);
            }

            testWorld.m_ServerWorlds = newServerList;
        }

        [Test(Description = "This test makes sure we can shutdown client and server worlds and start again. This also makes sure we can run netcode worlds with no driver setup.")]
        public async Task TestShutdown_StartAgain([Values] bool disposeWorlds, [Values] bool withSpawnedGhosts, [Values] bool testIdempotence)
        {
            await using var testWorld = new NetCodeTestWorld();
            bool isHost = NetCodeTestWorld.OverrideUseSingleWorldHost;
            await testWorld.SetupGameObjectTest(clientCount: 0, serverCount: 0, singleWorldHostCount: 0, userSystems: typeof(TestMessageReceiver));

            StartServer();
            var additionalClientWorld = StartAndConnectNewClient(testWorld);
            await testWorld.TickUntilConnectedAsync(testWorld.ClientWorlds[0]);
            await TestMessageExchange(testWorld.ServerWorld, testWorld.ClientWorlds[0], testWorld);
            CheckConnected(testWorld, ConnectionState.State.Connected, 1);

            GameObject prefab = null;
            GameObject serverObj = null;
            if (withSpawnedGhosts)
            {
                prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("BasicData").gameObject;
                serverObj = GameObject.Instantiate(prefab);
                testWorld.ServerWorld.AllConnections[0].EnableGhostReplication(true);
                additionalClientWorld.LocalConnection.EnableGhostReplication(true);
                await testWorld.TickMultipleAsync(6);
                Assert.AreEqual(1, GetGhostObjectsForWorld(testWorld.ClientWorlds[0]).Count(), $"wrong ghost count on client world {testWorld.ClientWorlds[0]}");
            }

            Netcode.RequestDisconnectAllClients();
            await testWorld.TickAsync();
            Netcode.Shutdown(disposeWorlds);
            if (testIdempotence)
                Netcode.Shutdown(disposeWorlds);
            await testWorld.TickMultipleAsync(2); // required to let the connections clean up. 2 when there's spawns, since the ghost cleanup happens in the next BeginSimulation ECB

            if (withSpawnedGhosts)
            {
                var allGhosts = GameObject.FindObjectsByType<GhostObject>().Where(ghost => !ghost.IsPrefab()).ToArray();
                Assert.AreEqual(disposeWorlds ? 0 : 1, allGhosts.Length, "all ghosts should have been despawned. If we keep the server world, then we don't take ownership over those ghosts and let them be.");
            }

            if (disposeWorlds)
            {
                Assert.AreEqual(0, ClientServerBootstrap.ClientWorlds.Count);
                Assert.AreEqual(0, ClientServerBootstrap.ServerWorlds.Count);
                Assert.AreEqual(ConnectionState.State.Unknown, Netcode.LocalConnection.GetConnectionState());
            }
            else
            {
                Assert.LessOrEqual(additionalClientWorld.LocalConnection.GetConnectionState(), ConnectionState.State.Disconnected);
                if (NetCodeTestWorld.OverrideUseSingleWorldHost)
                    Assert.LessOrEqual(testWorld.ServerWorld.LocalConnection.GetConnectionState(), ConnectionState.State.Disconnected);
            }
            Assert.AreEqual(0, Netcode.AllConnections.Count);

            CleanupTestWorldAfterDispose(testWorld); // We're manipulating worlds and disposing them, this isn't what NetcodeTestWorld is used to, so we clean it up
            await testWorld.TickMultipleAsync(30); // test a few ticks to make sure there's no errors

            if (!disposeWorlds)
            {
                // make sure there's no server worlds that are listening, that they are really shutdown
                foreach (var world in World.All)
                {
                    if (world.IsServer() && world is NetcodeWorld netWorld)
                    {
                        Assert.IsFalse(netWorld.Listening());
                    }
                }
                CheckConnected(testWorld, ConnectionState.State.Unknown, 0);
                if (withSpawnedGhosts)
                {
                    Assert.AreEqual(1, GetGhostObjectsForWorld(testWorld.ServerWorld).Count(), "Netcode shouldn't destroy server side ghosts even when shutting everything down. Ghosts should be allowed to exist offline.");
                    GameObject.Destroy(serverObj); // Destroying for test sanity later
                }
            }

            StartServer();
            if (isHost)
                Assert.GreaterOrEqual(testWorld.ServerWorld.LocalConnection.GetConnectionState(), ConnectionState.State.Connecting);
            // Netcode.Connect(ClientConnectEndpoint); // TODO-next@mppmTests test with Netcode.Connect, this can't be tested locally
            if (disposeWorlds)
                additionalClientWorld = StartAndConnectNewClient(testWorld);
            else
                additionalClientWorld.Connect(ClientConnectEndpoint);
            await testWorld.TickUntilConnectedAsync(additionalClientWorld);
            CheckConnected(testWorld, ConnectionState.State.Connected, 1);
            await TestMessageExchange(testWorld.ServerWorld, additionalClientWorld, testWorld);

            if (withSpawnedGhosts)
            {
                var server = GameObject.Instantiate(prefab);
                testWorld.ServerWorld.AllConnections[0].EnableGhostReplication(true);
                additionalClientWorld.LocalConnection.EnableGhostReplication(true);
                await testWorld.TickMultipleAsync(6);
                foreach (var gameObject in GetGhostObjectsForWorld(testWorld.ClientWorlds[0]))
                {
                    Debug.Log($"{GhostObject.GetDebugName(gameObject.GetComponent<GhostObject>())}");
                }
                Assert.AreEqual(1, GetGhostObjectsForWorld(testWorld.ClientWorlds[0]).Count(), "final ghost count is wrong on client");
            }
        }

        // Forces a real UDP socket, which is unavailable on WebGL (no UDPNetworkInterface / socket bind), so this
        // failure-path test cannot run there.
        [UnityPlatform(exclude = new[] { RuntimePlatform.WebGLPlayer })]
        [Test(Description = "test various connection operation failures")]
        public async Task Test_OperationFailure()
        {
            var isHost = NetCodeTestWorld.OverrideUseSingleWorldHost;
            await using var testWorld = new NetCodeTestWorld();
            testWorld.ConnectTimeout = 1000;
            testWorld.MaxConnectAttempts = 1;
            testWorld.ForceUDPSocket = true;
            testWorld.DriverFixedTime = 100; // requires 10 ticks for triggering a timeout
            await testWorld.SetupGameObjectTest(serverCount: 0, clientCount: 0, singleWorldHostCount: 0);

            // Test listen on same port
            // This should work, as it's reusing the same world and resetting its driver
            if (isHost)
            {
                Assert.GreaterOrEqual(Netcode.StartAsHost(ServerListenEndpoint).GetConnectionState(), ConnectionState.State.Connecting);
                Assert.GreaterOrEqual(Netcode.StartAsHost(ServerListenEndpoint).GetConnectionState(), ConnectionState.State.Connecting, "driver store should have gotten reset and should have restarted the listen.");
            }
            else
            {
                Assert.IsTrue(Netcode.Listen(ServerListenEndpoint));
                Assert.IsTrue(Netcode.Listen(ServerListenEndpoint), "driver store should have gotten reset and should have restarted the listen.");
            }

            // A new world shouldn't work
            var newServer = testWorld.CreateServerWorld("Additional Server World");
            LogAssert.Expect(new Regex($".*Failed to bind UDP socket because the address is already in use. Likely because there is another process using port {k_TestPort}.*"));
            LogAssert.Expect(new Regex(".*Error trying to listen.*"));
            Assert.IsFalse(newServer.Listen(ServerListenEndpoint));

            // this shouldn't timeout, it should just fail, a server world can't connect
            Assert.Throws<InvalidOperationException>(() =>
            {
                newServer.Connect(ClientConnectEndpoint);
            });

            bool gotDisconnected = false;
            NetworkStreamDisconnectReason disconnectReason = default;
            Netcode.OnConnectionEvent += (connection, @event) =>
            {
                if (@event.State == ConnectionState.State.Disconnected)
                {
                    gotDisconnected = true;
                    disconnectReason = @event.DisconnectReason;
                }
            };
            testWorld.CreateAdditionalClientWorlds(1, tickWorldAfterCreation: false);
            testWorld.ClientWorlds[0].Connect(NetworkEndpoint.LoopbackIpv4.WithPort(k_TestPort - 1)); // test on invalid port, this should timeout
            await testWorld.TickMultipleAsync(14, dt: 0.1f);
            Assert.IsTrue(gotDisconnected, "didn't get expected timeout event in time");
            Assert.AreEqual(NetworkStreamDisconnectReason.MaxConnectionAttempts, disconnectReason, disconnectReason == default ? $"Got the default {default(NetworkStreamDisconnectReason)}" : "");
        }

        [Test(Description = "Test that the various callbacks are called at the right time")]
        public async Task Test_CallbacksAreCalled([Values] bool testWorldEvents, [Values] bool registerBeforeWorldCreation)
        {
            if (testWorldEvents && registerBeforeWorldCreation) Assert.Ignore("Invalid combination");
            // test on shutdown that ondisconnect is called
            var isHost = NetCodeTestWorld.OverrideUseSingleWorldHost;
            await using var testWorld = new NetCodeTestWorld();
            NetcodeConfig.Global.ConnectTimeoutMS = 1000;
            NetcodeConfig.Global.MaxConnectAttempts = 1;
            await testWorld.SetupGameObjectTest(serverCount: 0, clientCount: 0, singleWorldHostCount: 0);

            List<(Connection connection, NetcodeConnectionEvent ev, ConnectionState.State currentState, NetworkId connectionId)> eventHistory = new();
            void EventHandler(Connection connection, NetcodeConnectionEvent connectionEvent)
            {
                eventHistory.Add((connection, connectionEvent, connection.GetConnectionState(), connection.NetworkId));
            }

            const string normalError = "normal exception that shouldn't affect other events";
            void ExceptionEventHandler(Connection connection, NetcodeConnectionEvent connectionEvent)
            {
                throw new Exception(normalError);
            }

            for (int i = 0; i < 5; i++)
            {
                LogAssert.Expect(new Regex($".*{normalError}.*"));
            }

            if (registerBeforeWorldCreation)
            {
                Netcode.OnConnectionEvent += ExceptionEventHandler; // C# should guarantee this gets executed before the next one. If we don't handle exceptions properly, then the next one will be skipped
                Netcode.OnConnectionEvent += EventHandler;
            }

            using var server = testWorld.CreateServerWorld("server");
            testWorld.CreateAdditionalClientWorlds(1, tickWorldAfterCreation: false);
            using var client = testWorld.ClientWorlds[0];

            if (testWorldEvents)
            {
                server.OnConnectionEvent += ExceptionEventHandler; // C# should guarantee this gets executed before the next one. If we don't handle exceptions properly, then the next one will be skipped
                client.OnConnectionEvent += ExceptionEventHandler; // C# should guarantee this gets executed before the next one. If we don't handle exceptions properly, then the next one will be skipped
                server.OnConnectionEvent += EventHandler;
                client.OnConnectionEvent += EventHandler;
            }
            else if (!registerBeforeWorldCreation)
            {
                Netcode.OnConnectionEvent += ExceptionEventHandler; // C# should guarantee this gets executed before the next one. If we don't handle exceptions properly, then the next one will be skipped
                Netcode.OnConnectionEvent += EventHandler;
            }

            server.Listen(ServerListenEndpoint);

            client.Connect(ClientConnectEndpoint);
            await testWorld.TickUntilConnectedAsync(client);

            Netcode.OnConnectionEvent -= ExceptionEventHandler;

            Assert.GreaterOrEqual(5, eventHistory.Count);

            void CheckEvent(NetcodeWorld world, ConnectionState.State expectedState, int index, NetworkStreamDisconnectReason expectedDisconnectReason = default)
            {
                if (index >= eventHistory.Count)
                    Assert.Fail("missed an event! Expected event not there in test history");
                var eventToTest = eventHistory[index];
                Assert.IsTrue(eventToTest.connection.World.IsServer() == world.IsServer(), $"wrong IsServer, got {eventToTest.connection.World.IsServer()}");
                Assert.AreEqual(world, eventToTest.connection.World);
                Entity connectionEntity = default;
                if (world.IsHost())
                {
                    var connectionEntities = world.EntityManager.CreateEntityQuery(typeof(NetworkId)).ToEntityArray(Allocator.Temp);
                    foreach (var ent in connectionEntities)
                    {
                        if (!world.EntityManager.HasComponent<LocalConnection>(ent))
                            connectionEntity = ent;
                    }
                }
                else
                {
                    connectionEntity = testWorld.TryGetSingletonEntity<NetworkId>(world);
                }

                if (expectedState != ConnectionState.State.Disconnected && expectedState != ConnectionState.State.Unknown)
                {
                    Assert.AreEqual(connectionEntity, eventToTest.ev.ConnectionEntity);
                    Assert.AreEqual(connectionEntity, eventToTest.connection.ConnectionEntity);
                    if (!world.IsServer())
                        Assert.AreEqual(world.LocalConnection.ConnectionEntity, eventToTest.connection.ConnectionEntity);
                    else
                        Assert.AreEqual(world.AllConnections[0].ConnectionEntity, eventToTest.connection.ConnectionEntity);
                    Assert.AreEqual(eventToTest.connectionId, eventToTest.ev.Id); // when disconnected, you no longer have a NetworkId, so checking for LocalConnection.NetworkId is not valid
                }
                Assert.AreEqual(expectedState, eventToTest.currentState);
                if (expectedState == ConnectionState.State.Connected)
                    Assert.AreEqual(world.EntityManager.GetComponentData<NetworkId>(connectionEntity), eventToTest.connection.NetworkId);

                Assert.AreEqual(expectedDisconnectReason, eventToTest.ev.DisconnectReason);
            }
            CheckEvent(client, ConnectionState.State.Connecting, 0);
            CheckEvent(server, ConnectionState.State.Handshake, 1);
            CheckEvent(client, ConnectionState.State.Handshake, 2);
            CheckEvent(server, ConnectionState.State.Connected, 3);
            CheckEvent(client, ConnectionState.State.Connected, 4);

            eventHistory.Clear();

            // test client disconnecting itself
            client.Shutdown();
            await testWorld.TickMultipleAsync(3);
            CheckEvent(client, ConnectionState.State.Unknown, 0, NetworkStreamDisconnectReason.ConnectionClose);
            CheckEvent(server, ConnectionState.State.Unknown, 1, NetworkStreamDisconnectReason.ClosedByRemote);

            Assert.GreaterOrEqual(client.Connect(ClientConnectEndpoint).GetConnectionState(), ConnectionState.State.Connecting);
            await testWorld.TickUntilConnectedAsync(client);
            eventHistory.Clear();
            // test server kicking client
            server.DisconnectAClient(server.AllConnections[0]);
            await testWorld.TickMultipleAsync(3);
            CheckEvent(server, ConnectionState.State.Unknown, 0, NetworkStreamDisconnectReason.ConnectionClose);
            CheckEvent(client, ConnectionState.State.Unknown, 1, NetworkStreamDisconnectReason.ClosedByRemote);

            eventHistory.Clear();
            // make sure low level connect also triggers the appropriate events
            testWorld.GetSingletonRW<NetworkStreamDriver>(client).ValueRW.Connect(client.EntityManager, ClientConnectEndpoint);
            await testWorld.TickUntilConnectedAsync(client);

            CheckEvent(client, ConnectionState.State.Connecting, 0);
            CheckEvent(server, ConnectionState.State.Handshake, 1);
            CheckEvent(client, ConnectionState.State.Handshake, 2);
            CheckEvent(server, ConnectionState.State.Connected, 3);
            CheckEvent(client, ConnectionState.State.Connected, 4);
        }

        [Test(Description = "Test the various status APIs work, even if no Netcode.Something has been called")]
        public async Task Test_StatusAPIs()
        {
            // make sure to use driver side connection/listen APIs so that we test the status still work without the high level Netcode APIs
            bool isHost = NetCodeTestWorld.OverrideUseSingleWorldHost;
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest(serverCount: 0, clientCount: 0, singleWorldHostCount: 0);
            Assert.IsFalse(Netcode.IsActive);
            Assert.IsFalse(Netcode.IsClientRole);
            Assert.IsFalse(Netcode.IsServerRole);
            Assert.IsFalse(Netcode.LocalConnection.IsValid());

            testWorld.CreateServerWorld("test");
            Assert.IsFalse(Netcode.IsActive);
            Assert.IsFalse(Netcode.IsClientRole); // host has a fake connection, but since it's not listening, it's not "connected" to itself
            Assert.IsFalse(Netcode.IsServerRole); // server/host not listening yet
            Assert.IsTrue(Netcode.LocalConnection.IsValid() == isHost);

            testWorld.StartSeverListen();
            Assert.IsTrue(Netcode.IsActive);
            Assert.IsTrue(Netcode.IsClientRole == isHost);
            Assert.IsTrue(Netcode.IsServerRole);
            Assert.IsTrue(Netcode.LocalConnection.IsValid() == isHost);

            testWorld.CreateAdditionalClientWorlds(1, tickWorldAfterCreation: false);
            Assert.IsTrue(Netcode.IsActive);
            Assert.IsTrue(Netcode.IsClientRole == isHost);
            Assert.IsTrue(Netcode.IsServerRole);
            Assert.IsTrue(Netcode.LocalConnection.IsValid() == isHost);

            await testWorld.ConnectAsync(serverListen: false);
            Assert.IsTrue(Netcode.IsActive);
            Assert.IsTrue(Netcode.IsClientRole); // standalone client is now connected
            Assert.IsTrue(Netcode.IsServerRole);
            Assert.IsTrue(Netcode.LocalConnection.IsValid());

            Netcode.Shutdown();
            CleanupTestWorldAfterDispose(testWorld);
            Assert.IsFalse(Netcode.IsActive);
            Assert.IsFalse(Netcode.IsClientRole);
            Assert.IsFalse(Netcode.IsServerRole);
            Assert.IsFalse(Netcode.LocalConnection.IsValid());
        }

        [Test(Description = "Test listening on address different from AnyIPv4")]
        public async Task Test_Listen_NonDefaultAddress()
        {
            if (NetCodeTestWorld.OverrideUseSingleWorldHost)
                Assert.Ignore("This is a binary world test only, ignoring single world case");
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest(userSystems: typeof(TestMessageReceiver), clientCount: 0, serverCount: 0, singleWorldHostCount: 0);

            // make sure "0.0.0.0" works for binary world and that the generated client world is able to connect to it properly
            Netcode.StartAsHost(NetworkEndpoint.AnyIpv4.WithPort(k_TestPort), NetcodeConfig.HostWorldMode.BinaryWorlds);

            await testWorld.TickUntilConnectedAsync(testWorld.ClientWorlds[0]);
            Assert.AreNotEqual(testWorld.ServerWorld.SequenceNumber, testWorld.ClientWorlds[0].SequenceNumber);
            await TestMessageExchange(testWorld.ServerWorld, testWorld.ClientWorlds[0], testWorld);
        }

        [Test(Description = "Test various connection APIs work when there's already a world there")]
        public async Task Test_APIWorks_WithExistingWorlds()
        {
            if (NetCodeTestWorld.OverrideUseSingleWorldHost)
                Assert.Ignore("Handling host vs non-host manually for test simplicity");
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest(userSystems: typeof(TestMessageReceiver), clientCount: 1, serverCount: 1, singleWorldHostCount: 0);
            Assert.AreEqual(1, testWorld.m_ServerWorlds.Count);
            Assert.AreEqual(1, testWorld.ClientWorlds.Length);
            var server = testWorld.ServerWorld;
            var client = testWorld.ClientWorlds[0];
            Assert.AreNotEqual(server, client);

            Netcode.StartAsHost(ServerListenEndpoint, hostWorldMode: NetcodeConfig.HostWorldMode.BinaryWorlds);
            Assert.IsTrue(Netcode.LocalConnection.IsValid());
            Assert.AreEqual(1, testWorld.m_ServerWorlds.Count);
            Assert.AreEqual(1, testWorld.ClientWorlds.Length);
            Assert.AreEqual(server, testWorld.ServerWorld);
            Assert.AreEqual(client, testWorld.ClientWorlds[0]);

            testWorld.CreateAdditionalClientWorlds(1, tickWorldAfterCreation: false);
            var otherClient = testWorld.ClientWorlds[1];
            otherClient.Connect(ClientConnectEndpoint);
            await testWorld.TickUntilConnectedAsync(client);
            await testWorld.TickUntilConnectedAsync(otherClient);
            await TestMessageExchange(server, client, testWorld);
            await TestMessageExchange(server, otherClient, testWorld);
            Assert.AreEqual(Netcode.LocalConnection, client.LocalConnection);
            Assert.IsTrue(Netcode.LocalConnection.IsValid());

            Netcode.Shutdown();
            CleanupTestWorldAfterDispose(testWorld);
            Assert.AreEqual(0, testWorld.m_ServerWorlds.Count);
            Assert.AreEqual(0, testWorld.ClientWorlds.Length);

            testWorld.CreateWorlds(numHostWorlds: 1, numServer: 0, numClients: 1, tickWorldAfterCreation: false);
            server = testWorld.ServerWorld;
            Assert.IsTrue(server.IsHost());
            client = testWorld.ClientWorlds[0];
            Assert.IsFalse(client.IsServer());

            Netcode.StartAsHost(ServerListenEndpoint, hostWorldMode: NetcodeConfig.HostWorldMode.SingleWorld);
            Assert.AreEqual(1, testWorld.m_ServerWorlds.Count);
            Assert.AreEqual(1, testWorld.ClientWorlds.Length);
            Assert.IsTrue(Netcode.LocalConnection.IsValid());
            client.Connect(ClientConnectEndpoint);
            Assert.AreEqual(1, testWorld.m_ServerWorlds.Count);
            Assert.AreEqual(1, testWorld.ClientWorlds.Length);
            await testWorld.TickUntilConnectedAsync(client);
            await TestMessageExchange(server, client, testWorld);
            Assert.AreNotEqual(Netcode.LocalConnection, client.LocalConnection);
            Assert.AreEqual(Netcode.LocalConnection, server.LocalConnection);
            Assert.IsTrue(Netcode.LocalConnection.IsValid());

            Netcode.Shutdown();
            CleanupTestWorldAfterDispose(testWorld);
            Assert.AreEqual(0, testWorld.m_ServerWorlds.Count);
            Assert.AreEqual(0, testWorld.ClientWorlds.Length);

            testWorld.CreateWorlds(numHostWorlds: 0, numServer: 1, numClients: 1, tickWorldAfterCreation: false);
            server = testWorld.ServerWorld;
            Assert.IsFalse(server.IsHost());
            Assert.IsTrue(server.IsServer());
            client = testWorld.ClientWorlds[0];
            Assert.IsTrue(client.IsClient());
            Assert.AreNotEqual(client, server);

            Netcode.Listen(ServerListenEndpoint);
            Assert.AreEqual(1, testWorld.m_ServerWorlds.Count);
            Assert.AreEqual(1, testWorld.ClientWorlds.Length);
            Netcode.Connect(ClientConnectEndpoint);
            Assert.AreEqual(Netcode.LocalConnection, client.LocalConnection);
            Assert.IsTrue(Netcode.LocalConnection.IsValid());
            Assert.AreEqual(1, testWorld.m_ServerWorlds.Count);
            Assert.AreEqual(1, testWorld.ClientWorlds.Length);
            Assert.AreNotEqual(client, server);
            Assert.AreEqual(client, testWorld.ClientWorlds[0]);
            Assert.AreEqual(server, testWorld.m_ServerWorlds[0]);
            await testWorld.TickUntilConnectedAsync(client);
            await TestMessageExchange(server, client, testWorld);
            Assert.AreEqual(Netcode.LocalConnection, client.LocalConnection);
            Assert.IsTrue(Netcode.LocalConnection.IsValid());

            int netWorldCount = 0;
            foreach (var world in World.All)
            {
                if (world.IsServer() || world.IsClient() || world.IsThinClient())
                    netWorldCount++;
            }
            Assert.AreEqual(2, netWorldCount);
        }

        [Test(Description = "Make sure active world is set to another potential world as soon is it gets destroyed")]
        public async Task Test_OnWorldDispose_ActiveWorldStillThere()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            Assert.AreEqual(testWorld.ServerWorld, Netcode.Instance.m_ActiveWorld);
            testWorld.ServerWorld.Dispose();
            Assert.AreEqual(testWorld.ClientWorlds[0], Netcode.Instance.m_ActiveWorld);
            testWorld.ClientWorlds[0].Dispose();
            Assert.AreEqual(null, Netcode.Instance.m_ActiveWorld);
        }

        [Test]
        public async Task Test_ListenDifferentAddresses()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest(userSystems: typeof(TestMessageReceiver));

            var firstListenEndpoint = NetworkEndpoint.AnyIpv4.WithPort(6666);
            Assert.IsTrue(Netcode.Listen(firstListenEndpoint));
            var server = testWorld.ServerWorld;

            NetworkStreamDriver serverDriverComponent;
            NetworkStreamDriver clientDriverComponent;
            serverDriverComponent = testWorld.GetSingleton<NetworkStreamDriver>(server);
            Assert.AreEqual(firstListenEndpoint, serverDriverComponent.LastEndPoint);
            testWorld.CreateAdditionalClientWorlds(1, tickWorldAfterCreation: false);
            var client = testWorld.ClientWorlds[0];

            var clientFirstEndpoint = NetworkEndpoint.LoopbackIpv4.WithPort(6666);
            client.Connect(clientFirstEndpoint);
            clientDriverComponent = testWorld.GetSingleton<NetworkStreamDriver>(client);
            Assert.AreEqual(clientFirstEndpoint, clientDriverComponent.LastEndPoint);

            await testWorld.TickUntilConnectedAsync(client);
            await TestMessageExchange(server, client, testWorld);
            clientDriverComponent = testWorld.GetSingleton<NetworkStreamDriver>(client);
            Assert.AreEqual(clientFirstEndpoint, clientDriverComponent.LastEndPoint);

            client.Shutdown();
            await testWorld.TickMultipleAsync(4);
            server.Shutdown();

            serverDriverComponent = testWorld.GetSingleton<NetworkStreamDriver>(server);
            Assert.AreEqual(firstListenEndpoint, serverDriverComponent.LastEndPoint);

            var secondListenEndpoint = NetworkEndpoint.LoopbackIpv4.WithPort(7777);
            var secondConnectEndpoint = NetworkEndpoint.LoopbackIpv4.WithPort(7777);
            Assert.IsTrue(Netcode.Listen(secondListenEndpoint));
            serverDriverComponent = testWorld.GetSingleton<NetworkStreamDriver>(server);
            Assert.AreEqual(secondListenEndpoint, serverDriverComponent.LastEndPoint);
            client.Connect(secondConnectEndpoint);
            clientDriverComponent = testWorld.GetSingleton<NetworkStreamDriver>(client);
            Assert.AreEqual(secondConnectEndpoint, clientDriverComponent.LastEndPoint);
            await testWorld.TickUntilConnectedAsync(client);
            await TestMessageExchange(server, client, testWorld);
            clientDriverComponent = testWorld.GetSingleton<NetworkStreamDriver>(client);
            Assert.AreEqual(secondConnectEndpoint, clientDriverComponent.LastEndPoint);
        }

        // TODO-next@mppmTests: have a "lobby" step waiting for players to be ready before enabling state replication
        // TODO-next@mppmTests: will need mppm tests for testing static active world. Should test all APIs relying on m_ActiveWorld
        // TODO-next@mppmTests try changing the role of a device. Client device becomes the host. Won't work if try to reuse a host world to connect as a client world. Should just guide users to use disposeWorld=true in that case?
        // TODO-next@mppmTests: test Netcode.Connect can reuse an existing client world.

        // TODO-next@connection Test calling APIs from ECS systems
        // TODO-next@connection Test shutdown while in OnDisconnect? Can't kill world while system is running, what would be the pattern for users to shutdown with a world dispose when being disconnected by the server?

        // TODO-next@connection figure out bootstrapping flow and first time user experience. Right now if I disable our bootstrapper, the default one will create a world that corresponds to the target role. If both are clientAndServer, then if you click the connect button it'll fail with an error saying a host can't connect. We really need to completely remove world creation. Or go full in and have a default port setup as well to allow full connection in a default scene. We can't have this in between like we do right now.

    }
}

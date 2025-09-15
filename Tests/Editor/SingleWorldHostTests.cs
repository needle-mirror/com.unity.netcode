using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

// TODO most of these tests should be useless once we have a global flag for single world host testing
namespace Unity.NetCode.Tests
{
    // little util to make test sequence clearer
    // [AutoStaticsCleanup]
    [DisableAutoCreation]
    internal partial class GenericExecuteOnUpdateSystem : SystemBase
    {
        public delegate void ExecOnUpdateDelegate(World world);

        public static ExecOnUpdateDelegate ExecOnUpdate;

        protected override void OnUpdate()
        {
            ExecOnUpdate?.Invoke(this.World);
        }

        protected override void OnDestroy()
        {
            ExecOnUpdate = null;
        }
    }

    internal class SingleWorldHostTests
    {
        // TODO-release should add tests for
        // GhostOwnerIsLocal behaviour change
        // LocalConnection
        // No NetworkStreamConnection
        // Fake connection event
        // Fake Disconnect events?
        // Check client system executes in same world as server system
        // input tick being +1 vs server tick when in off frames
        // pending RPC for disconnected clients, while disconnecting the host?
        // test passthrough RPCs, with custom serialization
        // test stripping is done appropriately
        // test spawn while in off frame, make sure spawn tick is set correctly


        [Test]
        [TestCase(true, Ignore = "not implemented yet")]
        [TestCase(false)]
        public void SingleWorldHostValueChecks(bool useNetcodeAPI)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true);

            // if (useNetcodeAPI)
            // {
            //     Netcode.Server.StartAsHost(NetworkEndpoint.AnyIpv4.WithPort(9999), hostWorldMode: NetCodeConfig.HostWorldMode.SingleWorld);
            // }
            // else
            {
                testWorld.CreateWorlds(server: false, numClients: 0, numHostWorlds: 1);
                testWorld.Connect(withConnectionState: true);
            }

            Assert.That(ClientServerBootstrap.ClientWorld, Is.Not.Null);
            Assert.That(ClientServerBootstrap.ClientWorld.IsClient(), Is.True);
            Assert.That(ClientServerBootstrap.ClientWorld.IsServer(), Is.True);
            Assert.That(ClientServerBootstrap.ServerWorld, Is.Not.Null);
            Assert.That(ClientServerBootstrap.ServerWorld.IsClient(), Is.True);
            Assert.That(ClientServerBootstrap.ServerWorld.IsServer(), Is.True);
            Assert.That(ClientServerBootstrap.ClientWorld.IsHost(), Is.True);
            Assert.That(ClientServerBootstrap.ServerWorld.IsHost(), Is.True);
            Assert.That(ClientServerBootstrap.ServerWorld, Is.EqualTo(ClientServerBootstrap.ClientWorld));
            Assert.That(ClientServerBootstrap.ClientWorlds.Count, Is.EqualTo(1));
            if (useNetcodeAPI)
            {
                // Assert.That(Netcode.Server.Connections.Count, Is.EqualTo(1));
                // Assert.That(Netcode.Client.Connection, Is.EqualTo(Netcode.Server.Connections[0]));
                // Assert.That(Netcode.Client.Connection.IsValid);
                // Assert.That(Netcode.Client.Connection.GetConnectionState(), Is.EqualTo(ConnectionState.State.Connected));
                // Assert.That(Netcode.IsClientRole, Is.True);
                // Assert.That(Netcode.IsServerRole, Is.True);
                // Assert.That(Netcode.IsActive, Is.True);
            }
            else
            {
                var serverConnectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkId));
                Assert.AreEqual(1, serverConnectionQuery.CalculateEntityCount());
            }
        }

        // TODO-next uncomment once we have ghost adapter backported
        // [UnityTest]
        // public IEnumerator SimpleTest([Values] bool useSingleWorld, [Values] bool useRemotes)
        // {
        //     using var testWorld = new NetCodeTestWorld();
        //     using var _ = new TestRPCSend(); // for auto static cleaning
        //     testWorld.Bootstrap(includeNetCodeSystems: true, userSystems: typeof(SimpleMessageHandler));
        //     testWorld.CreateWorlds(server: !useSingleWorld, numClients: useSingleWorld ? 0 : 1, numHostWorlds: useSingleWorld ? 1 : 0);
        //     testWorld.Connect(isHost: useSingleWorld);
        //
        //     var firstNetworkId = Netcode.Client.Connection.NetworkId;
        //
        //     if (useRemotes)
        //     {
        //         TestRPCSend.SendTestMessage(123);
        //         testWorld.RunTicks(3);
        //         yield return testWorld.RunYieldUpdates(3); // requires yield since currently remotes don't update in systems, but using the internal main loop
        //         Assert.That(TestRPCSend.TestMessageReceivedCount, Is.EqualTo(1));
        //     }
        //     else
        //     {
        //         var clientWorld = testWorld.ClientWorlds[0];
        //         var serverWorld = testWorld.ServerWorld;
        //
        //         TestMessageExchange(clientWorld, serverWorld, testWorld);
        //     }
        //
        //     testWorld.CreateAdditionalClientWorlds(1);
        //     World pureClientWorld = testWorld.ClientWorlds[1];
        //     testWorld.GetSingletonRW<NetworkStreamDriver>(pureClientWorld).ValueRW.Connect(NetworkEndpoint.LoopbackIpv4.WithPort(7979));
        //     testWorld.TickUntilConnected(pureClientWorld);
        //
        //     // TODO remotes aren't built for multi world testing. reenable this once they do
        //     // if (useRemotes)
        //     // {
        //     //     testWorld.GoInGame(pureClientWorld); // todo not required?
        //     //     TestRPCSend.SendToClientsMessage(123);
        //     //     testWorld.RunTicks(3);
        //     //     yield return testWorld.RunYieldUpdates(3);
        //     //     Assert.That(TestRPCSend.TestMessageReceivedCountFromServer, Is.EqualTo(2), "wrong server to client remote call count");
        //     // }
        //     // else
        //     {
        //         TestMessageExchange(ClientServerBootstrap.ServerWorld, pureClientWorld, testWorld);
        //     }
        //
        //     Assert.That(Netcode.Client.Connection.NetworkId, Is.EqualTo(firstNetworkId), "Netcode.Client.Connection should be always the same on a single world host and shouldn't change when other clients connect");
        // }

        public struct TestRPC : IRpcCommand
        {
            public int value;
        }

        [Test]
        public void RPCs_InSingleWorldHost_WorksTheSame([Values] bool useSingleWorld)
        {
            // makes sure RPCs work the same way with both modes
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true);
            testWorld.CreateWorlds(server: !useSingleWorld, numClients: useSingleWorld ? 0 : 1, numHostWorlds: useSingleWorld ? 1 : 0);
            testWorld.Connect();

            int valueToTest = 123;

            var serverEntity = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(TestRPC), typeof(SendRpcCommandRequest));
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new TestRPC(){value = valueToTest});
            testWorld.TickMultiple(2);

            World worldToCheck;
            if (useSingleWorld)
                worldToCheck = testWorld.ServerWorld;
            else
                worldToCheck = testWorld.ClientWorlds[0];
            var clientQuery = worldToCheck.EntityManager.CreateEntityQuery(typeof(TestRPC), typeof(ReceiveRpcCommandRequest));
            Assert.AreEqual(valueToTest, clientQuery.GetSingleton<TestRPC>().value);
        }

        [Test, Description("Sanity check to make sure the test world setup is as expected")]
        public void NetcodeTestWorld_SanityCheck_WhenUsingSingleWorld()
        {
            var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(GenericExecuteOnUpdateSystem));
            testWorld.CreateWorlds(server: false, numClients: 0, numHostWorlds: 1);
            // Assert.That(testWorld.ServerWorld, Is.EqualTo(testWorld.ClientWorlds[0]));

            // Validate that there's no system duplicates
            var allServerSystems = testWorld.ServerWorld.Unmanaged.GetAllSystems(Allocator.Temp);
            HashSet<SystemHandle> systemSet = new HashSet<SystemHandle>(allServerSystems);
            Assert.That(systemSet.Count, Is.EqualTo(allServerSystems.Length), "duplicate found!");

            // check that a system ticks only once
            var updateCount = 0;

            void CountUpdates(World _)
            {
                updateCount++;
            }

            GenericExecuteOnUpdateSystem.ExecOnUpdate += CountUpdates;
            testWorld.Tick();
            GenericExecuteOnUpdateSystem.ExecOnUpdate -= CountUpdates;
            Assert.That(updateCount, Is.EqualTo(1));
            testWorld.Dispose();
            // make sure this is cleaned up correctly
            Assert.AreEqual(0, ClientServerBootstrap.ServerWorlds.Count);
            Assert.AreEqual(0, ClientServerBootstrap.ClientWorlds.Count);
        }

        [Test]
        public void SingleWorldHost_PartialSnapshot_Works([Values] bool useSingleWorld)
        {
            // single world host changes the way GhostSendSystem works.
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(GenericExecuteOnUpdateSystem));

            var ghostGameObject = new GameObject("Ghost");
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(server: !useSingleWorld, numClients: 1, numHostWorlds: useSingleWorld ? 1 : 0);
            var prefabCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            var prefab = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection)[0].Value;
            testWorld.Connect(maxSteps: 16);
            testWorld.GoInGame();
            testWorld.TickMultiple(100); // stabilize

            int ghostCount = 200;
            using var entities = testWorld.ServerWorld.EntityManager.Instantiate(prefab, ghostCount, Allocator.Persistent);

            testWorld.TickMultiple(3);
            var clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance)).ToEntityArray(Allocator.Temp);
            Assert.IsTrue(clientGhosts.Length < ghostCount);
            Assert.IsTrue(0 < clientGhosts.Length);
            testWorld.Tick();
            clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance)).ToEntityArray(Allocator.Temp);
            Assert.AreEqual(ghostCount, clientGhosts.Length);
        }
    }
}

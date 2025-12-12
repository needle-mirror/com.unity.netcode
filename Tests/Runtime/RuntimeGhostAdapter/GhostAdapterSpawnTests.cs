#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    internal class GhostAdapterSpawnTests
    {
        // Can use await null for Update() and await new WaitForFixedUpdate() for fixed update.
        // But that should be the exception. Hopefully our design should be System centric enough to be ticked by NetcodeTestWorld
        // We still need playmode tests to trigger Awake, Start, etc. But we should be able to tick the world and not rely on await null
        // TODO-next reenable this once we have proper connection APIs
        // [Test(Description = "Basic integration test. Tests connect, spawn cube, move it, for host and DGS"]
        // public async Task TestCubeWithHostOrServer([Values] bool testWithHost)
        // {
        //     // This tests a pure client and a server/host connecting to each other.
        //
        //     // Test Setup
        //     // LogAssert.ignoreFailingMessages = true; // todo-next playmode window null refs right now if you await null in a test. current test
        //     // doesn't yield, but future tests should look into this
        //
        //     // can't use testWorld.Connect since we have custom startup with StartAsHost
        //     var clientConfig = Netcode.Client.Config;
        //
        //     if (testWithHost)
        //     {
        //         Netcode.Server.StartAsHost(NetworkEndpoint.LoopbackIpv4.WithPort(kTestPort));
        //     }
        //     else
        //     {
        //         Netcode.Server.Listen(NetworkEndpoint.AnyIpv4.WithPort(kTestPort));
        //     }
        //
        //     // TODO-next: Create thin client instead some client specific for testing
        //     var otherClient = Netcode.CreateClient(clientConfig);
        //     Netcode.StartClient(otherClient);
        //
        //     await TestWorld.TickUntilConnectedAsync(otherClient.world);
        //     if (testWithHost)
        //     {
        //         await TestWorld.TickUntilConnectedAsync(ClientServerBootstrap.ClientWorld);
        //     }
        //     TestWorld.GoInGame();
        //
        //     Assert.That(Netcode.Server.Connections.Count, Is.EqualTo(testWithHost ? 2 : 1));
        //
        //     // check otherClient doesn't have cube yet
        //     Assert.That(TestMoveCube.CubeInstances, Is.EqualTo(0));
        //
        //     // server spawns cube
        //     // var cubePrefab = Resources.Load<TestMoveCube>("BasicCube");
        //     //*
        //     var serverCube = GhostAdapterUtils.CreateGhostAdapterMockWithPrefabSetup<TestMoveCube>("ServerCube"); // in theory we could enable this test to run in builds as well, it doesn't use editor only features
        //
        //     serverCube.name = "ServerCube";
        //     Assert.That(serverCube.isActiveAndEnabled, Is.True);
        //     Assert.That(serverCube.BelongTo(TestWorld.ServerWorld), Is.True);
        //     Assert.That(serverCube.transform.position, Is.EqualTo(Vector3.zero));
        //
        //     // client sees cube spawned
        //     await TestWorld.TickMultipleAsync(6); // 1 tick for prefab syncing, 3 for spawn, 2 for interpolated spawn
        //
        //     var foundClient = false;
        //     var foundServer = false;
        //     TestMoveCube otherClientCube = null;
        //     foreach (var oneCube in GameObject.FindObjectsByType<TestMoveCube>(FindObjectsSortMode.None))
        //     {
        //         if (oneCube.BelongTo(otherClient.world))
        //         {
        //             foundClient = true;
        //             otherClientCube = oneCube;
        //         }
        //
        //         if (oneCube.BelongTo(ClientServerBootstrap.ServerWorld))
        //         {
        //             foundServer = true;
        //         }
        //     }
        //
        //     Assert.True(foundServer, "couldn't find a server cube belonging to the right server world");
        //     Assert.True(foundClient, "couldn't find a client cube belonging to the right client world");
        //     Assert.That(TestMoveCube.CubeInstances, Is.EqualTo(testWithHost ? 3 : 2)); // server + client
        //
        //     // server moves cube
        //     await TestWorld.TickMultipleAsync(30); // cube script moves itself in its prediction update and is ticked by TestWorld :)
        //     Assert.That(serverCube.transform.position, Is.EqualTo(Vector3.one));
        //     // client sees the cube interpolated to destination within expected time
        //     Assert.That(otherClientCube.transform.position, Is.EqualTo(Vector3.one));
        //     Assert.That(otherClientCube, Is.Not.EqualTo(serverCube));
        //
        //     // client can't move its own cube
        //     otherClientCube.transform.position = Vector3.zero;
        //     await TestWorld.TickMultipleAsync(30);
        //     Assert.That(otherClientCube.transform.position, Is.EqualTo(Vector3.one));
        // }

        [Test]
        [Ignore("TODO-release No support for thin clients yet with PrefabRegistry")]
        public async Task TestClientWithThinClients([Values] bool testHost)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

#if UNITY_EDITOR
            Assert.That(MultiplayerPlayModePreferences.RequestedNumThinClients, Is.EqualTo(0), "Sanity check failed, must not have any thin clients configured to run via Multiplayer PlayMode Tools");
#endif
            // Test setup
            testWorld.UseFakeSocketConnection = testHost ? 0 : 1;
            await testWorld.CreateWorldsAsync(false, 1, useThinClients:true, throwIfWorldsAlreadyExist: false); // extra world

            var thinClientWorld = testWorld.ClientWorlds[^1];
            if (testHost)
            {
                var ep = NetworkEndpoint.LoopbackIpv4.WithPort(7979);
                throw new NotImplementedException();
                // Assert.True(Netcode.Server.StartAsHost(ep));
                // Assert.NotNull(testWorld.ServerWorld);
                // await testWorld.TickUntilConnectedAsync(ClientServerBootstrap.ClientWorld);
                // var driverQuery = thinClientWorld.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<NetworkStreamDriver>());
                // driverQuery.GetSingleton<NetworkStreamDriver>().Connect(thinClientWorld.EntityManager, ep);
                // await testWorld.TickUntilConnectedAsync(thinClientWorld);
                // testWorld.GoInGame();
            }
            else
                await testWorld.ConnectAsync(enableGhostReplication: true); // this does a lot of the boilerplate of connecting, ticking, enabling replication

            // Execute the test
            var cubePrefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "BasicCube", typeof(TestMoveCube)).GetComponent<TestMoveCube>();

            var serverCube = GameObject.Instantiate<TestMoveCube>(cubePrefab);
            serverCube.name = "ServerCube";
            Assert.That(serverCube.isActiveAndEnabled, Is.True);
            Assert.That(serverCube.BelongTo(testWorld.ServerWorld), Is.True);
            Assert.That(serverCube.transform.position, Is.EqualTo(Vector3.zero));

            // tick until cubes are spawned client side
            await testWorld.TickMultipleAsync(6); // 1 tick for prefab syncing, 3 for spawn, 2 for interpolated spawn

            var foundClient = false;
            var foundServer = false;
            TestMoveCube otherClientCube = null;
            var cubes = GameObject.FindObjectsByType<TestMoveCube>(FindObjectsSortMode.None);
            Assert.That(cubes.Length, Is.EqualTo(2)); // Thin client spawns no ghosts
            foreach (var oneCube in cubes)
            {
                if (oneCube.BelongTo(ClientServerBootstrap.ClientWorld))
                {
                    foundClient = true;
                    otherClientCube = oneCube;
                }

                if (oneCube.BelongTo(ClientServerBootstrap.ServerWorld))
                {
                    foundServer = true;
                }
            }

            Assert.True(foundServer, "couldn't find a server cube belonging to the right server world");
            Assert.True(foundClient, "couldn't find a client cube belonging to the right client world");
            Assert.That(TestMoveCube.CubeInstances, Is.EqualTo(2)); // 1 clients, 1 server. no pseudo prefab, so no noise :)

            // server moves cube
            await testWorld.TickMultipleAsync(30);

            // client sees the cube interpolated to destination within expected time
            Assert.That(serverCube.transform.position, Is.EqualTo(Vector3.one));
            Assert.That(otherClientCube.transform.position, Is.EqualTo(Vector3.one));

            // cubes are all different instances
            Assert.That(otherClientCube, Is.Not.EqualTo(serverCube));
            // clients can't move its own cube
            otherClientCube.transform.position = Vector3.zero;

            await testWorld.TickMultipleAsync(30);
            Assert.That(otherClientCube.transform.position, Is.EqualTo(Vector3.one));

            Assert.That(Netcode.Server.Connections.Count, Is.EqualTo(2)); // Server sees 2 client connections (thin client did indeed connect)
        }

        [Test(Description = "Test that an inactive prefab can be spawned and activated later")]
        public async Task TestInactivePrefab()
        {
            // Test setup
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true); // this does a lot of the boilerplate of connecting, ticking, enabling replication

            var prefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "Empty", typeof(EmptyBehaviour));
            prefab.SetActive(false);

            Assert.That(prefab.gameObject.activeInHierarchy, Is.False);

            EmptyBehaviour serverCube = GameObject.Instantiate(prefab).GetComponent<EmptyBehaviour>();
            serverCube.gameObject.SetActive(true); // triggers the ghost initialization
            // TODO-release the above SetActive isn't synced properly with entities integration. But then, what does it mean to have a Disabled prefab entities side for Netcode? If we instantiate that prefab client side, it'll be disabled and not processed. Should we sync Disabled status as well and make sure if the server side entity is enabled, it's enabled as well client side? How will that mess with pooling if we go that route? We need to come back to this.
            await testWorld.TickMultipleAsync(6); // 1 tick for prefab syncing, 3 for spawn, 2 for interpolated spawn
            var foundObjects = GameObject.FindObjectsByType<EmptyBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Assert.That(foundObjects.Length, Is.EqualTo(2), "found object count mismatch, expecting 1 client and 1 server object in total"); // 1 server, 1 client
            EmptyBehaviour clientCube = null;

            foreach (var foundObject in foundObjects)
            {
                if (foundObject.Ghost.World == testWorld.ServerWorld)
                {
                    Assert.That(foundObject, Is.EqualTo(serverCube), $"rogue object found: {foundObject.gameObject.name} entity is {foundObject.Ghost.Entity}");
                }
                else if (foundObject.Ghost.World == testWorld.ClientWorlds[0])
                {
                    clientCube = foundObject;
                    Assert.That(foundObject.gameObject.activeInHierarchy, Is.False);
                }
                else
                {
                    Assert.Fail("found unexpected object");
                }
            }

            clientCube.gameObject.SetActive(true);

            await testWorld.TickMultipleAsync(30); // spend some time

            Assert.That(clientCube.gameObject.activeInHierarchy, Is.True);
            Assert.That(clientCube.m_Ghost.World, Is.EqualTo(testWorld.ClientWorlds[0]));

            // move cube and make sure it replicates client side
            Assert.That(serverCube.transform.position, Is.EqualTo(Vector3.zero));
            Assert.That(clientCube.transform.position, Is.EqualTo(Vector3.zero));
            serverCube.transform.position = Vector3.one;
            await testWorld.TickMultipleAsync(30);
            Assert.That(serverCube.transform.position, Is.EqualTo(Vector3.one));
            Assert.That(clientCube.transform.position, Is.EqualTo(Vector3.one));
            Assert.That(clientCube.m_Ghost.World, Is.EqualTo(testWorld.ClientWorlds[0]));
            Assert.That(serverCube.m_Ghost.World, Is.EqualTo(testWorld.ServerWorld));
            Assert.That(clientCube, Is.Not.EqualTo(serverCube));
        }

        [Test]
        public async Task InactiveObjectStaysNonTracked()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true);

            var prefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "BasicCube", typeof(TestMoveCube));

            prefab.gameObject.SetActive(false);
            Assert.IsFalse(prefab.gameObject.activeSelf, "sanity check failed, prefab should be inactive");

            var serverCube = GameObject.Instantiate(prefab);
            Assert.IsFalse(serverCube.gameObject.activeSelf, "sanity check failed, if prefab is inactive, instantiated object should be inactive too");
            await testWorld.TickMultipleAsync(30); // spend some time, make sure it's not spawned client side
            Assert.That(GameObject.FindObjectsByType<GhostAdapter>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(1), "found ghost adapters count mismatch");
        }

        [Category(NetcodeTestCategories.Foundational)]
        [Test(Description = "Sanity check to see if skipping auto registration works")]
        public async Task Test_SkipAutoRegistration([Values] bool skip)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicCube", skipAutoRegistration: skip);
            // if skip is false, this should be ignored, the prefab should be already registered and initialized by this point and it should use the default of false
            prefab.GetComponent<GhostAdapter>().HasOwner = true;
            Netcode.RegisterPrefab(prefab.gameObject); // if skip is false, this should no-op
            var serverObj = GameObject.Instantiate(prefab);
            await testWorld.TickAsync();

            Assert.IsTrue(serverObj.Ghost.World.EntityManager.HasComponent<GhostOwner>(serverObj.Ghost.Entity) == skip, $"skip is {skip}, which means we should{(skip ? "" : "n't")} have been able to customize the prefab before registering.");
        }

        [Test(Description = "OnDestroy is called even when SetActive is false, so this should work fine, but sanity checking with this test to make sure this assumption holds true.")]
        public async Task GameObjectDespawn_WorksWhen_GOIsInactive()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicCube", skipAutoRegistration: true);
            prefab.GetComponent<GhostAdapter>().DefaultGhostMode = GhostMode.Predicted; // to get the spawn/despawn faster with no interpolation delays
            Netcode.RegisterPrefab(prefab.gameObject);
            var predictionCallbackHelper = prefab.GetComponent<PredictionCallbackHelper>();
            predictionCallbackHelper.CallbackHolder.OnAwake += o =>
            {
                GhostEntityMapping.AcquireEntityReferenceGameObject(o.GetEntityId(), o.transform.GetEntityId(), prefab.GetEntityId(), GhostSpawningContext.Current.GetWorld());
            };
            GameObject serverObject = GameObject.Instantiate(prefab).gameObject;

            void OnDestroy(GameObject go)
            {
                // If on destroy isn't called, then this reference won't be released and entity destruction won't happen
                GhostEntityMapping.ReleaseGameObjectEntityReference(go.GetEntityId(), go.WorldExt().IsCreated);
            }

            serverObject.GetComponent<PredictionCallbackHelper>().OnDestroyEvent += OnDestroy;
            await testWorld.TickMultipleAsync(4);
            GameObject clientObject = PredictionCallbackHelper.ClientInstances[0].gameObject;
            clientObject.GetComponent<PredictionCallbackHelper>().OnDestroyEvent += OnDestroy;

            clientObject.gameObject.SetActive(false);
            GameObject.Destroy(serverObject);
            await testWorld.TickMultipleAsync(4);
            var clientGhostQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
            var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance));
            Assert.IsTrue(clientGhostQuery.IsEmpty);
            Assert.IsTrue(serverGhostQuery.IsEmpty);
            Assert.IsTrue(clientObject == null, "client object should have been despawned");
            Assert.IsTrue(serverObject == null, "server object should have been despawned");

        }
    }
}
#endif

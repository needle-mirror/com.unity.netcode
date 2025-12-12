#if UNITY_EDITOR
using System;
using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using Object = UnityEngine.Object;

namespace Unity.NetCode.Tests
{
    internal class GhostAdapterGameObjectTrackingTests
    {
        static void VerifyLength(int length, PerWorldIndexedTransformTrackingSingleton transformTracking)
        {
            Assert.AreEqual(length, transformTracking.m_Transforms.length);
            Assert.AreEqual(length, transformTracking.m_EntitiesForTransforms.Length);
            Assert.AreEqual(length, transformTracking.m_IndexedGameObjectIds.Length);
        }

        [Test]
        public async Task SpawnedGameObjectAreTrackedCorrectlyByServer()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            var cubePrefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "BasicCube",
                typeof(TestMoveCube));

            //Instantiate on the server
            List<GameObject> serverInstances = new List<GameObject>();
            for (int i = 0; i < 50; ++i)
                serverInstances.Add(Object.Instantiate(cubePrefab));

            for (int i = 0; i < 50; ++i)
            {
                var ga = serverInstances[i].GetComponent<GhostAdapter>();
                Assert.AreNotEqual(Entity.Null, ga.Entity);
                Assert.IsNotNull(ga.World);
                Assert.IsTrue(ga.World.IsServer());
            }

            // The ghost entity itself is initialised but its GhostInstance.Id, GhostInstance.SpawnTick and
            // Type are not yet. The GhostSendSystem is responsible for doing that later on (this is also true in ECS)
            // Users should not care about this in general.
            // All gameobject should have been registered and all entities should have a state
            // regardless if there are client connected or not
            var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostInstance>());
            Assert.IsFalse(query.IsEmpty, "The GhostInstance should have been added to the created entities");
            Assert.AreEqual(50, query.CalculateEntityCount());
            var transformTracking = testWorld.GetSingleton<PerWorldIndexedTransformTrackingSingleton>(testWorld.ServerWorld);
            VerifyLength(50, transformTracking);
            foreach (var go in serverInstances)
            {
                var ent = go.GetComponent<GhostAdapter>().Entity;
                Assert.IsTrue(transformTracking.m_IndexedGameObjectIds.Contains(GhostEntityMapping.GameObjectKey.GetForGameObject(go.GetEntityId())), $"the m_GameObject list should contain {go.name}");
                Assert.IsTrue(transformTracking.m_EntitiesForTransforms.Contains(ent), $"entity {ent} should be inside the m_Entities array");
                var map = Netcode.EntityMappingRef.m_MappedEntities[GhostEntityMapping.GameObjectKey.GetForGameObject(go.GetEntityId())];
                Assert.AreEqual(transformTracking.m_IndexedGameObjectIds[map.TransformIndex], GhostEntityMapping.GameObjectKey.GetForGameObject(go.GetEntityId()));
                Assert.AreEqual(transformTracking.m_EntitiesForTransforms[map.TransformIndex], ent);
                Assert.AreEqual(transformTracking.m_Transforms[map.TransformIndex].GetEntityId(), go.transform.GetEntityId());
                Assert.AreEqual(testWorld.ServerWorld.EntityManager.GetComponentData<GhostGameObjectLink>(ent).AssociatedGameObject, go.GetEntityId());
            }

            //Destroying the gameobject should remove the instance and the entity from the system.
            //Critical scenario: remove the last one in the list. Because of the swapping mechanism I want to
            //verify this is actually correctly not causing problems.
            serverInstances.Remove(transformTracking.m_Transforms[transformTracking.m_Transforms.length-1].gameObject);
            Object.Destroy(transformTracking.m_Transforms[transformTracking.m_Transforms.length-1].gameObject);
            await testWorld.TickAsync();

            VerifyLength(serverInstances.Count, transformTracking);
            //All serverInstances should still be mapped to the correct gameobject
            void VerifyObjectMapping(int startIndex)
            {
                for (int i = startIndex; i < serverInstances.Count; ++i)
                {
                    GameObject go = serverInstances[i]; // specifying GameObject type as we don't want to do a GetEntityId on a monobehaviour by accident
                    var ent = serverInstances[i].GetComponent<GhostAdapter>().Entity;
                    var map = Netcode.EntityMappingRef.m_MappedEntities[GhostEntityMapping.GameObjectKey.GetForGameObject(go.GetEntityId())];
                    var mappedObject = transformTracking.m_Transforms[map.TransformIndex].gameObject;
                    Assert.AreEqual(serverInstances[i], mappedObject, $"Expected {serverInstances[i].name} to be mapped to entity {ent} but the " +
                    $"index in the {nameof(GhostEntityMapping.MappedEntity)} point to {map.TransformIndex} that is {mappedObject.name} with instance id {mappedObject.GetEntityId()}");
                    Assert.AreEqual(transformTracking.m_EntitiesForTransforms[map.TransformIndex], ent);
                    Assert.AreEqual(transformTracking.m_IndexedGameObjectIds[map.TransformIndex].gameObjectId, go.GetEntityId());
                }
            }
            VerifyObjectMapping(0);
            var random = new System.Random(0x72828231);

            // shuffle and destroy at random
            // shuffle
            int n = serverInstances.Count;
            while (n > 1)
            {
                n--;
                var k = random.Next(n + 1);
                (serverInstances[k], serverInstances[n]) = (serverInstances[n], serverInstances[k]);
            }

            // destroy
            for (int i = 0; i < serverInstances.Count; ++i)
            {
                Object.Destroy(serverInstances[i]);
                await testWorld.TickAsync();

                // verify the remainder of the objects are still mapped correctly
                VerifyObjectMapping(i+1);
            }

            //all destroyed
            Assert.IsTrue(query.IsEmpty);
            VerifyLength(0, transformTracking);
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public async Task ClientTrackGhostsSpawnedFromNetwork()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var cubePrefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "BasicCube",
                typeof(TestMoveCube));
            await testWorld.ConnectAsync(enableGhostReplication:true);
            //warmup to have in sync prefab list and received some snapshot for time sync
            await testWorld.TickMultipleAsync(16);

            uint testServerStartTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick.TickIndexForValidTick;
            var ghostCount = 50;

            //Instantiate on the server a couple of instances. All these should fit into one snapshot
            List<GhostAdapter> serverInstances = new List<GhostAdapter>();

            for (int i = 0; i < ghostCount; ++i)
                serverInstances.Add(Object.Instantiate(cubePrefab).GetComponent<GhostAdapter>());

            foreach (var ga in serverInstances)
            {
                Assert.AreNotEqual(Entity.Null, ga.Entity);
                Assert.IsNotNull(ga.World);
                Assert.IsTrue(ga.World.IsServer());
            }
            var serverGhosts = testWorld.ServerWorld.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostInstance>());
            Assert.IsFalse(serverGhosts.IsEmpty, "The GhostAdapterGameObjectState should have been added to the created entities");
            Assert.AreEqual(ghostCount, serverGhosts.CalculateEntityCount());

            var transformTrackingServer = testWorld.GetSingleton<PerWorldIndexedTransformTrackingSingleton>(testWorld.ServerWorld);

            void VerifyObjectMapping(World world, IEnumerable<GhostAdapter> instances)
            {
                var transformTracking = testWorld.GetSingleton<PerWorldIndexedTransformTrackingSingleton>(world);
                foreach (GhostAdapter ghostAdapter in instances)
                {
                    var ent = ghostAdapter.Entity;
                    var map = Netcode.EntityMappingRef.m_MappedEntities[GhostEntityMapping.GameObjectKey.GetForGameObject(ghostAdapter.gameObject)];
                    var mappedObject = transformTracking.m_Transforms[map.TransformIndex].gameObject;
                    Assert.IsTrue(transformTracking.m_IndexedGameObjectIds.Contains(GhostEntityMapping.GameObjectKey.GetForGameObject(ghostAdapter.gameObject)), $"the GameObject tracking list should contain {ghostAdapter.name}");
                    Assert.IsTrue(transformTracking.m_EntitiesForTransforms.Contains(ent), $"entity {ent} should be inside the entities tracking array");

                    Assert.IsNotNull(mappedObject);
                    Assert.AreEqual(ghostAdapter.gameObject, mappedObject, $"Expected {ghostAdapter.name} to be mapped to entity {ent} but the " +
                                                                           $"index in the state tracking singleton points to index {map.TransformIndex} that is {mappedObject.name} with instance id {mappedObject.GetEntityId()}");
                }
            }
            //All gameobject should have been registered (server) and all entities should have a state
            VerifyLength(ghostCount, transformTracking: transformTrackingServer);
            VerifyObjectMapping(testWorld.ServerWorld, serverInstances);

            //Server has registered the ghost (added the GhostCleanup component) last tick and it now will dispatch the
            //the new ghosts to the client.
            //So, from spawning a ghost from gameobject or outside the Entities world update,
            //require 2 ticks:
            // - 1 tick to detect the spawn
            // - 1 tick to send the new ghost
            //i.e server
            //21 :gameobject spawn,
            //22 :detected the spawn, set id and spawntick to 22
            //23: send the ghost to the client. This is the tick the client will spawn the interpolated ghost.
            // Because we already ticked once before this point, this extra tick is what make the client now receive the
            // new ghost. We could even check in the spawning queue, but it is outside the scope of this test.
            await testWorld.TickMultipleAsync(2);
            var sb = testWorld.GetSingletonBuffer<GhostSpawnBuffer>(testWorld.ClientWorlds[0]);
            //All in spawn queue.
            Assert.AreEqual(ghostCount, sb.Length, "wrong ghost count");
            Assert.AreEqual(testServerStartTick+1, sb[0].ServerSpawnTick.TickIndexForValidTick);
            Assert.AreEqual(testServerStartTick+2, sb[0].ClientSpawnTick.TickIndexForValidTick);
            //Client have received the snapshot and entities queued for spawning at the last received server tick
            // (+1 in respect the tick the ghost spawned on the server).
            //Placeholder ghosts (fake entities with the PendingSpawnPlaceholder) are created while the waiting for
            //the real spawn.
            // Given the default interpolation delay ~2 ticks behind the server last snapshot, the client need another
            // 2 ticks before spawning the real instances.
            Assert.AreEqual(testServerStartTick, testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).InterpolationTick.TickIndexForValidTick);
            await testWorld.TickMultipleAsync(2);
            Assert.AreEqual(testServerStartTick+2, testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).InterpolationTick.TickIndexForValidTick);
            //After this two ticks the interpolation tick is at 23.x. But because the fraction < 1, the last full interpolated
            //tick was 22. That is what the GhostSpawningSystem is checking to spawn.
            //Looks to me a little incorrect, but for now let's keep the logic as is.
            using var placeholder = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                new EntityQueryBuilder(Allocator.Temp)
                    .WithAllRW<GhostInstance, PendingSpawnPlaceholder>()
            );
            using var clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                new EntityQueryBuilder(Allocator.Temp)
                    .WithAllRW<GhostInstance>()
                    .WithNone<PendingSpawnPlaceholder>()
            );
            Assert.IsFalse(placeholder.IsEmpty);
            Assert.AreEqual(ghostCount, placeholder.CalculateEntityCount());
            Assert.IsTrue(clientGhosts.IsEmpty);
            //All real ghosts should had spawn now.
            await testWorld.TickAsync();
            Assert.IsTrue(placeholder.IsEmpty);
            Assert.AreEqual(ghostCount, clientGhosts.CalculateEntityCount());
            //And all entities should have been tracked
            var trackedGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
            Assert.IsFalse(trackedGhosts.IsEmpty);
            Assert.AreEqual(ghostCount, trackedGhosts.CalculateEntityCount());
            //GameObject should have spawned now
            var clientGameObjectsList = Object.FindObjectsByType<GhostAdapter>(FindObjectsSortMode.None)
                .Where(go => !go.GetComponent<GhostAdapter>().World.IsServer()).ToList();
            Assert.AreEqual(ghostCount, clientGameObjectsList.Count);

            var transformTrackingClient = testWorld.GetSingleton<PerWorldIndexedTransformTrackingSingleton>(testWorld.ServerWorld);
            VerifyLength(ghostCount, transformTrackingClient);
            VerifyObjectMapping(testWorld.ClientWorlds[0], clientGameObjectsList);

            foreach (var clientGhostAdapter in clientGameObjectsList)
            {
                Assert.IsFalse(clientGhostAdapter.World.EntityManager.HasComponent<PredictedGhost>(clientGhostAdapter.Entity), "assumption broken, ghost is predicted when it should be interpolated instead");
            }

            //shuffle and destroy at random
            var seed = 0x729b4ed2;
            var random = new System.Random(seed);
            int n = serverInstances.Count;
            while (n > 1) {
                n--;
                var k = random.Next(n + 1);
                (serverInstances[k], serverInstances[n]) = (serverInstances[n], serverInstances[k]);
            }
            for (int i = serverInstances.Count-1; i >= 0 ; --i)
            {
                var ent = serverInstances[i].Entity;
                Object.Destroy(serverInstances[i].gameObject);
                var go = serverInstances[i].gameObject;
                serverInstances.RemoveAt(i);
                await testWorld.TickMultipleAsync(2); // this calls OnDestroy first (after LateUpdate), then next frame's Update + ECS Tick TODO-release hack WaitForEndOfFrame revert to count=1, this is due to WaitForEndOfFrame issue
                Assert.IsFalse(go);
                //The entity is still alive, it should have the cleanup only and waiting for the client to ack
                //the despawn. That would requires one or two ticks
                Assert.IsTrue(testWorld.ServerWorld.EntityManager.Exists(ent), "server entity doesn't exist, should still be there with cleanup components present");
                Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostCleanup>(ent));
                Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<GhostInstance>(ent), "GhostInstance should have been destroyed as part of the entity destruction");
                VerifyObjectMapping(testWorld.ServerWorld, serverInstances);
                //The client received the despawn, but being the object interpolated, it will destroy this later
                //(2 ticks). We need at least 3 tick on average because the destroy is enqueued in a command buffer (next tick)
                //We are using 4 to avoid some InterpolatedTick adjustment that would make this test brittle.
                // Debug.Log(testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).InterpolationTick.ToFixedString());
                var dq = testWorld.GetSingleton<GhostDespawnQueues>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(1, dq.InterpolatedDespawnQueue.Count, "wrong despawn queue count in client");
                await testWorld.TickMultipleAsync(4);
                Assert.AreEqual(serverInstances.Count, clientGhosts.CalculateEntityCount(), "client and server ghost count mismatch");
                //Tracking update right after entity has been destroyed. So it should be in sync
                Assert.AreEqual(serverInstances.Count, trackedGhosts.CalculateEntityCount(), "client tracked ghost count mismatch with server instance count");
                VerifyObjectMapping(testWorld.ServerWorld, serverInstances);
            }
            //all destroyed
            Assert.IsTrue(serverGhosts.IsEmpty);
            Assert.IsTrue(clientGhosts.IsEmpty);
            Assert.IsTrue(trackedGhosts.IsEmpty);
            VerifyLength(0, transformTrackingServer);
            VerifyLength(0, transformTrackingClient);
        }

        [Test]
        public async Task DestroyGameObjectImmediatelyAfterSpawn()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var cubePrefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "BasicCube",
                typeof(TestMoveCube));
            await testWorld.ConnectAsync(enableGhostReplication:true);
            await testWorld.TickMultipleAsync(16);
            //sync the test execution to after fixed update, but before update
            await Awaitable.FixedUpdateAsync();

            var instance = Object.Instantiate(cubePrefab);
            var ghosts = testWorld.ServerWorld.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostInstance>());
            var serverCleanup = testWorld.ServerWorld.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostCleanup>());
            //Destroy immediately can have some side effect. In particular, if the destroy occurs inside a coroutine and it is scheduled
            //in the LateUpdate (it may occurs in either the Fixed, Update, or LateUpdate phase, depending on what come first and the time scheduled)
            //the destruction of the object can fight with any pending operation in command buffers scheduled for the next frame.
            //This is indeed an annoying problem.
            Assert.IsFalse(ghosts.IsEmpty, "ghosts.IsEmpty");
            Assert.IsTrue(serverCleanup.IsEmpty);
            Object.Destroy(instance);
            //validate nothing actually changed
            Assert.IsFalse(ghosts.IsEmpty);
            Assert.IsTrue(instance);
            Assert.IsTrue(Resources.EntityIdIsValid(instance.GetEntityId()));
            //We are already in a coroutine, that run after FixedUpdate.
            //if we return null here, we are executing inside the DelayedCall, and after the destroy callback. that is what occurs normally.
            //in that case, expected 0 errors (this mimic doing that inside Update or FixedUpdate or FixedUpdate coroutines)
            await Awaitable.NextFrameAsync();
            // Server Object should be gone now
            Assert.IsFalse(Resources.EntityIdIsValid(instance.GetEntityId()));
            Assert.IsTrue(ghosts.IsEmpty);


            async Task ValidateNoClientGOSpawnsForAWhile()
            {
                int frameCountToCheckNothingHappens = 32;
                for (int i = 0; i < frameCountToCheckNothingHappens; i++)
                {
                    var clientGhosts = Object.FindObjectsByType<GhostAdapter>(FindObjectsSortMode.None);
                    Assert.AreEqual(0, clientGhosts.Length, $"no spawn should have happened client side, but found {clientGhosts.Length} ghosts after {i} ticks!");
                    await testWorld.TickAsync();
                }
            }

            //Nothing really happen in this case.
            await ValidateNoClientGOSpawnsForAWhile();

            await testWorld.TickAsync();
            //What though if we do that inside a coroutine ? Is the destroy called still in the same way?
            //Let's sync again after Update
            await Awaitable.NextFrameAsync();
            instance = Object.Instantiate(cubePrefab);
            Object.Destroy(instance);
            //we can't yield now, because that would wait a whole frame. We should wait until all the delayed call ended.. Can we?
            //to mimic what we want, we call Tick here
            Assert.IsFalse(ghosts.IsEmpty);
            await testWorld.TickAsync();
            //run a full frame update
            await Awaitable.NextFrameAsync();

            await ValidateNoClientGOSpawnsForAWhile();

            Assert.IsTrue(serverCleanup.IsEmpty, "All cleanup should be gone");
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public async Task DestroyingMultipleGameObjects()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var cubePrefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "BasicCube",
                typeof(TestMoveCube));
            await testWorld.ConnectAsync(enableGhostReplication:true);
            //warmup to have in sync prefab list and received some snapshot for time sync
            await testWorld.TickMultipleAsync(16);
            //Instantiate on the server
            List<GameObject> instances = new List<GameObject>();
            for (int i = 0; i < 50; ++i)
                instances.Add(Object.Instantiate(cubePrefab));
            await testWorld.TickMultipleAsync(8);
            //everything should have been spawned
            var goAdapters = Object.FindObjectsByType<GhostAdapter>(FindObjectsSortMode.None);
            Assert.AreEqual(100, goAdapters.Length);
            //and tracked
            var serverGhosts = testWorld.ServerWorld.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostCleanup>());
            Assert.IsFalse(serverGhosts.IsEmpty, "All GhostCleanup should have been added by the server for spawned ghosts.");
            Assert.AreEqual(50, serverGhosts.CalculateEntityCount());
            var clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostInstance>());
            Assert.IsFalse(clientGhosts.IsEmpty, "ghost should have been spawned by the client");
            Assert.AreEqual(50, clientGhosts.CalculateEntityCount(), "All ghost should have been spawned by the client");
            var clientGo = Object.FindObjectsByType<GhostAdapter>(FindObjectsSortMode.None).Where(ga=>!ga.World.IsServer()).ToArray();
            Assert.AreEqual(50, clientGo.Length);
            var ghostEntities = serverGhosts.ToEntityArray(Allocator.Temp).ToArray();
            //Despawn all at once,
            for (int i = 0; i < 50; ++i)
                Object.Destroy(instances[i]);
            Assert.IsFalse(serverGhosts.IsEmpty, "ghost should be still present on the server");
            for (int i = 0; i < 50; ++i)
            {
                Assert.IsTrue(instances[i]);
                Assert.IsTrue(Resources.EntityIdIsValid(testWorld.ServerWorld.EntityManager.GetComponentData<GhostGameObjectLink>(ghostEntities[i]).AssociatedGameObject));
            }
            //After 1 tick, server should have notice the despawn and object destroyed. Because of cleanup and acks logic,
            //we need another (min) 4 ticks before the entity is actually disposed.
            //The gameobject itself during this time is already gone.
            for (int tick = 0; tick < 5; ++tick) // TODO-release hack taking one additional tick (5 instead of 4) because of waitForNextFrame vs WaitForEndOfFrame shenanigans
            {
                await testWorld.TickMultipleAsync(1);
                for (int i = 0; i < 50; ++i)
                {
                    Assert.IsFalse(instances[i]);
                    Assert.IsFalse(Resources.EntityIdIsValid(instances[i].GetEntityId()));
                    //the instance still exist on the server until cleanup
                    Assert.AreEqual(tick < 4, testWorld.ServerWorld.EntityManager.Exists(ghostEntities[i])); // TODO-release hack should be back to 3 once we fix the WaitForEndOfFrame issue
                }
            }
            //on client everything is disposed as well after 4 ticks
            goAdapters = Object.FindObjectsByType<GhostAdapter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Assert.AreEqual(0, goAdapters.Length);
            Assert.IsTrue(serverGhosts.IsEmpty, "All GhostCleanup should have been removed on the server");
            Assert.IsTrue(clientGhosts.IsEmpty, "All ghost should have been despawn by the client");
        }

        //There is not a dual test on client because client normaly can't destroy ghost entities. This will
        //trigger errors if the client is in game in ECS so it is not the use case we are trying to check here.
        //The test is validating the GhostAdapter layer destroy correctly spawned ghosts on the server, when
        //entities are destroyed
        [Ignore("Temporarily while waiting for engine side feature to have an entity destruction trigger a GameObject destruction. This is mostly useful for hybrid flows where a system would destroy the entity. With the current implementation, users would need to destroy the GO itself, rather than the underlying entity.")]
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public async Task DestroyingMultipleGhostEntities()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var cubePrefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "BasicCube",
                typeof(TestMoveCube));
            await testWorld.ConnectAsync(enableGhostReplication:true);
            //warmup to have in sync prefab list and received some snapshot for time sync
            await testWorld.TickMultipleAsync(16);
            //Instantiate on the server
            List<GameObject> instances = new List<GameObject>();
            for (int i = 0; i < 50; ++i)
                instances.Add(Object.Instantiate(cubePrefab));

            await testWorld.TickMultipleAsync(8);
            //everything should have been spawned
            var goAdapters = Object.FindObjectsByType<GhostAdapter>(FindObjectsSortMode.None);
            Assert.AreEqual(100, goAdapters.Length);
            //and tracked
            var serverGhosts = testWorld.ServerWorld.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostCleanup>());
            Assert.IsFalse(serverGhosts.IsEmpty, "All GhostCleanup should have been added by the server for spawned ghosts.");
            Assert.AreEqual(50, serverGhosts.CalculateEntityCount());
            var clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostInstance>());
            Assert.IsFalse(clientGhosts.IsEmpty, "ghost should have been spawned by the client");
            Assert.AreEqual(50, clientGhosts.CalculateEntityCount(), "All ghost should have been spawned by the client");
            var clientGo = Object.FindObjectsByType<GhostAdapter>(FindObjectsSortMode.None).Where(ga=>!ga.World.IsServer()).ToArray();
            Assert.AreEqual(50, clientGo.Length);
            var ghostIds = serverGhosts.ToEntityArray(Allocator.Temp).ToArray();
            //Despawn all at once
            testWorld.ServerWorld.EntityManager.DestroyEntity(serverGhosts);
            //GameObjects should also gone (destroy immediate in this case)
            for (int i = 0; i < 50; ++i)
            {
                Assert.IsFalse(instances[i]);
                Assert.IsFalse(Resources.EntityIdIsValid(instances[i].GetEntityId()));
                //entity still exist, the cleanup component keep them alive
                Assert.IsTrue(testWorld.ServerWorld.EntityManager.Exists(ghostIds[i]));
            }
            //We need 4 ticks with 0 latency for despawn process to be completed on the server
            // 1 tick: server will notice the despawn.
            //   - Despawn sent to the client.
            //   - Client receive the despawns, queue them in command buffer
            // 2 tick: server will notice the ack from the clients
            //   - server verify all despawned packet acked by client. This actually uses a value updated in a prev
            //     job, that imply, we are actually despawning at best, the LastAck - 1 tick. We need then 1 extra
            //     tick to verify the spawn occurred in hte previous tick
            //   - Client will now despawn all ghosts
            // 3 tick:
            //   - cleanup components are now removed in command buffer
            // 4 tick:
            //   - cleanup removed, all ghost are not gone.
            //
            // During this time, the gameobject itself should be gone.
            // **IMPORTANT**
            // The release of the entity id depend on the network condition and client disconnection detection as well.
            // That means it can take an bounded, but long time, before the id is actually released.
            // THIS FLOW CANNOT BE REPLICATED EASILY WITHOUT CLEANUP COMPONENTS, UNLESS WE CREATE ANOTHER SET OF ENTITIES
            // THAT WE TRACK OR WE COPY THE ENTITY INFORMATION IN ANOTHER DESPAWN LIST (SUGGESTED)
            for (int tick = 0; tick < 4; ++tick)
            {
                await testWorld.TickMultipleAsync(1);
                for (int i = 0; i < 50; ++i)
                    Assert.AreEqual(tick < 3, testWorld.ServerWorld.EntityManager.Exists(ghostIds[i]));
            }
            //on client everything is disposed as well after 4 ticks
            goAdapters = Object.FindObjectsByType<GhostAdapter>(FindObjectsSortMode.None);
            Assert.AreEqual(0, goAdapters.Length);
            Assert.IsTrue(serverGhosts.IsEmpty, "All GhostCleanup should have been removed on the server");
            Assert.IsTrue(clientGhosts.IsEmpty, "All ghost should have been despawn by the client");
        }

        [Test]
        public async Task DespawnFromNetworkWorksProperly([Values] bool destroyInLateUpdate)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var cubePrefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicCube");
            await testWorld.ConnectAsync(enableGhostReplication:true);
            var serverObj = GameObject.Instantiate(cubePrefab);
            await testWorld.TickMultipleAsync(6);
            var clientObj = PredictionCallbackHelper.ClientInstances[0];

            void OnDestroy(GameObject gameObject)
            {
                var adapter = gameObject.GetComponent<GhostAdapter>();
                var behaviour = gameObject.GetComponent<PredictionCallbackHelper>();

                Assert.IsTrue(gameObject.WorldExt() != null, "world null in OnDestroy");
                Assert.IsTrue(adapter.World != null, "world null in OnDestroy");
                Assert.IsTrue(adapter.World == gameObject.WorldExt());
                Assert.IsTrue(gameObject.EntityExt(isPrefab: false) != Entity.Null, "entity null in OnDestroy");
                Assert.IsTrue(adapter.Entity != Entity.Null, "entity null in OnDestroy");
                Assert.IsTrue(adapter.Entity == gameObject.EntityExt(isPrefab: false));
                Assert.IsTrue(gameObject.WorldExt().EntityManager.Exists(gameObject.EntityExt(isPrefab: false)), "entity doesn't exist in OnDestroy");
                // just want to check if we can access entity components at all
                gameObject.WorldExt().EntityManager.GetComponentData<LocalTransform>(gameObject.EntityExt(isPrefab: false)); // TODO-release it's possible to already have released the entity here. So should destroy the entity in an ECB instead? And destroy it later? Right now users can prevent the entity from being destroyed by calling base.OnDestroy after they do their entity related stuff. but it's a bit of a gotcha. This WILL go away as soon as we have entities integration though, since then the entity lifecycle will be managed engine side.

                // TODO-next uncomment this once inputs are available
                // behaviour.InputData = new DummyInput() { value = 123 };
                // Assert.IsTrue(behaviour.InputData.value == 123);
            }

            serverObj.OnDestroyEvent += OnDestroy;
            clientObj.OnDestroyEvent += OnDestroy;

            Assert.AreNotEqual(clientObj, serverObj);
            Assert.IsTrue(clientObj != null);
            Assert.IsTrue(serverObj != null);

            if (!destroyInLateUpdate)
            {
                // tick happened, so we're in LateUpdate now
                await Awaitable.NextFrameAsync(); // jumping to after Update and before LateUpdate
                Object.Destroy(serverObj.gameObject);
                await testWorld.TickAsync();
            }
            else
            {
                Object.Destroy(serverObj.gameObject); // LateUpdate, then OnDestroy, then ECS tick. So GhostAdapter OnDestroy (and its cleanup destroying the associated entity) happens before Netcode's systems cleanup.

                // Destroy called
                // next engine event loop, call delayed destroy
            }

            await testWorld.TickMultipleAsync(4);

            Assert.IsTrue(Object.FindObjectsByType<GhostAdapter>(FindObjectsInactive.Include, sortMode: FindObjectsSortMode.None).Length == 0, "GhostAdapter should be destroyed");
            Assert.IsTrue(serverObj == null, "serverObj should be null and despawned");
            Assert.IsTrue(clientObj == null, "clientObj should be null and despawned");

            await testWorld.TickMultipleAsync(30); // make sure there's no error messages
        }
        // TODO-release add more tests for destroying GhostBehaviour at runtime
    }
}
#endif

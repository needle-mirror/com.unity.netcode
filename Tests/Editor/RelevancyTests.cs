using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode.Tests;
using Unity.Jobs;
using UnityEngine;
using Unity.NetCode;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections;
using System.Collections.Generic;

namespace Unity.NetCode.Tests
{
    public class GhostRelevancyTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new GhostOwnerComponent());
        }
    }

    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    [DisableAutoCreation]
    internal class AutoMarkIrrelevantSystem : SystemBase
    {
        public static int s_ConnectionId;
        public static NativeHashMap<int,int> s_IrrelevantGhosts;
        GhostSendSystem m_GhostSendSystem;
        protected override void OnCreate()
        {
            m_GhostSendSystem = World.GetExistingSystem<GhostSendSystem>();
        }
        protected override void OnUpdate()
        {
            var relevancySet = m_GhostSendSystem.GhostRelevancySet;
            var clearDep = Job.WithCode(() => {
                relevancySet.Clear();
            }).Schedule(m_GhostSendSystem.GhostRelevancySetWriteHandle);
            Dependency = JobHandle.CombineDependencies(clearDep, Dependency);
            var connectionId = s_ConnectionId;
            var irrelevantGhosts = s_IrrelevantGhosts;
            Entities.ForEach((in GhostComponent ghost, in GhostOwnerComponent owner) => {
                if (irrelevantGhosts.TryGetValue(owner.NetworkId, out var temp))
                    relevancySet.TryAdd(new RelevantGhostForConnection(connectionId, ghost.ghostId), 1);
            }).Schedule();
            m_GhostSendSystem.GhostRelevancySetWriteHandle = Dependency;
        }
    }

    public class RelevancyTests
    {
        const float frameTime = 1.0f / 60.0f;
        GameObject bootstrapAndSetup(NetCodeTestWorld testWorld, System.Type additionalSystem = null)
        {
            if (additionalSystem != null)
                testWorld.Bootstrap(true, additionalSystem);
            else
                testWorld.Bootstrap(true);

            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostRelevancyTestConverter();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

            testWorld.CreateWorlds(true, 1);
            return ghostGameObject;
        }
        Entity spawnAndSetId(NetCodeTestWorld testWorld, GameObject ghostGameObject, int id)
        {
            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, serverEnt);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent{NetworkId = id});
            return serverEnt;
        }

        int connectAndGoInGame(NetCodeTestWorld testWorld, int maxFrames = 4)
        {
            // Connect and make sure the connection could be established
            Assert.IsTrue(testWorld.Connect(frameTime, maxFrames));

            // Go in-game
            testWorld.GoInGame();

            var con = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
            Assert.AreNotEqual(Entity.Null, con);
            return testWorld.ServerWorld.EntityManager.GetComponentData<NetworkIdComponent>(con).Value;
        }
        [Test]
        public void EmptyIsRelevantSetSendsNoGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

                spawnAndSetId(testWorld, ghostGameObject, 1);

                connectAndGoInGame(testWorld);

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(Entity.Null, clientEnt);
            }
        }
        [Test]
        public void FullIsRelevantSetSendsAllGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

                var serverConnectionId = connectAndGoInGame(testWorld);

                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);
                testWorld.Tick(frameTime);
                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostComponent>(serverEnt).ghostId;
                ghostSendSystem.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwnerComponent>(clientEnt).NetworkId);
            }
        }
        [Test]
        public void HalfIsRelevantSetSendsHalfGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

                var serverConnectionId = connectAndGoInGame(testWorld);

                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);
                testWorld.Tick(frameTime);
                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostComponent>(serverEnt).ghostId;
                ghostSendSystem.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);
                spawnAndSetId(testWorld, ghostGameObject, 2);

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwnerComponent>(clientEnt).NetworkId);
            }
        }
        [Test]
        public void EmptyIsIrrelevantSetSendsAllGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                spawnAndSetId(testWorld, ghostGameObject, 1);

                connectAndGoInGame(testWorld);

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwnerComponent>(clientEnt).NetworkId);
            }
        }
        [Test]
        public void FullIsIrrelevantSetSendsNoGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = connectAndGoInGame(testWorld);

                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);
                testWorld.Tick(frameTime);
                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostComponent>(serverEnt).ghostId;
                ghostSendSystem.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(Entity.Null, clientEnt);
            }
        }
        [Test]
        public void HalfIsIrrelevantSetSendsHalfGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = connectAndGoInGame(testWorld);

                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);
                testWorld.Tick(frameTime);
                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostComponent>(serverEnt).ghostId;
                ghostSendSystem.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);
                spawnAndSetId(testWorld, ghostGameObject, 2);

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                Assert.AreEqual(2, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwnerComponent>(clientEnt).NetworkId);
            }
        }
        [Test]
        public void MarkedIrrelevantAtSpawnIsNeverSeen()
        {
            using (var testWorld = new NetCodeTestWorld())
            using (AutoMarkIrrelevantSystem.s_IrrelevantGhosts = new NativeHashMap<int, int>(128, Allocator.TempJob))
            {
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = connectAndGoInGame(testWorld);
                AutoMarkIrrelevantSystem.s_ConnectionId = serverConnectionId;

                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, 2);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);
                AutoMarkIrrelevantSystem.s_IrrelevantGhosts.TryAdd(1, 1);

                var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwnerComponent>());
                for (int i = 0; i < 16; ++i)
                {
                    var clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                    Assert.AreEqual(128, clientValues.Length);
                    for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                        Assert.AreEqual(2, clientValues[ghost].NetworkId);

                    testWorld.Tick(frameTime);
                }
            }
        }
        [Test]
        public void MarkedIrrelevantIsDespawned()
        {
            using (var testWorld = new NetCodeTestWorld())
            using (AutoMarkIrrelevantSystem.s_IrrelevantGhosts = new NativeHashMap<int, int>(128, Allocator.TempJob))
            {
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = connectAndGoInGame(testWorld);
                AutoMarkIrrelevantSystem.s_ConnectionId = serverConnectionId;

                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, 2);
                }
                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwnerComponent>());
                var clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                Assert.AreEqual(129, clientValues.Length);
                bool foundOne = false;
                for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                {
                    if (!foundOne && clientValues[ghost].NetworkId == 1)
                        foundOne = true;
                    else
                        Assert.AreEqual(2, clientValues[ghost].NetworkId);
                }
                Assert.IsTrue(foundOne);

                AutoMarkIrrelevantSystem.s_IrrelevantGhosts.TryAdd(1, 1);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                Assert.AreEqual(128, clientValues.Length);
                for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                    Assert.AreEqual(2, clientValues[ghost].NetworkId);
            }
        }
        void checkValidSet(HashSet<int> checkHashSet, NativeArray<GhostOwnerComponent> clientValues, int start, int end)
        {
            checkHashSet.Clear();
            Assert.AreEqual(end-start, clientValues.Length);
            for (int ghost = 0; ghost < clientValues.Length; ++ghost)
            {
                var id = clientValues[ghost].NetworkId;
                Assert.IsTrue(id > start && id <= end);
                Assert.IsFalse(checkHashSet.Contains(id));
                checkHashSet.Add(id);
            }
        }
        [Test]
        [TestCase(16)]
        [TestCase(23)]
        public void MarkIrrelevantAtRuntimeReachTheClient(int ghostsPerFrame)
        {
            using (var testWorld = new NetCodeTestWorld())
            using (AutoMarkIrrelevantSystem.s_IrrelevantGhosts = new NativeHashMap<int, int>(128, Allocator.TempJob))
            {
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = connectAndGoInGame(testWorld);
                AutoMarkIrrelevantSystem.s_ConnectionId = serverConnectionId;

                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, ghost+1);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwnerComponent>());

                var checkHashSet = new HashSet<int>();
                var clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                checkValidSet(checkHashSet, clientValues, 0, 128);

                // For every update we make ghostsPerFrame new ghosts irrelevant and check that the change was propagated
                for (int start = 0; start+ghostsPerFrame < 128; start += ghostsPerFrame)
                {
                    for (int i = 0; i < ghostsPerFrame; ++i)
                        AutoMarkIrrelevantSystem.s_IrrelevantGhosts.TryAdd(start+i+1, 1);
                    for (int i = 0; i < 4; ++i)
                        testWorld.Tick(frameTime);

                    clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                    checkValidSet(checkHashSet, clientValues, start+ghostsPerFrame, 128);
                }
            }
        }
        [Test]
        [TestCase(16)]
        [TestCase(23)]
        public void MarkRelevantAtRuntimeReachTheClient(int ghostsPerFrame)
        {
            using (var testWorld = new NetCodeTestWorld())
            using (AutoMarkIrrelevantSystem.s_IrrelevantGhosts = new NativeHashMap<int, int>(128, Allocator.TempJob))
            {
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = connectAndGoInGame(testWorld);
                AutoMarkIrrelevantSystem.s_ConnectionId = serverConnectionId;

                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, ghost+1);
                    AutoMarkIrrelevantSystem.s_IrrelevantGhosts.TryAdd(ghost+1, 1);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwnerComponent>());

                var checkHashSet = new HashSet<int>();
                var clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                Assert.AreEqual(0, clientValues.Length);

                // For every update we make ghostsPerFrame new ghosts relevant and check that the change was propagated
                for (int start = 0; start+ghostsPerFrame < 128; start += ghostsPerFrame)
                {
                    for (int i = 0; i < ghostsPerFrame; ++i)
                        AutoMarkIrrelevantSystem.s_IrrelevantGhosts.Remove(start+i+1);
                    for (int i = 0; i < 4; ++i)
                        testWorld.Tick(frameTime);

                    clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                    checkValidSet(checkHashSet, clientValues, 0, start+ghostsPerFrame);
                }
            }
        }
        [Test]
        [TestCase(16)]
        [TestCase(23)]
        public void ChangeRelevantSetAtRuntimeReachTheClient(int ghostsPerFrame)
        {
            using (var testWorld = new NetCodeTestWorld())
            using (AutoMarkIrrelevantSystem.s_IrrelevantGhosts = new NativeHashMap<int, int>(128, Allocator.TempJob))
            {
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = connectAndGoInGame(testWorld);
                AutoMarkIrrelevantSystem.s_ConnectionId = serverConnectionId;

                // The relevant set is 3x the changes per frame, this means 1/3 is added, 1/3 is removed and 1/3 remains relevant
                int end = ghostsPerFrame*3;
                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, ghost+1);
                    if (ghost >= end)
                        AutoMarkIrrelevantSystem.s_IrrelevantGhosts.TryAdd(ghost+1, 1);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwnerComponent>());

                var checkHashSet = new HashSet<int>();
                var clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                checkValidSet(checkHashSet, clientValues, 0, end);

                // For every update we make ghostsPerFrame new ghosts relevant and check that the change was propagated
                for (int start = 0; end+ghostsPerFrame < 128; start += ghostsPerFrame, end += ghostsPerFrame)
                {
                    for (int i = 0; i < ghostsPerFrame; ++i)
                    {
                        AutoMarkIrrelevantSystem.s_IrrelevantGhosts.TryAdd(start+i+1, 1);
                        AutoMarkIrrelevantSystem.s_IrrelevantGhosts.Remove(end+i+1);
                    }
                    for (int i = 0; i < 4; ++i)
                        testWorld.Tick(frameTime);

                    clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                    checkValidSet(checkHashSet, clientValues, start+ghostsPerFrame, end+ghostsPerFrame);
                }
            }
        }
        [Test]
        public void ToggleEveryFrameDoesNotRepetedlySpawn()
        {
            using (var testWorld = new NetCodeTestWorld())
            using (AutoMarkIrrelevantSystem.s_IrrelevantGhosts = new NativeHashMap<int, int>(128, Allocator.TempJob))
            {
                testWorld.DriverSimulatedDelay = 10;
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = connectAndGoInGame(testWorld, 16);
                AutoMarkIrrelevantSystem.s_ConnectionId = serverConnectionId;

                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, 2);
                }
                spawnAndSetId(testWorld, ghostGameObject, 1);
                // Start with the ghost irrelevant
                AutoMarkIrrelevantSystem.s_IrrelevantGhosts.TryAdd(1, 1);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwnerComponent>());
                var clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                // Check that the ghost does not exist
                Assert.AreEqual(128, clientValues.Length);
                for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                    Assert.AreEqual(2, clientValues[ghost].NetworkId);


                int sawGhost = 0;
                bool foundOne;
                // Loop unevent number of times so the ghost ends as relevant
                for (int i = 0; i < 63; ++i)
                {
                    clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                    if (clientValues.Length == 128)
                    {
                        Assert.AreEqual(128, clientValues.Length);
                        for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                            Assert.AreEqual(2, clientValues[ghost].NetworkId);
                    }
                    else
                    {
                        Assert.AreEqual(129, clientValues.Length);

                        foundOne = false;
                        for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                        {
                            if (!foundOne && clientValues[ghost].NetworkId == 1)
                                foundOne = true;
                            else
                                Assert.AreEqual(2, clientValues[ghost].NetworkId);
                        }
                        Assert.IsTrue(foundOne);
                        ++sawGhost;
                    }

                    // Toggle the host between relevant and not relevant every frame
                    if ((i&1) == 0)
                        AutoMarkIrrelevantSystem.s_IrrelevantGhosts.Remove(1);
                    else
                        AutoMarkIrrelevantSystem.s_IrrelevantGhosts.TryAdd(1, 1);
                    testWorld.Tick(frameTime);
                }
                // The ghost should have been relevant less than half the frames, since some spawns were skipped to to a pending despawn
                Assert.Less(sawGhost, 32);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);
                // Check that it ended up relevant after toggling for many frames since it ended on relevant
                clientValues = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                foundOne = false;
                for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                {
                    if (!foundOne && clientValues[ghost].NetworkId == 1)
                        foundOne = true;
                    else
                        Assert.AreEqual(2, clientValues[ghost].NetworkId);
                }
                Assert.IsTrue(foundOne);
            }
        }
    }
}

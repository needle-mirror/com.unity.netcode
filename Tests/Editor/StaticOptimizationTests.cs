#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using System;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;

namespace Unity.NetCode.Tests
{
    internal class StaticOptimizationTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
        }
    }

    internal class ZeroChangeGhostStaticOptimizationTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.None);
            baker.AddComponent(entity, new GhostOwner());
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class StaticOptimizationTestSystem : SystemBase
    {
        public static int s_ModifyNetworkId;
        protected override void OnUpdate()
        {
            int modifyNetworkId = s_ModifyNetworkId;
            Entities.ForEach((ref LocalTransform trans, in GhostOwner ghostOwner) => {
                if (ghostOwner.NetworkId != modifyNetworkId)
                    return;
                trans.Position.x += 1;
            }).ScheduleParallel();
        }
    }

    internal class StaticOptimizationTests
    {
        void SetupBasicTest(NetCodeTestWorld testWorld, NetCodeTestLatencyProfile latencyProfile, TestNetCodeAuthoring.IConverter testConverter, int entitiesToSpawn = 1)
        {
            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = testConverter;
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.OptimizationMode = GhostOptimizationMode.Static;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.SetTestLatencyProfile(latencyProfile);
            testWorld.CreateWorlds(true, 1);

            for (int i = 0; i < entitiesToSpawn; ++i)
            {
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
            }

            // Connect and make sure the connection could be established
            testWorld.Connect(maxSteps:16);

            // Go in-game
            testWorld.GoInGame();

            // Let the game run for a bit so the ghosts are spawned on the client
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();
        }
        [Test]
        public void StaticGhostsAreNotSent([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                SetupBasicTest(testWorld, latencyProfile, new StaticOptimizationTestConverter(), 16);

                var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
                using var clientQuery = clientEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                var clientEntities = clientQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(16, clientEntities.Length);

                var lastSnapshot = new NativeArray<NetworkTick>(clientEntities.Length, Allocator.Temp);
                for (int i = 0; i < clientEntities.Length; ++i)
                {
                    var clientEnt = clientEntities[i];
                    // Store the last tick we got for this
                    var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                    var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                    lastSnapshot[i] = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                }

                // Run a bit longer
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                // Verify that we did not get any new snapshot
                for (int i = 0; i < clientEntities.Length; ++i)
                {
                    var clientEnt = clientEntities[i];
                    // Store the last tick we got for this
                    var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                    var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                    Assert.AreEqual(lastSnapshot[i], clientSnapshot.GetLatestTick(clientSnapshotBuffer));
                }
            }
        }
        [Test]
        public void GhostsCanBeStaticWhenChunksAreDirty([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                // The system will get write access to translation which will dirty the chunk, but not actually write anything
                testWorld.Bootstrap(true, typeof(StaticOptimizationTestSystem));
                StaticOptimizationTestSystem.s_ModifyNetworkId = 1;

                SetupBasicTest(testWorld, latencyProfile, new StaticOptimizationTestConverter(), 16);

                var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
                using var clientQuery = clientEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                var clientEntities = clientQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(16, clientEntities.Length);

                var lastSnapshot = new NativeArray<NetworkTick>(clientEntities.Length, Allocator.Temp);
                for (int i = 0; i < clientEntities.Length; ++i)
                {
                    var clientEnt = clientEntities[i];
                    // Store the last tick we got for this
                    var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                    var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                    lastSnapshot[i] = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                }

                // Run a bit longer
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                // Verify that we did not get any new snapshot
                for (int i = 0; i < clientEntities.Length; ++i)
                {
                    var clientEnt = clientEntities[i];
                    // Store the last tick we got for this
                    var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                    var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                    Assert.AreEqual(lastSnapshot[i], clientSnapshot.GetLatestTick(clientSnapshotBuffer));
                }
            }
        }
        [Test]
        public void StaticGhostsAreNotApplied([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                const int entitiesToSpawn = 7;
                const int constantlyChangingIndex = 3;
                testWorld.Bootstrap(true, typeof(StaticOptimizationTestSystem));
                StaticOptimizationTestSystem.s_ModifyNetworkId = constantlyChangingIndex;

                SetupBasicTest(testWorld, latencyProfile, new StaticOptimizationTestConverter(), entitiesToSpawn);

                // Set one to be constantly modified.
                var clientEm = testWorld.ClientWorlds[0].EntityManager;
                var clientEntities = clientEm.CreateEntityQuery(ComponentType.ReadWrite<GhostOwner>()).ToEntityArray(Allocator.Temp);
                Assert.AreEqual(entitiesToSpawn, clientEntities.Length);
                clientEm.SetComponentData(clientEntities[constantlyChangingIndex], new GhostOwner{NetworkId = constantlyChangingIndex});

                // Write some CLIENT data directly into the ghost fields, so we can verify that it was not touched by
                // the ghost apply of the constantly changing entity.
                var expectedPos = new float3(3, 4, 5);
                var expectedRot = Mathematics.quaternion.Euler(5, 6, 7);
                const int expectedScale = 8;
                for (var i = 0; i < clientEntities.Length; i++)
                {
                    clientEm.SetComponentData(clientEntities[i], new LocalTransform
                    {
                        Position = expectedPos,
                        Rotation = expectedRot,
                        Scale = expectedScale,
                    });
                }

                // Tick for a bit:
                for(int i = 0; i < 16; i++)
                    testWorld.Tick();

                // Run test:
                for (var i = 0; i < clientEntities.Length; i++)
                {
                    var clientTrans = clientEm.GetComponentData<LocalTransform>(clientEntities[i]);
                    var serverTick = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]).ServerTick;
                    // Note: GhostField's are "applied" on a per-field basis, so other GhostFields on this struct shouldn't change,
                    // even when the LocalTransform.Position does!
                    if (i == constantlyChangingIndex)
                        Assert.AreNotEqual(expectedPos, clientTrans.Position, $"Unexpectedly NOT changed on idx:{i} i.e. ServerTick:{serverTick.ToFixedString()}");
                    else Assert.AreEqual(expectedPos, clientTrans.Position, $"Unexpected change on idx:{i} i.e. ServerTick:{serverTick.ToFixedString()}");
                    Assert.AreEqual(expectedRot, clientTrans.Rotation, $"Unexpected change on idx:{i} i.e. ServerTick:{serverTick.ToFixedString()}");
                    Assert.AreEqual(expectedScale, clientTrans.Scale, $"Unexpected change on idx:{i} i.e. ServerTick:{serverTick.ToFixedString()}");
                }
            }
        }
        [Test]
        public void StaticGhostsAreSentWhenModified([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(StaticOptimizationTestSystem));
                StaticOptimizationTestSystem.s_ModifyNetworkId = -1;

                SetupBasicTest(testWorld, latencyProfile, new StaticOptimizationTestConverter());

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
                // Store the last tick we got for this
                var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                var lastSnapshot = clientSnapshot.GetLatestTick(clientSnapshotBuffer);

                // Run a bit longer
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // Verify taht we did not get any new snapshot
                clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                Assert.AreEqual(lastSnapshot, clientSnapshot.GetLatestTick(clientSnapshotBuffer));

                // Run N ticks with modifications
                StaticOptimizationTestSystem.s_ModifyNetworkId = 0;
                testWorld.Tick();
                StaticOptimizationTestSystem.s_ModifyNetworkId = -1;
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // Verify taht we did not get any new snapshot
                clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                var newLastSnapshot = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                Assert.AreNotEqual(lastSnapshot, newLastSnapshot);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // Verify that the snapshot stayed static at the new position
                clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                Assert.AreEqual(newLastSnapshot, clientSnapshot.GetLatestTick(clientSnapshotBuffer));
            }
        }

        [Test]
        public void StaticGhostsAreSentWhenUnmodified([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            const int entitiesToSpawn = 2;
            SetupBasicTest(testWorld, latencyProfile, new ZeroChangeGhostStaticOptimizationTestConverter(), entitiesToSpawn:entitiesToSpawn);

            // Verify the ghosts are spawned on client (bug in 1.5):
            var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
            var clientEntities = clientEntityManager.CreateEntityQuery(ComponentType.ReadWrite<GhostOwner>()).ToEntityArray(Allocator.Temp);
            Assert.AreEqual(entitiesToSpawn, clientEntities.Length);

            // Verify that static optimization kicked in:
            var currentTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
            foreach (var clientEnt in clientEntities)
            {
                var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                var ghostsLatestReceivedTick = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                var ticksSince = currentTick.TicksSince(ghostsLatestReceivedTick);
                Assert.IsTrue(ticksSince > 3, ticksSince.ToString());
            }
        }

        [Test]
        public void RelevancyChangesSendsStaticGhosts([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                // Spawn 16 ghosts
                SetupBasicTest(testWorld, latencyProfile, new StaticOptimizationTestConverter(), 16);
                // Set the ghost id for one of them to 1 so it is modified
                using var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                int ghostId;
                var serverEntities = serverQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(16, serverEntities.Length);
                ghostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntities[0]).ghostId;
                var con = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                Assert.AreNotEqual(Entity.Null, con);
                var connectionId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkId>(con).Value;

                // Get the changes across to the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
                using var clientQuery = clientEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                var clientEntities = clientQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                Assert.AreEqual(16, clientEntities.Length);


                // Make one of the ghosts irrelevant
                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
                var key = new RelevantGhostForConnection{Connection = connectionId, Ghost = ghostId};
                ghostRelevancy.GhostRelevancySet.TryAdd(key, 1);

                // Get the changes across to the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                clientEntities = clientQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                Assert.AreEqual(15, clientEntities.Length);

                // Allow it to spawn again
                ghostRelevancy.GhostRelevancySet.Remove(key);

                // Get the changes across to the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                clientEntities = clientQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                Assert.AreEqual(16, clientEntities.Length);
            }
        }
    }
}

using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class GhostPredictedOnlyConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostPredictedOnly());
            baker.AddComponent(entity, new GhostInterpolatedOnly());
            baker.AddComponent(entity, new GhostOwner());
        }
    }

    [GhostComponent(SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
    internal struct GhostPredictedOnly : IComponentData
    {
        [GhostField] public int Value;
    }
    [GhostComponent(SendTypeOptimization = GhostSendType.OnlyInterpolatedClients)]
    internal struct GhostInterpolatedOnly : IComponentData
    {
        [GhostField] public int Value;
    }

    [Category(NetcodeTestCategories.Foundational)]
    internal class PartialSendTests
    {
        [Test]
        public void OwnerPredictedSendsDataToPredicted()
        {
            TestHelper(true, true);
        }
        [Test]
        public void OwnerPredictedSendsDataToInterpolated()
        {
            TestHelper(false, true);
        }
        [Test]
        public void AlwaysPredictedSendsDataToPredicted()
        {
            TestHelper(true, false);
        }
        [Test]
        public void AlwaysInterpolatedSendsDataToInterpolated()
        {
            TestHelper(false, false);
        }
        private void TestHelper(bool predicted, bool ownerPrediction)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostPredictedOnlyConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                if (ownerPrediction)
                {
                    ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                }
                else
                {
                    ghostConfig.SupportedGhostModes = predicted ? GhostModeMask.Predicted : GhostModeMask.Interpolated;
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                var serverEnt = testWorld.TryGetSingletonEntity<GhostPredictedOnly>(testWorld.ServerWorld);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostPredictedOnly{Value = 1});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostInterpolatedOnly{Value = 1});

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Check the clients network id
                var clientNetworkId = testWorld.GetSingleton<NetworkId>(testWorld.ClientWorlds[0]);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = predicted ? clientNetworkId.Value : -1}); // set to client nid or invalid
                var serverCon = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ServerWorld);
                Assert.AreNotEqual(Entity.Null, serverCon);
                Assert.AreEqual(clientNetworkId.Value, testWorld.ServerWorld.EntityManager.GetComponentData<NetworkId>(serverCon).Value);
                Assert.Greater(clientNetworkId.Value, 0);

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                Assert.AreEqual(predicted, testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhost>(clientEnt));
                Assert.AreEqual(predicted ? 0 : 1, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInterpolatedOnly>(clientEnt).Value);
                Assert.AreEqual(predicted ? 1 : 0, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostPredictedOnly>(clientEnt).Value);
            }
        }

        /// <summary>Minimal ghost (replicated transform only) so all spawns share one chunk.</summary>
        internal class PartialSendTransformOnlyConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                baker.GetEntity(TransformUsageFlags.Dynamic);
            }
        }

        [Test, Description("Guards that a dynamic ghost's partial send continues from where it stopped instead of restarting from entity 0 each tick, and that NumRelevant reflects the whole chunk on continuation ticks.")]
        public unsafe void DynamicGhost_PartialSend_ContinuesFromLastPosition()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var ghostGameObject = new GameObject("PartialSendDynamicGhost");
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PartialSendTransformOnlyConverter();
            ghostGameObject.AddComponent<GhostAuthoringComponent>();

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);

            const int spawnedGhostCount = 64;
            var firstServerEntity = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, firstServerEntity);
            var extraGhosts = new NativeArray<Entity>(spawnedGhostCount - 1, Allocator.Temp);
            testWorld.ServerWorld.EntityManager.Instantiate(firstServerEntity, extraGhosts);

            ref var ghostSendSystemData = ref testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW;
            ghostSendSystemData.MaxSendChunks = 1;
            ghostSendSystemData.MaxIterateChunks = 1;
            ghostSendSystemData.DefaultSnapshotPacketSize = (int)GhostSystemConstants.MinSnapshotPacketSize;

            testWorld.Connect();
            testWorld.GoInGame();

            var serverConnection = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ServerWorld);

            var ghostSendSystemHandle = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
            ref var ghostSendSystem = ref testWorld.ServerWorld.Unmanaged.GetUnsafeSystemRef<GhostSendSystem>(ghostSendSystemHandle);

            const int maxTicks = 128;
            int observedStartIndex = -1;
            int observedNumRelevant = -1;
            bool sawContinuation = false;

            using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<GhostInstance>());

            int observedChunkCount = -1;
            for (int tickIndex = 0; tickIndex < maxTicks; ++tickIndex)
            {
                testWorld.Tick();

                var (jobHandle, connectionStateData) = ghostSendSystem.GetConnectionStateData(serverConnection);
                jobHandle.Complete();

                using var ghostChunks = ghostQuery.ToArchetypeChunkArray(Allocator.Temp);
                foreach (var ghostChunk in ghostChunks)
                {
                    if (!connectionStateData.SerializationState->TryGetValue(ghostChunk, out var ghostChunkState))
                        continue;
                    var startIndex = ghostChunkState.GetStartIndex();
                    if (startIndex > 0)
                    {
                        sawContinuation = true;
                        observedStartIndex = startIndex;
                        observedNumRelevant = ghostChunkState.GetNumRelevant();
                        observedChunkCount = ghostChunk.Count;
                        break;
                    }
                }
                if (sawContinuation) break;
            }

            if (!sawContinuation)
            {
                Assert.Inconclusive(
                    $"startIndex never advanced past 0 within {maxTicks} ticks, so no partial send occurred " +
                    $"(packet size / ghost count may be too small to force one).");
                return;
            }

            // No ghosts are irrelevant, so the whole-chunk relevant count should equal chunk.Count.
            Assert.AreEqual(observedChunkCount, observedNumRelevant,
                $"On a continuation tick (startIndex={observedStartIndex}), GetNumRelevant()={observedNumRelevant} " +
                $"but should count the whole chunk ({observedChunkCount}).");
        }

        /// <summary>Wide payload (four GhostFields) so one chunk overflows a MinSnapshotPacketSize packet, forcing partial sends.</summary>
        internal struct PartialSendWideValue : IComponentData
        {
            [GhostField] public int V0;
            [GhostField] public int V1;
            [GhostField] public int V2;
            [GhostField] public int V3;
        }

        /// <summary>Bakes the wide payload; TransformUsageFlags.None keeps the archetype small so all spawns share one chunk.</summary>
        internal class PartialSendWideValueConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.None);
                baker.AddComponent(entity, new PartialSendWideValue());
                baker.AddComponent(entity, new GhostOwner());
            }
        }

        const int OverfullChunkGhostCount = 64;

        /// <summary>Writes a different poorly-compressible value to every ghost so delta compression can't shrink the chunk.</summary>
        static void ScatterWideValues(NetCodeTestWorld testWorld, NativeArray<Entity> serverEntities, int tick)
        {
            var em = testWorld.ServerWorld.EntityManager;
            for (int i = 0; i < serverEntities.Length; ++i)
            {
                int v = unchecked((int)((uint)tick * 2654435761u + (uint)i * 40503u));
                em.SetComponentData(serverEntities[i], new PartialSendWideValue { V0 = v, V1 = ~v, V2 = v ^ i, V3 = v + i });
            }
        }

        /// <summary>Builds a world with 64 ghosts in one chunk and a MinSnapshotPacketSize packet, so the chunk can't fit one snapshot.</summary>
        static NativeArray<Entity> SetupOverfullChunk(NetCodeTestWorld testWorld, GhostOptimizationMode optimizationMode)
        {
            testWorld.Bootstrap(true);

            var ghostGameObject = new GameObject("PartialSendOverfullChunk");
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PartialSendWideValueConverter();
            ghostGameObject.AddComponent<GhostAuthoringComponent>().OptimizationMode = optimizationMode;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);

            var firstServerEntity = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, firstServerEntity);
            var extraGhosts = new NativeArray<Entity>(OverfullChunkGhostCount - 1, Allocator.Temp);
            testWorld.ServerWorld.EntityManager.Instantiate(firstServerEntity, extraGhosts);

            ref var ghostSendSystemData = ref testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW;
            ghostSendSystemData.DefaultSnapshotPacketSize = (int)GhostSystemConstants.MinSnapshotPacketSize;

            testWorld.Connect();
            testWorld.GoInGame();

            var serverEntities = testWorld.ServerWorld.EntityManager
                .CreateEntityQuery(ComponentType.ReadWrite<PartialSendWideValue>())
                .ToEntityArray(Allocator.Persistent);
            Assert.AreEqual(OverfullChunkGhostCount, serverEntities.Length);
            return serverEntities;
        }

        [Test, Description("An over-full chunk whose ghosts all change every tick can only fit its leading entities per packet; if serialization restarts from entity 0 each tick the tail never replicates. Guards that every ghost still replicates while changes are ongoing.")]
        public unsafe void OverfullChunk_AllGhostsReplicate_WhileConstantlyChanging([Values] GhostOptimizationMode optimizationMode)
        {
            using var testWorld = new NetCodeTestWorld();
            using var serverEntities = SetupOverfullChunk(testWorld, optimizationMode);

            var serverConnection = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ServerWorld);
            var ghostSendSystemHandle = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
            ref var ghostSendSystem = ref testWorld.ServerWorld.Unmanaged.GetUnsafeSystemRef<GhostSendSystem>(ghostSendSystemHandle);
            using var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PartialSendWideValue>());

            // Change every ghost each tick so the static zero-change skip never applies and the chunk stays over-full.
            const int changingTicks = 128;
            int peakClientGhostCount = 0;
            int peakServerStartIndex = 0;
            int observedChunkCount = 0;
            for (int tick = 1; tick <= changingTicks; ++tick)
            {
                ScatterWideValues(testWorld, serverEntities, tick);
                testWorld.Tick();
                peakClientGhostCount = math.max(peakClientGhostCount, CountClientGhosts(testWorld));

                // Diagnostic: how far the server advanced startIndex into the chunk this tick.
                var (jobHandle, connectionStateData) = ghostSendSystem.GetConnectionStateData(serverConnection);
                jobHandle.Complete();
                using var serverChunks = serverGhostQuery.ToArchetypeChunkArray(Allocator.Temp);
                observedChunkCount = serverChunks.Length;
                foreach (var serverChunk in serverChunks)
                    if (connectionStateData.SerializationState->TryGetValue(serverChunk, out var chunkState))
                        peakServerStartIndex = math.max(peakServerStartIndex, chunkState.GetStartIndex());
            }

            Assert.AreEqual(OverfullChunkGhostCount, peakClientGhostCount,
                $"Only {peakClientGhostCount}/{OverfullChunkGhostCount} ghosts replicated while the over-full chunk " +
                $"was constantly changing ({optimizationMode}); the tail never got sent. " +
                $"[diag: peakServerStartIndex={peakServerStartIndex}, serverChunkCount={observedChunkCount}]");
        }

#if !NETCODE_SNAPSHOT_HISTORY_SIZE_6
        // The zero-change re-arm timing depends on the default snapshot history depth.
        [Test, Description("After a constantly-changing static over-full chunk settles, every ghost must receive the final value and the chunk must then go quiet (zero-change re-armed).")]
        public void StaticOverfullChunk_ReArmsZeroChange_AfterChangesStop()
        {
            using var testWorld = new NetCodeTestWorld();
            using var serverEntities = SetupOverfullChunk(testWorld, GhostOptimizationMode.Static);

            // Phase 1: constant change to exercise the partial-send continuation path.
            for (int tick = 1; tick <= 64; ++tick)
            {
                ScatterWideValues(testWorld, serverEntities, tick);
                testWorld.Tick();
            }

            // Phase 2: settle on a single known value and let the partial sends finish delivering it everywhere.
            const int finalValue = 0x05EADBEE;
            var em = testWorld.ServerWorld.EntityManager;
            for (int i = 0; i < serverEntities.Length; ++i)
                em.SetComponentData(serverEntities[i], new PartialSendWideValue { V0 = finalValue, V1 = finalValue, V2 = finalValue, V3 = finalValue });
            for (int tick = 0; tick < 128; ++tick)
                testWorld.Tick();

            var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
            using var clientQuery = clientEntityManager.CreateEntityQuery(ComponentType.ReadOnly<PartialSendWideValue>());
            using (var clientValues = clientQuery.ToComponentDataArray<PartialSendWideValue>(Allocator.Temp))
            {
                Assert.AreEqual(OverfullChunkGhostCount, clientValues.Length, "Not every ghost replicated.");
                for (int i = 0; i < clientValues.Length; ++i)
                    Assert.AreEqual(finalValue, clientValues[i].V0, $"Ghost {i} has a stale/skipped value (V0).");
            }

            // Record the latest received tick per client ghost, then verify static optimization stops sending.
            using var clientEntities = clientQuery.ToEntityArray(Allocator.Temp);
            var lastReceivedTick = new NativeArray<NetworkTick>(clientEntities.Length, Allocator.Temp);
            for (int i = 0; i < clientEntities.Length; ++i)
                lastReceivedTick[i] = clientEntityManager.GetComponentData<SnapshotData>(clientEntities[i])
                    .GetLatestTick(clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEntities[i]));

            for (int tick = 0; tick < 16; ++tick)
                testWorld.Tick();

            for (int i = 0; i < clientEntities.Length; ++i)
            {
                var nowTick = clientEntityManager.GetComponentData<SnapshotData>(clientEntities[i])
                    .GetLatestTick(clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEntities[i]));
                Assert.AreEqual(lastReceivedTick[i], nowTick,
                    $"Static ghost {i} kept receiving snapshots after coming to rest (zero-change was not re-armed).");
            }
        }
#endif

        /// <summary>Count instantiated ghosts of the over-full-chunk type on the (single) client.</summary>
        static int CountClientGhosts(NetCodeTestWorld testWorld)
        {
            using var clientQuery = testWorld.ClientWorlds[0].EntityManager
                .CreateEntityQuery(ComponentType.ReadOnly<PartialSendWideValue>());
            return clientQuery.CalculateEntityCount();
        }
    }
}

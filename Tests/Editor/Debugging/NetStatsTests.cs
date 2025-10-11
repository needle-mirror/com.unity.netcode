#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
#if NETCODE_DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.Editor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    partial struct MispredictionSystem : ISystem
    {
        public unsafe void OnUpdate(ref SystemState state)
        {
            var increment = state.WorldUnmanaged.IsServer() ? 1 : 2;
            foreach (var c in SystemAPI.Query<RefRW<GhostGenTestTypes.GhostGenBigStruct>>())
            {
                int* v = (int*)UnsafeUtility.AddressOf(ref c.ValueRW);
                for (int i = 0; i < 101; ++i)
                    v[i] += increment;
            }
        }
    }

    class NetStatsTests
    {
        const int k_SnapshotMaxHeaderSizeInBits = 200; // we assume a snapshot's header size is smaller than this value for this test. If we add more header values, we should update this value.
        [Test]
        public void TestLargeNumberOfPredictionErrorsAreReported([Values]bool useMetrics)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(MispredictionSystem));
            //I need a very very long string
            testWorld.CreateGhostCollection();
            testWorld.CreateWorlds(true, 1);
            var serverEntity = DebuggingTestUtils.CreateEntityPrefab(testWorld.ServerWorld);
            DebuggingTestUtils.CreateEntityPrefab(testWorld.ClientWorlds[0]);
            var clientMetrics = testWorld.TryCreateGhostMetricsSingleton(testWorld.ClientWorlds[0]);
            UpdateMetrics(testWorld.ClientWorlds[0], useMetrics, clientMetrics);

            testWorld.Connect();
            testWorld.GoInGame();
            for(int i=0; i<32; ++i)
                testWorld.Tick();

            //Verify that the ghost collection stats is in the condition we expect:
            var statsCollectionData = testWorld.GetSingletonRW<GhostStatsCollectionData>(testWorld.ServerWorld);
            var errorNames = testWorld.ClientWorlds[0].EntityManager.GetBuffer<PredictionErrorNames>(clientMetrics);
            Assert.Less(errorNames.Length, 101);
            Assert.AreEqual(statsCollectionData.ValueRO.m_PredictionErrors.Length, 101);

            if (useMetrics)
            {
                var predictionErrors = testWorld.ClientWorlds[0].EntityManager.GetBuffer<PredictionErrorMetrics>(clientMetrics);
                Assert.AreEqual(predictionErrors.Length, predictionErrors.Length);
            }

            //spawn the entity.
            testWorld.ServerWorld.EntityManager.Instantiate(serverEntity);

            //Fake debugger connected
            statsCollectionData = testWorld.GetSingletonRW<GhostStatsCollectionData>(testWorld.ClientWorlds[0]);
            if (!useMetrics)
            {
                statsCollectionData.ValueRW.m_StatIndex = 0;
                statsCollectionData.ValueRW.m_CollectionTick = NetworkTick.Invalid;;
                statsCollectionData.ValueRW.m_PacketQueue.Clear();
                statsCollectionData.ValueRW.m_UsedPacketPoolSize = 0;
                if (statsCollectionData.ValueRW.m_LastNameAndErrorArray.Length > 0)
                    statsCollectionData.ValueRW.AppendNamePacket(testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0]));
                testWorld.GetSingletonRW<GhostStats>(testWorld.ClientWorlds[0]).ValueRW.IsConnected = true;
            }

            //wait for entity to spawn
            for(int i=0; i<4; ++i)
                testWorld.Tick();

            //predict for a bit, check that no error or exception are thrown
            for (int i = 0; i < 32; ++i)
            {
                testWorld.Tick();
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                statsCollectionData = testWorld.GetSingletonRW<GhostStatsCollectionData>(testWorld.ClientWorlds[0]);
                var statsErrors = statsCollectionData.ValueRW.m_PredictionErrors;
                //we arechecking the data after one tick here because the Stats are updated the next frame in the Initialization
                //system group (we validate the last frame).
                if (i > 0)
                {
                    for (int err = 0; err < statsErrors.Length; ++err)
                    {
                        Assert.IsTrue(math.abs(1f - statsErrors[err]) < 1e-3f);
                    }
                    if (useMetrics)
                    {
                        var predictionErrors = testWorld.ClientWorlds[0].EntityManager.GetBuffer<PredictionErrorMetrics>(clientMetrics);
                        for (int err = 0; err < predictionErrors.Length; ++err)
                        {
                            Assert.IsTrue(math.abs(1f - predictionErrors[err].Value) < 1e-3f);
                        }
                    }
                }
            }
        }

        [Test, Description("Test that accessing the unsafe array still throws with our custom safety checks")]
        public void NetStats_UsingDisposedStats_ShouldFail()
        {
            UnsafeGhostStatsSnapshot nullStats = default;
            Assert.Throws<NullReferenceException>(() =>
            {
                _ = nullStats.PerGhostTypeStatsListRO;
            });
            Assert.Throws<NullReferenceException>(() =>
            {
                _ = nullStats.PerGhostTypeStatsListRefRW;
            });
            UnsafeGhostStatsSnapshot stats = new UnsafeGhostStatsSnapshot(1, Allocator.Temp);
            stats.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
            {
                _ = stats.PerGhostTypeStatsListRO;
            });
            Assert.Throws<ObjectDisposedException>(() =>
            {
                _ = stats.PerGhostTypeStatsListRefRW;
            });
        }

        [Test, Description("make sure we can blit to and from editor profiler's byte array")]
        public unsafe void NetStats_BlittableDataForProfiler_IsValid()
        {
            var stats = new UnsafeGhostStatsSnapshot(2, Allocator.Temp);
            try
            {
                stats.DespawnCount = 1;
                stats.DestroySizeInBits = 2;
                stats.Tick = new NetworkTick(3);
                stats.PacketsCount = 33;
                stats.SnapshotTotalSizeInBits = 34;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).ChunkCount = 4;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).EntityCount = 5;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).SizeInBits = 6;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).UncompressedCount = 7;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).PerComponentStatsList.Resize(2, NativeArrayOptions.ClearMemory);
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).PerComponentStatsList.ElementAt(0).SizeInSnapshotInBits = 8;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).PerComponentStatsList.ElementAt(1).SizeInSnapshotInBits = 9;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).ChunkCount = 41;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount = 51;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits = 61;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount = 71;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).PerComponentStatsList.Resize(2, NativeArrayOptions.ClearMemory);
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).PerComponentStatsList.ElementAt(0).SizeInSnapshotInBits = 81;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).PerComponentStatsList.ElementAt(1).SizeInSnapshotInBits = 91;

                var bytes = stats.ToBlittableData(Allocator.Temp);
                var deserializedStats = UnsafeGhostStatsSnapshot.FromBlittableData(Allocator.Temp, bytes);
                Assert.AreEqual(1, deserializedStats.DespawnCount);
                Assert.AreEqual(2, deserializedStats.DestroySizeInBits);
                Assert.AreEqual(3, deserializedStats.Tick.TickIndexForValidTick);
                Assert.AreEqual(33, deserializedStats.PacketsCount);
                Assert.AreEqual(34, deserializedStats.SnapshotTotalSizeInBits);
                Assert.AreEqual(4, deserializedStats.PerGhostTypeStatsListRO[0].ChunkCount);
                Assert.AreEqual(5, deserializedStats.PerGhostTypeStatsListRO[0].EntityCount);
                Assert.AreEqual(6, deserializedStats.PerGhostTypeStatsListRO[0].SizeInBits);
                Assert.AreEqual(7, deserializedStats.PerGhostTypeStatsListRO[0].UncompressedCount);
                Assert.AreEqual(8, deserializedStats.PerGhostTypeStatsListRO[0].PerComponentStatsList[0].SizeInSnapshotInBits);
                Assert.AreEqual(9, deserializedStats.PerGhostTypeStatsListRO[0].PerComponentStatsList[1].SizeInSnapshotInBits);
                Assert.AreEqual(41, deserializedStats.PerGhostTypeStatsListRO[1].ChunkCount);
                Assert.AreEqual(51, deserializedStats.PerGhostTypeStatsListRO[1].EntityCount);
                Assert.AreEqual(61, deserializedStats.PerGhostTypeStatsListRO[1].SizeInBits);
                Assert.AreEqual(71, deserializedStats.PerGhostTypeStatsListRO[1].UncompressedCount);
                Assert.AreEqual(81, deserializedStats.PerGhostTypeStatsListRO[1].PerComponentStatsList[0].SizeInSnapshotInBits);
                Assert.AreEqual(91, deserializedStats.PerGhostTypeStatsListRO[1].PerComponentStatsList[1].SizeInSnapshotInBits);
            }
            finally
            {
                stats.Dispose();
            }
        }

        [Test, Description("general stats validation test for simple spawn")]
        public void NetStats_StatsAreValid()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateGhostCollection();
            testWorld.CreateWorlds(true, 1);
            testWorld.TryCreateGhostMetricsSingleton(testWorld.ServerWorld);
            testWorld.TryCreateGhostMetricsSingleton(testWorld.ClientWorlds[0]);
            var serverPrefab = DebuggingTestUtils.CreateEntityPrefab(testWorld.ServerWorld);
            DebuggingTestUtils.CreateEntityPrefab(testWorld.ClientWorlds[0]);

            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 32; i++)
            {
                testWorld.Tick();
            }

            var serverEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefab);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypes.GhostGenBigStruct() { field000 = 123 }); // need to set non default value to get per component stats
            testWorld.Tick();
            testWorld.Tick(); // entity is sent, then client world receives it. Both jobs happen one after the other in the same Tick() call. server write stat should contain new entry now, client write stats should also be written to
            testWorld.Tick(); // both client and server write stats are copied to the respective read stats buffer
            // Client also now has the ghost spawned

            Assert.AreEqual(123, testWorld.GetSingleton<GhostGenTestTypes.GhostGenBigStruct>(testWorld.ClientWorlds[0]).field000, "sanity check failed");

            var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntity);
            var ghostType = ghostInstance.ghostType;
            var spawnTick = ghostInstance.spawnTick;

            UnsafeGhostStatsSnapshot.PerGhostTypeStats GetGhostStats(int ghostType, bool isServer)
            {
                var stats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(isServer ? testWorld.ServerWorld : testWorld.ClientWorlds[0]);
                var readStats = stats.GetAsyncStatsReader();
                var perGhostTypeStats = readStats.PerGhostTypeStatsListRO;
                return perGhostTypeStats[ghostType];
            }

            var serverStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ServerWorld);
            var readStats = serverStats.GetAsyncStatsReader();
            {
                // validate server stats
                Assert.AreEqual(spawnTick.TickIndexForValidTick + 1, readStats.Tick.TickIndexForValidTick, "stats tick should be the spawn tick"); // the way we send ghosts right now, we only send on the next tick, so spawn tick will be offset by one vs the snapshot tick
                Assert.AreEqual(0, readStats.DespawnCount, "despawn should be zero");
                Assert.AreEqual(0, readStats.DestroySizeInBits, "destroy size should be zero");
                var perGhostTypeStats = readStats.PerGhostTypeStatsListRO;
                Assert.AreEqual(1, perGhostTypeStats.Length);
                Assert.AreEqual(1, readStats.PacketsCount, "PacketsCount should be one");
                Assert.Greater(readStats.SnapshotTotalSizeInBits, GetGhostStats(ghostType, true).SizeInBits, "total size should be larger than per ghost type size");
                Assert.Less(readStats.SnapshotTotalSizeInBits, GetGhostStats(ghostType, true).SizeInBits + k_SnapshotMaxHeaderSizeInBits, "total size shouldn't be too big");
                Assert.AreEqual(1, GetGhostStats(ghostType, true).EntityCount, "entity count");
                Assert.AreEqual(1, GetGhostStats(ghostType, true).ChunkCount, "chunk count");
                Assert.AreEqual(0, GetGhostStats(ghostType, true).UncompressedCount, "uncompressed count should be uninitialized server side");
                Assert.IsTrue(GetGhostStats(ghostType, true).SizeInBits > 8, "size in bits for ghost type");
                Assert.AreEqual(0, GetGhostStats(ghostType, true).UncompressedCount);
                Assert.AreEqual(1, GetGhostStats(ghostType, true).PerComponentStatsList.Length);
                Assert.IsTrue(GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits > 8, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)}");
                Assert.IsTrue(GetGhostStats(ghostType, true).SizeInBits > GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits, "per component stats should be less than total ghost size");
            }
            {
                // validate client
                // client read stats should now show the spawn stats
                var clientStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0]);
                var clientStatsReader = clientStats.GetAsyncStatsReader();
                Assert.AreEqual(1, clientStatsReader.PerGhostTypeStatsListRO.Length);
                Assert.AreEqual(0, clientStatsReader.DespawnCount);
                Assert.AreEqual(0, clientStatsReader.DestroySizeInBits);
                Assert.AreEqual(spawnTick.TickIndexForValidTick + 1, clientStatsReader.Tick.TickIndexForValidTick, "received snapshot tick should be spawn tick");
                Assert.AreEqual(readStats.PacketsCount, clientStatsReader.PacketsCount, "sent and received snapshot packet count should be the same");
                Assert.AreEqual(readStats.SnapshotTotalSizeInBits, clientStatsReader.SnapshotTotalSizeInBits, "sent and received snapshot size should be the same");
                Assert.AreEqual(1, GetGhostStats(ghostType, false).EntityCount, "entity count");
                Assert.AreEqual(0, GetGhostStats(ghostType, false).ChunkCount, "chunk count should be uninitialized client side");
                Assert.IsTrue(GetGhostStats(ghostType, false).SizeInBits > 8, "size in bits for ghost type");
                Assert.IsTrue(GetGhostStats(ghostType, false).PerComponentStatsList[0].SizeInSnapshotInBits > 8, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)}");
                Assert.AreEqual(1, GetGhostStats(ghostType, false).UncompressedCount, "uncompressed count");
                Assert.IsTrue(GetGhostStats(ghostType, false).SizeInBits > GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits, "per component stats should be less than total ghost size");
            }

            testWorld.Tick();
            {
                // if there is noise in server stats, we can check it next tick
                // validate server doesn't have new stats with no change
                Assert.IsTrue(GetGhostStats(ghostType, true).SizeInBits > 8, "should still be sending metadata for ghost even with no data change");
                Assert.AreEqual(0, GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits, "no data change so there should be no stats for component");
            }

            // update server side component to trigger component stats
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypes.GhostGenBigStruct() { field000 = 124 });
            testWorld.Tick(); // data is sent
            testWorld.Tick(); // server read buffer is updated
            {
                // validate data change server side
                Assert.IsTrue(GetGhostStats(ghostType, true).SizeInBits > 8, "ghost type size should be bigger than 8 bits");
                Assert.IsTrue(GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits > 0, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)}");
                Assert.IsTrue(GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits < 16, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)} should be small due to compression and small change");
                Assert.IsTrue(GetGhostStats(ghostType, true).SizeInBits > GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits, "per component stats should be less than total ghost size");

            }
            Assert.AreEqual(124, testWorld.GetSingleton<GhostGenTestTypes.GhostGenBigStruct>(testWorld.ClientWorlds[0]).field000, "sanity check failed");
            {
                // validate data change client side
                Assert.IsTrue(GetGhostStats(ghostType, false).SizeInBits > 8, "ghost type size should be bigger than 8 bits");
                Assert.IsTrue(GetGhostStats(ghostType, false).PerComponentStatsList[0].SizeInSnapshotInBits > 0, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)}");
                Assert.IsTrue(GetGhostStats(ghostType, false).PerComponentStatsList[0].SizeInSnapshotInBits < 16, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)} should be small due to compression and small change");
                Assert.IsTrue(GetGhostStats(ghostType, false).SizeInBits > GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits, "per component stats should be less than total ghost size");
            }
        }

        [Test]
        public void NetStats_PartialSnapshotStats_AreValid([Values(1, 2)] int clientCount)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateGhostCollection();
            testWorld.CreateWorlds(true, clientCount);
            testWorld.TryCreateGhostMetricsSingleton(testWorld.ServerWorld);
            for (int i = 0; i < clientCount; i++)
            {
                testWorld.TryCreateGhostMetricsSingleton(testWorld.ClientWorlds[i]);
                DebuggingTestUtils.CreateEntityPrefab(testWorld.ClientWorlds[i]);
            }
            var serverPrefab = DebuggingTestUtils.CreateEntityPrefab(testWorld.ServerWorld);

            const int MaxPayloadSize = 1375;

            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 32; i++)
            {
                testWorld.Tick();
            }

            var ghostCount = 200; // 400 bytes per ghost, this should go above one MTU with delta compression
            var serverGhosts = new List<Entity>();
            for (int i = 0; i < ghostCount; i++)
            {
                var serverEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefab);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypes.GhostGenBigStruct() { field000 = 123 }); // need to set non default value to get per component stats
                serverGhosts.Add(serverEntity);
            }

            void IncrementAll()
            {
                for (int i = 0; i < ghostCount; i++)
                {
                    var ghostGenBigStruct = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGenTestTypes.GhostGenBigStruct>(serverGhosts[i]);
                    ghostGenBigStruct.Increment();
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverGhosts[i], ghostGenBigStruct);
                }
            }

            testWorld.Tick();
            IncrementAll(); // make sure there's some bandwidth usage generated
            testWorld.Tick(); // entity is sent, then client world receives it. Both jobs happen one after the other in the same Tick() call. server write stat should contain new entry now, client write stats should also be written to
            IncrementAll();
            testWorld.Tick(); // both client and server write stats are copied to the respective read stats buffer
            IncrementAll();

            var serverStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ServerWorld);
            Assert.AreEqual(1 * clientCount, serverStats.GetAsyncStatsReader().PacketsCount);
            for (int i = 0; i < clientCount; i++)
            {
                var clientStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i]);
                Assert.AreEqual(1, clientStats.GetAsyncStatsReader().PacketsCount);
            }

            testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW.DefaultSnapshotPacketSize = MaxPayloadSize*2;
            // we now have enough room for 2 packets, so stats should reflect this as well

            testWorld.Tick();
            IncrementAll();
            testWorld.Tick();
            IncrementAll();
            testWorld.Tick();
            IncrementAll();

            serverStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ServerWorld);
            Assert.AreEqual(2 * clientCount, serverStats.GetAsyncStatsReader().PacketsCount);
            Assert.Greater(serverStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits, MaxPayloadSize * 8 * clientCount); // test we sent more than one MTU
            Assert.Greater(serverStats.GetAsyncStatsReader().SnapshotTotalSizeInBits, serverStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits, "total snapshot size should be greater than per ghost type size");
            Assert.Less(serverStats.GetAsyncStatsReader().SnapshotTotalSizeInBits, serverStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits + k_SnapshotMaxHeaderSizeInBits * clientCount);
            for (int i = 0; i < clientCount; i++)
            {
                var clientStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i]);
                Assert.AreEqual(2, clientStats.GetAsyncStatsReader().PacketsCount);
                Assert.Greater(clientStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits, MaxPayloadSize * 8); // test we sent more than one mtu
                Assert.AreEqual(serverStats.GetAsyncStatsReader().SnapshotTotalSizeInBits / clientCount, clientStats.GetAsyncStatsReader().SnapshotTotalSizeInBits);
            }

            testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW.DefaultSnapshotPacketSize = MaxPayloadSize*4;
            // we now have enough room for 2 packets, so stats should reflect this as well

            testWorld.Tick();
            IncrementAll();
            testWorld.Tick();
            IncrementAll();
            testWorld.Tick();
            IncrementAll();

            serverStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ServerWorld);
            Assert.AreEqual(4 * clientCount, serverStats.GetAsyncStatsReader().PacketsCount);
            Assert.Greater(serverStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits, MaxPayloadSize * 8 * 3 * clientCount); // test we sent more than 3 MTU
            for (int i = 0; i < clientCount; i++)
            {
                var clientStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i]);
                Assert.AreEqual(4, clientStats.GetAsyncStatsReader().PacketsCount);
                Assert.Greater(clientStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits, MaxPayloadSize * 8 * 3); // test we sent more than 3 MTU
            }
        }

        void UpdateMetrics(World world, bool useMetrics, Entity metricsSingleton)
        {
            if (useMetrics)
                world.EntityManager.AddBuffer<PredictionErrorMetrics>(metricsSingleton);
        }
    }
}
#endif

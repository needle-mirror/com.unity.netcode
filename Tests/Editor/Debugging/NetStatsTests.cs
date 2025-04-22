using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.NetCode.Tests;
using Unity.Transforms;
using UnityEngine;

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
        [Test]
        public void TestLargeNumberOfPredictionErrorsAreReported([Values]bool useMetrics)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(MispredictionSystem));
            //I need a very very long string
            testWorld.CreateGhostCollection();
            testWorld.CreateWorlds(true, 1);
            var serverEntity = CreateEntityPrefab(testWorld.ServerWorld);
            CreateEntityPrefab(testWorld.ClientWorlds[0]);
            var clientMetrics = CreateMetrics(testWorld.ClientWorlds[0], useMetrics);
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
                    statsCollectionData.ValueRW.AppendNamePacket();
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

        private Entity CreateEntityPrefab(World world)
        {
            var entity = world.EntityManager.CreateEntity(typeof(GhostGenTestTypes.GhostGenBigStruct));
            GhostPrefabCreation.ConvertToGhostPrefab(world.EntityManager, entity, new GhostPrefabCreation.Config
            {
                Name = "GhostGenBigStruct",
                SupportedGhostModes = GhostModeMask.Predicted,
            });
            return entity;
        }

        Entity CreateMetrics(World world, bool useMetrics)
        {
            var typeList = new NativeArray<ComponentType>(useMetrics ? 5 : 4, Allocator.Temp);
            typeList[0] = ComponentType.ReadWrite<GhostMetricsMonitor>();
            typeList[1] = ComponentType.ReadWrite<GhostNames>();
            typeList[2] = ComponentType.ReadWrite<GhostMetrics>();
            typeList[3] = ComponentType.ReadWrite<PredictionErrorNames>();
            if (useMetrics)
                typeList[4] = ComponentType.ReadWrite<PredictionErrorMetrics>();
            var archetype = world.EntityManager.CreateArchetype(typeList);
            return world.EntityManager.CreateEntity(archetype);
        }
    }
}

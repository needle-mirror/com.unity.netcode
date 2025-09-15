using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.NetCode.Tests;
using Unity.Transforms;
using UnityEngine;

[DisableAutoCreation]
[CreateBefore(typeof(DefaultVariantSystemGroup))]
[UpdateInGroup(typeof(DefaultVariantSystemGroup))]
partial class SingleBaselineDefaultConfig : DefaultVariantSystemBase
{
    protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
    {
        defaultVariants.Add(new ComponentType(typeof(EnableableComponent_0)), Rule.ForAll(typeof(EnableableComponent_0)));
        defaultVariants.Add(new ComponentType(typeof(EnableableComponent_1)), Rule.ForAll(typeof(EnableableComponent_1)));
        defaultVariants.Add(new ComponentType(typeof(EnableableComponent_2)), Rule.ForAll(typeof(EnableableComponent_2)));
        defaultVariants.Add(new ComponentType(typeof(EnableableComponent_3)), Rule.ForAll(typeof(EnableableComponent_3)));
        defaultVariants.Add(new ComponentType(typeof(EnableableBuffer_0)), Rule.ForAll(typeof(EnableableBuffer_0)));
        defaultVariants.Add(new ComponentType(typeof(EnableableBuffer_1)), Rule.ForAll(typeof(EnableableBuffer_1)));
        defaultVariants.Add(new ComponentType(typeof(EnableableBuffer_2)), Rule.ForAll(typeof(EnableableBuffer_2)));
    }
}

[DisableAutoCreation]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
partial struct ChangeComponentValueSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var nt = SystemAPI.GetSingleton<NetworkTime>();
        foreach (var c in SystemAPI.Query<RefRW<EnableableComponent_0>>())
            c.ValueRW.SetValue(c.ValueRW.GetValue() + 5);
        foreach (var c in SystemAPI.Query<RefRW<EnableableComponent_1>>())
            c.ValueRW.SetValue(c.ValueRW.GetValue() + 5);
        foreach (var c in SystemAPI.Query<RefRW<EnableableComponent_2>>())
            c.ValueRW.SetValue(c.ValueRW.GetValue() + 5);
        foreach (var c in SystemAPI.Query<RefRW<EnableableComponent_3>>())
            c.ValueRW.SetValue(c.ValueRW.GetValue() + 5);

        //Buffer are never using 3 baselines. They are still here to validate nothing break. But for both bandwidth and cpu
        //there is no difference.
        foreach (var c in SystemAPI.Query<DynamicBuffer<EnableableBuffer_0>>())
        {
            for (int i = 0; i < c.Length; i++)
            {
                c.ElementAt(i).SetValue(c.ElementAt(i).GetValue() + 5);
            }
        }
        foreach (var c in SystemAPI.Query<DynamicBuffer<EnableableBuffer_1>>())
        {
            for (int i = 0; i < c.Length; i++)
            {
                c.ElementAt(i).SetValue(c.ElementAt(i).GetValue() + 5);
            }
        }
        foreach (var c in SystemAPI.Query<DynamicBuffer<EnableableBuffer_2>>())
        {
            for (int i = 0; i < c.Length; i++)
            {
                c.ElementAt(i).SetValue(c.ElementAt(i).GetValue() + 5);
            }
        }
    }
}

class SingleBaselineTests
{
    //This create a ghost with 5 child entites, of which 3 in the same chunk, and other 2 in distinct chunks
    //for an an overall use of 4 archetypes per ghost.
    private static Entity CreatePrefab(EntityManager entityManager, GhostPrefabCreation.Config config)
    {
        var prefab = entityManager.CreateEntity();
        entityManager.AddComponentData(prefab, new EnableableComponent_0 { value = 1 });
        entityManager.AddComponentData(prefab, new EnableableComponent_1 { value = 2 });
        entityManager.AddComponentData(prefab, new EnableableComponent_2 { value = 3 });
        entityManager.AddComponentData(prefab, new EnableableComponent_3 { value = 4 });
        entityManager.AddComponentData(prefab, LocalTransform.Identity);
        entityManager.AddComponent<GhostOwner>(prefab);
        entityManager.AddBuffer<EnableableBuffer_0>(prefab).ResizeUninitialized(3);
        entityManager.AddBuffer<EnableableBuffer_1>(prefab).ResizeUninitialized(4);
        entityManager.AddBuffer<EnableableBuffer_2>(prefab).ResizeUninitialized(5);
        entityManager.AddBuffer<LinkedEntityGroup>(prefab);
        entityManager.GetBuffer<LinkedEntityGroup>(prefab).Add(prefab);
        for (int i = 0; i < 5; ++i)
        {
            var child = entityManager.CreateEntity();
            entityManager.AddComponent<Prefab>(child);
            if (i < 3)
            {
                entityManager.AddComponentData(child, new EnableableComponent_1 { value = 10 + i });
                entityManager.AddComponentData(child, new EnableableComponent_2 { value = 20 + i });
                entityManager.AddComponentData(child, new EnableableComponent_3 { value = 30 + i });
                entityManager.AddBuffer<EnableableBuffer_0>(child).ResizeUninitialized(3);
                entityManager.AddBuffer<EnableableBuffer_1>(child).ResizeUninitialized(4);
            }
            else if (i == 3)
            {
                entityManager.AddComponentData(child, new EnableableComponent_1 { value = 10 + i });
                entityManager.AddComponentData(child, new EnableableComponent_2 { value = 20 + i });
            }
            else if (i == 4)
            {
                entityManager.AddComponentData(child, new EnableableComponent_0 { value = 10 + i });
                entityManager.AddComponentData(child, new EnableableComponent_1 { value = 30 + i });
                entityManager.AddBuffer<EnableableBuffer_0>(child).ResizeUninitialized(3);
            }

            entityManager.GetBuffer<LinkedEntityGroup>(prefab).Add(child);
        }
        GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, config);
        return prefab;
    }

    //This create a ghost with 5 child entites, of which 3 in the same chunk, and other 2 in distinct chunks
    //for an an overall use of 4 archetypes per ghost.
    private static Entity CreatePrefabForBandwidth(EntityManager entityManager, GhostPrefabCreation.Config config)
    {
        var prefab = entityManager.CreateEntity();
        entityManager.AddComponentData(prefab, new EnableableComponent_0 { value = 1 });
        entityManager.AddComponentData(prefab, new EnableableComponent_1 { value = 2 });
        entityManager.AddComponentData(prefab, new EnableableComponent_2 { value = 3 });
        entityManager.AddComponentData(prefab, new EnableableComponent_3 { value = 4 });
        entityManager.AddComponentData(prefab, new EnableableComponent_5 { value = 1 });
        entityManager.AddComponentData(prefab, new EnableableComponent_6 { value = 2 });
        entityManager.AddComponentData(prefab, new EnableableComponent_7 { value = 3 });
        entityManager.AddComponentData(prefab, new EnableableComponent_8 { value = 4 });
        entityManager.AddComponentData(prefab, new EnableableComponent_9 { value = 1 });
        entityManager.AddComponentData(prefab, new EnableableComponent_10 { value = 2 });
        GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, config);
        return prefab;
    }

    private static void VerifyComponent<T>(NetCodeTestWorld testWorld, Entity serverEntity, Entity clientEntity)
        where T : unmanaged, IComponentData, IComponentValue
    {
        if (testWorld.ServerWorld.EntityManager.HasComponent<T>(serverEntity))
        {
            Assert.AreEqual(testWorld.ServerWorld.EntityManager.GetComponentData<T>(serverEntity).GetValue(),
                testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntity).GetValue());
        }
    }

    private static void VerifyBuffer<T>(NetCodeTestWorld testWorld, Entity serverEntity, Entity clientEntity)
        where T : unmanaged, IBufferElementData, IComponentValue
    {
        if (testWorld.ServerWorld.EntityManager.HasComponent<T>(serverEntity))
        {
            var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntity);
            var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<T>(clientEntity);
            Assert.AreEqual(serverBuffer.Length, clientBuffer.Length);
            for (int i = 0; i < serverBuffer.Length; ++i)
                Assert.AreEqual(serverBuffer[i].GetValue(), clientBuffer[i].GetValue());
        }
    }

    private static void VerifyEntities(NetCodeTestWorld testWorld, Entity serverEntity, Entity clientEntity)
    {
        var serverEg = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEntity);
        var clientEg = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
        for (int i = 0; i < serverEg.Length; ++i)
        {
            VerifyComponent<EnableableComponent_0>(testWorld, serverEg[i].Value, clientEg[i].Value);
            VerifyComponent<EnableableComponent_1>(testWorld, serverEg[i].Value, clientEg[i].Value);
            VerifyComponent<EnableableComponent_2>(testWorld, serverEg[i].Value, clientEg[i].Value);
            VerifyComponent<EnableableComponent_3>(testWorld, serverEg[i].Value, clientEg[i].Value);
            VerifyBuffer<EnableableBuffer_0>(testWorld, serverEg[i].Value, clientEg[i].Value);
            VerifyBuffer<EnableableBuffer_1>(testWorld, serverEg[i].Value, clientEg[i].Value);
            VerifyBuffer<EnableableBuffer_2>(testWorld, serverEg[i].Value, clientEg[i].Value);
        }
    }

    [Test(Description = "Verify that the GhostSendSystemData.ForceSingleBaseline option works correctly.")]
    public void ForcingServerSingleBaselineWork()
    {
        using var testWorld = new NetCodeTestWorld();
        testWorld.Bootstrap(true, typeof(ChangeComponentValueSystem),
            typeof(SingleBaselineDefaultConfig));
        testWorld.CreateWorlds(true, 1);
        var config = new GhostPrefabCreation.Config
        {
            Name = "ComplexPrefab",
            Importance = 0,
            SupportedGhostModes = GhostModeMask.Predicted,
            OptimizationMode = GhostOptimizationMode.Dynamic,
            UsePreSerialization = false
        };
        var serverPrefab = CreatePrefab(testWorld.ServerWorld.EntityManager, config);
        CreatePrefab(testWorld.ClientWorlds[0].EntityManager, config);

        testWorld.Connect();
        testWorld.GoInGame();
        for (int i = 0; i < 64; ++i)
            testWorld.Tick();

        var systemDataEntity = testWorld.TryGetSingletonEntity<GhostSendSystemData>(testWorld.ServerWorld);
        var data = new GhostSendSystemData();
        //Use three baselines, all values should be normally replicated
        var serverEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefab);
        for (int i = 0; i < 4; ++i)
            testWorld.Tick();
        var clientEntity = testWorld.TryGetSingletonEntity<GhostInstance>(testWorld.ClientWorlds[0]);
        Assert.AreNotEqual(Entity.Null, clientEntity);
        for (int i = 0; i < 64; ++i)
        {
            VerifyEntities(testWorld, serverEntity, clientEntity);
            testWorld.Tick();
        }

        //force single baseline, verify that everything still get serialized as expected
        data.ForceSingleBaseline = true;
        testWorld.ServerWorld.EntityManager.SetComponentData(systemDataEntity, data);

        for (int i = 0; i < 64; ++i)
        {
            VerifyEntities(testWorld, serverEntity, clientEntity);
            testWorld.Tick();
        }
    }

    [Test]
    public void DifferenceInBandwidth()
    {
        var config = new GhostPrefabCreation.Config
        {
            Name = "ComplexPrefab",
            Importance = 0,
            SupportedGhostModes = GhostModeMask.Predicted,
            OptimizationMode = GhostOptimizationMode.Dynamic,
            UsePreSerialization = false,
            UseSingleBaseline = false
        };
        var threeBaseline = Scenario(config);
        config.UseSingleBaseline = true;
        var singleBaseline = Scenario(config);
        Debug.Log(singleBaseline.Diff(threeBaseline));
    }

    struct Bandwidth
    {
        public long avgSizePerPacket;
        public long avgSizePerEntity;
        public long totalGhosts;
        public long totalBits;

        public string Diff(Bandwidth other)
        {
            Bandwidth delta = new Bandwidth
            {
                avgSizePerPacket = avgSizePerPacket - other.avgSizePerPacket,
                avgSizePerEntity = avgSizePerEntity - other.avgSizePerEntity,
                totalGhosts = totalGhosts - other.totalGhosts,
                totalBits = totalBits - other.totalBits
            };
            //delta = A*c / 100;
            //100*delta / A = c
            return $"ditt: total: {delta.totalBits} bits [{(100*delta.totalBits)/totalBits}%] " +
                   $"entities:{delta.totalGhosts} [{(100*delta.totalGhosts)/totalGhosts}%] " +
                   $"avg packet size: {delta.avgSizePerPacket} bits [{(100*delta.avgSizePerPacket)/avgSizePerPacket}%] " +
                   $"avg entity size: {delta.avgSizePerEntity} bits [{(100*delta.avgSizePerEntity)/avgSizePerEntity}%]";
        }
        public override string ToString()
        {
            return $"total: {totalBits} entities:{totalGhosts} avg packet size (bits): {avgSizePerPacket} avg entity size: {avgSizePerEntity}";
        }
    }

    Bandwidth Scenario(GhostPrefabCreation.Config config)
    {
        using var testWorld = new NetCodeTestWorld();
        testWorld.Bootstrap(true, typeof(ChangeComponentValueSystem));
        testWorld.CreateWorlds(true, 1);
        testWorld.Connect();
        testWorld.GoInGame();
        for (int i = 0; i < 64; ++i)
            testWorld.Tick();

        var entities = new Entity[30];
        var serverPrefab = CreatePrefabForBandwidth(testWorld.ServerWorld.EntityManager, config);
        CreatePrefabForBandwidth(testWorld.ClientWorlds[0].EntityManager, config);

        for(int i=0;i<30;++i)
            entities[i] = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefab);

        Entity metrics = testWorld.TryCreateGhostMetricsSingleton(testWorld.ClientWorlds[0]);
        for (int i = 0; i < 4; ++i)
            testWorld.Tick();
        SnapshotMetrics firstSnapshot = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SnapshotMetrics>(metrics);
        SnapshotMetrics[] tickMetrics = new SnapshotMetrics[64];
        for (int i = 0; i < 64; ++i)
        {
            for(int k=0;k<entities.Length;++k)
            {
                //linear progression
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[k], new EnableableComponent_0 { value = 100+i });
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[k], new EnableableComponent_1 { value = 200+i });
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[k], new EnableableComponent_2 { value = 300+i });
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[k], new EnableableComponent_3 { value = 400+i });
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[k], new EnableableComponent_5 { value = 500+i });
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[k], new EnableableComponent_6 { value = 600+i });
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[k], new EnableableComponent_7 { value = 700+i });
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[k], new EnableableComponent_8 { value = 800+i });
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[k], new EnableableComponent_9 { value = 900+i });
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[k], new EnableableComponent_10 { value = 1000+i });
            }
            testWorld.Tick();
            tickMetrics[i] = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SnapshotMetrics>(metrics);
        }
        //do an average
        Bandwidth result = new Bandwidth();
        long avgBitSize = 0;
        long totalEntCount = 0;
        for (int i = 0; i < tickMetrics.Length; ++i)
        {
            avgBitSize += tickMetrics[i].TotalSizeInBits;
            totalEntCount += tickMetrics[i].TotalGhostCount;
        }
        result.avgSizePerEntity = avgBitSize / totalEntCount;
        result.avgSizePerPacket = avgBitSize/64;
        result.totalBits = avgBitSize;
        result.totalGhosts = totalEntCount;
        Debug.Log(result);
        return result;
    }
}

using NUnit.Framework;
using NUnit.Framework.Internal;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.NetCode.Tests.Editor
{
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSpawnClassificationSystem))]
    partial struct TestSpawnBufferClassifier : ISystem
    {
        private LowLevel.SnapshotDataLookupHelper lookupHelper;
        private BufferLookup<SnapshotDataBuffer> snapshotBufferLookup;
        public int ClassifiedPredictedSpawns { get; private set; }
        public void OnCreate(ref SystemState state)
        {
            lookupHelper = new LowLevel.SnapshotDataLookupHelper(ref state);
            snapshotBufferLookup = state.GetBufferLookup<SnapshotDataBuffer>(true);
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<GhostSpawnQueueComponent>();
            state.RequireForUpdate<PredictedGhostSpawn>();
            state.RequireForUpdate<NetworkIdComponent>();
        }

        public void OnDestroy(ref SystemState state)
        {
        }

        public void OnUpdate(ref SystemState state)
        {
            lookupHelper.Update(ref state);
            snapshotBufferLookup.Update(ref state);
            var ghostMap = SystemAPI.GetSingleton<SpawnedGhostEntityMap>().Value;
            var ghostCollection = SystemAPI.GetSingletonEntity<GhostCollection>();
            var snapshotLookup = lookupHelper.CreateSnapshotBufferLookup(ghostCollection, ghostMap);
            var predictedSpawnList = SystemAPI.GetSingletonBuffer<PredictedGhostSpawn>(true);

            foreach (var (spawnBuffer, spawnDataBuffer)
                     in SystemAPI.Query<DynamicBuffer<GhostSpawnBuffer>, DynamicBuffer<SnapshotDataBuffer>>()
                         .WithAll<GhostSpawnQueueComponent>())
            {
                for (int i = 0; i < spawnBuffer.Length; ++i)
                {
                    UnityEngine.Debug.LogWarning($"Checking ghost {i}");
                    var ghost = spawnBuffer[i];
                    Assert.IsTrue(snapshotLookup.HasGhostOwner(ghost));
#if !ENABLE_TRANSFORM_V1
                    Assert.IsTrue(snapshotLookup.HasComponent<LocalTransform>(ghost.GhostType));
#else
                    Assert.IsTrue(snapshotLookup.HasComponent<Translation>(ghost.GhostType));
#endif
                    Assert.IsTrue(snapshotLookup.HasComponent<SomeData>(ghost.GhostType));
                    Assert.IsTrue(snapshotLookup.HasBuffer<GhostGenTest_Buffer>(ghost.GhostType));
                    Assert.AreEqual(1, snapshotLookup.GetGhostOwner(ghost, spawnDataBuffer));
#if !ENABLE_TRANSFORM_V1
                    Assert.IsTrue(snapshotLookup.TryGetComponentDataFromSpawnBuffer(ghost, spawnDataBuffer, out LocalTransform transform));
                    Assert.IsTrue(math.distance(new float3(40f, 10f, 90f), transform.Position) < 1.0e-4f);
#else
                    Assert.IsTrue(snapshotLookup.TryGetComponentDataFromSpawnBuffer(ghost, spawnDataBuffer, out Translation translation));
                    Assert.IsTrue(math.distance(new float3(40f, 10f, 90f), translation.Value) < 1.0e-4f);
#endif
                    Assert.IsTrue(snapshotLookup.TryGetComponentDataFromSpawnBuffer(ghost, spawnDataBuffer, out SomeData someData));
                    Assert.AreEqual(10000, someData.Value);
                    Assert.IsTrue(snapshotLookup.TryGetComponentDataFromSpawnBuffer(ghost, spawnDataBuffer, out GhostOwnerComponent ownerComponent));
                    Assert.AreEqual(1, ownerComponent.NetworkId);

                    if (ghost.SpawnType != GhostSpawnBuffer.Type.Predicted || ghost.HasClassifiedPredictedSpawn || ghost.PredictedSpawnEntity != Entity.Null)
                        continue;
                    for(int j=0;j<predictedSpawnList.Length;++j)
                    {
                        if (predictedSpawnList[j].ghostType == spawnBuffer[i].GhostType)
                        {
                            Assert.IsTrue(snapshotBufferLookup.HasBuffer(predictedSpawnList[j].entity));
                            var historyBuffer = snapshotBufferLookup[predictedSpawnList[j].entity];
#if !ENABLE_TRANSFORM_V1
                            Assert.IsTrue(snapshotLookup.TryGetComponentDataFromSnapshotHistory(ghost.GhostType, historyBuffer, out LocalTransform predictedTx));
                            Assert.AreEqual(transform.Position, predictedTx.Position);
#else
                            Assert.IsTrue(snapshotLookup.TryGetComponentDataFromSnapshotHistory(ghost.GhostType, historyBuffer, out Translation predictedTx));
                            Assert.AreEqual(translation.Value, predictedTx.Value);
#endif
                            Assert.IsTrue(snapshotLookup.TryGetComponentDataFromSnapshotHistory(ghost.GhostType, historyBuffer, out SomeData predSomeData));
                            Assert.AreEqual(someData.Value, predSomeData.Value);
                            Assert.IsTrue(snapshotLookup.TryGetComponentDataFromSnapshotHistory(ghost.GhostType, historyBuffer, out GhostOwnerComponent predOwnerComp));
                            Assert.AreEqual(ownerComponent.NetworkId, predOwnerComp.NetworkId);
                            ++ClassifiedPredictedSpawns;
                        }
                    }
                }
            }
        }
    }

    public class SnapshotDataBufferLookupTests
    {
        [Test]
        public void ComponentCanBeInspected()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(TestSpawnBufferClassifier));
                testWorld.CreateGhostCollection();
                testWorld.CreateWorlds(true, 1);
                BuildPrefab(testWorld.ServerWorld.EntityManager, "TestPrefab");
                BuildPrefab(testWorld.ClientWorlds[0].EntityManager, "TestPrefab");
                testWorld.Connect(1f / 60f, 10);
                testWorld.GoInGame();
                for(var i=0;i<32;++i)
                    testWorld.Tick(1.0f/60f);
                Assert.AreEqual(testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld).Length,1);
                Assert.AreEqual(testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ClientWorlds[0]).Length,1);
                var serverGhost = testWorld.ServerWorld.EntityManager.Instantiate(testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld).ElementAt(0).GhostPrefab);
                SetComponentsData(testWorld.ServerWorld, serverGhost);
                for(var i=0;i<32;++i)
                    testWorld.Tick(1.0f/60f);
            }
        }

        [Test]
        public void ComponentCanBeExtractedFromPredictedSpawnBuffer()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(TestSpawnBufferClassifier));
                testWorld.CreateGhostCollection();
                testWorld.CreateWorlds(true, 1);
                BuildPrefab(testWorld.ServerWorld.EntityManager, "TestPrefab");
                var clientPrefab = BuildPrefab(testWorld.ClientWorlds[0].EntityManager, "TestPrefab");
                var predictedSpawnVariant = CreatePredictedSpawnVariant(testWorld.ClientWorlds[0].EntityManager, clientPrefab);
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequestComponent>(predictedSpawnVariant));
                testWorld.Connect(1f / 60f, 10);
                testWorld.GoInGame();
                for(var i=0;i<32;++i)
                    testWorld.Tick(1.0f/60f);
                Assert.AreEqual(testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld).Length,1);
                Assert.AreEqual(testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ClientWorlds[0]).Length,1);
                //Predict the spawning on the client. And match the one coming from server
                var clientGhost = testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedSpawnVariant);
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequestComponent>(clientGhost));
                SetComponentsData(testWorld.ClientWorlds[0], clientGhost);
                for(var i=0;i<2;++i)
                    testWorld.Tick(1.0f/60f);
                var serverGhost = testWorld.ServerWorld.EntityManager.Instantiate(testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld).ElementAt(0).GhostPrefab);
                SetComponentsData(testWorld.ServerWorld, serverGhost);
                for(var i=0;i<32;++i)
                    testWorld.Tick(1.0f/60f);
                var classifier = testWorld.ClientWorlds[0].GetExistingSystem<TestSpawnBufferClassifier>();
                var systemRef = testWorld.ClientWorlds[0].Unmanaged.GetUnsafeSystemRef<TestSpawnBufferClassifier>(classifier);
                Assert.AreEqual(1, systemRef.ClassifiedPredictedSpawns);
            }
        }

        [Test]
        public void ComponentCanBeExtractedForDifferentGhostTypes()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(TestSpawnBufferClassifier));
                testWorld.CreateGhostCollection();
                testWorld.CreateWorlds(true, 1);
                var ghostsPrefabs = new Entity[5];
                for (int i = 0; i < ghostsPrefabs.Length; ++i)
                {
                    BuildPrefab(testWorld.ServerWorld.EntityManager, $"TestPrefab_{i}");
                    var clientPrefab = BuildPrefab(testWorld.ClientWorlds[0].EntityManager, $"TestPrefab_{i}");
                    ghostsPrefabs[i] = CreatePredictedSpawnVariant(testWorld.ClientWorlds[0].EntityManager, clientPrefab);
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequestComponent>(ghostsPrefabs[i]));
                }


                testWorld.Connect(1f / 60f, 10);
                testWorld.GoInGame();
                for(var i=0;i<32;++i)
                    testWorld.Tick(1.0f/60f);
                Assert.AreEqual(ghostsPrefabs.Length, testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld).Length);
                Assert.AreEqual(ghostsPrefabs.Length, testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ClientWorlds[0]).Length);
                //Predict the spawning on the client. And match the one coming from server
                for (int i = 0; i < ghostsPrefabs.Length; ++i)
                {
                    var clientGhost = testWorld.ClientWorlds[0].EntityManager.Instantiate(ghostsPrefabs[i]);
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequestComponent>(clientGhost));
                    SetComponentsData(testWorld.ClientWorlds[0], clientGhost);
                }
                for(var i=0;i<2;++i)
                    testWorld.Tick(1.0f/60f);

                for (int i = 0; i < ghostsPrefabs.Length; ++i)
                {
                    var serverGhost = testWorld.ServerWorld.EntityManager.Instantiate(testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld).ElementAt(i).GhostPrefab);
                    SetComponentsData(testWorld.ServerWorld, serverGhost);
                }
                for(var i=0;i<32;++i)
                    testWorld.Tick(1.0f/60f);
                var classifier = testWorld.ClientWorlds[0].GetExistingSystem<TestSpawnBufferClassifier>();
                var systemRef = testWorld.ClientWorlds[0].Unmanaged.GetUnsafeSystemRef<TestSpawnBufferClassifier>(classifier);
                Assert.AreEqual(ghostsPrefabs.Length, systemRef.ClassifiedPredictedSpawns);
            }
        }

        private void SetComponentsData(World world, Entity entity)
        {
#if !ENABLE_TRANSFORM_V1
            world.EntityManager.SetComponentData(entity, LocalTransform.FromPosition(40f,10f, 90f));
#else
            world.EntityManager.SetComponentData(entity, new Translation{Value = new float3(40f,10f, 90f)});
#endif
            world.EntityManager.SetComponentData(entity, new GhostOwnerComponent { NetworkId = 1});
            world.EntityManager.SetComponentData(entity, new SomeData { Value = 10000 });
            world.EntityManager.GetBuffer<GhostGenTest_Buffer>(entity).Add(new GhostGenTest_Buffer{IntValue = 10});
        }

        private Entity BuildPrefab(EntityManager entityManager, string prefabName)
        {
            var archetype = entityManager.CreateArchetype(
#if !ENABLE_TRANSFORM_V1
                new ComponentType(typeof(Transforms.LocalTransform)),
#else
                new ComponentType(typeof(Transforms.Translation)),
#endif
                new ComponentType(typeof(GhostOwnerComponent)),
                new ComponentType(typeof(GhostGenTest_Buffer)),
                new ComponentType(typeof(SomeData)));
            var prefab = entityManager.CreateEntity(archetype);
            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = prefabName,
                Importance = 1000,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.OwnerPredicted,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });
            return prefab;
        }

        private Entity CreatePredictedSpawnVariant(EntityManager entityManager, Entity entity)
        {
            var predicted = entityManager.Instantiate(entity);
            entityManager.AddComponent<Prefab>(entity);
            if (entityManager.HasComponent<LinkedEntityGroup>(predicted))
            {
                var leg = entityManager.GetBuffer<LinkedEntityGroup>(predicted, true);
                foreach (var ent in leg)
                    entityManager.AddComponent<Prefab>(ent.Value);
            }
            entityManager.AddComponent<PredictedGhostSpawnRequestComponent>(predicted);
            return predicted;
        }
    }
}

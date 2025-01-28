using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Transforms;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    public struct Data : IComponentData
    {
        [GhostField]
        public int Value;
    }

    [GhostComponent(SendDataForChildEntity = true)]
    public struct ChildData : IComponentData
    {
        [GhostField]
        public int Value;
    }

    public class DataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<Data>(entity);
        }
    }

    public class PredictedGhostDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
            baker.AddComponent(entity, new Data());
            baker.AddComponent(entity, new EnableableComponent_0());
            baker.AddComponent(entity, new EnableableComponent_1());
            baker.AddComponent(entity, new EnableableComponent_2());
            baker.AddBuffer<EnableableBuffer_0>(entity);
            baker.AddBuffer<EnableableBuffer_1>(entity);
        }
    }

    public class ChildDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new ChildData());
            baker.AddComponent(entity, new ChildOnlyComponent_3());
            baker.AddBuffer<EnableableBuffer>(entity);
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class UpdateDataSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((ref Data data) =>
            {
                data.Value++;
            }).Run();
        }
    }

    [TestFixture]
    public partial class SpawnTests
    {
        [DisableAutoCreation]
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        [UpdateInGroup(typeof(GhostSpawnClassificationSystemGroup))]
        [UpdateAfter(typeof(GhostSpawnClassificationSystem))]
        public partial class TestSpawnClassificationSystem : SystemBase
        {
            // Track which entities have been handled by this classification system
            public NativeList<Entity> PredictedEntities;
            protected override void OnCreate()
            {
                RequireForUpdate<GhostSpawnQueue>();
                RequireForUpdate<PredictedGhostSpawnList>();
                PredictedEntities = new NativeList<Entity>(5,Allocator.Persistent);
            }

            protected override void OnDestroy()
            {
                PredictedEntities.Dispose();
            }

            protected override void OnUpdate()
            {
                var spawnListEntity = SystemAPI.GetSingletonEntity<PredictedGhostSpawnList>();
                var spawnListFromEntity = GetBufferLookup<PredictedGhostSpawn>();
                var predictedEntities = PredictedEntities;
                Entities
                    .WithAll<GhostSpawnQueue>()
                    .ForEach((DynamicBuffer<GhostSpawnBuffer> ghosts) =>
                    {
                        var spawnList = spawnListFromEntity[spawnListEntity];
                        for (int i = 0; i < ghosts.Length; ++i)
                        {
                            var ghost = ghosts[i];
                            if (ghost.SpawnType != GhostSpawnBuffer.Type.Predicted || ghost.HasClassifiedPredictedSpawn || ghost.PredictedSpawnEntity != Entity.Null)
                                continue;

                            // Only classify the first item in the list (default system will then catch the rest) and
                            // handle it no matter what (no spawn tick checks etc)
                            if (spawnList.Length > 1)
                            {
                                if (ghost.GhostType == spawnList[0].ghostType)
                                {
                                    ghost.PredictedSpawnEntity = spawnList[0].entity;
                                    ghost.HasClassifiedPredictedSpawn = true;
                                    spawnList.RemoveAtSwapBack(0);
                                    predictedEntities.Add(ghost.PredictedSpawnEntity);
                                    ghosts[i] = ghost;
                                    break;
                                }
                            }
                        }
                    }).Run();
            }
        }

        /* Set up 2 prefabs with a predicted ghost and interpolated ghost
         *  - Verify spawning the predicted one on the client works as expected
         *  - Verify server spawning interpolated ghosts works as well
         *  - Verify the prefabs on the clients have the right components set up
         *  - Verify a locally spawned predicted ghost is properly synchronized to other clients.
         *  - Uses default spawn classification system
         */
        [Test]
        public void PredictSpawnGhost()
        {
            const int PREDICTED = 0;
            const int INTERPOLATED = 1;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(UpdateDataSystem));

                // Predicted ghost
                var predictedGhostGO = new GameObject("PredictedGO");
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new DataConverter();
                var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                ghostConfig.HasOwner = true;

                // One child nested on predicted ghost
                var predictedGhostGOChild = new GameObject("PredictedGO-Child");
                predictedGhostGOChild.AddComponent<TestNetCodeAuthoring>().Converter = new ChildDataConverter();
                predictedGhostGOChild.transform.parent = predictedGhostGO.transform;

                // Interpolated ghost
                var interpolatedGhostGO = new GameObject("InterpolatedGO");
                interpolatedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new DataConverter();
                ghostConfig = interpolatedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
                ghostConfig.SupportedGhostModes = GhostModeMask.Interpolated;

                Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO, interpolatedGhostGO));

                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // Predictively spawn ghost on client
                var prefabsListQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                var prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                var prefabs = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
                var predictedPrefab = prefabs[PREDICTED].Value;
                var clientEntity = testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedPrefab);

                // Verify you've instantiated the predict spawn version of the prefab
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));

                // Verify the predicted ghost has a linked entity (the child on the GO)
                var linkedEntities = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                Assert.AreEqual(2, linkedEntities.Length);

                // server spawns normal ghost for the client spawned one
                prefabsListQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                prefabs = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
                Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<PredictedGhostSpawnRequest>(prefabs[PREDICTED].Value));
                testWorld.ServerWorld.EntityManager.Instantiate(prefabs[PREDICTED].Value);


                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();

                //The request has been consumed.
                Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));

                // Verify ghost field data has been updated on the clients instance, and we only have one entity spawned
                var compQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Data>());
                var clientData = compQuery.ToComponentDataArray<Data>(Allocator.Temp);
                Assert.AreEqual(1, clientData.Length);
                Assert.IsTrue(clientData[0].Value > 1);

                // server spawns normal interpolated ghost
                prefabsListQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                prefabs = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
                testWorld.ServerWorld.EntityManager.Instantiate(prefabs[INTERPOLATED].Value);
                Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<PredictedGhostSpawnRequest>(prefabs[INTERPOLATED].Value));

                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();

                // Verify ghost field data has been updated on the clients instance for the predicted entity we spawned
                compQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new ComponentType[] { typeof(Data), typeof(PredictedGhost) },
                });
                compQuery.ToComponentDataArray<Data>(Allocator.Temp);
                Assert.AreEqual(1, clientData.Length);
                Assert.IsTrue(clientData[0].Value > 1);

                // Verify the interpolated ghost has also propagated to the client and updated
                compQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new ComponentType[] { typeof(Data) },
                    None = new ComponentType[] { typeof(PredictedGhost) }
                });
                compQuery.ToComponentDataArray<Data>(Allocator.Temp);
                Assert.AreEqual(1, clientData.Length);
                Assert.IsTrue(clientData[0].Value > 1);

                // On client there are two predicted prefabs, one for predicted spawning and one normal server spawn
                var queryDesc = new EntityQueryDesc
                {
                    All = new ComponentType[]
                    {
                        typeof(Data),
                        typeof(Prefab),
                        typeof(PredictedGhost)
                    },
                    Options = EntityQueryOptions.IncludePrefab
                };
                compQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(queryDesc);
                Assert.AreEqual(1, compQuery.CalculateEntityCount());

                // Verify children are correctly replicated in the prefab copy.
                // Iterate though the LinkedEntityGroup of each predicted prefab
                // check the child entity listed there and verify it's linking back to the parent
                var entityPrefabs = compQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entityPrefabs.Length; ++i)
                {
                    var parentEntity = entityPrefabs[i];
                    var links = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(parentEntity);
                    Assert.AreEqual(2, links.Length);
                    var child = links[1].Value;
                    var parentLink = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Parent>(child).Value;
                    Assert.AreEqual(parentEntity, parentLink);
                }

                // Server will have 2 prefabs (interpolated, predicted)
                compQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(queryDesc);
                Assert.AreEqual(2, compQuery.CalculateEntityCount());
            }
        }

        [Test]
        public void CustomSpawnClassificationSystem()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(TestSpawnClassificationSystem));

                // Predicted ghost
                var predictedGhostGO = new GameObject("PredictedGO");
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new DataConverter();
                var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                ghostConfig.HasOwner = true;

                Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO));

                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // Predictively spawn ghost on client
                var prefabsListQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                var prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                var prefabs = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
                var predictedPrefab = prefabs[0].Value;

                // Instantiate two ghosts on the same frame
                testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedPrefab);
                testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedPrefab);

                // Server spawns normal ghost for the client spawned one
                prefabsListQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                prefabs = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);

                // Server also instantiates twice
                testWorld.ServerWorld.EntityManager.Instantiate(prefabs[0].Value);
                testWorld.ServerWorld.EntityManager.Instantiate(prefabs[0].Value);

                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();

                // Verify the custom spawn classification system ran instead of the default only for the first spawn
                var classifiedGhosts = testWorld.ClientWorlds[0].GetExistingSystemManaged<TestSpawnClassificationSystem>();
                Assert.AreEqual(1, classifiedGhosts.PredictedEntities.Length);

                // Verify we have the right amount of total ghosts spawned
                var compQuery = testWorld.ClientWorlds[0].EntityManager
                    .CreateEntityQuery(typeof(Data));
                Assert.AreEqual(2, compQuery.CalculateEntityCount());
            }
        }

        [DisableAutoCreation]
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
        [UpdateBefore(typeof(GhostReceiveSystem))]
        unsafe partial struct VerifyInitialization : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.Enabled = false;
            }
            public void OnUpdate(ref SystemState state)
            {
                //We need to decode the snapshot data here and check it is correct.
                var clientEntity = SystemAPI.GetSingletonEntity<GhostInstance>();
                var prefabType = SystemAPI.GetSingletonBuffer<GhostCollectionPrefab>()[0];
                var clientEntity2 = state.EntityManager.Instantiate(prefabType.GhostPrefab);
                var deserializeHelper = new GhostDeserializeHelper(ref state,
                    SystemAPI.GetSingletonEntity<GhostCollection>(), clientEntity, 0);
                var ghostComponentCollection = SystemAPI.GetSingletonBuffer<GhostCollectionComponentType>();
                DynamicTypeList.PopulateList(ref state, ghostComponentCollection, false, ref deserializeHelper.ghostChunkComponentTypes);

                var info = state.EntityManager.GetStorageInfo(clientEntity2);
                deserializeHelper.CopySnapshotToEntity(info);
                var linkedEntities = state.EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                var linkedEntities2 = state.EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity2);
                Assert.AreEqual(
                    state.EntityManager.GetComponentData<GhostOwner>(clientEntity).NetworkId,
                    state.EntityManager.GetComponentData<GhostOwner>(clientEntity2).NetworkId);
                Assert.AreEqual(
                    state.EntityManager.GetComponentData<Data>(clientEntity).Value,
                    state.EntityManager.GetComponentData<Data>(clientEntity2).Value);
                Assert.AreEqual(
                    state.EntityManager.GetComponentData<EnableableComponent_0>(clientEntity).value,
                    state.EntityManager.GetComponentData<EnableableComponent_0>(clientEntity2).value);
                Assert.AreEqual(
                    state.EntityManager.GetComponentData<EnableableComponent_1>(clientEntity).value,
                    state.EntityManager.GetComponentData<EnableableComponent_1>(clientEntity2).value);
                Assert.AreEqual(
                    state.EntityManager.GetComponentData<EnableableComponent_2>(clientEntity).value,
                    state.EntityManager.GetComponentData<EnableableComponent_2>(clientEntity2).value);
                {
                    var b1 = state.EntityManager.GetBuffer<EnableableBuffer_0>(clientEntity);
                    var b2 = state.EntityManager.GetBuffer<EnableableBuffer_0>(clientEntity2);
                    Assert.AreEqual(b1.Length, b2.Length);
                    for (int b = 0; b < b1.Length; b++)
                    {
                        Assert.AreEqual(b1[b].value, b2[b].value);
                    }
                }
                {
                    var b1 = state.EntityManager.GetBuffer<EnableableBuffer_1>(clientEntity);
                    var b2 = state.EntityManager.GetBuffer<EnableableBuffer_1>(clientEntity2);
                    Assert.AreEqual(b1.Length, b2.Length);
                    for (int b = 0; b < b1.Length; b++)
                    {
                        Assert.AreEqual(b1[b].value, b2[b].value);
                    }
                }
                for (int i = 1; i < linkedEntities.Length; ++i)
                {
                    Assert.AreEqual(
                        state.EntityManager.GetComponentData<ChildData>(linkedEntities[i].Value).Value,
                        state.EntityManager.GetComponentData<ChildData>(linkedEntities2[i].Value).Value);
                    Assert.AreEqual(
                        state.EntityManager.GetComponentData<ChildOnlyComponent_3>(linkedEntities[i].Value).value,
                        state.EntityManager.GetComponentData<ChildOnlyComponent_3>(linkedEntities2[i].Value).value);
                    var b1 = state.EntityManager.GetBuffer<EnableableBuffer>(linkedEntities[i].Value);
                    var b2 = state.EntityManager.GetBuffer<EnableableBuffer>(linkedEntities2[i].Value);
                    Assert.AreEqual(b1.Length, b2.Length);
                    for (int b = 0; b < b1.Length; b++)
                    {
                        Assert.AreEqual(b1[b].value, b2[b].value);
                    }
                }
                state.EntityManager.DestroyEntity(clientEntity2);
                state.Enabled = false;
            }
        }

        public enum PredictedSpawnDespawnDelay
        {
            DespawnAfterInterpolationTick,
            Despawn15AdditionalTicksLater,
        }
        [Test]
        public void IncorrectlyPredictedSpawnGhostsAreDestroyedCorrectly([Values]PredictedSpawnDespawnDelay predictedSpawnDespawnDelay)
        {
            var additionalDespawnDelayTicks = predictedSpawnDespawnDelay switch
            {
                PredictedSpawnDespawnDelay.DespawnAfterInterpolationTick => 0u,
                PredictedSpawnDespawnDelay.Despawn15AdditionalTicksLater => 15u,
                _ => throw new System.ArgumentOutOfRangeException(nameof(predictedSpawnDespawnDelay), predictedSpawnDespawnDelay, nameof(IncorrectlyPredictedSpawnGhostsAreDestroyedCorrectly)),
            };
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(VerifyInitialization));

            // Predicted ghost:
            var predictedGhostGO = new GameObject("BadPredictedGO");
            predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
            var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;
            ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
            Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO));

            // Begin:
            testWorld.CreateWorlds(true, 1);
            var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            clientTickRate.NumAdditionalClientPredictedGhostLifetimeTicks = (ushort) additionalDespawnDelayTicks;
            var clientServerTickRate = new ClientServerTickRate();
            clientServerTickRate.ResolveDefaults();
            var interpolationBufferTimeInTicks = clientTickRate.CalculateInterpolationBufferTimeInTicks(in clientServerTickRate);
            testWorld.ClientWorlds[0].EntityManager.CreateSingleton(clientTickRate);
            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            // Predictively spawn ghost on client:
            var expectedDespawnTick = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]).ServerTick;
            expectedDespawnTick.Add(additionalDespawnDelayTicks);
            var prefabsListQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
            var prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
            var prefabs = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
            var predictedPrefab = prefabs[0].Value;
            var clientEntity = testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedPrefab);


            // Wait for the interpolation tick to catch up to the spawn tick.
            var existedForTicks = 0;
            var entityExists = false;
            var previouslyExisted = true;
            NetworkTick currentInterpolationTick = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]).InterpolationTick;
            int numTicksToWait = expectedDespawnTick.TicksSince(currentInterpolationTick) + 6; // Margin of error.
            for (int i = 0; i < numTicksToWait; i++)
            {
                // Verify we have the predicted spawn version of the prefab:
                entityExists = testWorld.ClientWorlds[0].EntityManager.Exists(clientEntity);
                if(i == 0) Assert.IsTrue(entityExists, $"Sanity: Client predicted spawn should be created from the outset!");
                if (entityExists) existedForTicks++;
                Assert.IsFalse(!previouslyExisted && entityExists, $"Client predicted spawn should be created from the outset, then destroyed, then NEVER created again!? entityExists:{entityExists}, previouslyExisted:{previouslyExisted} ");
                previouslyExisted = entityExists;
                testWorld.Tick();
            }

            // Verify the despawn and alive duration:
            Assert.IsFalse(entityExists, $"After {numTicksToWait} ticks, the client predicted spawn should have despawned, as despawn tick (of {expectedDespawnTick.ToFixedString()}) is != currentInterpolationTick:{currentInterpolationTick.ToFixedString()})!");
            Assert.IsTrue(existedForTicks >= interpolationBufferTimeInTicks + additionalDespawnDelayTicks, $"The client predicted spawn should have existed for at least interpolationBufferTimeInTicks:{interpolationBufferTimeInTicks} + NumAdditionalClientPredictedGhostLifetimeTicks:{additionalDespawnDelayTicks} ticks, but it only existed for {existedForTicks} ticks!");
        }

        [Test]
        public void PredictedSpawnGhostAreInitializedCorrectly([Values]bool enableComponents)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyInitialization));

                // Predicted ghost
                var predictedGhostGO = new GameObject("PredictedGO");
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
                var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;

                // One child nested on predicted ghost
                var predictedGhostGOChild = new GameObject("PredictedGO-Child");
                predictedGhostGOChild.AddComponent<TestNetCodeAuthoring>().Converter = new ChildDataConverter();
                predictedGhostGOChild.transform.parent = predictedGhostGO.transform;

                Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO));

                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // Predictively spawn ghost on client
                var prefabsListQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                var prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                var prefabs = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
                var predictedPrefab = prefabs[0].Value;
                var clientEntity = testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedPrefab);

                InitializePredictedEntity(clientEntity, testWorld.ClientWorlds[0].EntityManager, enableComponents);

                // Verify you've instantiated the predict spawn version of the prefab
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));

                // Verify the predicted ghost has a linked entity (the child on the GO)
                var linkedEntities = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                Assert.AreEqual(2, linkedEntities.Length);

                //Do 1 tick: verify the spawned ghost is now been initialized on the client. Do a partial tick to also make a rollback once
                testWorld.Tick(1f/180f);
                {
                    //still true because the remove is in the command buffer.
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_0>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_1>(clientEntity), true);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_2>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer_0>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer_1>(clientEntity), true);
                    linkedEntities = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer>(linkedEntities[1].Value),enableComponents);
                }
                ref var systemState = ref testWorld.ClientWorlds[0].Unmanaged.GetExistingSystemState<VerifyInitialization>();
                systemState.Enabled = true;
                testWorld.Tick(1f/180f);
                {
                    Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_0>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_1>(clientEntity), true);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_2>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer_0>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer_1>(clientEntity), true);
                    linkedEntities = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer>(linkedEntities[1].Value),enableComponents);
                }
            }
        }

        private void InitializePredictedEntity(Entity clientEntity, EntityManager entityManager, bool enableComponents)
        {
            entityManager.SetComponentData(clientEntity, new GhostOwner{NetworkId = 1});
            entityManager.SetComponentData(clientEntity, new Data{Value = 10});
            entityManager.SetComponentData(clientEntity, new EnableableComponent_0(){ value= 100});
            entityManager.SetComponentData(clientEntity, new EnableableComponent_1(){ value= 200});
            entityManager.SetComponentData(clientEntity, new EnableableComponent_2(){ value= 300});
            var buffer = entityManager.GetBuffer<EnableableBuffer_0>(clientEntity);
            buffer.Add(new EnableableBuffer_0{value = 10});
            buffer.Add(new EnableableBuffer_0{value = 20});
            buffer.Add(new EnableableBuffer_0{value = 30});
            var buffer1 = entityManager.GetBuffer<EnableableBuffer_1>(clientEntity);
            buffer1.Add(new EnableableBuffer_1{value = 40});
            buffer1.Add(new EnableableBuffer_1{value = 50});
            buffer1.Add(new EnableableBuffer_1{value = 60});

            var childEntity = entityManager.GetBuffer<LinkedEntityGroup>(clientEntity)[1].Value;
            entityManager.SetComponentData(childEntity, new ChildData{Value = 10});
            entityManager.SetComponentData(childEntity, new ChildOnlyComponent_3{value = 20});
            var childBuffer = entityManager.GetBuffer<EnableableBuffer>(childEntity);
            childBuffer.Add(new EnableableBuffer{value = 10});
            childBuffer.Add(new EnableableBuffer{value = 20});
            childBuffer.Add(new EnableableBuffer{value = 30});

            //Some of these components setters are commented to make the test using a mix of enable/disabled
            //components
            entityManager.SetComponentEnabled<EnableableComponent_0>(clientEntity, enableComponents);
            //entityManager.SetComponentEnabled<EnableableComponent_1>(clientEntity, enableComponents);
            entityManager.SetComponentEnabled<EnableableComponent_2>(clientEntity, enableComponents);
            entityManager.SetComponentEnabled<EnableableBuffer_0>(clientEntity, enableComponents);
            //entityManager.SetComponentEnabled<EnableableBuffer_1>(clientEntity, enableComponents);
            entityManager.SetComponentEnabled<EnableableBuffer>(childEntity, enableComponents);
        }
    }
}

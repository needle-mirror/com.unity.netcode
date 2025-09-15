#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Transforms;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal struct Data : IComponentData
    {
        [GhostField]
        public int Value;
    }

    [GhostComponent(SendDataForChildEntity = true)]
    internal struct ChildData : IComponentData
    {
        [GhostField]
        public int Value;
    }

    internal class DataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<Data>(entity);
        }
    }

    internal class PredictedGhostDataConverter : TestNetCodeAuthoring.IConverter
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

    internal class ChildDataConverter : TestNetCodeAuthoring.IConverter
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
    internal partial class UpdateDataSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((ref Data data) =>
            {
                data.Value++;
            }).Run();
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSpawnClassificationSystemGroup))]
    [UpdateAfter(typeof(GhostSpawnClassificationSystem))]
    internal partial class TestSpawnClassificationSystem : SystemBase
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

    struct GhostSpawner : IComponentData
    {
        public Entity ghost;
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ServerSimulation)]
    partial class PredictSpawnGhost : SystemBase
    {
        public NetworkTick spawnTick;
        public PredictedGhostSpawnTests.PredictedGhostSpawnType spawnFromCommandBuffer;
        protected override void OnCreate()
        {
            RequireForUpdate<GhostSpawner>();
        }

        protected override void OnUpdate()
        {
            if(!spawnTick.IsValid)
                return;

            var spawner = SystemAPI.GetSingleton<GhostSpawner>();
            var serverTick = SystemAPI.GetSingleton<NetworkTime>();
            if (serverTick.IsFirstTimeFullyPredictingTick && !spawnTick.IsNewerThan(serverTick.ServerTick))
            {
                if (spawnFromCommandBuffer == PredictedGhostSpawnTests.PredictedGhostSpawnType.FromBeginFrame)
                {
                    var commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(World.Unmanaged);
                    var predictedEntity = commandBuffer.Instantiate(spawner.ghost);
                    commandBuffer.SetComponent(predictedEntity, new Data{Value = 100});
                }
                else if (spawnFromCommandBuffer == PredictedGhostSpawnTests.PredictedGhostSpawnType.FromEndPrediction)
                {
                    var commandBuffer = SystemAPI.GetSingleton<EndPredictedSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(World.Unmanaged);
                    var predictedEntity = commandBuffer.Instantiate(spawner.ghost);
                    commandBuffer.SetComponent(predictedEntity, new Data{Value = 100});
                }
                else
                {
                    var predictedEntity = EntityManager.Instantiate(spawner.ghost);
                    EntityManager.SetComponentData(predictedEntity, new Data{Value = 100});
                }
                spawnTick = default;
            }
        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(PredictSpawnGhost))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class PredictSpawnGhostUpdate : SystemBase
    {
        protected override void OnUpdate()
        {
            foreach(var data in SystemAPI.Query<RefRW<Data>>().WithAll<Simulate>())
            {
                ++data.ValueRW.Value;
            }
        }
    }

    class PredictedGhostSpawnTests
    {
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

        internal enum PredictedSpawnDespawnDelay
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

        internal enum PredictedGhostSpawnType
        {
            FromBeginFrame,
            FromEndPrediction,
            InsidePredictionLoop
        }

        [Test(Description = "This test verify predicted spawning initialize the entity data correctly in the snapshot buffer.")]
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
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));

                // Verify the predicted ghost has a linked entity (the child on the GO)
                var linkedEntities = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                Assert.AreEqual(2, linkedEntities.Length);

                //Do 1 tick: verify the spawned ghost is now been initialized on the client. Do a partial tick to also make a rollback once
                testWorld.Tick(1f/180f);
                {
                    //the PredictedGhostSpawnRequest is still present (will be destroyed the next tick)
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<PredictedGhostSpawnRequest>(clientEntity));
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

        internal enum PredictedSpawnRollbackOptions
        {
            RollbackToSpawnTick,
            DontRollbackToSpawnTick
        }
        internal enum KeepHistoryBufferOptions
        {
            UseHistoryBufferOnStructuralChanges,
            RollbackOnStructuralChanges
        }

        static void SetupSpawner(NetCodeTestWorld testWorld, World world, int prefabIndex)
        {
            var spawner = world.EntityManager.CreateEntity(typeof(GhostSpawner));
            world.EntityManager.SetComponentData(spawner, new GhostSpawner
            {
                ghost = testWorld.GetSingletonBuffer<NetCodeTestPrefab>(world)[prefabIndex].Value
            });
        }

        [Test(Description = "Test a current (little weird) condition that when spawning an entity from the command buffer, the spawn tick" + "is different for the client and server.")]
        public void PredictSpawnGhost_SpawnTick_DifferentForClientAndServer([Values]PredictedGhostSpawnType predictedGhostSpawnType)
        {
            var predictedGhostGO = new GameObject($"PredictedGO");
            predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new DataConverter();
            var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;
            ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;

            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(CountNumberOfRollbacksSystem),
                typeof(PredictSpawnGhost));

            Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 32; ++i)
                testWorld.Tick();

            //server spawn a predicted ghost such that every update predicted spawned ghost should rollback
            testWorld.SpawnOnServer(0);

            SetupSpawner(testWorld, testWorld.ServerWorld, 0);
            SetupSpawner(testWorld, testWorld.ClientWorlds[0], 0);

            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            var predictedGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
            Assert.AreEqual(1, predictedGhosts.CalculateEntityCount());

            // Ensure client actually ticks fully in the next ticks so the prediction loop will run multiple times
            var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            var spawnTick = clientTime.ServerTick;
            if(!clientTime.IsPartialTick)
                spawnTick.Add(1);

            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnFromCommandBuffer = predictedGhostSpawnType;
            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;
            testWorld.ServerWorld.GetExistingSystemManaged<PredictSpawnGhost>().spawnFromCommandBuffer = predictedGhostSpawnType;
            testWorld.ServerWorld.GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;

            //Spawn the ghost on the client. If from command buffer we need another tick to have it present
            testWorld.Tick();
            var predictedSpawnRequests = new EntityQueryBuilder(Allocator.Temp).WithPresent<PredictedGhostSpawnRequest>().Build(testWorld.ClientWorlds[0].EntityManager);
            if(predictedGhostSpawnType == PredictedGhostSpawnType.FromBeginFrame)
                testWorld.Tick();
            Assert.AreEqual(1, predictedSpawnRequests.CalculateEntityCount());
            var spawnedGhost = predictedSpawnRequests.GetSingletonEntity();
            Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<PredictedGhostSpawnRequest>(spawnedGhost));
            Assert.AreEqual(spawnTick, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(spawnedGhost).spawnTick);
            //Let's tick another bit and wait for the server spawning the same.
            //if there is mispredicted spawn, the entity
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            // Ensure that classification failed and the entity has been deleted.
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(spawnedGhost));
            Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.Exists(spawnedGhost));
            var expectServerTick = spawnTick;
            if(predictedGhostSpawnType == PredictedGhostSpawnType.FromBeginFrame)
                expectServerTick.Increment();
            Assert.AreEqual(expectServerTick, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(spawnedGhost).spawnTick);
        }

        [Test(Description = "The test verify that predicted spawned ghost instantiated inside or outside the prediction loop" +
                            "correctly initialize their state and tick and respect both history and rollback settings")]
        public void PredictSpawnGhost_RollbackAndHistoryBackup(
            [Values]PredictedGhostSpawnType predictedGhostSpawnType,
            [Values]PredictedSpawnRollbackOptions rollback,
            [Values]KeepHistoryBufferOptions keepHistoryOnStructuralChanges)
        {
            var gameObjects = SetupGhosts(rollback, keepHistoryOnStructuralChanges);

            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(CountNumberOfRollbacksSystem),
                typeof(PredictSpawnGhost));

            Assert.IsTrue(testWorld.CreateGhostCollection(gameObjects));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 32; ++i)
                testWorld.Tick();

            //server spawn a predicted ghost such that every update predicted spawned ghost should rollback
            testWorld.SpawnOnServer(0);

            SetupSpawner(testWorld, testWorld.ServerWorld, 0);
            SetupSpawner(testWorld, testWorld.ClientWorlds[0], 0);

            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            var predictedGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
            Assert.AreEqual(1, predictedGhosts.CalculateEntityCount());

            // Ensure client actually ticks fully in the next ticks so the prediction loop will run multiple times
            var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            testWorld.TickClientWorld((1 - clientTime.ServerTickFraction)/60f);
            clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            Assert.IsFalse(clientTime.IsPartialTick);
            var spawnTick = clientTime.ServerTick;
            spawnTick.Add(1);

            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnFromCommandBuffer = predictedGhostSpawnType;
            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;
            testWorld.ServerWorld.GetExistingSystemManaged<PredictSpawnGhost>().spawnFromCommandBuffer = predictedGhostSpawnType;
            testWorld.ServerWorld.GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;

            //Spawn the ghost on the client. If from command buffer we need another tick to have it present
            testWorld.Tick();
            var predictedSpawnRequests = new EntityQueryBuilder(Allocator.Temp).WithPresent<PredictedGhostSpawnRequest>().Build(testWorld.ClientWorlds[0].EntityManager);
            if(predictedGhostSpawnType == PredictedGhostSpawnType.FromBeginFrame)
            {
                testWorld.Tick();
                Assert.AreEqual(1, predictedSpawnRequests.CalculateEntityCount());
            }
            var ghostWithRollback = predictedSpawnRequests.GetSingletonEntity();
            testWorld.ClientWorlds[0].EntityManager.SetName(ghostWithRollback, "PredictedSpawnedGhost");
            Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<PredictedGhostSpawnRequest>(ghostWithRollback));
            Assert.AreEqual(spawnTick, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(ghostWithRollback).spawnTick);
            // we run a partial to ensure that we are not tight to full ticks. Client should not do an extra tick so we ensure the
            // portion of tick is small enough
            var partialTickFrac = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTickFraction;
            partialTickFrac /= 3f;
            testWorld.Tick((1f + partialTickFrac)/60f);
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(ghostWithRollback));
            var fromSpawnTickCount = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountSimulationFromSpawnTick>(ghostWithRollback);
            //we always start from the spawn tick, therefore should increase by 1
            Assert.AreEqual(1, fromSpawnTickCount.Value);
            //do a structural change here. We will now have another rollback to the spawn tick if we can't keep the history
            testWorld.ClientWorlds[0].EntityManager.RemoveComponent<EnableableComponent_0>(ghostWithRollback);
            testWorld.Tick(partialTickFrac/60f);
            fromSpawnTickCount = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountSimulationFromSpawnTick>(ghostWithRollback);
            //in both cases we have to forcibly restart from the spawn tick (in one case we have the backup for that tick, in the other the state)
            //Therefore the count increase to 1
            Assert.AreEqual(2, fromSpawnTickCount.Value);

            //Let's tick another bit and wait for the server spawning the same.
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            // Ensure that classification is correct. Also, based on spawning the spawn tick may have been changed!
            Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.Exists(ghostWithRollback));
            //check we re-predict the number of times we expect
            var expectedFromSpawnTickCount = fromSpawnTickCount.Value;
            var expectServerTick = spawnTick;
            if(predictedGhostSpawnType == PredictedGhostSpawnType.FromBeginFrame)
                expectServerTick.Increment();
            Assert.AreEqual(expectServerTick, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(ghostWithRollback).spawnTick);
            fromSpawnTickCount = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountSimulationFromSpawnTick>(ghostWithRollback);
            if (rollback == PredictedSpawnRollbackOptions.RollbackToSpawnTick)
                Assert.AreEqual(expectedFromSpawnTickCount, fromSpawnTickCount.Value);
            else
                Assert.AreEqual(expectedFromSpawnTickCount, fromSpawnTickCount.Value);
        }

        private static GameObject[] SetupGhosts(PredictedSpawnRollbackOptions rollback,
            KeepHistoryBufferOptions rollbackOnStructuralChanges)
        {
            var gameObjects = new GameObject[2];
            for (int i = 0; i < 2; ++i)
            {
                // Predicted ghost. We create two types of the same
                var predictedGhostGO = new GameObject($"PredictedGO-{i}");
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new GhostWithRollbackConverter();
                var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                if (i == 1)
                {
                    ghostConfig.RollbackPredictedSpawnedGhostState = rollback == PredictedSpawnRollbackOptions.RollbackToSpawnTick;
                    ghostConfig.RollbackPredictionOnStructuralChanges = rollbackOnStructuralChanges == KeepHistoryBufferOptions.RollbackOnStructuralChanges;
                }
                // One child nested on predicted ghost
                var predictedGhostGOChild = new GameObject("PredictedGO-Child");
                predictedGhostGOChild.AddComponent<TestNetCodeAuthoring>().Converter = new ChildDataConverter();
                predictedGhostGOChild.transform.parent = predictedGhostGO.transform;
                gameObjects[i] = predictedGhostGO;
            }

            return gameObjects;
        }

        [Test(Description = "The test verify that predicted spawned ghost instantiated inside in the prediction loop" +
                            "don't mispredict and rewind correctly")]
        public void PredictSpawnGhost_InsidePrediction_AlwaysRollbackCorrectly([Values]PredictedSpawnRollbackOptions rollback,
            [Values]KeepHistoryBufferOptions keepHistoryBufferOnStructuralChanges)
        {
            var gameObjects = SetupGhosts(rollback, keepHistoryBufferOnStructuralChanges);
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(PredictSpawnGhost),
                typeof(PredictSpawnGhostUpdate), typeof(CountNumberOfRollbacksSystem));
            Assert.IsTrue(testWorld.CreateGhostCollection(gameObjects));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            //We need a predicted ghost in order to even spawn one from the prediction loop.
            testWorld.SpawnOnServer(0);

            SetupSpawner(testWorld, testWorld.ServerWorld, 1);
            SetupSpawner(testWorld, testWorld.ClientWorlds[0], 1);

            for (int i = 0; i < 32; ++i)
                testWorld.Tick();

            var predictedGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
            Assert.AreEqual(1, predictedGhosts.CalculateEntityCount());

            // Ensure we're in a known state on the client
            var time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            testWorld.TickClientWorld((1 - time.ServerTickFraction)/60f);
            time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            Assert.IsFalse(time.IsPartialTick);
            var spawnTick = time.ServerTick;
            spawnTick.Add(1);

            //spawn the ghost only on the client and verify that the ghost is
            //- rewind correctly for partial ticks
            //- the state of the Data component is the one of the last full tick
            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;
            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnFromCommandBuffer = PredictedGhostSpawnType.InsidePredictionLoop;

            var predictedSpawnRequests = new EntityQueryBuilder(Allocator.Temp)
                .WithPresent<PredictedGhostSpawnRequest>().Build(testWorld.ClientWorlds[0].EntityManager);
            //Client will spawn the entity now. We will do a full tick + 1 partial tick. There will be a new backup
            //for the spawnTick, that is when the entity is spawned. The predicted spawned ghost is not initialized yet.
            testWorld.Tick();
            var predictedSpawnEntity = predictedSpawnRequests.GetSingletonEntity();
            testWorld.TickClientWorld(.5f/60f);
            //The intial value for the Data is 100. The data is always incremented every prediction update (partial or not).
            //Request consumed. The predicted spawn entity should have 102 here.
            //But in the snapshot buffer we should have 101. We verify this indirectly
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(predictedSpawnEntity));
            Assert.AreEqual(102, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
            var lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
            Assert.AreEqual(spawnTick.TickIndexForValidTick, lastBackupTick.TickIndexForValidTick);
            //In the next tick we are receiving new state from the server for the other ghost.
            //We have both a backup and the first state to go to so we always calculate 103 no matter what
            testWorld.Tick();
            lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(predictedSpawnEntity));
            Assert.AreEqual(spawnTick.TickIndexForValidTick + 1, lastBackupTick.TickIndexForValidTick);
            Assert.AreEqual(103, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
            //Now we do a partial tick. Depending on the setting:
            // - we restart from the spawn tick (27) and re-predict till now (29)
            // - we continue from the backup (28) until now (29)
            // in both cases we should still get 103.

            // we are forcing here a structural change to verify backup is also found as expected and continue from there.
            // This will move the entity into the other chunk, instead of reusing the same. This give the test
            // more predictable results.
            testWorld.ClientWorlds[0].EntityManager.RemoveComponent<EnableableComponent_0>(predictedSpawnEntity);
            testWorld.TickClientWorld(0.25f/60f);
            lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
            Assert.AreEqual(spawnTick.TickIndexForValidTick + 1, lastBackupTick.TickIndexForValidTick);
            time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            Assert.IsTrue(time.IsPartialTick);
            //because of structural change if we are not keeping the history, we are doing 2 ticks of prediction (from the spawnTick)
            var expectedPredictionCount = keepHistoryBufferOnStructuralChanges == KeepHistoryBufferOptions.RollbackOnStructuralChanges
                ? 2
                : 1;
            Assert.AreEqual(expectedPredictionCount, time.PredictedTickIndex);
            Assert.AreEqual(103, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
            //reset the counter. We are receiving new data from the server. We want verify we are either continue from prediction
            //or rollback
            testWorld.ClientWorlds[0].EntityManager.SetComponentData(predictedSpawnEntity, new CountSimulationFromSpawnTick{});
            testWorld.Tick();
            //How many prediction tick we did? We received from the server a new ghost update, so at least the delta in respect
            //the last received and the current client tick
            time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            var lastReceivedTick = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]).LastReceivedSnapshotByLocal;
            var expectedPredictionTicks = time.ServerTick.TicksSince(lastReceivedTick);
            Assert.AreEqual(expectedPredictionTicks, time.PredictedTickIndex);
            //We will either rollback from 101 or continue but always predict 104.
            Assert.AreEqual(104, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
            var expectedRewind = rollback == PredictedSpawnRollbackOptions.RollbackToSpawnTick ? 1 : 0;
            Assert.AreEqual(expectedRewind, testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountSimulationFromSpawnTick>(predictedSpawnEntity).Value);
        }

        [Test(Description = "server side ghost has a ICleanupComponent that gets removed after all clients have acked the despawn. Testing there's no regression with the amount of time it takes to clean that up.")]
        public void GhostDespawn_SanityCheck()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var ghostGO = new GameObject("TestGhost");
            ghostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
            var ghostConfig = ghostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
            ghostConfig.SupportedGhostModes = GhostModeMask.Interpolated;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGO));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            // Spawn server side
            var serverEnt = testWorld.SpawnOnServer(0);

            // Let is spawn client side
            testWorld.TickMultiple(16);

            // Destroy and check how much time it takes for the entity to be cleaned up
            testWorld.ServerWorld.EntityManager.DestroyEntity(serverEnt);

            for (int i = 0; i < 10; i++)
            {
                testWorld.Tick();
                var exists = testWorld.ServerWorld.EntityManager.Exists(serverEnt);
                if (i <= 2 && !exists)
                    Assert.Fail("GhostCleanup was removed too soon, most likely before the despawn was acked by the client");
                if (i > 2 && exists)
                    Assert.Fail("Tick count for server side ghost cleanup was exceeded, got a regression in the number of ticks it took to cleanup server ghosts. ");
            }
        }

        [Test]
        public void GhostDespawn_CheckLowNetworkRate()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var ghostGO = new GameObject("TestGhost");
            ghostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
            var ghostConfig = ghostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
            ghostConfig.SupportedGhostModes = GhostModeMask.Interpolated;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGO));
            testWorld.CreateWorlds(true, 1);
            var tickRateEntity = testWorld.TryGetSingletonEntity<ClientServerTickRate>(testWorld.ServerWorld);
            if (tickRateEntity == Entity.Null)
                tickRateEntity = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(ClientServerTickRate));
            var tickRate = new ClientServerTickRate();
            tickRate.ResolveDefaults();
            tickRate.SimulationTickRate = 60;
            tickRate.NetworkTickRate = 15;
            testWorld.ServerWorld.EntityManager.SetComponentData<ClientServerTickRate>(tickRateEntity, tickRate);
            testWorld.Connect();
            testWorld.GoInGame();

            // Spawn server side
            var serverEnt = testWorld.SpawnOnServer(0);

            // Let is spawn client side
            testWorld.TickMultiple(16);

            // Destroy and check how much time it takes for the entity to be cleaned up
            testWorld.ServerWorld.EntityManager.DestroyEntity(serverEnt);

            for (int i = 0; i < 50; i++)
            {
                testWorld.Tick();
                var exists = testWorld.ServerWorld.EntityManager.Exists(serverEnt);
                if (i <= 2 && !exists)
                    Assert.Fail("GhostCleanup was removed too soon, most likely before the despawn was acked by the client");
                if (i > 6 && exists)
                    Assert.Fail("Tick count for server side ghost cleanup was exceeded, got a regression in the number of ticks it took to cleanup server ghosts. ");
            }
        }

        [Test(Description = "Make sure that with lag and packet loss, the acking of despawns works properly.")]
        public void GhostDespawn_DespawnAck_WorksProperly()
        {
            // There used to be an issue where we would release a ghost id before we received acks from all clients, which meant weird behaviour and asserts.
            // This test reproduced the conditions for this to happen
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.DriverSimulatedDelay = 200;
            testWorld.DriverSimulatedDrop = 15;

            var ghostGO = new GameObject("TestGhost");
            ghostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
            var ghostConfig = ghostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
            ghostConfig.SupportedGhostModes = GhostModeMask.Interpolated;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGO));
            testWorld.CreateWorlds(true, 1);
            var tickRateEntity = testWorld.TryGetSingletonEntity<ClientServerTickRate>(testWorld.ServerWorld);
            if (tickRateEntity == Entity.Null)
                tickRateEntity = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(ClientServerTickRate));

            var tickRate = new ClientServerTickRate();
            tickRate.ResolveDefaults();
            tickRate.SimulationTickRate = 60;
            tickRate.NetworkTickRate = 15;
            testWorld.ServerWorld.EntityManager.SetComponentData<ClientServerTickRate>(tickRateEntity, tickRate);

            testWorld.Connect(maxSteps:100);
            testWorld.GoInGame();

            // Spawn server side
            var serverEnt = testWorld.SpawnOnServer(0);

            // Let it spawn client side
            testWorld.TickMultiple(16); // make sure it's this count, we align ticks to the network tick rate to reproduce potential issues

            testWorld.ServerWorld.EntityManager.DestroyEntity(serverEnt);

            // run this multiple times, since we have a drop rate chance
            for (int i = 0; i < 100; i++)
            {
                testWorld.TickMultiple(2);
                var newEnt = testWorld.SpawnOnServer(0);
                testWorld.TickMultiple(2);
                testWorld.ServerWorld.EntityManager.DestroyEntity(newEnt);
            }
            testWorld.TickMultiple(500);

            // make sure everything is cleaned up
            var existsServer = testWorld.ServerWorld.EntityManager.Exists(serverEnt);
            var existsClient = !testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance)).IsEmpty;
            Assert.IsFalse(existsClient);
            Assert.IsFalse(existsServer);
        }
    }
}

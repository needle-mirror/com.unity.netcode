using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Transforms;
using Unity.Scenes;
using UnityEngine;
using Unity.NetCode.PrespawnTests;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.SceneManagement;
using Hash128 = Unity.Entities.Hash128;
using System.Linq;
using Unity.NetCode.LowLevel.Unsafe;

#if ENABLE_HOST_MIGRATION

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class IncrementSomeDataSystem : SystemBase
    {
        private EntityQuery _someDataQuery;

        protected override void OnCreate()
        {
            _someDataQuery = GetEntityQuery(typeof(GhostInstance), typeof(SomeData));
        }

        protected override void OnUpdate()
        {
            var someDataEntites = _someDataQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < someDataEntites.Length; ++i)
            {
                EntityManager.SetComponentData(someDataEntites[i], new SomeData { Value = EntityManager.GetComponentData<SomeData>(someDataEntites[i]).Value + 1 });
            }
        }
    }

    [GhostComponent(PrefabType=GhostPrefabType.AllPredicted, OwnerSendType = SendToOwnerType.SendToNonOwner)]
    public struct HMRemoteInput : IInputComponentData
    {
        [GhostField] public int Horizontal;
        [GhostField] public int Vertical;
        [GhostField] public InputEvent Jump;
    }

    public partial class HostMigrationTests : TestWithSceneAsset
    {
        public struct UserConnectionComponent : IComponentData
        {
            public int Value1;
            public byte Value2;
        }

        public struct UserConnectionTagComponent : IComponentData { }

        public struct SomeBuffer : IBufferElementData
        {
            [GhostField] public int Value;
        }

        public struct AnotherBuffer : IBufferElementData
        {
            [GhostField] public int ValueOne;
            [GhostField] public int ValueTwo;
        }

        public struct SimpleData : IComponentData
        {
            [GhostField] public int IntValue;
            [GhostField] public Quaternion QuaternionValue;
            [GhostField] public FixedString128Bytes StringValue;
            public float FloatValue;
        }

        public struct MoreData : IComponentData
        {
            [GhostField] public int IntValue;
            public float FloatValue;
        }

        [GhostEnabledBit]
        public struct SomeEnableable : IComponentData, IEnableableComponent
        {
            [GhostField] public int IntValue;
        }

        [GhostComponent(PrefabType = GhostPrefabType.Server)]
        public struct HostOnlyData : IComponentData
        {
            // TODO: There must be at least one ghost field or this will not be tracked in the ghost component serializer state
            [GhostField] public int Value;
            public float FloatValue;
            // TODO: Containers are not supported and need to throw errors and/or be ignored
            //public NativeArray<int> IntArray;
        }

        [GhostComponent(PrefabType = GhostPrefabType.Server)]
        public struct HostOnlyBuffer : IBufferElementData
        {
            [GhostField] public int Value;
        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(GhostInputSystemGroup))]
        public partial class SetInputSystem : SystemBase
        {
            public static int TargetEventCount;
            public int SendCounter {get; set;}

            public void OnCreate(ref SystemState state)
            {
                SendCounter = 0;
            }

            protected override void OnUpdate()
            {
                foreach (var input in SystemAPI.Query<RefRW<HMRemoteInput>>().WithAll<GhostOwnerIsLocal>())
                {
                    if (SendCounter == TargetEventCount)
                    {
                        input.ValueRW.Horizontal = 0;
                        input.ValueRW.Vertical = 0;
                        input.ValueRW.Jump = default;
                        return;
                    }
                    SendCounter++;
                    input.ValueRW.Vertical = 1;
                    input.ValueRW.Horizontal = 1;
                    input.ValueRW.Jump.Set();
                }

            }
        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        public partial class GetInputSystem : SystemBase
        {
            public int ReceiveCounter { get; set; }
            public long EventCountValue { get; set; }

            protected override void OnUpdate()
            {
                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                foreach (var (input, entity) in SystemAPI.Query<RefRW<HMRemoteInput>>().WithAll<Simulate>().WithEntityAccess())
                {
                    if (input.ValueRW.Jump.IsSet && networkTime.IsFirstTimeFullyPredictingTick)
                    {
                        ReceiveCounter++;
                        EventCountValue += input.ValueRW.Jump.Count;
                    }
                }
            }
        }

        [Test]
        public unsafe void UseDataWriterWithCompression()
        {
            const int ghostCount = 80;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                CreatePrefab(testWorld.ClientWorlds[0].EntityManager);
                var prefab = CreatePrefab(testWorld.ServerWorld.EntityManager);

                var ghostList = new NativeList<Entity>(Allocator.Temp);
                ref var state = ref testWorld.ServerWorld.Unmanaged.GetExistingSystemState<ServerHostMigrationSystem>();
                for (int i = 0; i < ghostCount; ++i)
                {
                    var ghost = testWorld.ServerWorld.EntityManager.Instantiate(prefab);
                    ghostList.Add(ghost);
                    testWorld.ServerWorld.EntityManager.SetComponentData(ghost, new GhostOwner() { NetworkId = i+1 });
                    var beforePosition = new LocalTransform() { Position = new float3(i+1, i+2, i+3) };
                    testWorld.ServerWorld.EntityManager.SetComponentData(ghost, beforePosition);
                    var someBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<SomeBuffer>(ghost);
                    someBuffer.Add(new SomeBuffer() { Value = i+100 });
                    someBuffer.Add(new SomeBuffer() { Value = i+200 });
                    someBuffer.Add(new SomeBuffer() { Value = i+300 });
                    someBuffer.Add(new SomeBuffer() { Value = i+400 });
                    var anotherBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<AnotherBuffer>(ghost);
                    anotherBuffer.Add(new AnotherBuffer() { ValueOne = i+1000, ValueTwo = i+2000 });
                    anotherBuffer.Add(new AnotherBuffer() { ValueOne = i+3000, ValueTwo = i+4000 });
                }

                // Allow ghosts to spawn
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                using var prefabsQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollectionPrefab>());
                var prefabs = prefabsQuery.GetSingletonBuffer<GhostCollectionPrefab>();

                var ghostStorage = new GhostStorage();
                ghostStorage.GhostPrefabs = new NativeArray<GhostPrefabData>(prefabs.Length, Allocator.Temp);
                ghostStorage.Ghosts = new NativeArray<GhostData>(ghostList.Length, Allocator.Temp);

                for (int i = 0; i < prefabs.Length; ++i)
                    ghostStorage.GhostPrefabs[i] = new GhostPrefabData(){GhostTypeIndex = i, GhostTypeHash = prefabs[i].GhostType.GetHashCode()};

                var hostMigrationCache = new HostMigration.Data();
                hostMigrationCache.ServerOnlyComponentsFlag = new NativeList<int>(64, Allocator.Temp);
                hostMigrationCache.ServerOnlyComponentsPerGhostType = new NativeHashMap<int, NativeList<ComponentType>>(64, Allocator.Temp);

                var dataList = new NativeList<NativeArray<byte>>(Allocator.Temp);
                for (int i = 0; i < ghostList.Length; ++i)
                {
                    var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostList[i]);
                    var ghostData = HostMigration.GetGhostComponentData(hostMigrationCache, ref state, ghostList[i], ghostInstance.ghostType, out var hasErrors);
                    ghostStorage.Ghosts[i] = new GhostData()
                    {
                        GhostId = ghostInstance.ghostId,
                        GhostType = ghostInstance.ghostType,
                        Data = ghostData
                    };

                    dataList.Add(ghostData);
                    Assert.IsFalse(hasErrors);
                }

                var ghostDataBlob = new NativeList<byte>(1024, Allocator.Temp);
                HostMigration.SerializeGhostData(ref ghostStorage, ghostDataBlob);

                var compressedGhostData = new NativeList<byte>(ghostDataBlob.Length, Allocator.Temp);
                HostMigration.CompressAndEncodeGhostData(ghostDataBlob, compressedGhostData);

                var ghostDataSlice = compressedGhostData.AsArray().Slice();
                var decodedGhosts = HostMigration.DecompressGhostData(ghostDataSlice);
                Assert.AreEqual(prefabs.Length, decodedGhosts.GhostPrefabs.Length);
                for (int i = 0; i < prefabs.Length; ++i)
                {
                    Assert.AreEqual(i, decodedGhosts.GhostPrefabs[i].GhostTypeIndex);
                    Assert.AreEqual(prefabs[i].GhostType.GetHashCode(), decodedGhosts.GhostPrefabs[i].GhostTypeHash);
                }
                Assert.AreEqual(ghostList.Length, decodedGhosts.Ghosts.Length);
                for (int i = 0; i < ghostList.Length; ++i)
                {
                    var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostList[i]);
                    Assert.AreEqual(ghostInstance.ghostId, decodedGhosts.Ghosts[i].GhostId);
                    Assert.AreEqual(ghostInstance.ghostType, decodedGhosts.Ghosts[i].GhostType);
                    for (int j = 0; j < dataList[i].Length; ++j)
                       Assert.IsTrue(decodedGhosts.Ghosts[i].Data[j] == dataList[i][j]);
                }
            }
        }

        [Test]
        public void HostDataSizeIsCorrect()
        {
            int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // Skip using the test world ghost collection/baking as it requires custom spawning, but the
                // host migration needs to be able to spawn ghosts normally
                for (int i = 0; i < clientCount; ++i)
                    CreatePrefabWithOnlyComponents(testWorld.ClientWorlds[i].EntityManager);
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefabWithOnlyComponents(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(1, serverPrefabs.Length);

                // Add server owned ghosts
                for (int i = 0; i < 5; ++i)
                {
                    var serverGhostEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverGhostEntity, new SimpleData() { IntValue = 100 + i, FloatValue = 100f + i, QuaternionValue = Quaternion.Euler(1,2,3), StringValue = $"HelloWorldHelloWorldHelloWorld" });
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverGhostEntity, new MoreData() { IntValue = 1000 + i, FloatValue = 1000f + i});
                }

                // Add ghosts for each client on the server
                for (int i = 0; i < clientCount; ++i)
                {
                    var playerEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new SimpleData() { IntValue = 100 + i, FloatValue = 100f + i, QuaternionValue = Quaternion.Euler(1,2,3), StringValue = $"HelloWorldHelloWorldHelloWorld" });
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new MoreData() { IntValue = 1000 + i, FloatValue = 1000f + i});
                }

                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();

                // Wait until host migration data is gathered
                var migrationData = new NativeList<byte>(Allocator.Temp);
                var currentTime = testWorld.ServerWorld.Time.ElapsedTime;
                var migrationStats = testWorld.GetSingleton<HostMigrationStats>(testWorld.ServerWorld);
                var timeout = currentTime + 10;
                while (migrationStats.LastDataUpdateTime < currentTime)
                {
                    testWorld.Tick();
                    migrationStats = testWorld.GetSingleton<HostMigrationStats>(testWorld.ServerWorld);
                    if (testWorld.ServerWorld.Time.ElapsedTime > timeout)
                        Assert.Fail("Timeout while waiting for host migration data update");
                }

                var hostMigrationData = testWorld.GetSingletonRW<HostMigrationData>(testWorld.ServerWorld);
                var hostData = hostMigrationData.ValueRO.HostDataBlob;
                var ghostData = hostMigrationData.ValueRO.GhostDataBlob;

                var compressedGhostData = new NativeList<byte>(migrationData.Length, Allocator.Temp);
                HostMigration.CompressAndEncodeGhostData(ghostData, compressedGhostData);
                Assert.IsTrue(ghostData.Length > compressedGhostData.Length);

                var expectedSize = hostData.Length + compressedGhostData.Length + 2*sizeof(int);

                HostMigration.GetHostMigrationData(testWorld.ServerWorld, ref migrationData);
                Assert.AreEqual(expectedSize, migrationData.Length);
                Assert.AreEqual(0, migrationData[^1]);  // Last byte will always be 0

                migrationStats = testWorld.GetSingleton<HostMigrationStats>(testWorld.ServerWorld);
                Assert.AreEqual(expectedSize, migrationStats.UpdateSize);
                Assert.AreEqual(expectedSize, migrationStats.TotalUpdateSize);
            }
        }

        [Test]
        public void HostOnlyStateIsMigrated()
        {
            int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // Skip using the test world ghost collection/baking as it requires custom spawning, but the
                // host migration needs to be able to spawn ghosts normally
                for (int i = 0; i < clientCount; ++i)
                    CreateHostDataPrefab(testWorld.ClientWorlds[i].EntityManager);
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreateHostDataPrefab(testWorld.ServerWorld.EntityManager);

                using var driverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(1, serverPrefabs.Length);

                var hostDataEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                var hostArray = new NativeArray<int>(3, Allocator.Persistent);
                hostArray[0] = 1;
                hostArray[1] = 2;
                hostArray[2] = 3;
                testWorld.ServerWorld.EntityManager.SetComponentData(hostDataEntity, new HostOnlyData(){ Value = hostArray.Length, FloatValue = 100f});
                var hostDataBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<HostOnlyBuffer>(hostDataEntity);
                hostDataBuffer.Add(new HostOnlyBuffer() { Value = 100 });
                hostDataBuffer.Add(new HostOnlyBuffer() { Value = 200 });
                hostDataBuffer.Add(new HostOnlyBuffer() { Value = 300 });
                hostDataBuffer.Add(new HostOnlyBuffer() { Value = 400 });

                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();

                // There should be one spawned ghost in each world
                using var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                Assert.AreEqual(1, serverGhostQuery.CalculateEntityCount());
                var serverComponentCount = testWorld.ServerWorld.EntityManager.GetComponentTypes(serverGhostQuery.GetSingletonEntity()).Length;
                for (int i = 0; i < clientCount; ++i)
                {
                    // The client version of the host data should be there but empty, entity should not include the two host only ghost components
                    using var clientGhostQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    Assert.AreEqual(1, clientGhostQuery.CalculateEntityCount());
                    Assert.AreEqual(serverComponentCount - 2, testWorld.ClientWorlds[i].EntityManager.GetComponentTypes(clientGhostQuery.GetSingletonEntity()).Length);
                }

                GetHostMigrationData(testWorld, out var migrationData);

                // Destroy current server and create a new one
                //var oldServer = testWorld.ServerWorld;
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // Wait until client disconnects
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();
                for (int i = 0; i < clientCount; ++i)
                {
                    using var networkIdQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                    Assert.AreEqual(0, networkIdQuery.CalculateEntityCount());
                }

                // Need to restore the prefab/ghost collection but normally it would happen via subscene loading during migration
                CreateHostDataPrefab(testWorld.ServerWorld.EntityManager);

                // One of the clients will be the one local to the host, so we won't reconnect that one (always skip processing client 1 from now on)
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 1; i < clientCount; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // TODO: We don't handle connection restore on clients atm, so need to manually place in game
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // Allow ghost collection system to run
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // Validate both client/server ghost collections are correct
                var serverCollection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var prefabBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefab>(serverCollection);
                Assert.AreEqual(1, prefabBuffer.Length);
                for (int i = 1; i < clientCount; ++i)
                {
                    var clientCollection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ClientWorlds[i]);
                    prefabBuffer = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostCollectionPrefab>(clientCollection);
                    Assert.AreEqual(1, prefabBuffer.Length);
                }

                // Validate the ghost spawn looks correct
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                var ghostEntities = ghostQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(1, ghostEntities.Length);
                var hostOnlyData = testWorld.ServerWorld.EntityManager.GetComponentData<HostOnlyData>(ghostEntities[0]);
                Assert.AreEqual(hostArray.Length, hostOnlyData.Value);
                Assert.AreEqual(100f, hostOnlyData.FloatValue);
                var hostBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<HostOnlyBuffer>(ghostEntities[0]);
                Assert.AreEqual(4, hostBuffer.Length);
                Assert.AreEqual(100, hostBuffer[0].Value);
                Assert.AreEqual(200, hostBuffer[1].Value);
                Assert.AreEqual(300, hostBuffer[2].Value);
                Assert.AreEqual(400, hostBuffer[3].Value);
                // TODO: Disposing the original server leads to some EntityQuery disposal shenanigans
                //oldServer.Dispose();
            }
        }

        /// <summary>
        /// The simplest host migration scenario, a host (local client) with two connected clients and one spawned ghost per client.
        /// </summary>
        [Test]
        [TestCase(DataStorageMethod.StreamCompressed, 5, 500)]
        [TestCase(DataStorageMethod.StreamCompressed, 3, 1)]
        [TestCase(DataStorageMethod.StreamCompressed, 2, 0)]
        public void SimpleHostMigrationScenario(DataStorageMethod storageMethod, int clientCount, int serverGhostCount)
        {
            if (BurstCompiler.IsEnabled && storageMethod != DataStorageMethod.StreamCompressed)
                return;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // Skip using the test world ghost collection/baking as it requires custom spawning, but the
                // host migration needs to be able to spawn ghosts normally
                for (int i = 0; i < clientCount; ++i)
                {
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager);
                    CreatePrefabWithOnlyComponents(testWorld.ClientWorlds[i].EntityManager);
                }
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabWithOnlyComponents(testWorld.ServerWorld.EntityManager);
                int prefabCount = 2; // count is validated later, and is used in migration request

                using var entityConfigQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationConfig>());
                var config = entityConfigQuery.GetSingletonRW<HostMigrationConfig>();
                config.ValueRW.StorageMethod = storageMethod;

                using var driverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // TODO: Spawn at different ticks and check the SpawnTick
                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(prefabCount, serverPrefabs.Length);

                // Add server owned ghosts
                for (int i = 0; i < serverGhostCount; ++i)
                {
                    var serverGhostEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[1].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverGhostEntity, new SimpleData() { IntValue = 100 + i, FloatValue = 100f + i, QuaternionValue = Quaternion.Euler(1,2,3), StringValue = $"HelloWorldHelloWorldHelloWorld" });
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverGhostEntity, new MoreData() { IntValue = 1000 + i, FloatValue = 1000f + i});
                }

                // Add ghosts for each client on the server and set the owner to client connection
                for (int i = 0; i < clientCount; ++i)
                {
                    var playerEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new GhostOwner() { NetworkId = i+1 });
                    var beforePosition = new LocalTransform() { Position = new float3(i+1, i+2, i+3) };
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, beforePosition);
                    var someBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<SomeBuffer>(playerEntity);
                    someBuffer.Add(new SomeBuffer() { Value = i+100 });
                    someBuffer.Add(new SomeBuffer() { Value = i+200 });
                    someBuffer.Add(new SomeBuffer() { Value = i+300 });
                    someBuffer.Add(new SomeBuffer() { Value = i+400 });
                    var anotherBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<AnotherBuffer>(playerEntity);
                    anotherBuffer.Add(new AnotherBuffer() { ValueOne = i+1000, ValueTwo = i+2000 });
                    anotherBuffer.Add(new AnotherBuffer() { ValueOne = i+3000, ValueTwo = i+4000 });
                }

                for (int i = 0; i < 200; ++i)
                    testWorld.Tick();

                // There should be one spawned ghost for each client + the server owned ghost
                using var allGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                Assert.AreEqual(clientCount + serverGhostCount, allGhostQuery.CalculateEntityCount());
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientGhostQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    Assert.AreEqual(clientCount + serverGhostCount, clientGhostQuery.CalculateEntityCount());
                }

                // Save GhostType of spawned ghost for later
                using var ghostTypeQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostType>());
                var beforeGhostType = ghostTypeQuery.ToComponentDataArray<GhostType>(Allocator.Temp)[0];

                // Add components to connection entities on server, it should be migrated
                using var serverConnectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                var serverConnectionEntities = serverConnectionQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < serverConnectionEntities.Length; ++i)
                {
                    testWorld.ServerWorld.EntityManager.AddComponent<UserConnectionTagComponent>(serverConnectionEntities[i]);
                    testWorld.ServerWorld.EntityManager.AddComponentData(serverConnectionEntities[i], new UserConnectionComponent(){ Value1 = i+1, Value2 = 255});
                }

                GetHostMigrationData(testWorld, out var migrationData);

                // Destroy current server and create a new one
                //var oldServer = testWorld.ServerWorld;
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                using var hostMigrationDataQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationData>());
                var hostMigrationData = hostMigrationDataQuery.ToComponentDataArray<HostMigrationData>(Allocator.Temp);
                Assert.AreEqual(1, hostMigrationData.Length);

                // Validate amount of connection components saved matches actual
                for (int i = 0; i < clientCount; ++i)
                    Assert.AreEqual(2, hostMigrationData[0].HostData.Connections[i].Components.Length);

                // Need to restore the prefab/ghost collection but normally it would happen via subscene loading during migration
                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabWithOnlyComponents(testWorld.ServerWorld.EntityManager);

                // Wait until client disconnects
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();
                for (int i = 0; i < clientCount; ++i)
                {
                    using var networkIdQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                    Assert.AreEqual(0, networkIdQuery.CalculateEntityCount());
                }

                // One of the clients will be the one local to the host, so we won't reconnect that one (always skip processing client 1 from now on)
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 1; i < clientCount; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // TODO: We don't handle connection restore on clients atm, so need to manually place in game
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // Allow ghost collection system to run
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // Validate the new server connections contains the previously added component
                using var userComponentQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<UserConnectionComponent>(), ComponentType.ReadOnly<UserConnectionTagComponent>(), ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(clientCount-1, userComponentQuery.CalculateEntityCount());
                var userComponents = userComponentQuery.ToComponentDataArray<UserConnectionComponent>(Allocator.Temp);
                for (int i = 0; i < userComponents.Length; ++i)
                {
                    Assert.AreEqual(i+2, userComponents[i].Value1);
                    Assert.AreEqual(255, userComponents[i].Value2);
                }

                // Validate both client/server ghost collections are correct
                var serverCollection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var prefabBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefab>(serverCollection);
                Assert.AreEqual(prefabCount, prefabBuffer.Length);
                for (int i = 1; i < clientCount; ++i)
                {
                    var clientCollection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ClientWorlds[i]);
                    prefabBuffer = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostCollectionPrefab>(clientCollection);
                    Assert.AreEqual(prefabCount, prefabBuffer.Length);
                }

                // Validate the ghost spawn looks correct
                // Now the connections which were nr 2 and 3 are now 1 and 2, since the first client local to the host is gone
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostOwner>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<LocalTransform>());
                Assert.AreEqual(clientCount-1, ghostQuery.CalculateEntityCount());
                var ghostInstances = ghostQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                var ghostOwners = ghostQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                var ghostTypes = ghostQuery.ToComponentDataArray<GhostType>(Allocator.Temp);
                var ghostPositions = ghostQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var ghostEntities = ghostQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < clientCount-1; ++i)
                {
                    Assert.AreEqual(i+1, ghostOwners[i].NetworkId);     // Network ID owners on the ghost will as well be 1 and 2
                    Assert.AreEqual(beforeGhostType, ghostTypes[i]);
                    Assert.AreEqual(new float3(i+2, i+3, i+4), ghostPositions[i].Position); // The previous 1st connection will have been (1,2,3) so we'll start here from (2,3,4)
                    var someBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<SomeBuffer>(ghostEntities[i]);
                    Assert.AreEqual(4, someBuffer.Length);
                    Assert.AreEqual(100+(i+1), someBuffer[0].Value);
                    Assert.AreEqual(200+(i+1), someBuffer[1].Value);
                    Assert.AreEqual(300+(i+1), someBuffer[2].Value);
                    Assert.AreEqual(400+(i+1), someBuffer[3].Value);
                    var anotherBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<AnotherBuffer>(ghostEntities[i]);
                    Assert.AreEqual(2, anotherBuffer.Length);
                    Assert.AreEqual(1000+(i+1), anotherBuffer[0].ValueOne);
                    Assert.AreEqual(2000+(i+1), anotherBuffer[0].ValueTwo);
                    Assert.AreEqual(3000+(i+1), anotherBuffer[1].ValueOne);
                    Assert.AreEqual(4000+(i+1), anotherBuffer[1].ValueTwo);
                }
                using var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SimpleData>(), ComponentType.ReadOnly<MoreData>());
                Assert.AreEqual(serverGhostCount, serverGhostQuery.CalculateEntityCount());
                var someData = serverGhostQuery.ToComponentDataArray<SimpleData>(Allocator.Temp);
                var moreData = serverGhostQuery.ToComponentDataArray<MoreData>(Allocator.Temp);
                for (int i = 0; i < serverGhostCount - 1; ++i)
                {
                    Assert.AreEqual(new SimpleData(){ FloatValue = 100f + i, IntValue = 100 + i, QuaternionValue = Quaternion.Euler(1,2,3), StringValue = "HelloWorldHelloWorldHelloWorld"}, someData[i]);
                    Assert.AreEqual(new MoreData(){ IntValue = 1000 + i, FloatValue = 1000f + i }, moreData[i]);
                }
                // TODO: Disposing the original server leads to some EntityQuery disposal shenanigans
                //oldServer.Dispose();
            }
        }

        static void DisconnectServerAndCreateNewServerWorld(NetCodeTestWorld testWorld, ref NativeArray<byte> migrationData)
        {
            using var serverConnectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
            var connections = serverConnectionQuery.ToComponentDataArray<NetworkStreamConnection>(Allocator.Temp);
            for (int i = 0; i < connections.Length; ++i)
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.DriverStore.Disconnect(connections[i]);
            testWorld.Tick();
            testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.DriverStore.Dispose();
            var serverNetDebugQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetDebug>());
            var serverNetDebug = serverNetDebugQuery.GetSingleton<NetDebug>();
            var driverStore = new NetworkDriverStore();
            NetworkStreamReceiveSystem.DriverConstructor.CreateServerDriver(testWorld.ServerWorld, ref driverStore, serverNetDebug);
            var serverDriver = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
            serverDriver.ResetDriverStore(testWorld.ServerWorld.Unmanaged, ref driverStore);
            testWorld.ServerWorld = testWorld.CreateServerWorld("HostMigrationServerWorld");
            testWorld.TrySuppressNetDebug(true, true);
            testWorld.Tick();

            HostMigration.SetHostMigrationData(testWorld.ServerWorld, ref migrationData);
        }

        /// <summary>
        /// Validate that the enableable state of each component is transferred properly during migration
        /// </summary>
        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public void EnablableComponentsMigrateProperly(bool setAsEnabled)
        {
            int clientCount = 3;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // Skip using the test world ghost collection/baking as it requires custom spawning, but the
                // host migration needs to be able to spawn ghosts normally
                for (int i = 0; i < clientCount; ++i)
                    CreatePrefabWithEnableable(testWorld.ClientWorlds[i].EntityManager);
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefabWithEnableable(testWorld.ServerWorld.EntityManager);

                using var driverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);

                // Add ghosts for each client on the server and set the owner to client connection
                var playerEntities = new NativeList<Entity>(Allocator.Temp);
                for (int i = 0; i < clientCount; ++i)
                {
                    var playerEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                    playerEntities.Add(playerEntity);
                    var beforePosition = new LocalTransform() { Position = new float3(i+1, i+2, i+3) };
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, beforePosition);
                    var someBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<SomeBuffer>(playerEntity);
                    someBuffer.Add(new SomeBuffer() { Value = i+100 });
                    someBuffer.Add(new SomeBuffer() { Value = i+200 });
                    someBuffer.Add(new SomeBuffer() { Value = i+300 });
                    someBuffer.Add(new SomeBuffer() { Value = i+400 });
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new SomeEnableable() { IntValue = i+1 });
                }

                // Set enabled bit on all ghosts
                for (int i = 0; i < playerEntities.Length; ++i)
                    testWorld.ServerWorld.EntityManager.SetComponentEnabled<SomeEnableable>(playerEntities[i], setAsEnabled);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Verify the enable bit state on clients
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SomeEnableable>());
                    var clientGhostEntities = clientQuery.ToEntityArray(Allocator.Temp);
                    for (int j = 0; j < clientGhostEntities.Length; ++j)
                        Assert.AreEqual(setAsEnabled, testWorld.ClientWorlds[i].EntityManager.IsComponentEnabled<SomeEnableable>(clientGhostEntities[i]));
                }

                GetHostMigrationData(testWorld, out var migrationData);

                // Destroy current server and create a new one
                //var oldServer = testWorld.ServerWorld;
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // Wait until client disconnects
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();
                for (int i = 0; i < clientCount; ++i)
                {
                    using var networkIdQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                    Assert.AreEqual(0, networkIdQuery.CalculateEntityCount());
                }

                // Need to restore the prefab/ghost collection but normally it would happen via subscene loading during migration
                CreatePrefabWithEnableable(testWorld.ServerWorld.EntityManager);

                // One of the clients will be the one local to the host, so we won't reconnect that one (always skip processing client 1 from now on)
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 1; i < clientCount; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // TODO: We don't handle connection restore on clients atm, so need to manually place in game
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // Allow ghost collection system to run
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // Validate the enable bits after migration
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SomeEnableable>());
                var ghostEntities = ghostQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < ghostEntities.Length; ++i)
                {
                    Assert.AreEqual(setAsEnabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<SomeEnableable>(ghostEntities[i]));
                    var someBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<SomeBuffer>(ghostEntities[i]);
                    Assert.AreEqual(4, someBuffer.Length);
                    Assert.AreEqual(100+i, someBuffer[0].Value);
                    Assert.AreEqual(200+i, someBuffer[1].Value);
                    Assert.AreEqual(300+i, someBuffer[2].Value);
                    Assert.AreEqual(400+i, someBuffer[3].Value);
                }
                // TODO: Disposing the original server leads to some EntityQuery disposal shenanigans
                //oldServer.Dispose();
            }
        }

        [Test]
        public void InputBufferIsSkipped()
        {
            int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                for (int i = 0; i < clientCount; ++i)
                    CreatePrefabWithInputs(testWorld.ClientWorlds[i].EntityManager);
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefabWithInputs(testWorld.ServerWorld.EntityManager);

                using var driverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());

                testWorld.Connect(maxSteps: 10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // Instantiate the player on the server and set the owner to client connection
                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(1, serverPrefabs.Length);
                for (int i = 0; i < clientCount; ++i)
                {
                    // Add some data to make sure it's not being stomped during host save/restore
                    var playerEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new GhostOwner() { NetworkId = i+1 });
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new LocalTransform() { Position = new float3(i+1, i+2, i+3) });
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new SimpleData() {FloatValue = i+1, IntValue = i+1, QuaternionValue = new Quaternion(i+1, i+2, i+3, i+4), StringValue = $"HelloWorldHelloWorldHelloWorld"});
                }

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                for (int i = 0; i < 20; ++i)
                {
                    var ghostsQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HMRemoteInput>(), ComponentType.ReadOnly<InputBufferData<HMRemoteInput>>(), ComponentType.ReadOnly<GhostOwner>());
                    var ghostEntities = ghostsQuery.ToEntityArray(Allocator.Temp);
                    for (int k = 0; k < ghostEntities.Length; ++k)
                    {
                        var inputs = testWorld.ServerWorld.EntityManager.GetBuffer<InputBufferData<HMRemoteInput>>(ghostEntities[k]);

                        if (inputs.Length == 0)
                        {
                            inputs.Add(new InputBufferData<HMRemoteInput>() { InternalInput = new HMRemoteInput() { Horizontal = 1, Vertical = 1 } });
                        }
                        else
                        {
                            var prevInput = inputs[^1];
                            inputs.Add(new InputBufferData<HMRemoteInput>() { InternalInput = new HMRemoteInput() { Horizontal = ++prevInput.InternalInput.Horizontal, Vertical = ++prevInput.InternalInput.Vertical } });
                        }
                    }

                    testWorld.Tick();
                }

                GetHostMigrationData(testWorld, out var migrationData);

                // Destroy current server and create a new one
                //var oldServer = testWorld.ServerWorld;
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // We can't see exactly what components were added to the ghost data part of the host migration data, but we can
                // verify the expected size
                //   Unity.NetCode.GhostOwner - 4 bytes
                //   Unity.Transforms.LocalTransform - 32 bytes
                //   Unity.NetCode.Tests.HostMigrationTests+SomeData - 152 bytes
                //   Unity.NetCode.AutoCommandTarget - 1 bytes
                using var hostMigrationDataQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationData>());
                var hostMigrationData = hostMigrationDataQuery.ToComponentDataArray<HostMigrationData>(Allocator.Temp);
                Assert.AreEqual(1, hostMigrationData.Length);
                foreach (var ghost in hostMigrationData[0].Ghosts.Ghosts)
                    Assert.AreEqual(189, ghost.Data.Length);

                CreatePrefabWithInputs(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);

                // TODO: We don't handle connection restore on clients atm, so need to manually place in game
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // Allow ghost collection system to run
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // Verify migrated ghost data looks intact
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostOwner>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<SimpleData>());
                var ghostOwners = ghostQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                var ghostPositions = ghostQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var someDatas = ghostQuery.ToComponentDataArray<SimpleData>(Allocator.Temp);
                for (int i = 0; i < clientCount-1; ++i)
                {
                    Assert.AreEqual(i+2, ghostOwners[i].NetworkId);     // First client actually also reconnect and will get 1 but not any player spawns (removed during host data save)
                    Assert.AreEqual(new float3(i+2, i+3, i+4), ghostPositions[i].Position); // The previous 1st connection will have been (1,2,3) so we'll start here from (2,3,4)
                    Assert.AreEqual(new SimpleData(){FloatValue = i+2, IntValue = i+2, QuaternionValue = new Quaternion(i+2,i+3,i+4,i+5), StringValue = "HelloWorldHelloWorldHelloWorld"}, someDatas[i]);
                }
            }
        }

        [Test]
        public void MigrationWithMultiplePrefabTypes()
        {
            int clientCount = 3;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // Create two different types of prefabs, to ensure the chunks are iterated properly when copying data as these will be two different chunks
                for (int i = 0; i < clientCount; i++)
                {
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager);
                    CreatePrefabTypeTwo(testWorld.ClientWorlds[i].EntityManager);
                }
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabTypeTwo(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(2, serverPrefabs.Length);
                for (int i = 0; i < clientCount; ++i)
                {
                    var playerEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new GhostOwner() { NetworkId = i+1 });
                    var beforePosition = new LocalTransform() { Position = new float3(i+1, i+2, i+3) };
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, beforePosition);
                }

                // Spawn a few of the other prefab type
                const int miscEntityCount = 5;
                for (int i = 0; i < miscEntityCount; ++i)
                {
                    var miscEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[1].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(miscEntity, new SimpleData() {FloatValue = i+1, IntValue = i+1, QuaternionValue = new Quaternion(i+1,i+2,i+3,i+4), StringValue = "HelloWorldHelloWorldHelloWorld"});
                    testWorld.ServerWorld.EntityManager.SetComponentData(miscEntity, new LocalTransform(){Position = new float3(i+1, i+2, i+3)});
                }

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // There should be one player per client world and then all the misc entities of the second prefab type
                using var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                Assert.AreEqual(clientCount+miscEntityCount, serverGhostQuery.CalculateEntityCount());
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientGhostQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    Assert.AreEqual(clientCount+miscEntityCount, clientGhostQuery.CalculateEntityCount());
                }

                GetHostMigrationData(testWorld, out var migrationData);

                // Destroy current server and create a new one
                //var oldServer = testWorld.ServerWorld;
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // Need to restore the prefab/ghost collection but normally it would happen via subscene loading during migration
                // Create the prefabs in reverse order to the ghost type index would not match between old/new servers
                CreatePrefabTypeTwo(testWorld.ServerWorld.EntityManager);
                CreatePrefab(testWorld.ServerWorld.EntityManager);

                // One of the clients will be the one local to the host, so we won't reconnect that one (always skip processing client 1 from now on)
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 1; i < clientCount; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // TODO: We don't handle connection restore on clients atm, so need to manually place in game
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // Allow ghost collection system to run
                for (int i = 0; i < 6; ++i)
                    testWorld.Tick();

                // Validate that the data from the second prefab type has not been mangled, all 5 ghosts should still be there as these are not connected to clients/players
                using var ghostServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<SimpleData>(), ComponentType.ReadOnly<LocalTransform>());
                var ghostServerPositions = ghostServerQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var serverSomeDatas = ghostServerQuery.ToComponentDataArray<SimpleData>(Allocator.Temp);
                for (int i = 0; i < ghostServerPositions.Length; ++i)
                {
                    Assert.AreEqual(new float3(i+1, i+2, i+3), ghostServerPositions[i].Position);
                    Assert.AreEqual(new SimpleData(){FloatValue = i+1, IntValue = i+1, QuaternionValue = new Quaternion(i+1,i+2,i+3,i+4), StringValue = "HelloWorldHelloWorldHelloWorld"}, serverSomeDatas[i]);
                }
            }
        }

        [Test]
        public void MigrationWithPrespawnGhosts()
        {
            var clientCount = 10;
            VerifyGhostIds.GhostsPerScene = 25;

            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(SomeDataAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 5, 5, ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, clientCount);

                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableHostMigration));
                foreach (var client in testWorld.ClientWorlds)
                {
                    client.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                    client.EntityManager.CreateEntity(typeof(EnableHostMigration));
                }

                testWorld.Connect();
                testWorld.GoInGame();

                // Ensure the prespawn ghosts are actually there (GhostsPerScene matches actual count in scenes)
                for(int i=0;i<64;++i)
                {
                    testWorld.Tick();
                    var clientMatched = true;
                    foreach (var client in testWorld.ClientWorlds)
                        clientMatched &= client.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene;
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        clientMatched)
                        break;
                }

                // Move all the prespawns a bit
                using var prespawnGhostPositionsQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(LocalTransform), typeof(GhostInstance));
                var prespawnGhostPositions = prespawnGhostPositionsQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawnGhostPositions.Length);
                for (int i = 0; i < prespawnGhostPositions.Length; ++i)
                {
                    testWorld.ServerWorld.EntityManager.SetComponentData(prespawnGhostPositions[i], new LocalTransform(){Position = new float3(i+1, i+2, i+3)});
                    testWorld.ServerWorld.EntityManager.SetComponentData(prespawnGhostPositions[i], new SomeData(){Value = i});
                }
                for(int i=0;i<64;++i)
                    testWorld.Tick();
                // Verify the movement is reflected on the clients
                foreach (var client in testWorld.ClientWorlds)
                {
                    using var clientPrespawnQuery = client.EntityManager.CreateEntityQuery(typeof(LocalTransform), typeof(GhostInstance));
                    var clientPositions = clientPrespawnQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                    Assert.AreEqual(VerifyGhostIds.GhostsPerScene, clientPositions.Length);
                    for (int i = 0; i < clientPositions.Length; ++i)
                    {
                        var expectedPosition = new float3(i+1, i+2, i+3);
                        Assert.AreEqual(expectedPosition.x, clientPositions[i].Position.x, 0.001);
                        Assert.AreEqual(expectedPosition.y, clientPositions[i].Position.y, 0.001);
                        Assert.AreEqual(expectedPosition.z, clientPositions[i].Position.z, 0.001);
                    }
                }

                testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs();
                GetHostMigrationData(testWorld, out var migrationData);

                // Destroy current server and create a new one
                //var oldServer = testWorld.ServerWorld;
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // One of the clients will be the one local to the host, so we won't reconnect that one (always skip processing client 1 from now on)
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 1; i < clientCount; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Place the connected client in game on both client and server side
                using var newServerConnectionsQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkId));
                var newServerConnections = newServerConnectionsQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < newServerConnections.Length; ++i)
                    testWorld.ServerWorld.EntityManager.AddComponent<NetCode.NetworkStreamInGame>(newServerConnections[i]);
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // Allow ghost collection system to run
                for (int i = 0; i < 6; ++i)
                    testWorld.Tick();

                // Verify the new server restored the prespawns to correct positions
                using var ghostServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(LocalTransform), typeof(GhostInstance));
                var ghostServerPositions = ghostServerQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, ghostServerPositions.Length);
                for (int i = 0; i < ghostServerPositions.Length; ++i)
                {
                    Assert.AreEqual(new float3(i+1, i+2, i+3), ghostServerPositions[i].Position);
                }

                // Verify the clients also keep correct positions
                foreach (var client in testWorld.ClientWorlds)
                {
                    using var clientPrespawnQuery = client.EntityManager.CreateEntityQuery(typeof(LocalTransform), typeof(GhostInstance), typeof(SomeData));
                    var clientPositions = clientPrespawnQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                    var clientSomeData = clientPrespawnQuery.ToComponentDataArray<SomeData>(Allocator.Temp);
                    Assert.AreEqual(VerifyGhostIds.GhostsPerScene, clientPositions.Length);
                    for (int i = 0; i < clientPositions.Length; ++i)
                    {
                        var expectedPosition = new float3(i+1, i+2, i+3);
                        Assert.AreEqual(expectedPosition.x, clientPositions[i].Position.x, 0.001);
                        Assert.AreEqual(expectedPosition.y, clientPositions[i].Position.y, 0.001);
                        Assert.AreEqual(expectedPosition.z, clientPositions[i].Position.z, 0.001);
                        Assert.AreEqual(i, clientSomeData[i].Value);
                    }
                }
            }
        }

        /// <summary>
        /// This will send inputs events with an input component before and after host migrations.
        /// Checks if the input event counters in the input components look correct before and after.
        /// If the input buffers are migrated the counters can cause issues when clients reconnect
        /// and start sending new events (the decrement event functionality will try to subtract a count
        /// seen before the migration against 0 which the new client will start from).
        /// If input buffers are not migrated the counts will always start from 0 on both the new host and clients.
        /// </summary>
        [Test]
        public void InputEventCountsWorkAfterMigration()
        {
            int clientCount = 4;
            using (var testWorld = new NetCodeTestWorld())
            {
                SetInputSystem.TargetEventCount = 5;
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem), typeof(SetInputSystem), typeof(GetInputSystem));
                testWorld.CreateWorlds(true, clientCount);

                for (int i = 0; i < clientCount; ++i)
                {
                    testWorld.ClientWorlds[i].EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                    CreatePrefabWithInputs(testWorld.ClientWorlds[i].EntityManager);
                }
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefabWithInputs(testWorld.ServerWorld.EntityManager);

                using var driverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());

                testWorld.Connect(maxSteps: 10);
                testWorld.GoInGame();

                // Give ghost collection a chance to initialize
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // Instantiate the player on the server and set the owner to client connection
                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                using var connectionsOnServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                var networkIdsOnServer = connectionsOnServerQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
                var connectionsOnServer = connectionsOnServerQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(1, serverPrefabs.Length);
                for (int i = 0; i < clientCount; ++i)
                {
                    // Add some data to make sure it's not being stomped during host save/restore
                    var networkId = networkIdsOnServer[i].Value;
                    var playerEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new GhostOwner() { NetworkId = networkId });
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new LocalTransform() { Position = new float3(i+1, i+2, i+3) });
                    testWorld.ServerWorld.EntityManager.SetComponentData(connectionsOnServer[i], new CommandTarget(){ targetEntity = playerEntity});
                }

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // Set owner on client side as well
                foreach (var world in testWorld.ClientWorlds)
                {
                    var connectionOnClient = testWorld.TryGetSingletonEntity<NetworkId>(world);
                    var playerOnClient = testWorld.TryGetSingletonEntity<HMRemoteInput>(world);
                    world.EntityManager.SetComponentData(connectionOnClient, new CommandTarget{targetEntity = playerOnClient});
                    world.EntityManager.AddComponent<GhostOwnerIsLocal>(playerOnClient);
                }

                // Give input systems a chance to send all the required input events
                for (int i = 0; i < SetInputSystem.TargetEventCount * 2; ++i)
                    testWorld.Tick();

                for (int i = 0; i < clientCount; ++i)
                {
                    var setInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<SetInputSystem>();
                    var getInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<GetInputSystem>();
                    Assert.AreEqual(SetInputSystem.TargetEventCount, setInputSystem.SendCounter);
                    Assert.AreEqual(SetInputSystem.TargetEventCount, getInputSystem.ReceiveCounter);
                    // TODO: event count value should be the same as the receive counter
                    Assert.Greater(10000, getInputSystem.EventCountValue);
                }
                var serverInputSystem = testWorld.ServerWorld.GetExistingSystemManaged<GetInputSystem>();
                Assert.AreEqual(clientCount * SetInputSystem.TargetEventCount, serverInputSystem.ReceiveCounter);
                Assert.Greater(10000, serverInputSystem.EventCountValue);

                // This actually saves the migration data and restores it on a new server on the same tick where it left off (no delay like when uploading to a service at intervals)
                testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs();
                GetHostMigrationData(testWorld, out var migrationData);

                // Destroy current server and create a new one
                //var oldServer = testWorld.ServerWorld;
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // Reset input system counters
                for (int i = 0; i < clientCount; ++i)
                {
                    var setInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<SetInputSystem>();
                    setInputSystem.SendCounter = 0;
                    var getInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<GetInputSystem>();
                    getInputSystem.ReceiveCounter = 0;
                    getInputSystem.EventCountValue = 0;
                }

                // Allow host migration system to run, all the ghost need to spawn before we can handle reconnecting clients
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                CreatePrefabWithInputs(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);

                // Set command targets on new server
                using var playerEntitiesQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                var playerEntities = playerEntitiesQuery.ToEntityArray(Allocator.Temp);
                var playerGhostOwner = playerEntitiesQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                using var connectionsOnNewServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                networkIdsOnServer = connectionsOnNewServerQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
                connectionsOnServer = connectionsOnNewServerQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < playerGhostOwner.Length; ++i)
                {
                    testWorld.ServerWorld.EntityManager.SetComponentData(connectionsOnServer[i+1], new CommandTarget(){ targetEntity = playerEntities[i]});
                    Assert.AreEqual(playerGhostOwner[i].NetworkId, networkIdsOnServer[i+1].Value);
                }

                // TODO: We don't handle connection restore on clients atm, so need to manually place in game
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // Need to wait until player ghost is spawned on clients via server snapshot
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // Set owner on client side as well
                for (int i = 1; i < clientCount; ++i)
                {
                    var world = testWorld.ClientWorlds[i];
                    var connectionOnClient = testWorld.TryGetSingletonEntity<NetworkId>(world);
                    var playerOnClient = testWorld.TryGetSingletonEntity<HMRemoteInput>(world);
                    Assert.AreNotEqual(playerOnClient, Entity.Null);
                    world.EntityManager.SetComponentData(connectionOnClient, new CommandTarget{targetEntity = playerOnClient});
                    world.EntityManager.AddComponent<GhostOwnerIsLocal>(playerOnClient);
                }

                // Allow ghost collection system to run
                for (int i = 0; i < 20; ++i)
                    testWorld.Tick();

                for (int i = 1; i < clientCount; ++i)
                {
                    var setInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<SetInputSystem>();
                    var getInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<GetInputSystem>();
                    Assert.AreEqual(SetInputSystem.TargetEventCount, setInputSystem.SendCounter);
                    Assert.AreEqual(SetInputSystem.TargetEventCount, getInputSystem.ReceiveCounter);
                    // TODO: EventCountValue should be equal to ReceiveCounter
                    Assert.Greater(10000, getInputSystem.EventCountValue);
                }

                serverInputSystem = testWorld.ServerWorld.GetExistingSystemManaged<GetInputSystem>();
                Assert.AreEqual((clientCount-1) * SetInputSystem.TargetEventCount, serverInputSystem.ReceiveCounter);
                Assert.Greater(10000, serverInputSystem.EventCountValue);

                // Verify migrated ghost data looks intact
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostOwner>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<SimpleData>());
                var ghostOwners = ghostQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                var ghostPositions = ghostQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                for (int i = 0; i < clientCount-1; ++i)
                {
                    Assert.AreEqual(i+2, ghostOwners[i].NetworkId);     // First client actually also reconnect and will get 1 but not any player spawns (removed during host data save)
                    Assert.AreEqual(new float3(i+2, i+3, i+4), ghostPositions[i].Position); // The previous 1st connection will have been (1,2,3) so we'll start here from (2,3,4)
                }
            }
        }


        /// <summary>
        /// Tests that the prespawns snapshots buffer correcly handles having ther server tick rewound and snapshots with the same tick arriving at the client twice.
        /// The bug that inspired this test was happening after a migration. Prespawns would sometimes try to deserialise more data than expected, this
        /// was due to the prespawns snapshot buffer containing two snapshots for the same tick. One from before the migration and one after.
        /// If the snaphot from after the migration was placed after the snapshot from before the migration, it would incorrectly be used at the baseline.
        /// This could result in issues if the baseline changemask ended up being different causing unexpected behaviour.
        /// This test recreates the scenario by directly manipulating the snapshot buffer for a prespawn to be in an invalid state between a host migration
        /// </summary>

        [Test]
        public unsafe void MigrationWithPrespawnWithForcedBadSnapshotHistory()
        {
            // Create a prespawn with our update component
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(SomeDataAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubSceneWithPrefabs(scene, ScenePath, "subscene", new[] { ghost }, 1);
            SceneManager.SetActiveScene(scene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem), typeof(IncrementSomeDataSystem));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);

                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());

                using var driverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());

                testWorld.Connect();
                testWorld.GoInGame();

                // Tick the world a bunch to get the data flowing
                testWorld.TickMultiple(64);

                var prespawns = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(Unity.NetCode.Tests.SomeData), typeof(GhostInstance)).ToComponentDataArray<GhostInstance>(Allocator.Temp);

                // check out client has the correct compoenents in it and its a prespawn
                Assert.AreEqual(1, prespawns.Length, "Number of expected prespawns doesn't match.");
                Assert.IsTrue(PrespawnHelper.IsPrespawnGhostId(prespawns[0].ghostId));

                unsafe
                {
                    GetHostMigrationData(testWorld, out var migrationData);

                    // Tick a bunch to move on simulation so we have snapshots ahead
                    testWorld.TickMultiple(21); // we use 21 as that fills out the snapshot buffer

                    // Grab the snapshot buffer for the perespawn entity
                    using var ghostCollectionQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                    var ghostCollection = ghostCollectionQuery.GetSingletonEntity();
                    var ghostCollectionPrefabSerializers = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollection);

                    var prespawnGhostData = ghostCollectionPrefabSerializers[prespawns[0].ghostType];

                    // Modify the snapshot buffer data on the client
                    var prespawnEntities = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(Unity.NetCode.Tests.SomeData), typeof(GhostInstance)).ToEntityArray(Allocator.Temp);

                    SnapshotData entitySnapshotData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SnapshotData>(prespawnEntities[0]);

                    var clientSnapshotBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<SnapshotDataBuffer>(prespawnEntities[0]);
                    byte* snapshotData = (byte*)clientSnapshotBuffer.GetUnsafePtr();


                    void* tempData = UnsafeUtility.Malloc(prespawnGhostData.SnapshotSize, UnsafeUtility.AlignOf<byte>(), Allocator.Temp);
                    int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(prespawnGhostData.ChangeMaskBits);

                    // Going to flip the snapshot data so its in reverse order and place the LatestIndex in the middle
                    // so new snapshots after the migration will always be placed after old data
                    int bufferSize = clientSnapshotBuffer.Length / prespawnGhostData.SnapshotSize;
                    for (int i = 0; i < bufferSize / 2; ++i)
                    {
                        int dest = i * prespawnGhostData.SnapshotSize; // start of the buffer
                        int src = (bufferSize - 1 - i) * prespawnGhostData.SnapshotSize; // end of the buffer
                        uint* changeMask = (uint*)(snapshotData + src + sizeof(uint));

                        // really mess up the changemasks to force bad deserialisation
                        for (int cm = 0; cm < changeMaskUints; ++cm)
                        {
                            changeMask[cm] ^= 0xFFFFFFFF;
                        }

                        UnsafeUtility.MemCpy(tempData, snapshotData + dest, prespawnGhostData.SnapshotSize); // save the start
                        UnsafeUtility.MemCpy(snapshotData + dest, snapshotData + src, prespawnGhostData.SnapshotSize); // copy the end to the start
                        UnsafeUtility.MemCpy(snapshotData + src, tempData, prespawnGhostData.SnapshotSize); // restore the start at the end
                    }

                    UnsafeUtility.Free(tempData, Allocator.Temp);

                    // Set the index in the middle
                    entitySnapshotData.LatestIndex = bufferSize / 2;
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData<SnapshotData>(prespawnEntities[0], entitySnapshotData);

                    // Do the most migration
                    DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                    // Allow host migration system to run, all the ghost need to spawn before we can handle reconnecting clients
                    testWorld.TickMultiple(2);

                    // reconnect the clients
                    var ep = NetworkEndpoint.LoopbackIpv4;
                    ep.Port = 7979;
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                    }

                    testWorld.TickMultiple(16);

                    // Place the clients in game
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                        testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                    }

                    // Allow ghost collection system to run
                    testWorld.TickMultiple(6);

                    // Check for a successful migration
                    Assert.AreEqual(0, testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationInProgress>()).ToEntityArray(Allocator.Temp).Length, "'HostMigrationInProgress' component still exists. Migration failed/timed out.");
                    Assert.AreEqual(0, testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationRequest>()).ToEntityArray(Allocator.Temp).Length, "'HostMigrationRequest' component still exists. Migration failed/timed out.");

                    // Allow the worlds to tick to send snapshots, we should encounter the error in this time, if not we will fill out the snapshot buffer
                    // with new data and everything will be OK
                    testWorld.TickMultiple(64);

                }
            }
        }



        [Test]
        public unsafe void MigrationKeepsDynamicGhostIds()
        {
            // Check that tracked ghosts ids match, those ids are in our expected ids list (even ids)
            Action<World, string, string> CheckTrackerGhosts = (World world, string worldName, string errorPrefix) =>
            {
                var ghostTrackers = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<GhostIdAndTickChecker>());
                int[] expectedTrackerGhostIds = { 2, 4, 6, 8 };

                Assert.AreEqual(4, ghostTrackers.CalculateEntityCount(), $"{errorPrefix}: {worldName} World expecting 4 ghosts with tracking data found: {ghostTrackers.CalculateEntityCount()}");

                foreach (var e in ghostTrackers.ToEntityArray(Allocator.Temp))
                {
                    var ghostInstance = world.EntityManager.GetComponentData<GhostInstance>(e);
                    var ghostTracker = world.EntityManager.GetComponentData<GhostIdAndTickChecker>(e);

                    Assert.AreEqual(ghostInstance.ghostId, ghostTracker.originalGhostId, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked ghostId {ghostInstance.ghostId}:{ghostTracker.originalGhostId}");
                    Assert.AreEqual(ghostInstance.spawnTick, ghostTracker.originalSpawnTick, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked spawnTick {ghostInstance.spawnTick}:{ghostTracker.originalSpawnTick}");
                    Assert.IsTrue(expectedTrackerGhostIds.Contains(ghostInstance.ghostId), $"{errorPrefix}: {worldName} Ghost has id: {ghostInstance.ghostId} this should be one of 2,4,6,8");
                }
            };

            // Check that post migration ghost count is correct and they have been allocated the correct ids from the list (odd ids), this shows that id's are correcty returned to the free list both pre and post
            // migration
            Action<World, string, string> CheckPostMigrationActionGhosts = (World world, string worldName, string errorPrefix) =>
            {
                var postMighrationActionGhosts = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<CreatedPostHostMigrationAction>());
                int[] expectedPostMigrationmActionGhostIds = { 1, 3, 5, 7 };

                Assert.AreEqual(4, postMighrationActionGhosts.CalculateEntityCount(), $"{errorPrefix}: {worldName} World expecting 4 ghosts post migration action found: {postMighrationActionGhosts.CalculateEntityCount()}");

                foreach (var e in postMighrationActionGhosts.ToEntityArray(Allocator.Temp))
                {
                    var ghostInstance = world.EntityManager.GetComponentData<GhostInstance>(e);

                    Assert.IsTrue(expectedPostMigrationmActionGhostIds.Contains(ghostInstance.ghostId), $"{errorPrefix}: {worldName} Ghost has id: {ghostInstance.ghostId} this should be one of 1,3,5,7");
                }
            };


            const int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // Make our prefabs here
                var trackerPrefabTypes = new ComponentType[1];
                trackerPrefabTypes[0] = ComponentType.ReadOnly<GhostIdAndTickChecker>();
                var postHostMigratioActionPrefabTypes = new ComponentType[1];
                postHostMigratioActionPrefabTypes[0] = ComponentType.ReadOnly<CreatedPostHostMigrationAction>();

                for (int i = 0; i < testWorld.ClientWorlds.Length; i++)
                {
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager, "GhostIdTracker", trackerPrefabTypes);
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager, "PostHostMigrationAction", postHostMigratioActionPrefabTypes);
                }
                var serverEntityManager = testWorld.ServerWorld.EntityManager;
                serverEntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                var trackerPrefab = CreatePrefab(serverEntityManager, "GhostIdTracker", trackerPrefabTypes);
                var postHostMigrationActionPrefab = CreatePrefab(serverEntityManager, "PostHostMigrationAction", postHostMigratioActionPrefabTypes);

                testWorld.Connect(maxSteps: 10);
                testWorld.GoInGame();

                testWorld.TickMultiple(4);

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(2, serverPrefabs.Length);

                // check there are no ghosts yet
                var ghostEntitiesQuery = serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                Assert.AreEqual(0, ghostEntitiesQuery.ToEntityArray(Allocator.Temp).Length, "The test makes assumtions that there are no othter ghosts created so the test has complete control over the ghostIds.");

                // Spawn 8 ghosts
                for (int i = 0; i < 8; ++i)
                    serverEntityManager.Instantiate(trackerPrefab);

                testWorld.TickMultiple(4);

                // copy over the ghost ids and spawn ticks to the tracker and then delete any with odd ghost ids
                var serverGhostTrackers = serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<GhostIdAndTickChecker>());
                var ecb = new EntityCommandBuffer(Allocator.Temp);
                foreach (var e in serverGhostTrackers.ToEntityArray(Allocator.Temp))
                {
                    var ghostInstance = serverEntityManager.GetComponentData<GhostInstance>(e);

                    if (ghostInstance.ghostId % 2 == 0) // even ghost id
                    {
                        // keep and track
                        serverEntityManager.SetComponentData(e, new GhostIdAndTickChecker() { originalGhostId = ghostInstance.ghostId, originalSpawnTick = ghostInstance.spawnTick });
                    }
                    else // destroy odd components
                    {
                        ecb.DestroyEntity(e);
                    }
                }

                ecb.Playback(serverEntityManager);

                // allow the clients to catch up with the changes
                testWorld.TickMultiple(6);

                // Do the host migration
                GetHostMigrationData(testWorld, out var migrationData);

                // now create 4 more ghosts they should have Ids 1,3,5,7 since we reuse those Ids
                for (int i = 0; i < 4; ++i)
                    serverEntityManager.Instantiate(postHostMigrationActionPrefab);

                // allow the clients to catch up with the changes
                testWorld.TickMultiple(6);

                CheckAllWorlds(testWorld, "Pre Migration", new List<Action<World, string, string>> { CheckTrackerGhosts, CheckPostMigrationActionGhosts });

                // Destroy current server and create a new one
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                serverEntityManager = testWorld.ServerWorld.EntityManager;

                // Need to restore the prefab/ghost collection but normally it would happen via subscene loading during migration
                // Create the prefabs in reverse order to the ghost type index would not match between old/new servers
                CreatePrefab(serverEntityManager, "GhostIdTracker", trackerPrefabTypes);
                postHostMigrationActionPrefab = CreatePrefab(serverEntityManager, "PostHostMigrationAction", postHostMigratioActionPrefabTypes);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                testWorld.TickMultiple(8);

                // TODO: We don't handle connection restore on clients atm, so need to manually place in game
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // Allow ghost collection system to run
                testWorld.TickMultiple(6);

                // now create 4 more ghosts post migration they should have Ids 1,3,5,7 since we should reuse those Ids
                for (int i = 0; i < 4; ++i)
                    serverEntityManager.Instantiate(postHostMigrationActionPrefab);

                // allow the send system to run and allocate the ghost ids.
                testWorld.TickMultiple(6);

                // Check the migrated IDs are kept
                CheckAllWorlds(testWorld, "Post Migration", new List<Action<World, string, string>> { CheckTrackerGhosts, CheckPostMigrationActionGhosts });
            }
        }


        [Ignore("Currently disabled as we need to implement a system to support maintaining prespawn GhostIds between migrations. Currently unloading a scene (apart from the last one) will cause prespawn ghost id's to be allocated in a different order.")]
        [Test]
        public unsafe void MigrationKeepsPrespawnGhostIds()
        {
            int expectedGhosts = 0;
            Action<World, string, string> CheckPreSpawnGhostsAreCorrect = (World world, string worldName, string errorPrefix) =>
            {
                var ghostTrackers = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<GhostIdAndTickChecker>());

                Assert.AreEqual(expectedGhosts, ghostTrackers.CalculateEntityCount(), $"{errorPrefix}: {worldName} World expecting {expectedGhosts} ghosts with tracking data found: {ghostTrackers.CalculateEntityCount()}");

                foreach (var e in ghostTrackers.ToEntityArray(Allocator.Temp))
                {
                    var ghostInstance = world.EntityManager.GetComponentData<GhostInstance>(e);
                    var ghostTracker = world.EntityManager.GetComponentData<GhostIdAndTickChecker>(e);

                    Assert.AreEqual(ghostInstance.ghostId, ghostTracker.originalGhostId, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked ghostId {ghostInstance.ghostId}:{ghostTracker.originalGhostId}");
                    Assert.AreEqual(ghostInstance.spawnTick, ghostTracker.originalSpawnTick, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked spawnTick {ghostInstance.spawnTick}:{ghostTracker.originalSpawnTick}");
                    Debug.Log($"{errorPrefix}: {worldName} Ghost with ID {ghostInstance.ghostId}:{ghostTracker.originalGhostId} its a {(PrespawnHelper.IsPrespawnGhostId(ghostInstance.ghostId) ? "Prespawn" : "not a prespawn")}");
                }
            };


            // so we need to make a server with 3 prespawn scenes with a tracked ghost in each
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "Ghost", typeof(GhostAuthoringComponent), typeof(GhostIdAndTickCheckerAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "ParentScene");
            var subscene1 = SubSceneHelper.CreateSubSceneWithPrefabs(scene, ScenePath, "SubScene_1", new[] { ghost }, 1);
            var subscene2 = SubSceneHelper.CreateSubSceneWithPrefabs(scene, ScenePath, "SubScene_2", new[] { ghost }, 1);
            var subscene3 = SubSceneHelper.CreateSubSceneWithPrefabs(scene, ScenePath, "SubScene_3", new[] { ghost }, 1);
            SceneManager.SetActiveScene(scene);

            const int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);

                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());

                testWorld.Connect(maxSteps: 10);
                testWorld.GoInGame();

                testWorld.TickMultiple(8);

                var serverEntityManager = testWorld.ServerWorld.EntityManager;
                var serverGhostTrackers = serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<GhostIdAndTickChecker>());
                foreach (var e in serverGhostTrackers.ToEntityArray(Allocator.Temp))
                {
                    var ghostInstance = serverEntityManager.GetComponentData<GhostInstance>(e);
                    serverEntityManager.SetComponentData(e, new GhostIdAndTickChecker() { originalGhostId = ghostInstance.ghostId, originalSpawnTick = ghostInstance.spawnTick });
                }

                testWorld.TickMultiple(8);

                // We should have 3 subscenes
                Assert.AreEqual(3, serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<SceneReference>()).ToEntityArray(Allocator.Temp).Length);

                // so the world should be setup and we should have 3 trackers which match
                expectedGhosts = 3;
                CheckAllWorlds(testWorld, "Pre Migration 3 scenes", new List<Action<World, string, string>> { CheckPreSpawnGhostsAreCorrect });

                // Remove scene 2
                SceneSystem.UnloadScene(testWorld.ServerWorld.Unmanaged, subscene2.SceneGUID, SceneSystem.UnloadParameters.DestroyMetaEntities);

                testWorld.TickMultiple(8);

                Assert.AreEqual(2, serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<SceneReference>()).ToEntityArray(Allocator.Temp).Length);

                // so there should only be 2 ghosts now
                expectedGhosts = 2;
                CheckAllWorlds(testWorld, "Pre Migration 2 scenes", new List<Action<World, string, string>> { CheckPreSpawnGhostsAreCorrect });

                // do a host migration
                GetHostMigrationData(testWorld, out var migrationData);
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                serverEntityManager = testWorld.ServerWorld.EntityManager;

                testWorld.TickMultiple(2);

                // re-connect the clients
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);

                testWorld.TickMultiple(8);

                // TODO: We don't handle connection restore on clients atm, so need to manually place in game
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                testWorld.TickMultiple(32);

                // we should only have 2 scenes post migration
                Assert.AreEqual(2, serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<SceneReference>()).ToEntityArray(Allocator.Temp).Length);

                // so there should only be 2 ghosts now and their ids shgould match
                expectedGhosts = 2;
                CheckAllWorlds(testWorld, "Post Migration 2 scenes", new List<Action<World, string, string>> { CheckPreSpawnGhostsAreCorrect });
            }
        }

        [Test]
        public unsafe void GhostSpawnedOnSameFrameAsMigrationHasValidGhostType()
        {
            int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // need to create 2 prefabs to spawn ghosts from we need to make a ghost of second type just before we do a host migration
                for (int i = 0; i < clientCount; ++i)
                {
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager);
                    CreatePrefabTypeTwo(testWorld.ClientWorlds[i].EntityManager);
                }

                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabTypeTwo(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps: 10);
                testWorld.GoInGame();

                testWorld.TickMultiple(4);

                // create a ghost of the second prefab type
                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(2, serverPrefabs.Length);

                testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[1].GhostPrefab);

                // before we migrate the only ghost in the world should not have the ghost type
                {
                    var ghostInstanceQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    var ghostEntities = ghostInstanceQuery.ToEntityArray(Allocator.Temp);
                    Assert.AreEqual(1, ghostEntities.Length);
                    var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostEntities[0]);
                    Assert.AreEqual(0, ghostInstance.ghostId);
                    Assert.AreEqual(0, ghostInstance.ghostType);
                }

                var migrationConfig = testWorld.GetSingletonRW<HostMigrationConfig>(testWorld.ServerWorld);
                migrationConfig.ValueRW.ServerUpdateInterval = 0.0f; // Do a migration instantly

                GetHostMigrationData(testWorld, out var migrationData);

                // So after making the migration data we should have a valid ghost id
                {
                    var ghostInstanceQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    var ghostEntities = ghostInstanceQuery.ToEntityArray(Allocator.Temp);
                    Assert.AreEqual(1, ghostEntities.Length);
                    var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostEntities[0]);
                    Assert.AreNotEqual(0, ghostInstance.ghostId);
                    Assert.AreNotEqual(0, ghostInstance.ghostType);
                }

                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                using var hostMigrationDataQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationData>());
                var hostMigrationData = hostMigrationDataQuery.ToComponentDataArray<HostMigrationData>(Allocator.Temp);
                Assert.AreEqual(1, hostMigrationData.Length);

                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabTypeTwo(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps: 10);

                // TODO: We don't handle connection restore on clients atm, so need to manually place in game
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // Allow ghost collection system to run
                testWorld.TickMultiple(2);

                // OK so after the migration we should have a ghost whos type is 1
                {
                    var ghostInstanceQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    var ghostEntities = ghostInstanceQuery.ToEntityArray(Allocator.Temp);
                    Assert.AreEqual(1, ghostEntities.Length);
                    var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostEntities[0]);
                    Assert.AreNotEqual(0, ghostInstance.ghostId);
                    Assert.AreNotEqual(0, ghostInstance.ghostType);
                }
            }
        }

        static void CheckGhostAndTrackersMatch(World world, string worldName, string errorPrefix, int expectedGhosts)
        {
            var ghostTrackers = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<GhostIdAndTickChecker>());

            Assert.AreEqual(expectedGhosts, ghostTrackers.CalculateEntityCount(), $"{errorPrefix}: {worldName} World expecting {expectedGhosts} ghosts with tracking data found: {ghostTrackers.CalculateEntityCount()}");

            foreach (var e in ghostTrackers.ToEntityArray(Allocator.Temp))
            {
                var ghostInstance = world.EntityManager.GetComponentData<GhostInstance>(e);
                var ghostTracker = world.EntityManager.GetComponentData<GhostIdAndTickChecker>(e);

                Assert.AreEqual(ghostInstance.ghostId, ghostTracker.originalGhostId, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked ghostId {ghostInstance.ghostId}:{ghostTracker.originalGhostId}");
                Assert.AreEqual(ghostInstance.spawnTick, ghostTracker.originalSpawnTick, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked spawnTick {ghostInstance.spawnTick}:{ghostTracker.originalSpawnTick}");
            }
        }

        static void CheckAllWorlds(NetCodeTestWorld testWorld, string checkPrefix, List<Action<World, string, string>> checks )
        {
            foreach (var check in checks)
            {
                check(testWorld.ServerWorld, "Server", checkPrefix);
            }

            for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
            {
                foreach (var check in checks)
                {
                    check(testWorld.ClientWorlds[i], "Client", checkPrefix);
                }
            }
        }

        static void GetHostMigrationData(NetCodeTestWorld testWorld, out NativeArray<byte> migrationData)
        {
            var currentTime = testWorld.ServerWorld.Time.ElapsedTime;
            var migrationStats = testWorld.GetSingleton<HostMigrationStats>(testWorld.ServerWorld);
            var timeout = currentTime + 10;
            while (migrationStats.LastDataUpdateTime < currentTime)
            {
                testWorld.Tick();
                migrationStats = testWorld.GetSingleton<HostMigrationStats>(testWorld.ServerWorld);
                if (testWorld.ServerWorld.Time.ElapsedTime > timeout)
                    Assert.Fail("Timeout while waiting for host migration data update");
            }
            migrationData = new NativeArray<byte>(0, Allocator.Temp);
            HostMigration.TryGetHostMigrationData(testWorld.ServerWorld, ref migrationData, out var size);
            migrationData = new NativeArray<byte>(size, Allocator.Temp);
            HostMigration.TryGetHostMigrationData(testWorld.ServerWorld, ref migrationData, out size);
        }

        static Entity CreateHostDataPrefab(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponent<HostOnlyData>(prefab);
            entityManager.AddBuffer<HostOnlyBuffer>(prefab);

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "HostDataPrefab",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.Interpolated,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static Entity CreatePrefab( EntityManager entityManager, FixedString64Bytes name, ComponentType[] components, bool addTransform = false )
        {
            var prefab = entityManager.CreateEntity();
            if ( addTransform )
                entityManager.AddComponentData(prefab, LocalTransform.Identity);

            foreach ( var c in components )
            {
                entityManager.AddComponent(prefab, c);
            }

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = name,
                Importance = 0,
                SupportedGhostModes = GhostModeMask.Interpolated,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static Entity CreatePrefab(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponentData(prefab, LocalTransform.Identity);
            entityManager.AddComponent<GhostOwner>(prefab);
            entityManager.AddBuffer<SomeBuffer>(prefab);
            entityManager.AddBuffer<AnotherBuffer>(prefab);

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "PlayerPrefab",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.OwnerPredicted,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static Entity CreatePrefabWithOnlyComponents(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponent<SimpleData>(prefab);
            entityManager.AddComponent<MoreData>(prefab);

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "PrefabWithOnlyComponents",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }


        static Entity CreatePrefabTypeTwo(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponentData(prefab, LocalTransform.Identity);
            entityManager.AddComponent<SimpleData>(prefab);
            entityManager.AddBuffer<SomeBuffer>(prefab); // Empty buffer

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "PlayerPrefabTypeTwo",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.Interpolated,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static Entity CreatePrefabWithEnableable(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponentData(prefab, LocalTransform.Identity);
            entityManager.AddComponent<SomeEnableable>(prefab);
            entityManager.AddComponent<SimpleData>(prefab);
            entityManager.AddBuffer<SomeBuffer>(prefab);

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "PlayerPrefabWithEnableable",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.Predicted,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static Entity CreatePrefabWithInputs(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponentData(prefab, LocalTransform.Identity);
            entityManager.AddComponent<GhostOwner>(prefab);
            entityManager.AddComponent<HMRemoteInput>(prefab);
            entityManager.AddComponent<InputBufferData<HMRemoteInput>>(prefab);
            entityManager.AddComponent<AutoCommandTarget>(prefab);
            entityManager.SetComponentData(prefab, new AutoCommandTarget(){ Enabled = true });
            entityManager.AddComponent<SimpleData>(prefab);

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "PlayerPrefabWithInputs",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.OwnerPredicted,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }
    }
}

#endif // ENABLE_HOST_MIGRATION

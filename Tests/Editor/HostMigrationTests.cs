using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Burst;
using Unity.Serialization.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Transforms;
using UnityEngine;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Serialization.Binary;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode.Tests
{
    [GhostComponent(PrefabType=GhostPrefabType.AllPredicted, OwnerSendType = SendToOwnerType.SendToNonOwner)]
    public struct HMRemoteInput : IInputComponentData
    {
        [GhostField] public int Horizontal;
        [GhostField] public int Vertical;
        [GhostField] public InputEvent Jump;
    }

    public class HostMigrationTests
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

        public struct SomeData : IComponentData
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

#if USING_UNITY_SERIALIZATION
        [Test]
        public void TestNativeArraySerialization()
        {
            var migrationData = new HostDataStorage();
            migrationData.Connections = new NativeArray<HostConnectionData>(2, Allocator.Temp);
            var componentData = new NativeArray<byte>(4, Allocator.Temp);
            for (int i = 0; i < componentData.Length; i++)
                componentData[i] = (byte)i;
            var components = new NativeArray<ConnectionComponent>(2, Allocator.Temp);
            components[0] = new ConnectionComponent()
            {
                StableHash = 12345,
                Data = componentData
            };
            components[1] = new ConnectionComponent()
            {
                StableHash = 56789,
                Data = componentData
            };
            migrationData.Connections[0] = new HostConnectionData()
            {
                Components = components,
                UniqueId = 12345,
                NetworkId = 1
            };
            migrationData.Connections[1] = new HostConnectionData()
            {
                Components = components,
                UniqueId = 56789,
                NetworkId = 2
            };
            migrationData.SubScenes = new NativeArray<HostSubSceneData>(2, Allocator.Temp);
            migrationData.SubScenes[0] = new HostSubSceneData()
            {
                SubSceneGuid = new Hash128(0, 1, 2, 3)
            };
            migrationData.SubScenes[1] = new HostSubSceneData()
            {
                SubSceneGuid = new Hash128(4, 5, 6, 7)
            };

            var parameters = new JsonSerializationParameters
            {
                UserDefinedAdapters = new List<IJsonAdapter>
                {
                    new NativeArrayAdapter<byte>(),
                    new NativeArrayAdapter<ulong>(),
                    new NativeArrayAdapter<HostConnectionData>(),
                    new NativeArrayAdapter<HostSubSceneData>(),
                    new NativeArrayAdapter<ConnectionComponent>(),
                }
            };
            var json = JsonSerialization.ToJson(migrationData, parameters);

            var deserializedHostMigrationData = JsonSerialization.FromJson<HostDataStorage>(json, parameters);
            for (int i = 0; i < deserializedHostMigrationData.Connections.Length; i++)
            {
                Assert.AreEqual(deserializedHostMigrationData.Connections[i].UniqueId, migrationData.Connections[i].UniqueId);
                Assert.AreEqual(deserializedHostMigrationData.Connections[i].NetworkId, migrationData.Connections[i].NetworkId);
                Assert.AreEqual(deserializedHostMigrationData.Connections[i].NetworkStreamInGame, migrationData.Connections[i].NetworkStreamInGame);
                Assert.AreEqual(deserializedHostMigrationData.Connections[i].ScenesLoadedCount, migrationData.Connections[i].ScenesLoadedCount);
                for (int j = 0; j < deserializedHostMigrationData.Connections[i].Components.Length; j++)
                {
                    Assert.AreEqual(deserializedHostMigrationData.Connections[i].Components[j].StableHash, migrationData.Connections[i].Components[j].StableHash);
                    for (int k = 0; k < deserializedHostMigrationData.Connections[i].Components[j].Data.Length; k++)
                        Assert.AreEqual(deserializedHostMigrationData.Connections[i].Components[j].Data[k], migrationData.Connections[i].Components[j].Data[k]);
                }
            }
            Assert.AreEqual(deserializedHostMigrationData.SubScenes[0].SubSceneGuid, migrationData.SubScenes[0].SubSceneGuid);
        }
#endif

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
                hostMigrationCache.InputBuffers = new NativeHashMap<ComponentType, bool>(64, Allocator.Temp);

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
                HostMigration.CompressGhostData(testWorld.ServerWorld, ghostDataBlob, compressedGhostData);

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

#if USING_UNITY_SERIALIZATION
        [Test]
        public unsafe void UseBinarySerialization()
        {
            const int ghostCount = 30;
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

                var ghostStorage = new GhostStorage()
                {
                    Ghosts = new NativeArray<GhostData>(ghostCount, Allocator.Temp),
                    GhostPrefabs = new NativeArray<GhostPrefabData>(1, Allocator.Temp)
                };

                var hostMigrationCache = new HostMigration.Data();
                hostMigrationCache.ServerOnlyComponentsFlag = new NativeList<int>(64, Allocator.Temp);
                hostMigrationCache.ServerOnlyComponentsPerGhostType = new NativeHashMap<int, NativeList<ComponentType>>(64, Allocator.Temp);
                hostMigrationCache.InputBuffers = new NativeHashMap<ComponentType, bool>(64, Allocator.Temp);

                for (int i = 0; i < ghostCount; ++i)
                {
                    var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostList[i]);
                    var ghostData = HostMigration.GetGhostComponentData(hostMigrationCache, ref state, ghostList[i], ghostInstance.ghostType, out var hasErrors);
                    ghostStorage.Ghosts[i] = new GhostData()
                    {
                        GhostId = ghostInstance.ghostId,
                        GhostType = ghostInstance.ghostType,
                        Data = ghostData
                    };
                    Assert.IsFalse(hasErrors);
                }

                var parameters = new BinarySerializationParameters()
                {
                    UserDefinedAdapters = new List<IBinaryAdapter>
                    {
                        new NativeArrayBinaryAdapter<byte>(),
                        new NativeArrayBinaryAdapter<GhostData>(),
                        new NativeArrayBinaryAdapter<GhostPrefabData>()
                    }
                };

                using var stream = new UnsafeAppendBuffer(16, 8, Allocator.Temp);
                BinarySerialization.ToBinary(&stream, ghostStorage, parameters);

                Debug.Log($"Initial size {stream.Length}");

                var span = new ReadOnlySpan<byte>(stream.Ptr, (int)stream.Length);
                var encoded = Convert.ToBase64String(span);

                Debug.Log($"Base64 encoded size {encoded.Length}");

                var decoded = Convert.FromBase64String(encoded);
                Debug.Log($"Base64 decoded size {decoded.Length}");

                fixed (byte* ptr = decoded)
                {
                    using var decodeStream = new UnsafeAppendBuffer(ptr, decoded.Length);
                    decodeStream.ResizeUninitialized(decoded.Length);
                    var bufferReader = decodeStream.AsReader();
                    var deserializedGhostStorage = BinarySerialization.FromBinary<GhostStorage>(&bufferReader, parameters);

                    for (int i = 0; i < ghostCount; ++i)
                    {
                        Assert.AreEqual(ghostStorage.Ghosts[i].GhostId, deserializedGhostStorage.Ghosts[i].GhostId);
                        Assert.AreEqual(ghostStorage.Ghosts[i].GhostType, deserializedGhostStorage.Ghosts[i].GhostType);
                        for (int j = 0; j < ghostStorage.Ghosts[i].Data.Length; ++j)
                            Assert.IsTrue(ghostStorage.Ghosts[i].Data[j] == deserializedGhostStorage.Ghosts[i].Data[j]);
                    }
                }
            }
        }
#endif

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
#if USING_UNITY_SERIALIZATION
        [TestCase(DataStorageMethod.Json, 3, 1)]
        [TestCase(DataStorageMethod.JsonMinified, 3, 1)]
        [TestCase(DataStorageMethod.Binary, 3, 1)]
#endif
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
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverGhostEntity, new SomeData() { IntValue = 100 + i, FloatValue = 100f + i, QuaternionValue = Quaternion.Euler(1,2,3), StringValue = $"HelloWorldHelloWorldHelloWorld" });
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
                // Ghost Ids will also skip ghosts owned by previous client 1, so the ghost which was ID 2 is now 1 (first spawn in this test)
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostOwner>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<LocalTransform>());
                Assert.AreEqual(clientCount-1, ghostQuery.CalculateEntityCount());
                var ghostInstances = ghostQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                var ghostOwners = ghostQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                var ghostTypes = ghostQuery.ToComponentDataArray<GhostType>(Allocator.Temp);
                var ghostPositions = ghostQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var ghostEntities = ghostQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < clientCount-1; ++i)
                {
                    Assert.AreEqual(i+1, ghostInstances[i].ghostId);    // Ghost IDs will be 1 and 2 (previously 2 and 3)
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
                using var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SomeData>(), ComponentType.ReadOnly<MoreData>());
                Assert.AreEqual(serverGhostCount, serverGhostQuery.CalculateEntityCount());
                var someData = serverGhostQuery.ToComponentDataArray<SomeData>(Allocator.Temp);
                var moreData = serverGhostQuery.ToComponentDataArray<MoreData>(Allocator.Temp);
                for (int i = 0; i < serverGhostCount - 1; ++i)
                {
                    Assert.AreEqual(new SomeData(){ FloatValue = 100f + i, IntValue = 100 + i, QuaternionValue = Quaternion.Euler(1,2,3), StringValue = "HelloWorldHelloWorldHelloWorld"}, someData[i]);
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

        [Ignore("Skipping input buffers has been disabled to make burst work, test can be enabled again if this becomes burst-compatible later")]
        [Test]
        public void InputBufferIsSkipped()
        {
            int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem),
                    typeof(Unity_NetCode_EditorTests_Generated_Unity_NetCode_Tests.Unity_NetCode_EditorTests_Generated_Unity_NetCode_Tests_HMRemoteInputHMRemoteInputInputBufferDataSendCommandSystem),
                    typeof(Unity_NetCode_EditorTests_Generated_Unity_NetCode_Tests.Unity_NetCode_EditorTests_Generated_Unity_NetCode_Tests_HMRemoteInputHMRemoteInputInputBufferDataReceiveCommandSystem),
                    typeof(Unity_NetCode_EditorTests_Generated_Unity_NetCode_Tests.Unity_NetCode_EditorTests_Generated_Unity_NetCode_Tests_HMRemoteInputHMRemoteInputInputBufferDataCompareCommandSystem));
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
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new SomeData() {FloatValue = i+1, IntValue = i+1, QuaternionValue = new Quaternion(i+1, i+2, i+3, i+4), StringValue = $"HelloWorldHelloWorldHelloWorld"});
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
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostOwner>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<SomeData>());
                var ghostOwners = ghostQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                var ghostPositions = ghostQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var someDatas = ghostQuery.ToComponentDataArray<SomeData>(Allocator.Temp);
                for (int i = 0; i < clientCount-1; ++i)
                {
                    Assert.AreEqual(i+2, ghostOwners[i].NetworkId);     // First client actually also reconnect and will get 1 but not any player spawns (removed during host data save)
                    Assert.AreEqual(new float3(i+2, i+3, i+4), ghostPositions[i].Position); // The previous 1st connection will have been (1,2,3) so we'll start here from (2,3,4)
                    Assert.AreEqual(new SomeData(){FloatValue = i+2, IntValue = i+2, QuaternionValue = new Quaternion(i+2,i+3,i+4,i+5), StringValue = "HelloWorldHelloWorldHelloWorld"}, someDatas[i]);
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
                    testWorld.ServerWorld.EntityManager.SetComponentData(miscEntity, new SomeData() {FloatValue = i+1, IntValue = i+1, QuaternionValue = new Quaternion(i+1,i+2,i+3,i+4), StringValue = "HelloWorldHelloWorldHelloWorld"});
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
                using var ghostServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<SomeData>(), ComponentType.ReadOnly<LocalTransform>());
                var ghostServerPositions = ghostServerQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var serverSomeDatas = ghostServerQuery.ToComponentDataArray<SomeData>(Allocator.Temp);
                for (int i = 0; i < ghostServerPositions.Length; ++i)
                {
                    Assert.AreEqual(new float3(i+1, i+2, i+3), ghostServerPositions[i].Position);
                    Assert.AreEqual(new SomeData(){FloatValue = i+1, IntValue = i+1, QuaternionValue = new Quaternion(i+1,i+2,i+3,i+4), StringValue = "HelloWorldHelloWorldHelloWorld"}, serverSomeDatas[i]);
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
            entityManager.AddComponent<SomeData>(prefab);
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
            entityManager.AddComponent<SomeData>(prefab);
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
            entityManager.AddComponent<SomeData>(prefab);
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
            entityManager.AddComponent<SomeData>(prefab);

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

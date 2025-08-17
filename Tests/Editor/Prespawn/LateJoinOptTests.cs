using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using Unity.NetCode.Tests;
using Unity.Networking.Transport;
using Unity.Transforms;

namespace Unity.NetCode.PrespawnTests
{
    struct ServerOnlyTag : IComponentData
    {
    }

    internal class LateJoinOptTests : TestWithSceneAsset
    {
        private static void CheckPrespawnArePresent(int numObjects, NetCodeTestWorld testWorld)
        {
            //Before going in game there should N prespawned objects
            using var serverGhosts = testWorld.ServerWorld.EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new [] { ComponentType.ReadOnly(typeof(PreSpawnedGhostIndex))},
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            Assert.AreEqual(numObjects, serverGhosts.CalculateEntityCount());
            for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
            {
                using var clientGhosts = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new [] { ComponentType.ReadOnly(typeof(PreSpawnedGhostIndex))},
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
                Assert.AreEqual(numObjects, clientGhosts.CalculateEntityCount());
            }
        }

        private static void CheckComponents(int numObjects, NetCodeTestWorld testWorld)
        {

            Assert.IsFalse(testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(SomeData),typeof(Disabled)).IsEmpty);
            Assert.IsFalse(testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(SomeDataElement), typeof(Disabled)).IsEmpty);

            for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
            {
                Assert.IsFalse(testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(typeof(SomeData),typeof(Disabled)).IsEmpty);
                Assert.IsFalse(testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(typeof(SomeDataElement),typeof(Disabled)).IsEmpty);
            }
        }

        int FindGhostType(in DynamicBuffer<GhostCollectionPrefab> ghostCollection, GhostType ghostTypeComponent)
        {
            int ghostType;
            for (ghostType = 0; ghostType < ghostCollection.Length; ++ghostType)
            {
                if (ghostCollection[ghostType].GhostType == ghostTypeComponent)
                    break;
            }
            if (ghostType >= ghostCollection.Length)
                return -1;
            return ghostType;
        }

        private void CheckBaselineAreCreated(World world)
        {
            //Before going in game there should N prespawned objects
            var baselines = world.EntityManager.CreateEntityQuery(typeof(PrespawnGhostBaseline));
            Assert.IsFalse(baselines.IsEmptyIgnoreFilter);
            var entities = baselines.ToEntityArray(Allocator.Temp);
            var ghostCollectionEntity = world.EntityManager.CreateEntityQuery(typeof(GhostCollection)).GetSingletonEntity();
            var ghostCollection = world.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionEntity);
            var ghostPrefabs = world.EntityManager.GetBuffer<GhostCollectionPrefab>(ghostCollectionEntity);
            var ghostComponentIndex = world.EntityManager.GetBuffer<GhostCollectionComponentIndex>(ghostCollectionEntity);
            var ghostSerializers = world.EntityManager.GetBuffer<GhostComponentSerializer.State>(ghostCollectionEntity);
            Assert.AreEqual(3, ghostCollection.Length);
            foreach (var ent in entities)
            {
                var buffer = world.EntityManager.GetBuffer<PrespawnGhostBaseline>(ent);
                Assert.AreNotEqual(0, buffer.Length);
                //Check that the baseline contains what we expect
                unsafe
                {
                    var ghost = world.EntityManager.GetComponentData<GhostInstance>(ent);
                    if (world.IsClient())
                        Assert.AreEqual(-1, ghost.ghostType); // not set yet
                    var ghostType = world.EntityManager.GetComponentData<GhostType>(ent);
                    var idx = FindGhostType(ghostPrefabs, ghostType);
                    Assert.AreNotEqual(-1, idx);
                    //Need to lookup who is it
                    var typeData = ghostCollection[idx];
                    byte* snapshotPtr = (byte*) buffer.GetUnsafeReadOnlyPtr();
                    int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                    var snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(4 + changeMaskUints * 4);
                    for (int cm = 0; cm < changeMaskUints; ++cm)
                        Assert.AreEqual(0, ((uint*)snapshotPtr)[cm]);
                    var offset = snapshotOffset;
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int serializerIdx = ghostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        if (ghostSerializers[serializerIdx].ComponentType.IsBuffer)
                        {
                            Assert.AreEqual(16, ((uint*)(snapshotPtr + offset))[0]);
                            Assert.AreEqual(GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint)), ((uint*)(snapshotPtr + offset))[1]);
                        }
                        offset += GhostComponentSerializer.SizeInSnapshot(ghostSerializers[serializerIdx]);
                    }
                    if (typeData.NumBuffers > 0)
                    {
                        var dynamicDataPtr = snapshotPtr + typeData.SnapshotSize;
                        var bufferSize = ((uint*)dynamicDataPtr)[0];
                        Assert.AreEqual(GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint)) +
                                        GhostComponentSerializer.SnapshotSizeAligned(16*sizeof(uint)), bufferSize);
                    }
                }
            }
        }

        void ValidateReceivedSnapshotData(World clientWorld)
        {
            using var query = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
            using var collectionQuery = clientWorld.EntityManager.CreateEntityQuery(typeof(GhostCollection));
            var entities = query.ToEntityArray(Allocator.Temp);
            var ghostCollectionEntity = collectionQuery.GetSingletonEntity();
            var ghostCollection = clientWorld.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionEntity);
            var ghostComponentIndex = clientWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(ghostCollectionEntity);
            var ghostSerializers = clientWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(ghostCollectionEntity);

            unsafe
            {
                for (int i = 0; i < entities.Length; ++i)
                {
                    var ghost = clientWorld.EntityManager.GetComponentData<GhostInstance>(entities[i]);
                    Assert.AreNotEqual(-1, ghost.ghostType);
                    var typeData = ghostCollection[ghost.ghostType];
                    var snapshotData = clientWorld.EntityManager.GetComponentData<SnapshotData>(entities[i]);
                    var snapshotBuffer = clientWorld.EntityManager.GetBuffer<SnapshotDataBuffer>(entities[i]);

                    byte* snapshotPtr = (byte*)snapshotBuffer.GetUnsafeReadOnlyPtr();
                    int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                    int snapshotSize = typeData.SnapshotSize;
                    var snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(4 + changeMaskUints*4);
                    snapshotPtr += snapshotSize * snapshotData.LatestIndex;
                    uint* changeMask = (uint*)(snapshotPtr+4);

                    //Check that all the masks are zero
                    for (int cm = 0; cm < changeMaskUints; ++cm)
                        Assert.AreEqual(0, changeMask[cm]);

                    var offset = snapshotOffset;
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int serializerIdx = ghostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        if (ghostSerializers[serializerIdx].ComponentType.IsBuffer)
                        {
                            Assert.AreEqual(16, ((uint*)(snapshotPtr + offset))[0]);
                            Assert.AreEqual(0, ((uint*)(snapshotPtr + offset))[1]);
                        }
                        offset += GhostComponentSerializer.SizeInSnapshot(ghostSerializers[serializerIdx]);
                    }
                    if (typeData.NumBuffers > 0)
                    {
                        var dynamicData = clientWorld.EntityManager.GetBuffer<SnapshotDynamicDataBuffer>(entities[i]);
                        byte* dynamicPtr = (byte*) dynamicData.GetUnsafeReadOnlyPtr();
                        var bufferSize = ((uint*) dynamicPtr)[snapshotData.LatestIndex];
                        Assert.AreEqual(GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint)) +
                                        GhostComponentSerializer.SnapshotSizeAligned(16*sizeof(uint)), bufferSize);
                    }
                }
            }
        }

        unsafe void TestRunner(int numClients, int numObjectsPerPrefabs, int numPrefabs,
            uint[] initialDataSize,
            uint[] initialAvgBitsPerEntity,
            uint[] avgBitsPerEntity,
            bool enableFallbackBaseline)
        {
            var numObjects = numObjectsPerPrefabs * numPrefabs;
            var uncompressed = new uint[numClients];
            var totalDataReceived = new uint[numClients];
            var numReceived = new uint[numClients];
            using (var testWorld = new NetCodeTestWorld())
            {
                //Create a scene with a subscene and a bunch of objects in it
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, numClients);
                var mode = enableFallbackBaseline ? "WithBaseline" : "NoBaseline";

                //Stream the sub scene in
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect();
                CheckPrespawnArePresent(numObjects, testWorld);
                CheckComponents(numObjects, testWorld);
                //To Disable the prespawn optimization, just remove the baselines
                if (!enableFallbackBaseline)
                {
                    var builder = new EntityQueryBuilder(Allocator.Temp).WithPresent<PrespawnGhostBaseline>().WithOptions(EntityQueryOptions.IncludeDisabledEntities);
                    using var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(builder);
                    Assert.AreEqual(numObjects, serverQuery.CalculateEntityCount(), "Sanity! Ensure it'll be removed!");
                    testWorld.ServerWorld.EntityManager.RemoveComponent<PrespawnGhostBaseline>(serverQuery);
                    Assert.AreEqual(0, serverQuery.CalculateEntityCount(), "Sanity! Ensure it has been removed!");
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        using var clientQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(builder);
                        Assert.AreEqual(numObjects, clientQuery.CalculateEntityCount(), "Sanity! Ensure it'll be removed!");
                        testWorld.ClientWorlds[i].EntityManager.RemoveComponent<PrespawnGhostBaseline>(clientQuery);
                        Assert.AreEqual(0, clientQuery.CalculateEntityCount(), "Sanity! Ensure it has been removed!");
                    }
                }

                testWorld.GoInGame();

                var connections = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrespawnSectionAck>()).ToEntityArray(Allocator.Temp);
                for (int i = 0; i< 32; ++i)
                {
                    testWorld.Tick();
                    bool allSceneAcked = false;
                    foreach (var connection in (connections))
                    {
                        var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<PrespawnSectionAck>(connection);
                        allSceneAcked |= buffer.Length > 0;
                    }

                    if (allSceneAcked)
                        break;
                }
                // ----------------------------------------------------------------
                // From heere one the server will start sending some ghosts.
                // ----------------------------------------------------------------
                uint newObjects = 0;
                uint totalSceneData = 0;
                for(int tick=0;tick<32;++tick)
                {
                    testWorld.Tick();
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        var netStats = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i])).MainStatsWrite;
                        totalSceneData += netStats.PerGhostTypeStatsListRefRW.ElementAt(0).SizeInBits;
                        for (int gtype = 0; gtype < numPrefabs; ++gtype) // numPrefabs doesn't match with PerGhostTypeStats length, since the ghost stats list also contains the extra netcode owned prespawn ghost (first index)
                        {
                            numReceived[i] += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).EntityCount;
                            totalDataReceived[i] += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).SizeInBits;
                            uncompressed[i] += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).UncompressedCount;
                        }
                        if(enableFallbackBaseline)
                            ValidateReceivedSnapshotData(testWorld.ClientWorlds[i]);

                        //When the total uncompressed object equals 0 means no new ghosts is received
                        //This is always true for enableFallbackBaseline is true
                        newObjects = 0;
                        for (int gtype = 0; gtype < numPrefabs; ++gtype)
                            newObjects += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).UncompressedCount;
                    }

                    if (newObjects == 0 && numReceived[0] >= numObjects)
                        break;
                }

                //Without late join opt, the received data is more or equals than the totalCompressedDataBits for sure
                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                {
                    //Store the "join" data size
                    initialAvgBitsPerEntity[i] = totalDataReceived[i] / numReceived[i];
                    initialDataSize[i] = totalDataReceived[i];
                    Debug.Log($"{mode} Client {i} Initial Join: {numReceived[i]} - {totalDataReceived[i]} - {initialAvgBitsPerEntity[i]}");
                }

                //For the subsequent ticks the expectation is to reach the 0 bits for the entity data (everything is stationary).
                //Only header, masks, ghost ids, and baselines will be sent. The size should remain almost constant. It can still
                //change a bit because of the baselines and tick encoding)
                for (int tick = 0; tick < 32; ++tick)
                {
                    testWorld.Tick();
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        var netStats = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i])).MainStatsWrite;

                        for (int gtype = 0; gtype < numPrefabs; ++gtype)
                        {
                            Assert.AreEqual(0, netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).UncompressedCount); //No new object
                            numReceived[i] += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).EntityCount;
                            totalDataReceived[i] += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).SizeInBits;
                        }
                        ValidateReceivedSnapshotData(testWorld.ClientWorlds[i]);
                    }
                }

                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                {
                    avgBitsPerEntity[i] = totalDataReceived[i] / numReceived[i];
                    Debug.Log($"{mode} Client {i} At Regime: {numReceived[i]} - {totalDataReceived[i]} - {avgBitsPerEntity[i]}");
                }
            }
        }

        [Test]
        public void DataSentWithFallbackBaselineAreLessThanWithout()
        {
            const int numObjectsPerPrefab = 32;
            const int numClients = 1;
            const int numPrefabs = 4;

            //Set the scene with multiple prefab types
            var prefab1 = SubSceneHelper.CreateSimplePrefab(ScenePath, "Simple", typeof(GhostAuthoringComponent));
            var prefab2 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var prefab3 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithBuffer", typeof(GhostAuthoringComponent),
                typeof(SomeDataElementAuthoring));
            GameObject withChildren = new GameObject("WithChildren", typeof(GhostAuthoringComponent));
            GameObject children1 = new GameObject("Child1", typeof(SomeDataAuthoring));
            GameObject children2 = new GameObject("Child2", typeof(SomeDataAuthoring));
            children1.transform.parent = withChildren.transform;
            children2.transform.parent = withChildren.transform;
            var prefab4 = SubSceneHelper.CreatePrefab(ScenePath, withChildren);

            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "LateJoinTest");
            SubSceneHelper.CreateSubSceneWithPrefabs(parentScene, ScenePath, "subscene", new[]
            {
                prefab1,
                prefab2,
                prefab3,
                prefab4,
            }, numObjectsPerPrefab);
            var initialDataSize = new uint[numClients];
            var initialAvgBitsPerEntity = new uint[numClients];
            var averageEntityBits = new uint[numClients];
            TestRunner(numClients, numObjectsPerPrefab, numPrefabs, initialDataSize, initialAvgBitsPerEntity, averageEntityBits, false);
            var initialDataSizeWithFallback = new uint[numClients];
            var initialAvgBitsPerEntityWithFallback = new uint[numClients];
            var averageEntityBitsWithFallback = new uint[numClients];
            TestRunner(numClients, numObjectsPerPrefab, numPrefabs, initialDataSizeWithFallback,
                initialAvgBitsPerEntityWithFallback, averageEntityBitsWithFallback, true);
            for (int i = 0; i < numClients; ++i)
            {
                Assert.LessOrEqual(initialDataSizeWithFallback[i], initialDataSize[i]);
                Assert.LessOrEqual(initialAvgBitsPerEntityWithFallback[i], initialAvgBitsPerEntity[i]);
                //The average initial size should be less or equals to the one without opt
                Assert.LessOrEqual(initialAvgBitsPerEntityWithFallback[i], averageEntityBits[i]);
                Assert.LessOrEqual(averageEntityBitsWithFallback[i], averageEntityBits[i]);
            }

        }

        [Test]
        public void Test_BaselineAreCreated()
        {
            //Set the scene with multiple prefab types
            const int numObjects = 10;
            var prefab1 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var prefab2 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithBuffer", typeof(GhostAuthoringComponent),
                typeof(SomeDataElementAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "LateJoinTest");
            SubSceneHelper.CreateSubSceneWithPrefabs(parentScene, ScenePath, "subscene", new[]
            {
                prefab1,
                prefab2
            }, numObjects);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                //Stream the sub scene in
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect();
                CheckPrespawnArePresent(numObjects*2, testWorld);
                CheckComponents(numObjects*2, testWorld);
                testWorld.GoInGame();
                //Run some another tick to retrieve and process the prefabs and initialize the baselines
                for(int i=0;i<2;++i)
                    testWorld.Tick();
                CheckBaselineAreCreated(testWorld.ServerWorld);
                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                {
                    CheckBaselineAreCreated(testWorld.ClientWorlds[i]);
                }
            }
        }

        /// <param name="keepSnapshotHistoryOnStructuralChange">
        /// Ensures all <see cref="GhostChunkSerializer.UpdateChunkHistory"/> nuances are accounted for.
        /// Also note: Adding a DynamicBuffer to this ghost ALSO forces <see cref="keepSnapshotHistoryOnStructuralChange"/> to false.
        /// </param>
        /// <param name="latencyProfile">Static-optimization should be tested under various conditions.</param>
        [Test(Description = "Tests only the common set of static-optimized, prespawn ghost replication cases.")]
        public unsafe void UsingStaticOptimizationServerDoesNotSendData([Values]bool keepSnapshotHistoryOnStructuralChange, [Values] NetCodeTestLatencyProfile latencyProfile)
        {
            const int numObjects = 10;
            //Set the scene with multiple prefab types
            var prefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            prefab.GetComponent<GhostAuthoringComponent>().OptimizationMode = GhostOptimizationMode.Static;
            PrefabUtility.SavePrefabAsset(prefab);

            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "LateJoinTest");
            SubSceneHelper.CreateSubSceneWithPrefabs(parentScene, ScenePath, "subscene", new[]
            {
                prefab,
            }, numObjects);

            using (var testWorld = new NetCodeTestWorld())
            {
                //Create a scene with a subscene and a bunch of objects in it
                testWorld.Bootstrap(true);
                testWorld.SetTestLatencyProfile(latencyProfile);
                testWorld.CreateWorlds(true, 1);
                testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW.KeepSnapshotHistoryOnStructuralChange = keepSnapshotHistoryOnStructuralChange;

                //Stream the sub scene in
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect(maxSteps:16);
                CheckPrespawnArePresent(numObjects, testWorld);
                testWorld.GoInGame();

                uint uncompressed = 0;
                uint totalDataReceived = 0;
                uint numReceived = 0;
                var recvGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ClientWorlds[0]);
                for (int tick = 0; tick < 16; ++tick)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;

                    //Skip the first ghost type (is be the subscene list)
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                testWorld.TryLogPacket("\nTEST-CASE: Expect NO snapshot updates, as prespawns 'waking up' (i.e. becoming enabled) doesn't require us to" +
                                       " send individual ghosts (as they wake up as a result of their sub-scene being acked, and their prespawns being mapped, which happens via RPC IIRC).");
                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, uncompressed);
                Assert.AreEqual(0, totalDataReceived);
                numReceived = 0;
                totalDataReceived = 0;
                uncompressed = 0;

                var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                var serverGhosts = serverQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                var serverEntities = serverQuery.ToEntityArray(Allocator.Temp);
                var ghostCollectionEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostCollection)).GetSingletonEntity();
                Span<SomeData?> baselineSomeDataValues = stackalloc SomeData?[numObjects];
                for (int i = 0; i < numObjects; ++i)
                    baselineSomeDataValues[i] = testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntities[i]);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After prespawns enable themselves.");

                testWorld.TryLogPacket("\nTEST-CASE: Create a FALSE POSITIVE write, to test out the zero change optimization for prespawn baselines:\n");
                {
                    var data = testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntities[5]);
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEntities[5], data);
                }
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;

                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, uncompressed);
                Assert.AreEqual(0, totalDataReceived);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After FALSE-POSITIVE write.");

                testWorld.TryLogPacket("\nTEST-CASE: Make a structural change and verify that entities are STILL not sent (no changes in respect to the 0 baselines)\n");
                for (int i = 8; i < 10; ++i)
                {
                    //I will add a tag. This should cause changes on the server side but NOT on the client, that still see the entities
                    //as unchanged
                    testWorld.ServerWorld.EntityManager.AddComponent<ServerOnlyTag>(serverEntities[i]);
                }

                //We have now two chunks with like this
                // Chunk 1    Entities
                //              0 1 2 3 4 5 6 7
                // changed:     n n n n n n n n
                // Chunk 2:
                //              8 9
                // changed:     n n

                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();

                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After 8,9 changed chunk");
                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, uncompressed);
                Assert.AreEqual(0, totalDataReceived);

                testWorld.TryLogPacket("\nTEST-CASE: ACTUALLY change some components for entities 0,1,2\n");
                for (int i = 0; i < numObjects; ++i)
                {
                    var data = testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntities[i]);
                    if (i < 3)
                    {
                        data.Value += 100;
                        testWorld.ServerWorld.EntityManager.SetComponentData(serverEntities[i], data);
                    }
                }

                // The change is sent on this tick:
                for (int i = 0; i < 32; ++i)
                {
                    testWorld.Tick();

                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                //Even if I change 3 entities, I still receive the full chunk (8 now) delta compressed, but only once.
                if(latencyProfile == NetCodeTestLatencyProfile.None)
                    Assert.AreEqual(8, numReceived);
                else Assert.IsTrue(numReceived >= 8 && numReceived % 8 == 0, $"numReceived:{numReceived}");
                Assert.AreNotEqual(0, totalDataReceived);
                Assert.AreEqual(0, uncompressed);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After SomeData change on 0,1,2");

                {
                    var ghostCollection = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionEntity);
                    //Check that the change masks for the other entities are still 0
                    for (int i = 3; i < 8; ++i)
                    {
                        var ghost = new SpawnedGhost {ghostId = serverGhosts[i].ghostId, spawnTick = serverGhosts[i].spawnTick};
                        var ent = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value[ghost];
                        var snapshotData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SnapshotData>(ent);
                        var snapshotBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<SnapshotDataBuffer>(ent);
                        var ghostType = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(ent).ghostType;
                        var typeData = ghostCollection[ghostType];
                        int snapshotSize = typeData.SnapshotSize;
                        unsafe
                        {
                            byte* snapshotPtr = (byte*) snapshotBuffer.GetUnsafeReadOnlyPtr();
                            snapshotPtr += snapshotSize * snapshotData.LatestIndex;
                            int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                            uint* changeMask = (uint*) (snapshotPtr + 4);

                            for (int cm = 0; cm < changeMaskUints; ++cm)
                                Assert.AreEqual(0, changeMask[cm]);
                        }
                    }
                }
                //Entities 8,9 are still not received
                for (int i = 8; i < 10; ++i)
                {
                    var ghost = new SpawnedGhost {ghostId = serverGhosts[i].ghostId, spawnTick = serverGhosts[i].spawnTick};
                    var ent = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value[ghost];
                    var ghostType = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(ent).ghostType;
                    Assert.AreEqual(-1, ghostType);
                }

                testWorld.TryLogPacket("\nTEST-CASE: From here on I should NOT receive any ghosts again (since they're zero-change, as the zero-change has been acked)\n");
                numReceived = 0;
                totalDataReceived = 0;
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();

                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }

                    Assert.AreEqual(0, numReceived);
                    Assert.AreEqual(0, uncompressed);
                    Assert.AreEqual(0, totalDataReceived);
                }

                testWorld.TryLogPacket("\nTEST-CASE: Now make a structural change WITHOUT any GhostField changes,\n");
                // and verify that entities are NOT sent again (as we're still ZeroChange in respect to GhostField
                // data vs its baseline) UNLESS we don't correctly copy over said data (via `keepSnapshotHistoryOnStructuralChange:false`).
                for (int i = 3; i < 6; ++i)
                {
                    testWorld.ServerWorld.EntityManager.AddComponent<ServerOnlyTag>(serverEntities[i]);
                }

                //We have now two chunks with like this
                // Chunk 1    Entitites
                //              0 1 2 6 7
                // changed:     y y y n n
                // Chunk 2:
                //              3 4 5 8 9
                // changed:     n n n n n
                //
                // Will will not receive the 2nd chunk, even though the 3,4,5 version has been changed since they were in the first chunk.
                // Since we detect that all change are actually zero, and we are using all fallback baseline and nothing has changed
                numReceived = 0;
                totalDataReceived = 0;
                uncompressed = 0;
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick();

                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                if (keepSnapshotHistoryOnStructuralChange)
                {
                    Assert.AreEqual(0, numReceived);
                    Assert.AreEqual(0, uncompressed);
                    Assert.AreEqual(0, totalDataReceived);
                }
                else if (latencyProfile == NetCodeTestLatencyProfile.None)
                {
                    Assert.AreEqual(5, numReceived);
                    Assert.AreEqual(0, uncompressed);
                    Assert.AreNotEqual(0, totalDataReceived);
                }
                else
                {
                    Assert.IsTrue(numReceived >= 5 && numReceived % 5 == 0, $"numReceived:{numReceived}");
                    Assert.AreEqual(0, uncompressed);
                    Assert.AreNotEqual(0, totalDataReceived);
                }

                //Still entities 8,9 are not received yet
                for (int i = 8; i < 10; ++i)
                {
                    var ghost = new SpawnedGhost {ghostId = serverGhosts[i].ghostId, spawnTick = serverGhosts[i].spawnTick};
                    var ent = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value[ghost];
                    var ghostType = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(ent).ghostType;
                    Assert.AreEqual(-1, ghostType);
                }

                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After 3,4,5 changed chunk.");

                testWorld.TryLogPacket("\nTEST-CASE: Change 3,4 in the second chunk:\n");
                for (int i = 3; i < 5; ++i)
                {
                    var data = testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntities[i]);
                    data.Value += 100;
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEntities[i], data);
                }
                numReceived = 0;
                totalDataReceived = 0;
                uncompressed = 0;
                // Again, this change will be sent on the next tick, so expect it:
                for(int i = 0; i < 8; i++)
                {
                    testWorld.Tick();

                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }
                // Chunk 1 contains 5 entities:
                if(latencyProfile == NetCodeTestLatencyProfile.None)
                    Assert.AreEqual(5, numReceived);
                else Assert.IsTrue(numReceived >= 5 && numReceived % 5 == 0, $"numReceived:{numReceived}");
                Assert.AreNotEqual(0, totalDataReceived);
                Assert.AreEqual(0, uncompressed);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After 3,4 updated GhostField data.");

                numReceived = 0;
                totalDataReceived = 0;
                uncompressed = 0;

                testWorld.TryLogPacket("\nTEST-CASE: Expect no changes now.\n");
                for (int tick = 0; tick < 8; tick++)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, totalDataReceived);
                Assert.AreEqual(0, uncompressed);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After 3,4 update arrives - expect no more updates.");

                testWorld.TryLogPacket("\nTEST-CASE: EXTREMELY esoteric: Prespawn 3 is currently NOT matching their prespawn baseline, and NOT in their prespawn chunk.");
                testWorld.TryLogPacket("If we move prespawn 3 BACK to their prespawn chunk, AND revert their GhostField changes, will the GhostChunkSerializer understand that it needs to send said change?\n");
                testWorld.ServerWorld.EntityManager.RemoveComponent<ServerOnlyTag>(serverEntities[3]);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntities[3], baselineSomeDataValues[3].Value);

                numReceived = 0;
                totalDataReceived = 0;
                uncompressed = 0;
                for (int tick = 0; tick < 8; tick++)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After returning changed ghosts to their original chunk, and matching their GhostField data back to the prespawn values.");
                if(latencyProfile == NetCodeTestLatencyProfile.None)
                    Assert.AreEqual(6, numReceived);
                else Assert.IsTrue(numReceived >= 6 && numReceived % 6 == 0, $"numReceived:{numReceived}");
                Assert.AreEqual(0, uncompressed);
                Assert.AreNotEqual(0, totalDataReceived);

                testWorld.TryLogPacket("\nTEST-CASE: Again expect no changes.\n");
                numReceived = 0;
                uncompressed = 0;
                totalDataReceived = 0;
                for (int tick = 0; tick < 8; tick++)
                    testWorld.Tick();
                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, uncompressed);
                Assert.AreEqual(0, totalDataReceived);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "Expect no more changes.");

                testWorld.TryLogPacket("\nTEST-CASE: Revert all other SomeData back to their pre-spawn values, ensure it works:\n");
                for (int i = 0; i < numObjects; i++)
                {
                    testWorld.ServerWorld.EntityManager.RemoveComponent<ServerOnlyTag>(serverEntities[i]);
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEntities[i], baselineSomeDataValues[i].Value);
                }
                for (int tick = 0; tick < 8; tick++)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }
                if(latencyProfile == NetCodeTestLatencyProfile.None)
                    Assert.AreEqual(10, numReceived);
                else Assert.IsTrue(numReceived >= 10 && numReceived % 10 == 0, $"numReceived:{numReceived}");
                Assert.AreEqual(0, uncompressed);
                Assert.AreNotEqual(0, totalDataReceived);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After returning changed ghosts to their original is completed - expect no more changes.");

                testWorld.TryLogPacket("\nTEST-CASE: Again, expect no more changes:\n");
                numReceived = 0;
                uncompressed = 0;
                totalDataReceived = 0;
                for (int tick = 0; tick < 8; tick++)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }
                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, uncompressed);
                Assert.AreEqual(0, totalDataReceived);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "Final expect no changes.");
            }
        }

        private void VerifyReplicatedValues(int numObjects, NetCodeTestWorld testWorld, NativeArray<Entity> serverEntities, Entity recvGhostMapSingleton, string context)
        {
            string s = context;
            testWorld.TryLogPacket($"\n\nTEST-VerifyReplicatedValues:{context}\n");
            for (int i = 0; i < numObjects; ++i)
            {
                var serverEntity = serverEntities[i];
                var serverTrans = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEntity);
                var serverSomeData = testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntity);
                var serverGhost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntity);
                var clientEntity = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value[serverGhost];
                var clientTrans = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEntity);
                var clientSomeData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SomeData>(clientEntity);
                s += $"\n\t[{i}] GID:{serverGhost.ghostId}\n\tServer[{serverEntity.ToString()} in chunk:{testWorld.ServerWorld.EntityManager.GetChunk(serverEntity).SequenceNumber}, LocalTransform({serverTrans.ToString()}), SomeData:{serverSomeData.Value}]\n\tClient[{clientEntity.ToString()} in chunk:{testWorld.ClientWorlds[0].EntityManager.GetChunk(clientEntity).SequenceNumber}, LocalTransform({clientTrans.ToString()}), SomeData:{clientSomeData.Value}]";
                ApproximatelyEqual(serverTrans.Position, clientTrans.Position, $"[{i}]LocalTransform.Position {context} GID:{serverGhost.ghostId}", 0.0001f);
                ApproximatelyEqual(math.Euler(serverTrans.Rotation), math.Euler(clientTrans.Rotation), $"math.Euler([{i}]LocalTransform.Rotation) {context} GID:{serverGhost.ghostId}", 0.04f);
                ApproximatelyEqual(serverTrans.Scale, clientTrans.Scale, $"[{i}]LocalTransform.Scale {context} GID:{serverGhost.ghostId}", 0.0001f);
                Assert.AreEqual(serverSomeData.Value, clientSomeData.Value, $"[{i}].SomeData.Value {context} GID:{serverGhost.ghostId}");
            }
            Debug.Log(s);
        }

        private void ApproximatelyEqual(float3 server, float3 client, string context, float tolerance)
        {
            var delta = server - client;
            var deltaUnits = math.length(delta);
            Assert.IsTrue(deltaUnits <= tolerance, $"{context}\nserver:{server} - client:{client} = {delta}\n{deltaUnits} <= {tolerance}");
        }
    }
}

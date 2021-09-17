using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using NUnit.Framework.Internal;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.PrespawnTests;
using UnityEditor;
using UnityEngine;
using Unity.Scenes;
using Unity.Transforms;
using UnityEngine.SceneManagement;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostCollectionSystem))]
    public partial class LoadingGhostCollectionSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var collectionEntity = GetSingletonEntity<GhostCollection>();
            var ghostCollection = EntityManager.GetBuffer<GhostCollectionPrefab>(collectionEntity);
            var subScenes = GetEntityQuery(ComponentType.ReadOnly<SubSceneWithPrespawnGhosts>()).ToEntityArray(Allocator.Temp);
            var anyLoaded = false;
            var sceneSystem = World.GetExistingSystem<SceneSystem>();
            for (int i = 0; i < subScenes.Length; ++i)
                anyLoaded |= sceneSystem.IsSceneLoaded(subScenes[i]);
            for (int g = 0; g < ghostCollection.Length; ++g)
            {
                var ghost = ghostCollection[g];
                if (ghost.GhostPrefab == Entity.Null && !anyLoaded)
                {
                    ghost.Loading = GhostCollectionPrefab.LoadingState.LoadingActive;
                    ghostCollection[g] = ghost;
                }
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    public partial class UpdatePrespawnGhostTransform : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate(EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubScenePrespawnBaselineResolved>()));
        }

        protected override void OnUpdate()
        {
            float deltaTime = Time.DeltaTime;
            Entities
                .WithAll<PreSpawnedGhostIndex>()
                .ForEach((ref Translation translation) =>
                {
                    translation.Value = new float3(translation.Value.x, translation.Value.y + deltaTime*60.0f, translation.Value.z);
                }).Schedule();
        }
    }

    static class SubSceneStreamingTestHelper
    {
        static public DynamicBuffer<PrespawnSceneLoaded> GetPrespawnLoaded(in NetCodeTestWorld testWorld, World world)
        {
            var collection = testWorld.TryGetSingletonEntity<PrespawnSceneLoaded>(world);
            Assert.AreNotEqual(Entity.Null, collection, "The PrespawnLoaded entity does not exist");
            return world.EntityManager.GetBuffer<PrespawnSceneLoaded>(collection);
        }
    }
    public partial class SubSceneLoadingTests
    {
        private string ScenePath = "Assets/TestScenes";
        private DateTime LastWriteTime;


        [SetUp]
        public void SetupScene()
        {
            if (!Directory.Exists(ScenePath))
                Directory.CreateDirectory(ScenePath);
            Directory.CreateDirectory(ScenePath);
            LastWriteTime = Directory.GetLastWriteTime(Application.dataPath + ScenePath);
        }

        [TearDown]
        public void DestroyScenes()
        {
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                UnityEngine.Object.DestroyImmediate(go);

            AssetDatabase.DeleteAsset(ScenePath);
            var currentCache = Directory.GetFiles(Application.dataPath + "/SceneDependencyCache");
            foreach (var file in currentCache)
            {
                if (File.GetCreationTime(file) > LastWriteTime)
                    File.Delete(file);
            }
        }

        [Test]
        public void SubSceneListIsSentToClient()
        {
            //Set the scene with multiple prefab types
            const int numObjects = 10;
            var prefab1 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var prefab2 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData2", typeof(GhostAuthoringComponent),
                typeof(SomeDataElementAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "LateJoinTest");
            var subScene = SubSceneHelper.CreateSubSceneWithPrefabs(ScenePath, "subscene", new[]
            {
                prefab1,
                prefab2
            }, numObjects);
            SubSceneHelper.AddSubSceneToParentScene(parentScene, subScene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                //Stream the sub scene in
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                float frameTime = 1.0f / 60.0f;
                Assert.IsTrue(testWorld.Connect(frameTime, 4));
                testWorld.GoInGame();
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType
                    .ReadOnly<PrespawnsSceneInitialized>());
                Assert.IsTrue(query.IsEmptyIgnoreFilter);
                //First tick
                // - the Populate prespawn should run and add the ghosts to the mapping on the server.
                // - the scene list is populated
                testWorld.Tick(frameTime);
                query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubScenePrespawnBaselineResolved>());
                Assert.IsFalse(query.IsEmptyIgnoreFilter);
                //On the client we should received the prefabs. But prespawn asn subscenes are not initialized now (next frame)
                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubScenePrespawnBaselineResolved>());
                Assert.IsTrue(query.IsEmptyIgnoreFilter);
                //Second tick: server will send the subscene list ghost
                testWorld.Tick(frameTime);
                query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrespawnsSceneInitialized>());
                Assert.IsFalse(query.IsEmptyIgnoreFilter);
                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubScenePrespawnBaselineResolved>());
                Assert.IsFalse(query.IsEmptyIgnoreFilter);
                //Third tick: prespawn ghost start streaming
                for (int i = 0; i < 10; ++i)
                {
                    testWorld.Tick(frameTime);
                    var collection = testWorld.TryGetSingletonEntity<PrespawnSceneLoaded>(testWorld.ClientWorlds[0]);
                    if(collection != Entity.Null)
                        break;
                }
                var prespawnLoaded = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ClientWorlds[0]);
                Assert.AreEqual(1, prespawnLoaded.Length);

                //Need one more tick now to have the ghost map updated
                testWorld.Tick(frameTime);

                var sendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                sendSystem.LastGhostMapWriter.Complete();
                Assert.AreEqual(21, sendSystem.SpawnedGhostEntityMap.Count());
                var receiveSystem = testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>();
                receiveSystem.LastGhostMapWriter.Complete();
                Assert.AreEqual(21, receiveSystem.GhostEntityMap.Count());
                Assert.AreEqual(21, receiveSystem.SpawnedGhostEntityMap.Count());
                //Check that they are identically mapped.
                foreach (var kv in sendSystem.SpawnedGhostEntityMap)
                {

                    var ghost = kv.Key;
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghost.ghostId))
                        continue;
                    var serverPrespawnId = testWorld.ServerWorld.EntityManager.GetComponentData<PreSpawnedGhostIndex>(kv.Value);
                    Assert.AreEqual(PrespawnHelper.MakePrespawGhostId(serverPrespawnId.Value + 1), ghost.ghostId);
                    var clientGhost = receiveSystem.SpawnedGhostEntityMap[ghost];
                    var clientPrespawnId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<PreSpawnedGhostIndex>(clientGhost);
                    Assert.AreEqual(PrespawnHelper.MakePrespawGhostId(clientPrespawnId.Value + 1), ghost.ghostId);
                    Assert.AreEqual(serverPrespawnId.Value, clientPrespawnId.Value);
                }
            }
        }

        struct SetSomeDataJob : IJobChunk
        {
            public ComponentTypeHandle<SomeData> someDataHandle;
            public int offset;

            public void Execute(ArchetypeChunk batchInChunk, int chunkIndex, int firstEntityIndex)
            {
                var array = batchInChunk.GetNativeArray(someDataHandle);
                for (int i = 0; i < batchInChunk.Count; ++i)
                {
                    array[i] = new SomeData {Value = offset + firstEntityIndex + i};
                }
            }
        }

        [Test]
        public void ClientLoadSceneWhileInGame()
        {
            //The test is composed by two subscene.
            //The server load both scenes before having clients in game.
            //The client will load only the first one and then the second one after a bit

            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var sub0 = SubSceneHelper.AddSubSceneToParentScene(parentScene, SubSceneHelper.CreateSubSceneWithPrefabs(
                ScenePath, "Sub0", new[]
                {
                    ghostPrefab,
                }, numObjects));
            var sub1 = SubSceneHelper.AddSubSceneToParentScene(parentScene, SubSceneHelper.CreateSubSceneWithPrefabs(
                ScenePath, "sub1", new[]
                {
                    ghostPrefab,
                }, numObjects, 5.0f));

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                //Stream the sub scene in
                SubSceneHelper.LoadSubScene(testWorld.ServerWorld, sub0, sub1);
                SubSceneHelper.LoadSubScene(testWorld.ClientWorlds[0], sub0);
                float frameTime = 1.0f / 60.0f;
                Assert.IsTrue(testWorld.Connect(frameTime, 4));
                testWorld.GoInGame();
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(frameTime);
                }

                var q = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<SomeData>());
                Assert.IsFalse(q.IsEmptyIgnoreFilter);
                Assert.AreEqual(5, q.CalculateEntityCount());

                //Modify some data on the server
                q = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType
                    .ReadOnly<SubSceneWithPrespawnGhosts>());
                var subsceneList = q.ToComponentDataArray<SubSceneWithPrespawnGhosts>(Allocator.Temp);
                for (int i = 0; i < subsceneList.Length; ++i)
                {
                    q = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                        ComponentType.ReadWrite<SomeData>(), ComponentType.ReadOnly<SubSceneGhostComponentHash>());
                    q.SetSharedComponentFilter(new SubSceneGhostComponentHash
                    {
                        Value = subsceneList[i].SubSceneHash
                    });
                    var chunks = q.GetArchetypeChunkIterator();
                    var job = new SetSomeDataJob
                    {
                        someDataHandle = testWorld.ServerWorld.EntityManager.GetComponentTypeHandle<SomeData>(false),
                        offset = 100 + i * 100
                    };
                    job.RunWithoutJobs(ref chunks);
                }

                SubSceneHelper.LoadSubScene(testWorld.ClientWorlds[0], sub1);
                //Run some frame.
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(frameTime);
                }

                q = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                Assert.AreEqual(10, q.CalculateEntityCount());

                //Check everything is in sync
                for (int i = 0; i < subsceneList.Length; ++i)
                {
                    q = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                        ComponentType.ReadWrite<SomeData>(), ComponentType.ReadOnly<SubSceneGhostComponentHash>());
                    q.SetSharedComponentFilter(new SubSceneGhostComponentHash
                    {
                        Value = subsceneList[i].SubSceneHash
                    });
                    var data = q.ToComponentDataArray<SomeData>(Allocator.Temp);
                    for (int d = 0; d < numObjects; ++d)
                    {
                        Assert.AreEqual(100 + 100 * i + d, data[d].Value);
                    }

                    data.Dispose();
                }
            }
        }

        [Test]
        public void ServerAndClientsLoadSceneInGame()
        {
            //The test is composed by one scene.
            //The server and client starts without scene loaded.
            //The server initiate the load first
            //The client will then follow and load the scene as well.
            //Ghost should be synched

            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var sub0 = SubSceneHelper.AddSubSceneToParentScene(parentScene, SubSceneHelper.CreateSubSceneWithPrefabs(
                ScenePath, "Sub0", new[]
                {
                    ghostPrefab,
                }, numObjects));
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(LoadingGhostCollectionSystem));
                testWorld.CreateWorlds(true, 1);
                //Just create the scene entities proxies but not load any content
                SubSceneHelper.ResolveScenes(testWorld, 1.0f/60.0f, 100, sub0);
                float frameTime = 1.0f / 60.0f;
                Assert.IsTrue(testWorld.Connect(frameTime, 4));
                testWorld.GoInGame();
                //Run some frames, nothing should be synched or sent here
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<PrespawnSceneLoaded>(testWorld.ServerWorld));
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<PrespawnSceneLoaded>(testWorld.ClientWorlds[0]));
                //Server will load first. Wait some frame
                SubSceneHelper.LoadSubSceneAsync(testWorld.ServerWorld, testWorld, sub0.SceneGUID, frameTime, 128);
                //No subscene are ready on the client
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrespawnsSceneInitialized>()).IsEmpty);
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubScenePrespawnBaselineResolved>()).IsEmpty);
                //Run some frames, so the ghost scene list is synchronized
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);
                var subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ClientWorlds[0]);
                Assert.AreEqual(1, subSceneList.Length);
                //Client load the scene now
                SubSceneHelper.LoadSubSceneAsync(testWorld.ClientWorlds[0], testWorld, sub0.SceneGUID, frameTime, 128);
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);
                //Modify the data on the server
                {
                    var q = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PreSpawnedGhostIndex>(), ComponentType.ReadWrite<SomeData>());
                    var chunks = q.GetArchetypeChunkIterator();
                    var job = new SetSomeDataJob
                    {
                        someDataHandle = testWorld.ServerWorld.EntityManager.GetComponentTypeHandle<SomeData>(false),
                        offset = 100
                    };
                    job.RunWithoutJobs(ref chunks);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                {
                    //Check everything is in sync
                    var q = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PreSpawnedGhostIndex>(), ComponentType.ReadWrite<SomeData>());
                    var data = q.ToComponentDataArray<SomeData>(Allocator.Temp);
                    for (int i = 0; i < numObjects; ++i)
                    {
                        Assert.AreEqual(100 + i, data[i].Value);
                    }
                    data.Dispose();
                }
            }
        }

        [Test]
        public void ServerInitiatedSceneUnload()
        {
            Dictionary<ulong, uint2> GetIdsRanges(World world, in DynamicBuffer<PrespawnSceneLoaded> subSceneList)
            {
                //Get all the ids and collect the ranges from the ghost components. They are going to be used later
                //for checking ids re-use
                var ranges = new Dictionary<ulong, uint2>();
                for (int i = 0; i < subSceneList.Length; ++i)
                {
                    var q = world.EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<GhostComponent>(),
                        ComponentType.ReadOnly<SubSceneGhostComponentHash>());
                    q.SetSharedComponentFilter(new SubSceneGhostComponentHash
                    {
                        Value = subSceneList[i].SubSceneHash
                    });
                    var ghostComponents = q.ToComponentDataArray<GhostComponent>(Allocator.Temp);
                    var range = new uint2(uint.MaxValue, uint.MinValue);
                    for (int k = 0; k < ghostComponents.Length; ++k)
                    {
                        range.x = math.min(range.x, (uint)ghostComponents[k].ghostId);
                        range.y = math.max(range.y, (uint)ghostComponents[k].ghostId);
                    }
                    ranges.Add(subSceneList[i].SubSceneHash, range);
                    ghostComponents.Dispose();
                }

                return ranges;
            }

            //The test is composed by two scene.
            //The server and client starts with both scene loaded.
            //The server will unload one scene
            //The client will then follow (after a bit) and unload the scene as well.
            //The server and the client will then reload the scene again
            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var sub0 = SubSceneHelper.AddSubSceneToParentScene(parentScene, SubSceneHelper.CreateSubSceneWithPrefabs(
                ScenePath, "Sub0", new[]
                {
                    ghostPrefab,
                }, numObjects));
            SubSceneHelper.AddSubSceneToParentScene(parentScene, SubSceneHelper.CreateSubSceneWithPrefabs(
                ScenePath, "Sub1", new[]
                {
                    ghostPrefab,
                }, numObjects));
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                float frameTime = 1.0f / 60.0f;
                Assert.IsTrue(testWorld.Connect(frameTime, 4));
                testWorld.GoInGame();
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(frameTime);
                }

                var subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ServerWorld);
                var idsRanges = GetIdsRanges(testWorld.ServerWorld, subSceneList);
                //Server will unload the first scene. This will despawn ghosts and also update the scene list
                var sceneSystem = testWorld.ServerWorld.GetExistingSystem<SceneSystem>();
                sceneSystem.UnloadScene(sub0.SceneGUID,
                    SceneSystem.UnloadParameters.DestroySceneProxyEntity|
                    SceneSystem.UnloadParameters.DestroySectionProxyEntities|
                    SceneSystem.UnloadParameters.DestroySubSceneProxyEntities);
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(frameTime);
                }
                //Scene list should be 1 now
                subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ServerWorld);
                Assert.AreEqual(1, subSceneList.Length);
                subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ClientWorlds[0]);
                Assert.AreEqual(1, subSceneList.Length);
                //Only 5 ghost should be present on both
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                Assert.AreEqual(numObjects, query.CalculateEntityCount());
                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                Assert.AreEqual(numObjects, query.CalculateEntityCount());
                //Unload the scene on the client too
                sceneSystem = testWorld.ClientWorlds[0].GetExistingSystem<SceneSystem>();
                sceneSystem.UnloadScene(sub0.SceneGUID,
                    SceneSystem.UnloadParameters.DestroySceneProxyEntity|
                    SceneSystem.UnloadParameters.DestroySectionProxyEntities|
                    SceneSystem.UnloadParameters.DestroySubSceneProxyEntities);
                //And nothing should break
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(frameTime);
                }
                //Then re-load the scene. The ids should be reused and everything should be in sync again
                SubSceneHelper.LoadSubScene(testWorld.ServerWorld, sub0);
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(frameTime);
                }
                subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ServerWorld);
                Assert.AreEqual(2, subSceneList.Length);
                subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ClientWorlds[0]);
                Assert.AreEqual(2, subSceneList.Length);
                //Check that the assigned id for the sub0 are the same as before
                var newRanges = GetIdsRanges(testWorld.ServerWorld, subSceneList);
                for (int i = 0; i < subSceneList.Length; ++i)
                    Assert.AreEqual(idsRanges[subSceneList[i].SubSceneHash], newRanges[subSceneList[i].SubSceneHash]);
                SubSceneHelper.LoadSubScene(testWorld.ClientWorlds[0], sub0);
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(frameTime);
                }
            }
        }

        [Test]
        public void ClientLoadUnloadScene()
        {
            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var subScenes = new SubScene[4];
            for(int i=0;i<4;++i)
            {
                subScenes[i] = SubSceneHelper.AddSubSceneToParentScene(parentScene, SubSceneHelper.CreateSubSceneWithPrefabs(
                    ScenePath, $"Sub{i}", new[]
                    {
                        ghostPrefab,
                    }, numObjects)
                );
            }
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(LoadingGhostCollectionSystem), typeof(UpdatePrespawnGhostTransform));
                testWorld.CreateWorlds(true, 1);
                float frameTime = 1.0f / 60.0f;
                SubSceneHelper.LoadSubScene(testWorld.ServerWorld, subScenes);
                Assert.IsTrue(testWorld.Connect(frameTime, 4));
                //Here it is already required to have something that tell the client he need to load the prefabs
                testWorld.GoInGame();
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                var subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ServerWorld);
                Assert.AreEqual(4, subSceneList.Length);

                //Load/Unload all the scene 1 by 1
                for (int scene = 0; scene < 4; ++scene)
                {
                    //Client load the first scene
                    SubSceneHelper.LoadSubSceneAsync(testWorld.ClientWorlds[0], testWorld, subScenes[scene].SceneGUID, frameTime, 16);
                    //Run another bunch of frame to have the scene initialized
                    for (int i = 0; i < 4; ++i)
                        testWorld.Tick(frameTime);
                    var subSceneEntity = testWorld.TryGetSingletonEntity<PrespawnsSceneInitialized>(testWorld.ClientWorlds[0]);
                    Assert.AreNotEqual(Entity.Null, subSceneEntity);
                    //Only 5 ghost should be present
                    var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>(), ComponentType.ReadOnly<Translation>());
                    Assert.AreEqual(numObjects, query.CalculateEntityCount());

                    //Now I should receive the ghost with their state changed
                    for (int i = 0; i < 16; ++i)
                        testWorld.Tick(frameTime);

                    using var translations = query.ToComponentDataArrayAsync<Translation>(Allocator.TempJob, out var translationsJobHandle);
                    translationsJobHandle.Complete();
                    for (int i = 0; i < translations.Length; ++i)
                        Assert.AreNotEqual(0.0f, translations[i].Value);

                    //Unload the scene on the client
                    var sceneSystem = testWorld.ClientWorlds[0].GetExistingSystem<SceneSystem>();
                    sceneSystem.UnloadScene(subScenes[scene].SceneGUID);
                    for (int i = 0; i < 16; ++i)
                        testWorld.Tick(frameTime);
                    //0 ghost should be preset
                    query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                    Assert.AreEqual(0, query.CalculateEntityCount());
                }
            }
        }

        [Test]
        public void ClientReceiveDespawedGhostsWhenReloadingScene()
        {
            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "SimpleGhost", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var subScenes = new SubScene[2];
            for(int i=0;i<subScenes.Length;++i)
            {
                subScenes[i] = SubSceneHelper.AddSubSceneToParentScene(parentScene, SubSceneHelper.CreateSubSceneWithPrefabs(
                    ScenePath, $"Sub{i}", new[]
                    {
                        ghostPrefab,
                    }, numObjects)
                );
            }

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubScene(testWorld.ServerWorld, subScenes);
                SubSceneHelper.LoadSubScene(testWorld.ClientWorlds[0], subScenes[0]);

                testWorld.Connect(1.0f / 60f, 4);
                testWorld.GoInGame();

                //synch scene 0 but not scene 1
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(1.0f / 60.0f);

                testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>().LastGhostMapWriter.Complete();
                var spawnMap = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>().SpawnedGhostEntityMap;
                //Host despawn 2 ghost in scene 0 and 2 ghost in scene 1
                var despawnedGhosts = new[]
                {
                    new SpawnedGhost
                    {
                        ghostId = PrespawnHelper.MakePrespawGhostId(1),
                        spawnTick = 0
                    },
                    new SpawnedGhost
                    {
                        ghostId = PrespawnHelper.MakePrespawGhostId(4),
                        spawnTick = 0
                    },
                    new SpawnedGhost
                    {
                        ghostId = PrespawnHelper.MakePrespawGhostId(8),
                        spawnTick = 0
                    },
                    new SpawnedGhost
                    {
                        ghostId = PrespawnHelper.MakePrespawGhostId(9),
                        spawnTick = 0
                    },
                };

                //Swap the element in the list to match the query order
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SceneSectionData>());
                var sceneSectionDatas = query.ToComponentDataArray<SceneSectionData>(Allocator.Temp);
                if (sceneSectionDatas[0].SceneGUID != subScenes[0].SceneGUID)
                {
                    var t1 = despawnedGhosts[0];
                    despawnedGhosts[0] = despawnedGhosts[2];
                    despawnedGhosts[2] = t1;
                    t1 = despawnedGhosts[1];
                    despawnedGhosts[1] = despawnedGhosts[3];
                    despawnedGhosts[3] = t1;
                }

                for(int i=0;i<despawnedGhosts.Length;++i)
                    testWorld.ServerWorld.EntityManager.DestroyEntity(spawnMap[despawnedGhosts[i]]);

                //Client should despawn the two ghosts in scene 0
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(1.0f / 60.0f);

                testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>().LastGhostMapWriter.Complete();
                var clientSpawnMap = testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>().SpawnedGhostEntityMap;
                //3 prespawn and 1 ghost for the list
                Assert.AreEqual(4, clientSpawnMap.Count());
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[0]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[1]));

                //Client load scene 2. Should receive the despawn
                SubSceneHelper.LoadSubSceneAsync(testWorld.ClientWorlds[0], testWorld, subScenes[1].SceneGUID, 1.0f/60.0f, 32);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(1.0f / 60.0f);

                testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>().LastGhostMapWriter.Complete();
                clientSpawnMap = testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>().SpawnedGhostEntityMap;
                //6 prespawn and 1 ghost for the list
                Assert.AreEqual(7, clientSpawnMap.Count());
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[0]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[1]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[2]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[3]));

                //Client unload scene 0 and then reload it later it should receive the despawns
                //Unload the scene on the client
                var sceneSystem = testWorld.ClientWorlds[0].GetExistingSystem<SceneSystem>();
                sceneSystem.UnloadScene(subScenes[0].SceneGUID);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(1.0f / 60.0f);

                testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>().LastGhostMapWriter.Complete();
                clientSpawnMap = testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>().SpawnedGhostEntityMap;
                //3 prespawn and 1 ghost for the list
                Assert.AreEqual(4, clientSpawnMap.Count());
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[2]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[3]));

                SubSceneHelper.LoadSubSceneAsync(testWorld.ClientWorlds[0], testWorld, subScenes[0].SceneGUID, 1.0f/60.0f, 32);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(1.0f / 60.0f);

                testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>().LastGhostMapWriter.Complete();
                clientSpawnMap = testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>().SpawnedGhostEntityMap;
                //6 prespawn and 1 ghost for the list
                Assert.AreEqual(7, clientSpawnMap.Count());
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[0]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[1]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[2]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[3]));
            }
        }
    }
}

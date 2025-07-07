#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.Tests;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Mathematics;
using Unity.Networking.Transport;
using UnityEngine.TestTools;

namespace Unity.NetCode.PrespawnTests
{
    internal struct EnableVerifyGhostIds : IComponentData
    {}

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class VerifyGhostIds : SystemBase
    {
        public int Matches = 0;
        public static int GhostsPerScene = 7;
        private EntityQuery _ghostComponentQuery;
        private EntityQuery _preSpawnedGhostIdsQuery;

        protected override void OnCreate()
        {
            RequireForUpdate<EnableVerifyGhostIds>();
            _ghostComponentQuery = GetEntityQuery(typeof(GhostInstance), typeof(PreSpawnedGhostIndex));
            _preSpawnedGhostIdsQuery = GetEntityQuery(typeof(GhostInstance), typeof(PreSpawnedGhostIndex));
        }

        protected override void OnUpdate()
        {
            var ghostComponents = _ghostComponentQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
            var preSpawnedGhostIds = _preSpawnedGhostIdsQuery.ToComponentDataArray<PreSpawnedGhostIndex>(Allocator.Temp);
            {
                Matches = 0;
                var idList = new List<int>();
                for (int i = 0; i < ghostComponents.Length; ++i)
                {
                    if (ghostComponents[i].ghostId != 0 && preSpawnedGhostIds.Length >= i)
                    {
                        // Since ghost IDs will get patched across multiple scenes they might not match exactly
                        var ghostId = (int)(ghostComponents[i].ghostId & ~PrespawnHelper.PrespawnGhostIdBase);
                        var diff = ghostId - preSpawnedGhostIds[i].Value - 1;
                        Assert.That(diff % GhostsPerScene == 0, "Prespawned ID not applied properly preID=" + preSpawnedGhostIds[i].Value + " ghostID=" + ghostId);
                        Matches++;
                        idList.Add(ghostId);
                    }
                }

                if (idList.Count == ghostComponents.Length)
                {
                    idList.Sort();
                    for (int i = 0; i < idList.Count - 1; ++i)
                    {
                        Assert.That(idList[i] == idList[i + 1] - 1,
                            "Ghost IDs not in incrementing order [i=" + idList[i] + " i+1=" + idList[i + 1] + "]");
                    }
                }
            }
        }
    }

    internal class PreSpawnTests : TestWithSceneAsset
    {
        void CheckAllPrefabsInWorlds(NetCodeTestWorld testWorld)
        {
            CheckAllPrefabsInWorld(testWorld.ServerWorld);
            for(int i=0;i<testWorld.ClientWorlds.Length;++i)
                CheckAllPrefabsInWorld(testWorld.ClientWorlds[i]);
        }

        void CheckAllPrefabsInWorld(World world)
        {
            //TODO: dispose these
            Assert.IsFalse(world.EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new [] {ComponentType.ReadOnly<PreSpawnedGhostIndex>()},
                    Options = EntityQueryOptions.IncludeDisabledEntities
                }).IsEmptyIgnoreFilter);
            Assert.IsFalse(world.EntityManager.CreateEntityQuery(
                new EntityQueryDesc
                {
                    All = new [] {ComponentType.ReadOnly<NetCodePrespawnTag>()},
                    Options = EntityQueryOptions.IncludeDisabledEntities
                }).IsEmptyIgnoreFilter);
            //Check that prefab that does not have the NetCodePrespawnTag does not have any PreSpawnedGhostId
            var query = world.EntityManager.CreateEntityQuery(
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<Prefab>(),
                        ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                    },
                    None = new []
                    {
                        ComponentType.ReadOnly<NetCodePrespawnTag>()
                    },
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
            Assert.IsTrue(query.IsEmptyIgnoreFilter);
        }

        // Checks that prefabs and runtime spawned ghosts don't get the prespawn id component applied to them
        [Test]
        public void PrespawnIdComponentDoesntLeaksToOtherEntitiesInScene()
        {
            var prefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "nonghost");
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(NetCodePrespawnAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            var subScene = SubSceneHelper.CreateSubScene(scene, Path.GetDirectoryName(scene.path), "Sub0", 5, 5, ghost, Vector3.zero);
            for (int i = 0; i < 10; ++i)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                SceneManager.MoveGameObjectToScene(go, scene);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scene.path);
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                CheckAllPrefabsInWorlds(testWorld);
            }
        }

        [Test]
        public void PrespawnIdComponentDoesntLeaksToOtherEntitiesInSubScene()
        {
            var prefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "nonghost");
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(NetCodePrespawnAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 5, 5, ghost,
                new Vector3(0f, 0f, 0f));
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub1", 5, 5, prefab,
                new Vector3(5f, 0f, 0f));
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                CheckAllPrefabsInWorlds(testWorld);
            }
        }

        [Test]
        public void WithNoPrespawnsScenesAreNotInitialized()
        {
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 0, 0, null, Vector3.zero);
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect();
                testWorld.GoInGame();
                // Give Prespawn ghosts processing a chance to run a bunch of times
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PrespawnsSceneInitialized));
                Assert.AreEqual(0, query.CalculateEntityCount());
            }
        }

        [Test]
        public void VerifyPreSpawnIDsAreApplied()
        {
            VerifyGhostIds.GhostsPerScene = 25;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 5, 5, ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0;i<64;++i)
                {
                    testWorld.Tick();
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                        break;
                }
                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            }
        }

        [Test]
        public void DestroyedPreSpawnedObjectsCleanup()
        {
            VerifyGhostIds.GhostsPerScene = 7;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 1, VerifyGhostIds.GhostsPerScene, ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 2);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect();
                //Set in game the first client
                testWorld.SetInGame(0);
                // Delete one prespawned entity on the server
                var deletedId = 0;
                var q = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                var prespawnedQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                    var prespawnedGhost = q.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                    // Filter for GhostComoponent and grab it after prespawn processing is done (ghost id valid)
                    if (prespawnedGhost.Length == 0 || (prespawnedGhost.Length > 0 && prespawnedGhost[0].ghostId == 0))
                    {
                        prespawnedGhost.Dispose();
                        continue;
                    }

                    deletedId = prespawnedGhost[0].ghostId;
                    var prespawned = prespawnedQuery.ToEntityArray(Allocator.Temp);
                    testWorld.ServerWorld.EntityManager.DestroyEntity(prespawned[0]);
                    prespawned.Dispose();
                    prespawnedGhost.Dispose();
                    break;
                }
                Assert.True(deletedId < 0);
                // Server now has one less prespawned ghosts since one was deleted, updated the count
                testWorld.SetInGame(1);
                // Check that the deleted entity has been cleaned up on the second client
                bool exists = false;
                int prespawnedCount = 0;
                var query = testWorld.ClientWorlds[1].EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new [] {ComponentType.ReadOnly<PreSpawnedGhostIndex>()},
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
                prespawnedCount = query.CalculateEntityCount();
                for (int i = 0; i < 128; ++i)
                {
                    testWorld.Tick();
                    exists = false;
                    var prespawnedData = query.ToComponentDataArray<PreSpawnedGhostIndex>(Allocator.Temp);
                    prespawnedCount = prespawnedData.Length;
                    for (int j = 0; j < prespawnedData.Length; ++j)
                    {
                        // Entity will be loaded from subscene data, wait until it goes missing after first ghost snapshot update
                        if (prespawnedData[j].Value == deletedId)
                            exists = true;
                    }
                    prespawnedData.Dispose();
                    if (!exists)
                        break;
                }

                Assert.True(prespawnedCount > 0);
                Assert.False(exists, "Found the prespawned entity which should be deleted");
            }
        }

        // Checking expected behaviour:
        // - 4 prespawns and 1 spawn created
        // - client connects and disconnects
        // - server retains that count, no cleanup (5)
        // - client cleans runtime spawns only (4), prespawns are a part of the subscene
        [Test]
        public void GhostCleanup()
        {
            VerifyGhostIds.GhostsPerScene = 7;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 1, VerifyGhostIds.GhostsPerScene,
                ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateGhostCollection(new GameObject("DynamicGhost"));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                // If servers spawns something before connection is in game it will be registered as a prespawned entity
                // Wait until prespawned ghosts have been initialized
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex),
                    typeof(GhostInstance));
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                    var prespawns = query.CalculateEntityCount();
                    if (prespawns > 0)
                        break;
                }

                // Spawn something
                testWorld.SpawnOnServer(0);
                var ghostCount = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                // Wait until it's spawned on client
                int currentCount = 0;
                var clientQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(PreSpawnedGhostIndex));
                for (int i = 0; i < 64 && currentCount != ghostCount; ++i)
                {
                    testWorld.Tick();
                    currentCount = clientQuery.CalculateEntityCount();
                }
                Assert.That(ghostCount == currentCount, "Client did not spawn runtime entity (clientCount=" + currentCount + " serverCount=" + ghostCount + ")");

                // Verify spawned entities to not contain the prespawn id component
                var prespawnCount = testWorld.ServerWorld.EntityManager
                    .CreateEntityQuery(typeof(PreSpawnedGhostIndex), typeof(GhostInstance)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawnCount, "Runtime spawned server entity got prespawn component added");
                prespawnCount = testWorld.ClientWorlds[0].EntityManager
                    .CreateEntityQuery(typeof(PreSpawnedGhostIndex), typeof(GhostInstance)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawnCount, "Runtime spawned client entity got prespawn component added");

                testWorld.ClientWorlds[0].EntityManager.AddComponent<NetworkStreamRequestDisconnect>(
                    testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).GetSingletonEntity());

                // Wait until ghosts have been cleaned up
                int serverGhostCount = 0;
                int clientGhostCount = 0;
                int expectedServerGhostCount = VerifyGhostIds.GhostsPerScene + 2; //Also the ghost list
                int expectedClientGhostCount = VerifyGhostIds.GhostsPerScene; //only the prespawn should remain
                var serverGhosts = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance));
                var clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                    // clientGhostCount will be 6 for a bit as it creates an initial archetype ghost and later a delayed one when on the right tick
                    serverGhostCount = serverGhosts.CalculateEntityCount();
                    clientGhostCount = clientGhosts.CalculateEntityCount();
                    //Debug.Log("serverCount=" + serverGhostCount + " clientCount=" + clientGhostCount);
                    //DumpGhosts(serverWorld, clientWorld);
                    if (serverGhostCount == expectedServerGhostCount && clientGhostCount == 0)
                        break;
                }
                Assert.That(serverGhostCount == expectedServerGhostCount, "Server ghosts not correct (count=" + serverGhostCount + " should be " + expectedServerGhostCount);
                Assert.That(clientGhostCount == expectedClientGhostCount, "Ghosts not cleaned up on client (count=" + clientGhostCount + " should be " + expectedClientGhostCount);
            }
        }

        [Test]
        public void MultipleSubscenes()
        {
            const int GhostScenes = 3;
            VerifyGhostIds.GhostsPerScene = 4;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            for (int i = 0; i < GhostScenes; ++i)
            {
                SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), $"Sub_{i}", 2, 2, ghost,
                    new Vector3(i*2.0f, 0.0f, 0.0f)); }
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0;i<64;++i)
                    testWorld.Tick();

                int prespawnedGhostCount = VerifyGhostIds.GhostsPerScene*GhostScenes;
                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(prespawnedGhostCount, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(prespawnedGhostCount, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(prespawnedGhostCount, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(prespawnedGhostCount, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            }
        }

        [Test]
        public void ManyPrespawnedObjects()
        {
            const int SubSceneCount = 10;
            const int GhostsPerScene = 500;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            for (int i = 0; i < 10; ++i)
            {
                SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), $"Sub_{i}", 10, 50, ghost,
                    new Vector3((i%5)*10f, 0.0f, (i/5)*50f));
            }
            SceneManager.SetActiveScene(scene);
            VerifyGhostIds.GhostsPerScene = GhostsPerScene;
            var prespawnedGhostCount = GhostsPerScene * SubSceneCount;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for (int i=0; i<64;++i)
                {
                    testWorld.Tick();
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                        break;
                }

                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(prespawnedGhostCount, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(prespawnedGhostCount, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(prespawnedGhostCount, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(prespawnedGhostCount, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");

                var clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance), ComponentType.ReadOnly<PreSpawnedGhostIndex>())
                    .ToComponentDataArray<GhostInstance>(Allocator.Temp);
                var clientGhostPos = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(LocalTransform), ComponentType.ReadOnly<PreSpawnedGhostIndex>())
                    .ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var serverGhosts = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), ComponentType.ReadOnly<PreSpawnedGhostIndex>())
                    .ToComponentDataArray<GhostInstance>(Allocator.Temp);
                var serverGhostPos = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(LocalTransform), ComponentType.ReadOnly<PreSpawnedGhostIndex>())
                    .ToComponentDataArray<LocalTransform>(Allocator.Temp);

                var serverPosLookup = new NativeParallelHashMap<int, (float3 pos, GhostInstance ghostInstance)>(serverGhostPos.Length, Allocator.Temp);
                Assert.AreEqual(clientGhostPos.Length, serverGhostPos.Length);
                // Fill a hashmap with mapping from server ghost id to server position
                for (int i = 0; i < serverGhosts.Length; ++i)
                {
                    serverPosLookup.Add(serverGhosts[i].ghostId, (serverGhostPos[i].Position, serverGhosts[i]));
                }
                for (int i = 0; i < clientGhosts.Length; ++i)
                {
                    Assert.IsTrue(PrespawnHelper.IsPrespawnGhostId(clientGhosts[i].ghostId), "Prespawned ghosts not initialized");
                    // Verify that the client ghost id exists on the server with the same position and same ghostType
                    Assert.IsTrue(serverPosLookup.TryGetValue(clientGhosts[i].ghostId, out var serverPos));
                    Assert.LessOrEqual(math.distance(clientGhostPos[i].Position, serverPos.pos), 0.001f);
                    Assert.AreEqual(clientGhosts[i].ghostType, serverPosLookup[clientGhosts[i].ghostId].ghostInstance.ghostType);

                    // Remove the server ghost id which we already matched against to make sure htere are no duplicates
                    serverPosLookup.Remove(clientGhosts[i].ghostId);
                }
                // Verify that there are no additional server entities
                Assert.AreEqual(0, serverPosLookup.Count());
            }
        }

        [Test]
        public void PrefabVariantAreHandledCorrectly()
        {
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var variant = SubSceneHelper.CreatePrefabVariant(ghost);
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubSceneWithPrefabs(scene,Path.GetDirectoryName(scene.path), "Sub0", new []{ghost, variant}, 5);
            SceneManager.SetActiveScene(scene);
            VerifyGhostIds.GhostsPerScene = 10;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0;i<64;++i)
                {
                    testWorld.Tick();
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                        break;
                }
                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            }
        }

        [Test]
        public void PrefabModelsAreHandledCorrectly()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.unity.netcode/Tests/Editor/Prespawn/Assets/Whitebox_Ground_1600x1600_A.prefab");
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.unity.netcode/Tests/Editor/Prespawn/Assets/Whitebox_Ground_1600x1600_A Variant.prefab");
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubSceneWithPrefabs(scene,Path.GetDirectoryName(scene.path), "Sub0", new []{prefab, variant}, 2);
            SceneManager.SetActiveScene(scene);
            VerifyGhostIds.GhostsPerScene = 4;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0;i<64;++i)
                {
                    testWorld.Tick();
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                        break;
                }
                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            }
        }

        [Test]
        public void MulitpleSubScenesWithSameObjectsPositionAreHandledCorrectly()
        {
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 1, 5, ghost, Vector3.zero);
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub1", 1, 5, ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);
            VerifyGhostIds.GhostsPerScene = 5;
            var totalPrespawned = 2 * VerifyGhostIds.GhostsPerScene;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0;i<64;++i)
                {
                    testWorld.Tick();
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                        break;
                }
                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(totalPrespawned, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(totalPrespawned, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(totalPrespawned, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(totalPrespawned, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            }
        }

        [Test]
        public void MismatchedPrespawnClientServerScenesCantConnect()
        {
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "Scene1");
            var subScene = SubSceneHelper.CreateSubScene(parentScene, Path.GetDirectoryName(parentScene.path), $"SubScene1", 10, 50, ghost,
                    Vector3.zero);
            SceneManager.SetActiveScene(parentScene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld, subScene);

                //Tamper some prespawn on the server or the client such that their data aren't the same.
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                    ComponentType.ReadOnly<Disabled>());
                var entities = query.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < 10; ++i)
                {
                    testWorld.ServerWorld.EntityManager.SetComponentData(entities[i],
                        LocalTransform.FromPosition(new float3(-10000, 10, 10 * i)));
                }
                entities.Dispose();

                testWorld.Connect();
                testWorld.GoInGame();

                // Only expect to get the error once, as we disconnect immediately after getting it.
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new Regex(@"Subscene (\w+) baseline mismatch."));
                for(int i=0;i<10;++i)
                    testWorld.Tick();

                // Verify connection is now disconnected
                var conQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                Assert.AreEqual(0, conQuery.CalculateEntityCount());
            }
        }

        [Test]
        public void ServerTick_AndMaxBaselineAge_WrapAroundDoesNotCauseIssue()
        {
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            const int ensureTwoChunksWorthOfEntities = TypeManager.MaximumChunkCapacity + 2;
            SubSceneHelper.CreateSubSceneWithPrefabs(scene, Path.GetDirectoryName(scene.path), "Sub0", new[] {ghost}, ensureTwoChunksWorthOfEntities);
            SceneManager.SetActiveScene(scene);
            VerifyGhostIds.GhostsPerScene = ensureTwoChunksWorthOfEntities;

            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(VerifyGhostIds));
            testWorld.CreateWorlds(true, 1);
            SubSceneHelper.LoadSubSceneInWorlds(testWorld);

            var networkTick = new NetworkTick((UInt32.MaxValue >> 1) - 32);
            testWorld.SetServerTick(networkTick);
            Debug.Log($"Set ServerWorld.NetworkTime.ServerTick:{networkTick.ToFixedString()} to test NetworkTick wraparound!");
            testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
            testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));

            // Spawn:
            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 32; ++i)
            {
                testWorld.Tick();
                if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                    testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                    break;
            }

            var serverPrespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
            Assert.AreEqual(VerifyGhostIds.GhostsPerScene, serverPrespawned, "Didn't find expected amount of prespawned entities in the server subscene");
            var clientPrespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
            Assert.AreEqual(VerifyGhostIds.GhostsPerScene, clientPrespawned, "Didn't find expected amount of prespawned entities in the client subscene");
            Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
            Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            Assert.AreEqual(testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick.TickValue, testWorld.GetNetworkTime(testWorld.ServerWorld).InterpolationTick.TickValue, "ServerTick is not equal to InterpolationTick on server world");

            // Modify some:
            const int numToModify = 10;
            var serverLocalTransQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(LocalTransform));
            Assert.IsTrue(serverLocalTransQuery.CalculateChunkCount() > 1, $"Sanity - At least 2 chunks! Capacity:{serverLocalTransQuery.ToArchetypeChunkArray(Allocator.Temp)[0].Capacity}");
            const float multiplierA = 1.11f;
            ModifySomeLocalTransforms(serverLocalTransQuery, multiplierA);

            // Ensure nothing goes wrong:
            for (int i = 0; i < 32; i++)
                testWorld.Tick();
            VerifyLocalTransforms(serverLocalTransQuery, multiplierA, "NetworkTick-WrapAround");

            // Test MaxBaselineAge is handled correctly by jumping the server by at least MaxBaselineAge WHILE modifying the prefab:
            ref var networkTime = ref testWorld.GetSingletonRW<NetworkTime>(testWorld.ServerWorld).ValueRW;
            var prevServerTick = networkTime.ServerTick;
            Assert.IsTrue(prevServerTick.TickIndexForValidTick < 1000, "Sanity! Did wrap around!");
            networkTime.ServerTick.Add(GhostSystemConstants.MaxBaselineAge + 1);
            Debug.Log($"Jumped ServerWorld.NetworkTime.ServerTick:{prevServerTick} >> {networkTime.ServerTick} (delta:{networkTime.ServerTick.TicksSince(prevServerTick)}) for MaxBaselineAge!");
            Assert.AreNotEqual(networkTime.ServerTick, prevServerTick, "Sanity!");
            Assert.IsTrue(serverLocalTransQuery.CalculateChunkCount() > 1, $"Sanity - At least 2 chunks! Capacity:{serverLocalTransQuery.ToArchetypeChunkArray(Allocator.Temp)[0].Capacity}");
            const float multiplierB = 5.3f;
            ModifySomeLocalTransforms(serverLocalTransQuery, multiplierB);

            // Ensure nothing goes wrong:
            for (int i = 0; i < 64; i++)
                testWorld.Tick();
#if NETCODE_DEBUG
            LogAssert.Expect(LogType.Warning, new Regex("MaxBaselineAge"));
            LogAssert.Expect(LogType.Warning, new Regex("MaxBaselineAge"));
#endif
            VerifyLocalTransforms(serverLocalTransQuery, multiplierB, "MaxBaselineAge");

            // Done! Local funcs:
            static void ModifySomeLocalTransforms(EntityQuery serverLocalTransQuery, float mul)
            {
                var localTransforms = serverLocalTransQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                Assert.IsTrue(localTransforms.Length > numToModify, "Sanity!");
                Assert.AreEqual(ensureTwoChunksWorthOfEntities, localTransforms.Length, "Sanity!");
                var rw = localTransforms.AsSpan();
                for (var i = 0; i < numToModify; i++)
                {
                    ref var trans = ref rw[i];
                    trans.Position = new float3(1, 2, i * mul);
                    trans.Rotation = quaternion.Euler(3, 4, i * mul);
                }
                serverLocalTransQuery.CopyFromComponentDataArray(localTransforms);
            }
            static void VerifyLocalTransforms(EntityQuery serverLocalTransQuery, float mul, string context)
            {
                var localTransforms = serverLocalTransQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var ro = localTransforms.AsReadOnlySpan();
                for (var i = 0; i < numToModify; i++)
                {
                    ref readonly var trans = ref ro[i];
                    //Debug.Log($"[A] mul:{mul} {i} = {trans.ToString()}");
                    Assert.AreEqual(new float3(1, 2, i * mul), trans.Position, context);
                    Assert.AreEqual(quaternion.Euler(3, 4, i * mul), trans.Rotation, context);
                }
                for (var i = numToModify; i < localTransforms.Length; i++)
                {
                    ref readonly var trans = ref ro[i];
                    //Debug.Log($"[B] mul:{mul} {i} = {trans.ToString()}");
                    Assert.AreNotEqual(new float3(1, 2, i * mul), trans.Position, context);
                    Assert.AreNotEqual(quaternion.Euler(3, 4, i * mul), trans.Rotation, context);
                }
            }
        }

        [Test]
        public void PrespawnsCanGetRelevantAgain()
        {
            int rows = 5;
            int columns = 2;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "Scene1");
            var subScene = SubSceneHelper.CreateSubScene(parentScene, Path.GetDirectoryName(parentScene.path), $"SubScene1", rows, columns, ghost,
                    Vector3.zero);
            SceneManager.SetActiveScene(parentScene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld, subScene);

                testWorld.Connect();
                testWorld.GoInGame();

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
                var relevancySet = ghostRelevancy.GhostRelevancySet;
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                var ghostComponents = query.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                for (int i = 0; i < ghostComponents.Length; ++i)
                {
                    var ghostId = ghostComponents[i].ghostId;
                    relevancySet.Add(new RelevantGhostForConnection(1, ghostId), 1);
                }

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // Verify all ghosts have despawned
                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                Assert.AreEqual(0, query.CalculateEntityCount());

                relevancySet.Clear();

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // Verify all ghosts have been spawned again
                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                //+1 because there is also the scene list
                Assert.AreEqual(rows*columns, query.CalculateEntityCount());
            }
        }

        [Test]
        public void PrespawnBasicSerialization()
        {
            int rows = 5;
            int columns = 2;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(NetCodePrespawnAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "Scene1");
            var subScene = SubSceneHelper.CreateSubScene(parentScene, Path.GetDirectoryName(parentScene.path), $"SubScene1", rows, columns, ghost,
                    Vector3.zero);
            SceneManager.SetActiveScene(parentScene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 2);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld, subScene);

                testWorld.Connect();
                testWorld.GoInGame();

                // Prespawns on the client and server have the same values, even before replication.
                foreach (var clientWorld in testWorld.ClientWorlds)
                    ValidateClientVsServer(testWorld.ServerWorld, clientWorld);

                // Ensure the values don't get corrupted by early replication.
                for(int i=0;i<8;++i)
                    testWorld.Tick();

                foreach (var clientWorld in testWorld.ClientWorlds)
                    ValidateClientVsServer(testWorld.ServerWorld, clientWorld);

                // Modify these prespawn ghost values on the server:
                {
                    using var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<TestComponent1, TestComponent2, TestBuffer3>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
                    using var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(builder);
                    var s2 = serverQuery.ToComponentDataArray<TestComponent2>(Allocator.Temp);
                    var sEntities = serverQuery.ToEntityArray(Allocator.Temp);
                    for (int i = 0; i < sEntities.Length; i++)
                    {
                        var sEntityManager = testWorld.ServerWorld.EntityManager;
                        sEntityManager.SetComponentEnabled<TestComponent1>(sEntities[i], true);
                        sEntityManager.SetComponentEnabled<TestComponent2>(sEntities[i], false);
                        sEntityManager.SetComponentEnabled<TestBuffer3>(sEntities[i], true);

                        s2[i] = new TestComponent2
                        {
                            Test1 = 11,
                            Test2 = 12,
                            Test3 = 13,
                            Test4 = "TEST_14",
                        };

                        var sBuffer = sEntityManager.GetBuffer<TestBuffer3>(sEntities[i]);
                        sBuffer.Length = 20;
                        for (int j = 0; j < sBuffer.Length; j++)
                        {
                            sBuffer[j] = new TestBuffer3
                            {
                                Test1 = 21,
                                Test2 = 22,
                                Test3 = 23,
                                Test4 = 24
                            };
                        }
                    }
                    serverQuery.CopyFromComponentDataArray(s2);
                }

                // Replicate new values, then test again to ensure they replicate properly:
                for(int i=0;i<8;++i)
                    testWorld.Tick();

                foreach (var clientWorld in testWorld.ClientWorlds)
                    ValidateClientVsServer(testWorld.ServerWorld, clientWorld);

                static void ValidateClientVsServer(World serverWorld, World clientWorld)
                {
                    using var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<TestComponent1, TestComponent2, TestBuffer3>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
                    using var serverQuery = serverWorld.EntityManager.CreateEntityQuery(builder);
                    using var clientQuery = clientWorld.EntityManager.CreateEntityQuery(builder);

                    var s2 = serverQuery.ToComponentDataArray<TestComponent2>(Allocator.Temp);
                    var sEntities = serverQuery.ToEntityArray(Allocator.Temp);

                    var c1 = clientQuery.ToComponentDataArray<TestComponent1>(Allocator.Temp);
                    var c2 = clientQuery.ToComponentDataArray<TestComponent2>(Allocator.Temp);
                    var cEntities = clientQuery.ToEntityArray(Allocator.Temp);

                    Assert.AreEqual(sEntities.Length, cEntities.Length, "Different number of ghosts on the server vs client!");
                    for (var i = 0; i < sEntities.Length; i++)
                    {
                        // TestComponent1 is a flag component.
                        Assert.IsTrue(s2[i].Equals(c2[i]), "TestComponent2 is not the same on client vs server!");

                        var sBuffer = serverWorld.EntityManager.GetBuffer<TestBuffer3>(sEntities[i]);
                        var cBuffer = clientWorld.EntityManager.GetBuffer<TestBuffer3>(cEntities[i]);
                        Assert.AreEqual(sBuffer.Length, cBuffer.Length, "TestBuffer3.Length is not the same on client vs server!");
                        for (int j = 0; j < sBuffer.Length; j++)
                        {
                            Assert.IsTrue(sBuffer[j].Equals(cBuffer[j]), $"TestBuffer3[{j}] entry is not the same on client vs server!");
                        }

                        Assert.AreEqual(serverWorld.EntityManager.IsComponentEnabled<TestComponent1>(sEntities[i]), clientWorld.EntityManager.IsComponentEnabled<TestComponent1>(cEntities[i]), "TestComponent1 Enabled bit is not the same on client vs server!");
                        Assert.AreEqual(serverWorld.EntityManager.IsComponentEnabled<TestComponent2>(sEntities[i]), clientWorld.EntityManager.IsComponentEnabled<TestComponent2>(cEntities[i]), "TestComponent2 Enabled bit is not the same on client vs server!");
                        Assert.AreEqual(serverWorld.EntityManager.IsComponentEnabled<TestBuffer3>(sEntities[i]), clientWorld.EntityManager.IsComponentEnabled<TestBuffer3>(cEntities[i]), "TestBuffer3 Enabled bit is not the same on client vs server!");
                    }
                }
            }
        }

        [Test]
        public void DisconnectReconnectWithPrespawns()
        {
            // Load prespawn scene client and server side
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(NetCodePrespawnAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubSceneWithPrefabs(scene, ScenePath, "subscene", new[] { ghost }, 5);
            SceneManager.SetActiveScene(scene);
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            SubSceneHelper.LoadSubSceneInWorlds(testWorld);

            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 8; i++)
                testWorld.Tick();

            // PrespawnAckSection must be added to client connection on the server, which means it's received the client request to start streaming the prespawn ghosts
            var serverPrespawnAckQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrespawnSectionAck>());
            Assert.AreEqual(1, serverPrespawnAckQuery.GetSingletonBuffer<PrespawnSectionAck>().Length);

            for (int i = 0; i < 4; i++)
                testWorld.Tick();

            // Disconnect the client
            using var driverQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamDriver>());
            using var clientConnectionToServer = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
            var clientNetworkDriver = driverQuery.GetSingleton<NetworkStreamDriver>();
            testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
            clientNetworkDriver.DriverStore.Disconnect(clientConnectionToServer.GetSingleton<NetworkStreamConnection>());

            for (int i = 0; i < 4; i++)
                testWorld.Tick();

            // The streaming request flag has been disabled on the client
            using var clientGhostCleanupComponentQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubSceneWithGhostCleanup>());
            var cleanupData = clientGhostCleanupComponentQuery.GetSingleton<SubSceneWithGhostCleanup>();
            Assert.AreEqual(0, cleanupData.Streaming);

            // Change the prespawn data while client is disconnected
            var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestComponent2>());
            var serverGhostEntities = serverGhostQuery.ToEntityArray(Allocator.Temp);
            foreach (var testGhost in serverGhostEntities)
            {
                testWorld.ServerWorld.EntityManager.SetComponentData(testGhost, new TestComponent2(){ Test2 = 10000 });
                testWorld.ServerWorld.EntityManager.SetComponentEnabled<TestBuffer3>(testGhost, true);
                var buf = testWorld.ServerWorld.EntityManager.GetBuffer<TestBuffer3>(testGhost);
                buf.Add(new TestBuffer3() { Test2 = 10 });
                buf.Add(new TestBuffer3() { Test2 = 20 });
            }

            // Reconnect the client
            clientNetworkDriver.Connect(testWorld.ClientWorlds[0].EntityManager, NetworkEndpoint.LoopbackIpv4.WithPort(7979));
            for (int i = 0; i < 5; i++)
                testWorld.Tick();
            testWorld.GoInGame();

            // Stream request flag has been enabled again
            cleanupData = clientGhostCleanupComponentQuery.GetSingleton<SubSceneWithGhostCleanup>();
            Assert.AreEqual(1, cleanupData.Streaming);

            for (int i = 0; i < 6; i++)
                testWorld.Tick();

            // Verify client has latest prespawn ghost data
            using var clientGhostTest2Query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestComponent2>());
            using var clientGhostEntitiesQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestComponent2>(), ComponentType.ReadOnly<TestBuffer3>());
            var clientGhostTest2Data = clientGhostTest2Query.ToComponentDataArray<TestComponent2>(Allocator.Temp);
            var clientGhostEntities = clientGhostEntitiesQuery.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < clientGhostTest2Data.Length; ++i)
            {
                Assert.AreEqual(10000, clientGhostTest2Data[i].Test2);
                var buf = testWorld.ClientWorlds[0].EntityManager.GetBuffer<TestBuffer3>(clientGhostEntities[i]);
                Assert.AreEqual(7, buf.Length);
                Assert.AreEqual(10, buf[^2].Test2);
                Assert.AreEqual(20, buf[^1].Test2);
            }

            // Move prespawns again and verify the changes are transferred to the client
            foreach (var testGhost in serverGhostEntities)
            {
                testWorld.ServerWorld.EntityManager.SetComponentData(testGhost, new TestComponent2(){ Test2 = 20000 });
                var buf = testWorld.ServerWorld.EntityManager.GetBuffer<TestBuffer3>(testGhost);
                buf.Add(new TestBuffer3() { Test2 = 30 });
                buf.Add(new TestBuffer3() { Test2 = 40 });
            }
            for (int i = 0; i < 6; i++)
                testWorld.Tick();
            clientGhostTest2Data = clientGhostTest2Query.ToComponentDataArray<TestComponent2>(Allocator.Temp);
            for (int i = 0; i < clientGhostTest2Data.Length; ++i)
            {
                Assert.AreEqual(20000, clientGhostTest2Data[i].Test2);
                var buf = testWorld.ClientWorlds[0].EntityManager.GetBuffer<TestBuffer3>(clientGhostEntities[i]);
                Assert.AreEqual(9, buf.Length);
                Assert.AreEqual(30, buf[^2].Test2);
                Assert.AreEqual(40, buf[^1].Test2);
            }
        }

        [Test]
        public void TestPrespawnRelevancy()
        {
            // Prespawn info is stored in a ghost. We want to make sure internal unity ghosts are always relevant

            // load prespawn scene client and server side
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene, Path.GetDirectoryName(scene.path), $"Subscene", 2, 2, ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            SubSceneHelper.LoadSubSceneInWorlds(testWorld);

            var serverRelevancyQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostRelevancy));
            var clientPrespawnSceneQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PrespawnSceneLoaded));
            testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs(); // to access the relevancy set
            var relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

            // Test empty relevancy, so no ghosts should be relevant, except the internal prespawn tracking one
            relevancy.ValueRW.GhostRelevancySet.Clear();
            // Need to connect after relevancy is set to make sure we cover all cases and ghosts didn't get time to replicate by accident
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 4; i++)
            {
                testWorld.Tick();
            }

            Assert.That(clientPrespawnSceneQuery.CalculateEntityCount(), Is.EqualTo(1));

            // Test set always relevant query to not include prespawn ghost and make sure it is still relevant
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.DefaultRelevancyQuery = new EntityQueryBuilder(Allocator.Temp).WithNone<PrespawnSceneLoaded>().Build(testWorld.ServerWorld.EntityManager);
            for (int i = 0; i < 4; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientPrespawnSceneQuery.CalculateEntityCount(), Is.EqualTo(1));

            // test that prespawned ghosts are spawned correctly
            Assert.That(testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(LocalTransform)).CalculateEntityCount(), Is.EqualTo(4));
            Assert.That(testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(LocalTransform)).CalculateEntityCount(), Is.EqualTo(4));
        }
    }
}

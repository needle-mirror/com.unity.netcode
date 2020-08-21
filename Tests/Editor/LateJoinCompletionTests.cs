using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode.Tests;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.NetCode;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections;
using System.Text.RegularExpressions;

namespace Unity.NetCode.Tests
{
    public class LateJoinCompletionConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new GhostOwnerComponent());
        }
    }
    public class LateJoinCompletionTests
    {
        [Test]
        public void ServerGhostCountIsVisibleOnClient()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LateJoinCompletionConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(frameTime);

                var ghostReceiveSystem = testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>();
                // Validate that the ghost was deleted on the cliet
                Assert.AreEqual(8, ghostReceiveSystem.GhostCountOnServer);
                Assert.AreEqual(8, ghostReceiveSystem.GhostCountOnClient);

                // Spawn a few more and verify taht the count is updated
                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(frameTime);
                Assert.AreEqual(16, ghostReceiveSystem.GhostCountOnServer);
                Assert.AreEqual(16, ghostReceiveSystem.GhostCountOnClient);
            }
        }
        [Test]
        public void ServerGhostCountOnlyIncludesRelevantSet()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LateJoinCompletionConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);

                // Go in-game
                testWorld.GoInGame();

                testWorld.Tick(frameTime);

                // Setup relevancy
                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var serverConnectionId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkIdComponent>(serverConnectionEnt).Value;
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<GhostComponent>());
                var ghosts = query.ToComponentDataArray<GhostComponent>(Allocator.Temp);
                Assert.AreEqual(ghosts.Length, 8);
                for (int i = 0; i < 6; ++i)
                    ghostSendSystem.GhostRelevancySet.TryAdd(new RelevantGhostForConnection{Ghost = ghosts[i].ghostId, Connection = serverConnectionId}, 1);

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(frameTime);

                var ghostReceiveSystem = testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>();
                // Validate that the ghost was deleted on the cliet
                Assert.AreEqual(6, ghostReceiveSystem.GhostCountOnServer);
                Assert.AreEqual(6, ghostReceiveSystem.GhostCountOnClient);

                // Spawn a few more and verify taht the count is updated
                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(frameTime);
                Assert.AreEqual(6, ghostReceiveSystem.GhostCountOnServer);
                Assert.AreEqual(6, ghostReceiveSystem.GhostCountOnClient);
            }
        }
        [Test]
        public void ServerGhostCountDoesNotIncludeIrrelevantSet()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LateJoinCompletionConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);

                // Go in-game
                testWorld.GoInGame();

                testWorld.Tick(frameTime);

                // Setup relevancy
                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                var serverConnectionId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkIdComponent>(serverConnectionEnt).Value;
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<GhostComponent>());
                var ghosts = query.ToComponentDataArray<GhostComponent>(Allocator.Temp);
                Assert.AreEqual(ghosts.Length, 8);
                for (int i = 0; i < 6; ++i)
                    ghostSendSystem.GhostRelevancySet.TryAdd(new RelevantGhostForConnection{Ghost = ghosts[i].ghostId, Connection = serverConnectionId}, 1);

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(frameTime);

                var ghostReceiveSystem = testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>();
                // Validate that the ghost was deleted on the cliet
                Assert.AreEqual(2, ghostReceiveSystem.GhostCountOnServer);
                Assert.AreEqual(2, ghostReceiveSystem.GhostCountOnClient);

                // Spawn a few more and verify taht the count is updated
                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(frameTime);
                Assert.AreEqual(10, ghostReceiveSystem.GhostCountOnServer);
                Assert.AreEqual(10, ghostReceiveSystem.GhostCountOnClient);
            }
        }
    }
}
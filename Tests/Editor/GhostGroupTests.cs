using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode.Tests;
using Unity.Jobs;
using UnityEngine;
using Unity.NetCode;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections;

namespace Unity.NetCode.Tests
{
    public class GhostGroupGhostConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new GhostOwnerComponent());
            if (gameObject.name == "ParentGhost")
            {
                dstManager.AddBuffer<GhostGroup>(entity);
                dstManager.AddComponentData(entity, default(GhostGroupRoot));
            }
            else
                dstManager.AddComponentData(entity, default(GhostChildEntityComponent));
        }
    }
    public struct GhostGroupRoot : IComponentData
    {}
    public class GhostGroupTests
    {
        [Test]
        public void EntityMarkedAsChildIsNotSent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                var childGhostGameObject = new GameObject();
                childGhostGameObject.name = "ChildGhost";
                childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, childGhostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                testWorld.SpawnOnServer(childGhostGameObject);

                var serverEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ServerWorld);
                var serverChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntityComponent>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent{NetworkId = 42});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwnerComponent{NetworkId = 43});

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick(frameTime);

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ClientWorlds[0]);
                var clientChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntityComponent>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(42, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwnerComponent>(clientEnt).NetworkId);
                Assert.AreEqual(Entity.Null, clientChildEnt);
            }
        }
        [Test]
        public void EntityMarkedAsChildIsSentAsPartOfGroup()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                var childGhostGameObject = new GameObject();
                childGhostGameObject.name = "ChildGhost";
                childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, childGhostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                testWorld.SpawnOnServer(childGhostGameObject);

                var serverEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ServerWorld);
                var serverChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntityComponent>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent{NetworkId = 42});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwnerComponent{NetworkId = 43});
                testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick(frameTime);

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ClientWorlds[0]);
                var clientChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntityComponent>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(42, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwnerComponent>(clientEnt).NetworkId);
                Assert.AreNotEqual(Entity.Null, clientChildEnt);
                Assert.AreEqual(43, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwnerComponent>(clientChildEnt).NetworkId);
            }
        }
    }
}
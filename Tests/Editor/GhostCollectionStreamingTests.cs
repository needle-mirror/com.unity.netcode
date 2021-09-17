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
    public class GhostCollectionStreamingConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new GhostOwnerComponent());
        }
    }
    [DisableAutoCreation]
    [UpdateInWorld(TargetWorld.Client)]
    public partial class OnDemandLoadTestSystem : SystemBase
    {
        public bool IsLoading = false;
        protected override void OnUpdate()
        {
            var collectionEntity = GetSingletonEntity<GhostCollection>();
            var ghostCollection = EntityManager.GetBuffer<GhostCollectionPrefab>(collectionEntity);

            // This must be done on the main thread for now
            for (int i = 0; i < ghostCollection.Length; ++i)
            {
                var ghost = ghostCollection[i];
                if (ghost.GhostPrefab == Entity.Null && IsLoading)
                {
                    ghost.Loading = GhostCollectionPrefab.LoadingState.LoadingActive;
                    ghostCollection[i] = ghost;
                }
            }
        }
    }
    public class GhostCollectionStreamingTests
    {
        [Test]
        public void OnDemandLoadedPrefabsAreUsed()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(OnDemandLoadTestSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostCollectionStreamingConverter();

                testWorld.CreateWorlds(true, 1);

                // Create the ghost colleciton after the worlds so we can control when they are converted
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.ConvertGhostCollection(testWorld.ServerWorld);
                var onDemandSystem = testWorld.ClientWorlds[0].GetExistingSystem<OnDemandLoadTestSystem>();
                onDemandSystem.IsLoading = true;

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
                // Validate that the ghost was deleted on the client
                Assert.AreEqual(8, ghostReceiveSystem.GhostCountOnServer);
                Assert.AreEqual(0, ghostReceiveSystem.GhostCountOnClient);

                testWorld.ConvertGhostCollection(testWorld.ClientWorlds[0]);
                onDemandSystem.IsLoading = false;
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick(frameTime);
                // Validate that the ghost was deleted on the client
                Assert.AreEqual(8, ghostReceiveSystem.GhostCountOnServer);
                Assert.AreEqual(8, ghostReceiveSystem.GhostCountOnClient);
            }
        }
        [Test]
        public void OnDemandLoadFailureCauseError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(OnDemandLoadTestSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostCollectionStreamingConverter();

                testWorld.CreateWorlds(true, 1);

                // Create the ghost colleciton after the worlds so we can control when they are converted
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.ConvertGhostCollection(testWorld.ServerWorld);
                var onDemandSystem = testWorld.ClientWorlds[0].GetExistingSystem<OnDemandLoadTestSystem>();
                onDemandSystem.IsLoading = true;

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
                // Validate that the ghost was deleted on the client
                Assert.AreEqual(8, ghostReceiveSystem.GhostCountOnServer);
                Assert.AreEqual(0, ghostReceiveSystem.GhostCountOnClient);

                //testWorld.ConvertGhostCollection(testWorld.ClientWorlds[0]);
                onDemandSystem.IsLoading = false;
                LogAssert.Expect(UnityEngine.LogType.Error, "The ghost collection contains a ghost which does not have a valid prefab on the client");
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick(frameTime);
            }
        }
    }
}

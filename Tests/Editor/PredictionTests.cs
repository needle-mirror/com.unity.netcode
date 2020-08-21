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
using System.Collections.Generic;

namespace Unity.NetCode.Tests
{
    public class PredictionTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new GhostOwnerComponent());
        }
    }
    [UpdateInGroup(typeof(GhostPredictionSystemGroup))]
    [DisableAutoCreation]
    public class PredictionTestPredictionSystem : SystemBase
    {
        GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
        public static bool s_IsEnabled;
        protected override void OnCreate()
        {
            m_GhostPredictionSystemGroup = World.GetExistingSystem<GhostPredictionSystemGroup>();
        }
        protected override void OnUpdate()
        {
            if (!s_IsEnabled)
                return;
            var tick = m_GhostPredictionSystemGroup.PredictingTick;
            var deltaTime = Time.DeltaTime;
            Entities.ForEach((ref Translation trans, in PredictedGhostComponent predictedGhost) => {
                if (!GhostPredictionSystemGroup.ShouldPredict(tick, predictedGhost))
                    return;
                // Make sure we advance by one unit per tick, makes it easier to debug the values
                trans.Value.x += deltaTime * 60.0f;
            }).ScheduleParallel();
        }
    }
    public class PredictionTests
    {
        const float frameTime = 1.0f / 60.0f;
        [Test]
        public void PartialPredictionTicksAreRolledBack()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(PredictionTestPredictionSystem));
                PredictionTestPredictionSystem.s_IsEnabled = true;

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostAuthoringComponent.GhostMode.Predicted;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);

                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                var prevServer = testWorld.ServerWorld.EntityManager.GetComponentData<Translation>(serverEnt).Value;
                var prevClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Translation>(clientEnt).Value;
                for (int i = 0; i < 64; ++i)
                {
                    testWorld.Tick(frameTime / 4);
                    var curServer = testWorld.ServerWorld.EntityManager.GetComponentData<Translation>(serverEnt).Value;
                    var curClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Translation>(clientEnt).Value;
                    // Server does not do fractional ticks so it will not advance the position every frame
                    Assert.GreaterOrEqual(curServer.x, prevServer.x);
                    // Client does fractional ticks and position should be always increasing
                    Assert.Greater(curClient.x, prevClient.x);
                    prevServer = curServer;
                    prevClient = curClient;
                }

                // Stop updating, let it run for a while and check that they ended on the same value
                PredictionTestPredictionSystem.s_IsEnabled = false;
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                prevServer = testWorld.ServerWorld.EntityManager.GetComponentData<Translation>(serverEnt).Value;
                prevClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Translation>(clientEnt).Value;
                Assert.IsTrue(math.distance(prevServer, prevClient) < 0.01);
            }
        }
    }
}

using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;


namespace Unity.NetCode.Tests
{
    public class PredictionTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            baker.AddComponent(new GhostOwnerComponent());
        }
    }
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial class PredictionTestPredictionSystem : SystemBase
    {
        public static bool s_IsEnabled;
        protected override void OnUpdate()
        {
            if (!s_IsEnabled)
                return;
            var deltaTime = SystemAPI.Time.DeltaTime;
#if !ENABLE_TRANSFORM_V1
            Entities.WithAll<Simulate, GhostComponent>().ForEach((ref LocalTransform trans) => {
                // Make sure we advance by one unit per tick, makes it easier to debug the values
                trans.Position.x += deltaTime * 60.0f;
            }).ScheduleParallel();
#else
            Entities.WithAll<Simulate, GhostComponent>().ForEach((ref Translation trans) => {
                // Make sure we advance by one unit per tick, makes it easier to debug the values
                trans.Value.x += deltaTime * 60.0f;
            }).ScheduleParallel();
#endif
        }
    }
    public class PredictionTests
    {
        [TestCase((uint)0x229321)]
        [TestCase((uint)100)]
        [TestCase((uint)0x7FFF011F)]
        [TestCase((uint)0x7FFFFF00)]
        [TestCase((uint)0x7FFFFFF0)]
        [TestCase((uint)0x7FFFF1F0)]
        public void PredictionTickEvolveCorrectly(uint serverTickData)
        {
            var serverTick = new NetworkTick(serverTickData);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(PredictionTestPredictionSystem));
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.SetServerTick(serverTick);
                Assert.IsTrue(testWorld.Connect(frameTime, 4));
                testWorld.GoInGame();
                var serverEnt = testWorld.SpawnOnServer(0);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                for(int i=0;i<256;++i)
                    testWorld.Tick(1.0f/60f);
            }
        }

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
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;

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

#if !ENABLE_TRANSFORM_V1
                var prevServer = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt).Position;
                var prevClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt).Position;
#else
                var prevServer = testWorld.ServerWorld.EntityManager.GetComponentData<Translation>(serverEnt).Value;
                var prevClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Translation>(clientEnt).Value;
#endif
                for (int i = 0; i < 64; ++i)
                {
                    testWorld.Tick(frameTime / 4);
#if !ENABLE_TRANSFORM_V1
                    var curServer = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt);
                    var curClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt);
                    // Server does not do fractional ticks so it will not advance the position every frame
                    Assert.GreaterOrEqual(curServer.Position.x, prevServer.x);
                    // Client does fractional ticks and position should be always increasing
                    Assert.Greater(curClient.Position.x, prevClient.x);
                    prevServer = curServer.Position;
                    prevClient = curClient.Position;
#else
                    var curServer = testWorld.ServerWorld.EntityManager.GetComponentData<Translation>(serverEnt).Value;
                    var curClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Translation>(clientEnt).Value;
                    // Server does not do fractional ticks so it will not advance the position every frame
                    Assert.GreaterOrEqual(curServer.x, prevServer.x);
                    // Client does fractional ticks and position should be always increasing
                    Assert.Greater(curClient.x, prevClient.x);
                    prevServer = curServer;
                    prevClient = curClient;
#endif
                }

                // Stop updating, let it run for a while and check that they ended on the same value
                PredictionTestPredictionSystem.s_IsEnabled = false;
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

#if !ENABLE_TRANSFORM_V1
                prevServer = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt).Position;
                prevClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt).Position;
#else
                prevServer = testWorld.ServerWorld.EntityManager.GetComponentData<Translation>(serverEnt).Value;
                prevClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Translation>(clientEnt).Value;
#endif
                Assert.IsTrue(math.distance(prevServer, prevClient) < 0.01);
            }
        }
    }
}

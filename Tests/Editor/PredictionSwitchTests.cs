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
    public class PredictionSwitchTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            baker.AddComponent(new GhostOwnerComponent());
            baker.AddComponent(new PredictedOnlyTestComponent{Value = 42});
            baker.AddComponent(new InterpolatedOnlyTestComponent{Value = 43});
        }
    }

    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    public struct PredictedOnlyTestComponent : IComponentData
    {
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.InterpolatedClient)]
    public struct InterpolatedOnlyTestComponent : IComponentData
    {
        public int Value;
    }
    public class PredictionSwitchTests
    {
        const float frameTime = 1.0f / 60.0f;
        [Test]
        public void SwitchingPredictionAddsAndRemovesComponent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionSwitchTestConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();

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

                var firstClientWorld = testWorld.ClientWorlds[0];
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(firstClientWorld);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                // Validate that the entity is interpolated
                var entityManager = firstClientWorld.EntityManager;
                ref var ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;

                Assert.IsFalse(entityManager.HasComponent<PredictedGhostComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<PredictedOnlyTestComponent>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<InterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<SwitchPredictionSmoothing>(clientEnt));
                Assert.AreEqual(43, entityManager.GetComponentData<InterpolatedOnlyTestComponent>(clientEnt).Value);

                ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEnt,
                    TransitionDurationSeconds = 0f,
                });
                testWorld.Tick(frameTime);
                Assert.IsTrue(entityManager.HasComponent<PredictedGhostComponent>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<PredictedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<InterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<SwitchPredictionSmoothing>(clientEnt));
                Assert.AreEqual(42, entityManager.GetComponentData<PredictedOnlyTestComponent>(clientEnt).Value);

                ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEnt,
                    TransitionDurationSeconds = 2f,
                });
                testWorld.Tick(frameTime);
                Assert.IsFalse(entityManager.HasComponent<PredictedGhostComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<PredictedOnlyTestComponent>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<InterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<SwitchPredictionSmoothing>(clientEnt));
                Assert.AreEqual(43, entityManager.GetComponentData<InterpolatedOnlyTestComponent>(clientEnt).Value);
            }
        }
    }
}

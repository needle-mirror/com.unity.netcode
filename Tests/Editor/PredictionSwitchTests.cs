using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.NetCode.Tests
{
    internal struct PredictionSwitchComponent : IComponentData { } // Component to identify the ghosts we're testing

    internal class PredictionSwitchTestConverter : TestNetCodeAuthoring.IConverter
    {
        internal const int bufferElementCount = 100;

        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new PredictionSwitchComponent());
            baker.AddComponent(entity, new PredictedOnlyTestComponent{Value = 42});
            baker.AddComponent(entity, new InterpolatedOnlyTestComponent{Value = 43});
            var buffer = baker.AddBuffer<BufferInterpolatedOnlyTestComponent>(entity);
            for (int i = 0; i < bufferElementCount; i++)
            {
                buffer.Add(new BufferInterpolatedOnlyTestComponent() { Value = i });
            }
        }
    }

    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    internal struct PredictedOnlyTestComponent : IComponentData
    {
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.InterpolatedClient)]
    internal struct InterpolatedOnlyTestComponent : IComponentData
    {
        public int Value;
    }

    [GhostComponent(PrefabType = GhostPrefabType.InterpolatedClient)]
    [InternalBufferCapacity(0)] // to make sure if there's wrong memory access that we crash instead of running the risk of silently overwriting. Without this, we still get an error in the test about wrong length for the buffer without the buffer length fix, but better safe than sorry.
    internal struct BufferInterpolatedOnlyTestComponent : IBufferElementData
    {
        [GhostField] public int Value;
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class PredictionSwitchMoveTestSystem : SystemBase
    {
        public static NetworkTick TickFreeze;
        public static bool SkipOneOfTwo;
        protected override void OnCreate()
        {
            TickFreeze = NetworkTick.Invalid;
            SkipOneOfTwo = false;
        }

        public const float k_valueIncrease = 5f;

        protected override void OnUpdate()
        {
            var currentTick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            if (TickFreeze != NetworkTick.Invalid && currentTick.IsNewerThan(TickFreeze)) return;
            // Only update transform every second tick
            if (SkipOneOfTwo && (currentTick.TickIndexForValidTick&1u) == 0)
                return;
            foreach (var trans in SystemAPI.Query<RefRW<LocalTransform>>().WithAll<PredictionSwitchComponent>().WithAll<Simulate>())
            {
                trans.ValueRW.Position += new float3(k_valueIncrease, 0, 0);
                trans.ValueRW = trans.ValueRO.RotateX(math.radians(k_valueIncrease));
            }
        }
    }

    internal class PredictionSwitchTests
    {
        [Test]
        public void SwitchingPredictionAddsAndRemovesComponent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionSwitchTestConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                // Ghost is interpolated by default

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var firstClientWorld = testWorld.ClientWorlds[0];
                var clientEnt = testWorld.TryGetSingletonEntity<PredictionSwitchComponent>(firstClientWorld);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                // Validate that the entity is interpolated
                var entityManager = firstClientWorld.EntityManager;
                ref var ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;

                Assert.IsFalse(entityManager.HasComponent<PredictedGhost>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<PredictedOnlyTestComponent>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<InterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<SwitchPredictionSmoothing>(clientEnt));
                Assert.AreEqual(43, entityManager.GetComponentData<InterpolatedOnlyTestComponent>(clientEnt).Value);
                var buffer = entityManager.GetBuffer<BufferInterpolatedOnlyTestComponent>(clientEnt);
                for (int i = 0; i < PredictionSwitchTestConverter.bufferElementCount; i++)
                {
                    Assert.AreEqual(buffer[i].Value, i);
                }

                ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEnt,
                    TransitionDurationSeconds = 0f,
                });
                testWorld.Tick();
                Assert.IsTrue(entityManager.HasComponent<PredictedGhost>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<PredictedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<InterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasBuffer<BufferInterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<SwitchPredictionSmoothing>(clientEnt));
                Assert.AreEqual(42, entityManager.GetComponentData<PredictedOnlyTestComponent>(clientEnt).Value);

                ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEnt,
                    TransitionDurationSeconds = 2f,
                });
                testWorld.Tick();
                Assert.IsFalse(entityManager.HasComponent<PredictedGhost>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<PredictedOnlyTestComponent>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<InterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<SwitchPredictionSmoothing>(clientEnt));
                Assert.AreEqual(43, entityManager.GetComponentData<InterpolatedOnlyTestComponent>(clientEnt).Value);
                buffer = entityManager.GetBuffer<BufferInterpolatedOnlyTestComponent>(clientEnt);
                for (int i = 0; i < PredictionSwitchTestConverter.bufferElementCount; i++)
                {
                    Assert.AreEqual(buffer[i].Value, i);
                }
            }
        }

        // To get as much precision as possible with no interpolation noise
        [GhostComponentVariation(typeof(Transforms.LocalTransform), nameof(ClampedTransformVariant))]
        [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
        internal struct ClampedTransformVariant
        {
            [GhostField(Quantization=0, Smoothing=SmoothingAction.Clamp)]
            public float3 Position;

            [GhostField(Quantization=0, Smoothing=SmoothingAction.Clamp)]
            public float Scale;

            [GhostField(Quantization=0, Smoothing=SmoothingAction.Clamp)]
            public quaternion Rotation;
        }

        [DisableAutoCreation]
        [CreateBefore(typeof(Unity.NetCode.TransformDefaultVariantSystem))]
        sealed partial class ClampedTransformVariantRegisterSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                defaultVariants.Add(typeof(LocalTransform), Rule.ForAll(typeof(ClampedTransformVariant)));
            }
        }

        static ref GhostPredictionSwitchingQueues InitTest(NetCodeTestWorld testWorld, bool UseOwnerPredicted, out Vector3 originalPosParent, out World firstClientWorld, out EntityManager entityManager, out EntityQuery timeQuery, out Entity clientEnt, out float originalRotation)
        {
            var ghostGameObject = new GameObject();
            var childGameObject = new GameObject();

            childGameObject.transform.parent = ghostGameObject.transform;

            childGameObject.AddComponent<NetcodeTransformUsageFlagsTestAuthoring>();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionSwitchTestConverter();
            originalRotation = 45f;
            ghostGameObject.transform.Rotate(new Vector3(0, originalRotation, 0)); // give it an original rotation that's non-zero, to make sure matrix operations work properly
            originalPosParent = new Vector3(10, 20, 30);
            ghostGameObject.transform.position = originalPosParent;
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
            ghostConfig.HasOwner = UseOwnerPredicted;
            ghostConfig.SupportAutoCommandTarget = UseOwnerPredicted;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

            testWorld.CreateWorlds(true, 1);

            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, serverEnt);

            // Connect and make sure the connection could be established
            testWorld.Connect();

            // Go in-game
            testWorld.GoInGame();

            firstClientWorld = testWorld.ClientWorlds[0];
            entityManager = firstClientWorld.EntityManager;
            timeQuery = entityManager.CreateEntityQuery(typeof(NetworkTime));
            PredictionSwitchMoveTestSystem.SkipOneOfTwo = false;
            // Let time sync client side and so the ghosts are spawned on the client
            for (int i = 0; i < 60; ++i)
                testWorld.Tick();

            clientEnt = testWorld.TryGetSingletonEntity<PredictionSwitchComponent>(firstClientWorld);
            Assert.AreNotEqual(Entity.Null, clientEnt);

            // Validate that the entity is interpolated
            Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), Is.Not.True, "Sanity check failed, the entity should be marked as interpolated now");
            return ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;
        }

        [Test]
        public void SwitchingPredictionSmoothChildEntities()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var fuzzyEqual = 0.0001f;

                testWorld.Bootstrap(true, typeof(PredictionSwitchMoveTestSystem), typeof(ClampedTransformVariantRegisterSystem));

                ref var ghostPredictionSwitchingQueues = ref InitTest(testWorld, false, out var originalPosParent, out var firstClientWorld, out var entityManager, out var timeQuery, out var clientEnt, out var originalRotation);

                var childEnt = entityManager.GetBuffer<LinkedEntityGroup>(clientEnt)[1].Value;
                Assert.AreNotEqual(Entity.Null, childEnt);
                ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEnt,
                    TransitionDurationSeconds = 1f,
                });

                var originalLocalToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);

                testWorld.Tick(); // one prediction iteration, position everything in its place
                PredictionSwitchMoveTestSystem.TickFreeze = timeQuery.GetSingleton<NetworkTime>().ServerTick; // have the entity interpolate to a now frozen predicted position (to make testing value changes easier)

                Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), "Sanity check failed, the entity should be marked as predicted now");
                var networkTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                // Expected min tick count between predicted tick and interpolated tick. 2 InterpolationTimeNetTicks + 2 TargetCommandSlack + 2 syncing + partial
                var currentDeltaTickBetweenInterpAndPredictTick = networkTime.ServerTick.TicksSince(networkTime.InterpolationTick);
                Assert.GreaterOrEqual(currentDeltaTickBetweenInterpAndPredictTick, 6);
                currentDeltaTickBetweenInterpAndPredictTick += 1; // since we're doing one more tick after copying originalLocalToWorld
                var expectedIncrementPerTick = (currentDeltaTickBetweenInterpAndPredictTick * PredictionSwitchMoveTestSystem.k_valueIncrease) / 60f; // we expect to move by this much to catch up to the predicted value
                // with 1 second interpolation duration and 60 hz, it should take 60 frames to reach the target predicted position
                // with a +1 per tick and 8 ticks of diff between interpolated pos and predicted pos, we should expect a move of 8/60 per frame to reach the target
                {
                    var localToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);
                    var predictedTargetTransform = entityManager.GetComponentData<LocalTransform>(clientEnt);

                    Assert.That(math.distance(localToWorld.Position, predictedTargetTransform.Position), Is.Not.InRange(-fuzzyEqual, fuzzyEqual), "Sanity check failed, current value shouldn't be equal to predicted value");
                    Assert.That(math.degrees(math.angle(localToWorld.Rotation, predictedTargetTransform.Rotation)), Is.Not.InRange(-fuzzyEqual, fuzzyEqual), "Sanity check failed, current value shouldn't be equal to predicted value");

                    // validate the start transform is close to original value (in interpolation mode). This is testing we don't have a regression on MTT-8430
                    Assert.That(math.distance(localToWorld.Position, originalLocalToWorld.Position), Is.InRange(expectedIncrementPerTick - fuzzyEqual, expectedIncrementPerTick + fuzzyEqual), "Wrong expected first tick value for pos after switch smoothing lerp");
                    Assert.That(math.degrees(math.angle(localToWorld.Rotation, originalLocalToWorld.Rotation)), Is.InRange(expectedIncrementPerTick - fuzzyEqual, expectedIncrementPerTick + fuzzyEqual), "Wrong expected first tick value for rot after switch smoothing lerp");
                    Assert.That((localToWorld.Position - originalLocalToWorld.Position).x, Is.InRange(expectedIncrementPerTick - fuzzyEqual, expectedIncrementPerTick + fuzzyEqual));
                    Assert.That(localToWorld.Position.y, Is.EqualTo(originalPosParent.y));
                    Assert.That(localToWorld.Position.z, Is.EqualTo(originalPosParent.z));
                    Assert.That(localToWorld.Position, Is.Not.EqualTo(Vector3.zero));
                    Assert.That(math.degrees(math.Euler(localToWorld.Rotation).x) - math.degrees(math.Euler(originalLocalToWorld.Rotation).x), Is.InRange(expectedIncrementPerTick - fuzzyEqual, expectedIncrementPerTick + fuzzyEqual));
                    Assert.That(math.degrees(math.Euler(localToWorld.Rotation)).y, Is.InRange(originalRotation - fuzzyEqual, originalRotation + fuzzyEqual));
                    Assert.That(math.Euler(localToWorld.Rotation).z, Is.InRange(-fuzzyEqual, +fuzzyEqual));

                    for (int i = 0; i < 60; i++)
                    {
                        testWorld.Tick();
                    }

                    localToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);

                    // make sure we're now at the predicted target position
                    Assert.That(localToWorld.Position, Is.EqualTo(predictedTargetTransform.Position));
                    Assert.That(math.angle(localToWorld.Rotation, predictedTargetTransform.Rotation), Is.InRange(-fuzzyEqual, +fuzzyEqual));
                }

                {
                    // validate that the position updates every frame and that the child and parent entity has identical LocalToWorld
                    // and that this works with a moving predicted ghost

                    // Setup
                    {
                        // Set it back to interpolated
                        ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;
                        Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), Is.True, "Sanity check failed, the entity should be marked as interpolated now");
                        ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
                        {
                            TargetEntity = clientEnt,
                            TransitionDurationSeconds = 0f,
                        });
                        for (int i = 0; i < 16; i++)
                        {
                            testWorld.Tick();
                        }
                    }
                    {
                        // Set it to predicted for following test step
                        ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;
                        Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), Is.Not.True, "Sanity check failed, the entity should be marked as interpolated now");

                        ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                        {
                            TargetEntity = clientEnt,
                            TransitionDurationSeconds = 1f,
                        });
                    }

                    // allow movements
                    PredictionSwitchMoveTestSystem.SkipOneOfTwo = true;
                    PredictionSwitchMoveTestSystem.TickFreeze = NetworkTick.Invalid;

                    testWorld.Tick(); // converting and predicting

                    var oldLocalToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);

                    // Test

                    for (int i = 0; i < 60; ++i)
                    {
                        testWorld.Tick();
                        var nextLocalToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);
                        Assert.AreNotEqual(oldLocalToWorld.Value, nextLocalToWorld.Value, $"i is {i}");
                        var childLocalToWorld = entityManager.GetComponentData<LocalToWorld>(childEnt);
                        Assert.AreEqual(nextLocalToWorld.Value, childLocalToWorld.Value, $"i is {i}");

                        oldLocalToWorld = nextLocalToWorld;
                    }
                    PredictionSwitchMoveTestSystem.TickFreeze = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]).ServerTick;

                    testWorld.Tick(); // one last tick to make sure things stabilize

                    Assert.That(math.distance(oldLocalToWorld.Position, entityManager.GetComponentData<LocalToWorld>(clientEnt).Position), Is.InRange(-fuzzyEqual, fuzzyEqual));
                    Assert.That(math.angle(oldLocalToWorld.Rotation, entityManager.GetComponentData<LocalToWorld>(clientEnt).Rotation), Is.InRange(-fuzzyEqual, +fuzzyEqual));
                }
            }
        }


        [Test]
        public void TestSwitchAndInterpolation([Values] bool UseOwnerPredicted, [Values] bool testInterruptSwitch)
        {
            using var testWorld = new NetCodeTestWorld();
            PredictionSwitchMoveTestSystem.SkipOneOfTwo = false;
            PredictionSwitchMoveTestSystem.TickFreeze = NetworkTick.Invalid;

            testWorld.Bootstrap(true, typeof(PredictionSwitchMoveTestSystem));

            ref var ghostPredictionSwitchingQueues = ref InitTest(testWorld, UseOwnerPredicted, out var originalPosParent, out var firstClientWorld, out var entityManager, out var timeQuery, out var clientEnt, out var originalRotation);

            var oldLocalToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);
            // Set it to predicted for following test step
            ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;
            Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), Is.Not.True, "Sanity check failed, the entity should be marked as interpolated now");

            ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
            {
                TargetEntity = clientEnt,
                TransitionDurationSeconds = 1f,
            });

            testWorld.Tick();

            Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), Is.True, "Sanity check failed, the entity should be marked as interpolated now");

            var predictedTickDiff = 7; // number of ticks between predicted and interpolated
            var valueIncreasePerTick = PredictionSwitchMoveTestSystem.k_valueIncrease;
            var distancePredictedToInterpolated = valueIncreasePerTick * predictedTickDiff;
            var incrementApproximation = distancePredictedToInterpolated / 60f + valueIncreasePerTick;
            var veryFuzzyEqual = incrementApproximation * 0.5f; // We don't care about precise movements of this double interpolation, just that it moves forward in a somewhat expected manner. So we +/- 50%

            for (int i = 0; i < 59; ++i)
            {
                testWorld.Tick();
                var nextLocalToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);
                if (testInterruptSwitch)
                {
                    // This is undefined, so not testing for value changes, but still shouldn't error out
                    ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;
                    if (i == 20)
                    {
                        ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry()
                        {
                            TargetEntity = clientEnt,
                            TransitionDurationSeconds = 0.1f,
                        });
                    }
                    else if (i == 30)
                    {
                        ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry()
                        {
                            TargetEntity = clientEnt,
                            TransitionDurationSeconds = 0.1f,
                        });
                    }
                    Assert.AreNotEqual(oldLocalToWorld.Value, nextLocalToWorld.Value, $"i is {i}");
                }
                else
                {
                    // we expect the ghost to move at +PredictionSwitchMoveTestSystem.k_valueIncrease per 2 ticks.
                    // with a +1 per tick and 8 ticks of diff between interpolated pos and predicted pos, we should expect a move of 8/60 per frame to reach the target
                    Assert.That((nextLocalToWorld.Position - oldLocalToWorld.Position).x, Is.InRange(incrementApproximation - veryFuzzyEqual, incrementApproximation + veryFuzzyEqual), $"i is {i}");
                }

                oldLocalToWorld = nextLocalToWorld;
            }

            testWorld.Tick();
            // we're done switching, increment should be simple expected k_valueIncrease
            Assert.That((entityManager.GetComponentData<LocalToWorld>(clientEnt).Position - oldLocalToWorld.Position).x, Is.EqualTo(valueIncreasePerTick));
        }

        // If there is a single predicted ghost, then no ghost for a while, then a predicted ghost again,
        // we should not rollback to the last tick there was a predicted ghost.
        [Test]
        public void DoesNotRollbackAfterPredictionSwitching()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.UseFakeSocketConnection = 0;
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoring.SupportedGhostModes = GhostModeMask.All;
                authoring.DefaultGhostMode = GhostMode.Predicted;
                authoring.HasOwner = true;
                testWorld.CreateGhostCollection(ghostGameObject);
                testWorld.CreateWorlds(true, 1);
                var entity = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(entity, new GhostOwner()
                {
                    NetworkId = 1
                });
                testWorld.Connect();
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                testWorld.GoInGame();

                var clientQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PredictedGhost));
                int i = 0;
                while (clientQuery.IsEmpty)
                {
                    testWorld.Tick();
                    i++;
                    if (i > 16)
                    {
                        Assert.Fail("Timed out waiting for predicted ghost to spawn");
                        return;
                    }
                }

                var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                Assert.Greater(clientTime.PredictedTickIndex, 0);

                // Switch to non-predicted
                var clientEntity = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                var ghostPredictionSwitchingQueues = testWorld.GetSingleton<GhostPredictionSwitchingQueues>(testWorld.ClientWorlds[0]);
                ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEntity,
                });
                testWorld.Tick();

                clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                Assert.AreEqual(0, clientTime.PredictedTickIndex);

                // Run to the max ticks (2 less because we predict 2 ticks ahead)
                for (i = 0; i < CommandDataUtility.k_CommandDataMaxSize - 2; ++i)
                {
                    testWorld.Tick();
                }

                // Switch back to predicted
                ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEntity,
                });

                i = 0;
                while (clientQuery.IsEmpty)
                {
                    testWorld.Tick();
                    i++;
                    if (i > 16)
                    {
                        Assert.Fail("Timed out waiting for predicted ghost to spawn");
                        return;
                    }
                }

                for (i = 0; i < 3; i++)
                {
                    clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                    Assert.IsTrue(clientTime.PredictedTickIndex > 0 && clientTime.PredictedTickIndex < 5);
                    testWorld.Tick();
                }
            }
        }
    }
}

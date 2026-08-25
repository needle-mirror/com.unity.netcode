// DISABLE THE TESTS IN PLAYER FOR NOW
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.StateSave;
using Unity.NetCode.Tracing;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.SceneManagement;
using Assert = NUnit.Framework.Assert;
using Random = UnityEngine.Random;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    internal interface IFuzzyEquatable<T>
    {
        public bool FuzzyEqual(T right, float epsilon);
    }

    internal struct TracedGameObjectTransform : IComponentData, IFuzzyEquatable<TracedGameObjectTransform>
    {
        public Vector3 pos;
        public Quaternion rot;
        public bool FuzzyEqual(TracedGameObjectTransform right, float epsilon)
        {
            var left = this;
            var posx = math.abs(left.pos.x - right.pos.x) <= epsilon;
            var posy = math.abs(left.pos.y - right.pos.y) <= epsilon;
            var posz = math.abs(left.pos.z - right.pos.z) <= epsilon;
            var rotx = math.abs(left.rot.x - right.rot.x) <= epsilon;
            var roty = math.abs(left.rot.y - right.rot.y) <= epsilon;
            var rotz = math.abs(left.rot.z - right.rot.z) <= epsilon;
            var rotw = math.abs(left.rot.w - right.rot.w) <= epsilon;
            return posx && posy && posz && rotx && roty && rotz && rotw;
        }

        public override string ToString()
        {
            return $"pos:{pos:F16}, rot:{rot:F16}";
        }

    }

    internal struct TracedGameObjectRigidbody : IComponentData, IFuzzyEquatable<TracedGameObjectRigidbody>
    {
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public bool IsKinematic;
        public bool isSleep;

        public TracedGameObjectRigidbody(Rigidbody body)
        {
            Velocity = body.linearVelocity;
            AngularVelocity = body.angularVelocity;
            IsKinematic = body.isKinematic;
            isSleep = body.IsSleeping();
        }

        public bool FuzzyEqual(TracedGameObjectRigidbody right, float epsilon)
        {
            var left = this;
            var posx = math.abs(left.Velocity.x - right.Velocity.x) <= epsilon;
            var posy = math.abs(left.Velocity.y - right.Velocity.y) <= epsilon;
            var posz = math.abs(left.Velocity.z - right.Velocity.z) <= epsilon;
            var rotx = math.abs(left.AngularVelocity.x - right.AngularVelocity.x) <= epsilon;
            var roty = math.abs(left.AngularVelocity.y - right.AngularVelocity.y) <= epsilon;
            var rotz = math.abs(left.AngularVelocity.z - right.AngularVelocity.z) <= epsilon;
            return posx && posy && posz && rotx && roty && rotz && IsKinematic == right.IsKinematic && isSleep == right.isSleep;
        }
        public override string ToString()
        {
            return $"vel:{Velocity:F16},angVel:{AngularVelocity:F16}, kinematic:{IsKinematic}, isSleep:{isSleep}";
        }
    }

    internal class PhysicsTests
    {
        PredictionCallbackHelper m_Prefab;
        PredictionCallbackHelper m_UnregisteredPrefab;
        Scene m_ClientScene;
        Scene m_ServerScene;

        const float k_TransformEpsilon = 0.000_002f; // found experimentally. If test is too flaky, could be acceptable to use 0.000_01f instead
        const float k_VelocityEpsilon = 0.000_08f; // found experimentally. If test is too flaky, could be acceptable to use 0.000_1f instead

        static NetworkTick s_TickStartSimulate;

        bool m_OldDeterminismValue;

        NetCodeTestWorld TestWorld;

        void SanityCheck(NetCodeTestWorld testWorld)
        {
            var attrType = typeof(TransformVariantMaxPrecision).GetCustomAttribute<GhostComponentVariationAttribute>();

            using var collectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostComponentSerializerCollectionData>());
            var collectionData = collectionQuery.GetSingleton<GhostComponentSerializerCollectionData>();
            bool found = false;
            foreach (var ssIndex in collectionData.SerializationStrategiesComponentTypeMap.GetValuesForKey(attrType.ComponentType))
            {
                var ss = collectionData.SerializationStrategies[ssIndex];

                if (ss.Hash == GhostVariantsUtility.UncheckedVariantHashNBC(typeof(TransformVariantMaxPrecision).FullName, typeof(LocalTransform).FullName))
                {
                    found = true;
                    break;
                }
            }
            Assert.IsTrue(found, $"Sanity check failed, couldn't find {nameof(TransformVariantMaxPrecision)} in registered variants server side");
        }

        // TODO-release@multiWorld physics: ideally we'd need a per-scene SyncTransforms. Right now if we use scene Simulate, there's no automatic synctransforms and Physics.SyncTransforms sync everything
        // internal static void SyncTransforms(World world)
        // {
        //     // return;
        //     if (!s_ShouldSimulate) return;
        //     using var query = world.EntityManager.CreateEntityQuery(typeof(GhostRigidbodyData), typeof(GhostRigidbodyGameObjectTracker));
        //     using var entities = query.ToEntityArray(Allocator.Temp);
        //     var rbTrackers = query.ToComponentDataArray<GhostRigidbodyGameObjectTracker>(Allocator.Temp);
        //     for (int i = 0; i < entities.Length; i++)
        //     {
        //         Rigidbody rb = rbTrackers[i].Rigidbody.Value;
        //         rb.position = rb.gameObject.transform.position;
        //         rb.rotation = rb.gameObject.transform.rotation;
        //     }
        // }

        [SetUp]
        public async Task TestSetup()
        {
            s_TickStartSimulate = NetworkTick.Invalid; // this way we don't have random cubes falling around while setting things up and waiting for spawns

            TestWorld = new NetCodeTestWorld();

            m_OldDeterminismValue = GameObjectPhysicsSimulateSystem.GetIsDeterminism();
            GameObjectPhysicsSimulateSystem.SetDeterminism(true); // This needs to happen before the PhysicsScenes are created, as that serialized value is read on PhysX world creation native side

            // TestWorld.DriverSimulatedDelay = 100; // this fails, most likely because there's too much cumulative small inconsistencies.
            // ^^^ if adding lag, don't forget to also tick after the shouldSimulate=true. we need to leave some time to remove the initial misprediction due to ShouldSimulate being called on different ticks client and server side
            await TestWorld.SetupGameObjectTest(userSystems: new[]
            {
                typeof(AfterSimulateFixedPredictionCallbackSystem),
                typeof(UpdateInPredictionSystem),
            });

            SanityCheck(TestWorld);

            Physics.simulationMode = SimulationMode.Script;

            m_ClientScene = SceneManager.CreateScene("clientScene", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            TestWorld.m_AdditionalScenesToCleanup.Add(m_ClientScene);
            m_ServerScene = SceneManager.CreateScene("serverScene", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            TestWorld.m_AdditionalScenesToCleanup.Add(m_ServerScene);

            PhysicsScene GetSceneForTest(World forWorld)
            {
                var currentTick = ((NetcodeWorld)forWorld).NetworkTime.ServerTick;
                if (!s_TickStartSimulate.IsValid || currentTick.IsOlderThan(s_TickStartSimulate))
                {
                    return default;
                }

                if (forWorld.IsServer())
                {
                    return m_ServerScene.GetPhysicsScene();
                }
                else
                {
                    return m_ClientScene.GetPhysicsScene();
                }
            }

            // Physics.SyncTransforms syncs ALL transforms, not per scene. So we need this for our tests
            // GameObjectPhysicsSimulateSystem.TestSyncTransformsCallback += SyncTransforms;
            GameObjectPhysicsSimulateSystem.GetPhysicsSceneCallback += GetSceneForTest;

            void SetupPrefab(PredictionCallbackHelper prefab)
            {
                prefab.gameObject.AddComponent<GhostRigidbody>();
                prefab.gameObject.AddComponent<BoxCollider>();
                var rb = prefab.GetComponent<Rigidbody>();
                rb.useGravity = true; // default, but still explicit
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous; // for low rate tests, else steps are too big
                var inspectionComponent = prefab.gameObject.AddComponent<GhostAuthoringInspectionComponent>();
                inspectionComponent.GetOrAddPrefabOverride(typeof(LocalTransform), PrefabsRegistry.EntityGuidFromGameObject(prefab.gameObject), GhostPrefabType.All);
                inspectionComponent.GetOrAddPrefabOverride(typeof(GhostRigidbodyData), PrefabsRegistry.EntityGuidFromGameObject(prefab.gameObject), GhostPrefabType.All);
                inspectionComponent.ComponentOverrides[0].VariantHash = GhostVariantsUtility.UncheckedVariantHashNBC(typeof(TransformVariantMaxPrecision).FullName, typeof(LocalTransform).FullName);
                inspectionComponent.ComponentOverrides[1].VariantHash = GhostVariantsUtility.UncheckedVariantHashNBC(typeof(GhostRigidbodyDataMaxPrecision).FullName, typeof(GhostRigidbodyData).FullName);
            }
            m_Prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("RigidbodyTest", autoRegister: false);
            SetupPrefab(m_Prefab);
            Netcode.RegisterPrefab(m_Prefab.gameObject);
            m_UnregisteredPrefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("Rigidbody2", autoRegister: false);
            SetupPrefab(m_UnregisteredPrefab);
        }

        [TearDown]
        public async Task Teardown()
        {
            GameObjectPhysicsSimulateSystem.SetDeterminism(m_OldDeterminismValue);
            await TestWorld.DisposeAsync();

            GameObjectPhysicsSimulateSystem.GetPhysicsSceneCallback = null;
        }

        // A plane with a big cube obstacle, rotated so to have a sort of pyramid in the middle
        static void SetupPhysicsScene(Scene destinationScene)
        {
            var floor = new GameObject("floor");
            floor.transform.localScale = new Vector3(50f, 0.5f, 50f);
            floor.transform.position = new Vector3(0, -10, 0);
            floor.transform.rotation = Quaternion.Euler(0, 0, 0);
            floor.AddComponent<BoxCollider>();
            var obstacle = new GameObject("obstacle");
            obstacle.transform.localScale = new Vector3(5f, 5f, 5f);
            obstacle.transform.position = new Vector3(0, -10, 0);
            obstacle.transform.rotation = Quaternion.Euler(45, 45, 45);
            obstacle.AddComponent<BoxCollider>();
            SceneManager.MoveGameObjectToScene(floor, destinationScene);
            SceneManager.MoveGameObjectToScene(obstacle, destinationScene);
        }

        // TODO-next@physics test forces, joints, colliders, constraints, kinematic, character controller, other runtime settings that can be changed
        [Test(Description = "Runs a small physics scene with cubes falling down on some obstacles. Traces those client/server side positions/velocities and make sure they are relatively the same (epsilon for that diff check defined above).")]
        [UnityPlatform(exclude = new RuntimePlatform[] {
            RuntimePlatform.LinuxEditor // Disabled for Instability https://jira.unity3d.com/browse/UUM-148354
        })]
        public async Task MakeSureRigidbody_AreDeterministicEnough([Values(30, 60)] int tickRateToTest, [Values(1, 5)] int objCount, [Values(0.1f, 0.005f)] float sleepThreshold, [Values] bool alwaysWakeUp, [Values(10f, 0.25f)] float depenetrationVelocity)
        {
#if !NETCODETESTWORLD_FORCE_SINGLE_WORLD_HOST
            Assert.IsTrue(GameObjectPhysicsSimulateSystem.GetIsDeterminism(), "the enhanced determinism setting is required for this test. Without it, we're getting 100x bigger errors than the current epsilons.");
            Physics.defaultSolverIterations = 6; // 6 is the default. Still setting it here in case we change it project side, to make sure we have the same value for all test runs
            Physics.defaultSolverVelocityIterations = 6; // normally 1 by default // TODO-next@physics doc: document this
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("YAMATO_PROJECT_ID")) &&
                (tickRateToTest != 60 || objCount != 5 || sleepThreshold != 0.005f || depenetrationVelocity != 10f)
                )
            {
                // The selected test to run in CI is the case that failed the most often while developing this. Keeping it as our canary in CI.
                Assert.Ignore("Ignoring test case as we're currently running as part of CI. This test case is for local iteration and takes too much time to run in CI.");
            }
            Physics.sleepThreshold = sleepThreshold;
            Physics.defaultMaxDepenetrationVelocity = depenetrationVelocity;

            if (alwaysWakeUp)
            {
                Physics.sleepThreshold = 0;
            }
            else
            {
                // TODO-next@physicsSleepWake
                Assert.Ignore("sleep is broken determinism wise, so we're ignoring this case for now");
            }

            SetupPhysicsScene(m_ServerScene);
            SetupPhysicsScene(m_ClientScene);

            var clientServerTickRateEntity = TestWorld.TryGetSingletonEntity<ClientServerTickRate>(TestWorld.ServerWorld);
            var clientTickRateEntity = TestWorld.TryGetSingletonEntity<ClientTickRate>(TestWorld.ClientWorlds[0]);
            var rateData = new ClientServerTickRate
            {
                MaxSimulationStepBatchSize = 1,
                MaxSimulationStepsPerFrame = 1000, // arbitrary high value, to make sure we're not skipping ticks
                SimulationTickRate = tickRateToTest,
                TargetFrameRateMode = ClientServerTickRate.FrameRateMode.BusyWait
            };
            rateData.ResolveDefaults();

            var clientRateData = NetworkTimeSystem.DefaultClientTickRate;
            clientRateData.MaxPredictionStepBatchSizeRepeatedTick = 1;
            clientRateData.MaxPredictionStepBatchSizeFirstTimeTick = 1;
            TestWorld.ServerWorld.EntityManager.SetComponentData(clientServerTickRateEntity, rateData);
            TestWorld.ClientWorlds[0].EntityManager.SetComponentData(clientTickRateEntity, clientRateData);

            // make sure we don't do any batching that could mess with determinism.
            var frameDt = 1f / tickRateToTest;
            var tickDt = 1f / tickRateToTest;

            await TestWorld.ConnectAsync(enableGhostReplication: true, dt: tickDt, maxSteps:100);

            Assert.AreEqual(tickRateToTest, TestWorld.GetSingleton<ClientServerTickRate>(TestWorld.ServerWorld).SimulationTickRate, "sanity check failed, config isn't applied properly");
            Assert.AreEqual(tickRateToTest, TestWorld.GetSingleton<ClientServerTickRate>(TestWorld.ClientWorlds[0]).SimulationTickRate, "sanity check failed, config isn't applied properly");

            // Setup scene
            Vector3 StartPositionFromIndex(int i)
            {
                var randX = Random.value;
                return new Vector3(randX, i, 0);
            }

            Assert.That(PredictionCallbackHelper.ClientInstances.Count, Is.EqualTo(0));
            Assert.That(PredictionCallbackHelper.ServerInstances.Count, Is.EqualTo(0));
            Random.InitState(seed: 1234);

            for (int i = 0; i < objCount; i++)
            {
                var pos = StartPositionFromIndex(i);
                var obj = GameObject.Instantiate(m_Prefab, pos, Quaternion.identity);
                TestWorld.ServerWorld.EntityManager.AddComponent<TracedGameObjectRigidbody>(obj.Ghost.Entity);
                TestWorld.ServerWorld.EntityManager.AddComponent<TracedGameObjectTransform>(obj.Ghost.Entity);
                SceneManager.MoveGameObjectToScene(obj.gameObject, m_ServerScene);
                pos.z += 5; // second set of ghosts that are offset and won't fall on the central obstacle, for testing stacking
                obj = GameObject.Instantiate(m_Prefab, pos, Quaternion.identity);
                TestWorld.ServerWorld.EntityManager.AddComponent<TracedGameObjectRigidbody>(obj.Ghost.Entity);
                TestWorld.ServerWorld.EntityManager.AddComponent<TracedGameObjectTransform>(obj.Ghost.Entity);
                SceneManager.MoveGameObjectToScene(obj.gameObject, m_ServerScene);
            }

            // wait for client side spawns
            await TestWorld.TickMultipleAsync(5, dt: tickDt);
            await TestWorld.TickMultipleAsync(20, dt: tickDt);

            // setup client objects so they are in their own physics scene client side.
            for (int i = 0; i < objCount * 2; i++)
            {
                var clientObject = PredictionCallbackHelper.ClientInstances[i];
                SceneManager.MoveGameObjectToScene(clientObject.gameObject, m_ClientScene);
                TestWorld.ClientWorlds[0].EntityManager.AddComponent<TracedGameObjectRigidbody>(clientObject.Ghost.Entity);
                TestWorld.ClientWorlds[0].EntityManager.AddComponent<TracedGameObjectTransform>(clientObject.Ghost.Entity);
            }

            Assert.That(PredictionCallbackHelper.ClientInstances.Count, Is.EqualTo(objCount * 2));
            Assert.That(PredictionCallbackHelper.ServerInstances.Count, Is.EqualTo(objCount * 2));

            await TestWorld.TickMultipleAsync(50, dt: tickDt);

            var startingTickServer = TestWorld.GetSingleton<NetworkTime>(TestWorld.ServerWorld).ServerTick;
            var startingTickClient = TestWorld.GetSingleton<NetworkTime>(TestWorld.ClientWorlds[0]).ServerTick;
            Debug.Log($"starting test and tick is client:{startingTickClient.ToFixedString()} server:{startingTickServer.ToFixedString()}");

            void RecordClientTraces(World world)
            {
                if (world.IsHost()) return;
                for (int i = 0; i < objCount * 2; i++)
                {
                    var clientObject = PredictionCallbackHelper.ClientInstances[i];
                    TestWorld.ClientWorlds[0].EntityManager.SetComponentData(clientObject.Ghost.Entity,
                        new TracedGameObjectTransform()
                        {
                            pos = clientObject.transform.position, rot = clientObject.transform.rotation
                        });
                    TestWorld.ClientWorlds[0].EntityManager.SetComponentData(clientObject.Ghost.Entity,
                        new TracedGameObjectRigidbody(clientObject.GetComponent<Rigidbody>()));
                }
            }

            void RecordServerTraces(World world)
            {
                for (int i = 0; i < objCount * 2; i++)
                {
                    var serverObject = PredictionCallbackHelper.ServerInstances[i];
                    TestWorld.ServerWorld.EntityManager.SetComponentData(serverObject.Ghost.Entity, new TracedGameObjectTransform(){pos = serverObject.transform.position, rot = serverObject.transform.rotation});
                    TestWorld.ServerWorld.EntityManager.SetComponentData(serverObject.Ghost.Entity, new TracedGameObjectRigidbody(serverObject.GetComponent<Rigidbody>()));
                }
            }

            int testDurationSeconds = 5;

            int iterationCount = testDurationSeconds * tickRateToTest;
            int expectedTickCount = testDurationSeconds * tickRateToTest;

            s_TickStartSimulate = startingTickClient;
            s_TickStartSimulate.Add(10);

            TracingDataAccess.ResetStaticState();
            NetCodeConfig.Global.TracingConfig.IgnorePartialTicks = true; // physics doesn't run in partial ticks
            TracingDataAccess.Config.Data.OnlyTraceAfter = true; // we only care about the state traced by the above callbacks, we don't care about the state of the world before those
            TracingDataAccess.Config.Data.AddRequiredTypeToTrace(ComponentType.ReadWrite<TracedGameObjectTransform>());
            TracingDataAccess.Config.Data.AddRequiredTypeToTrace(ComponentType.ReadWrite<TracedGameObjectRigidbody>());
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(TypeManager.GetSystemTypeIndex<UpdateInPredictionSystem>());
            NetCodeConfig.Global.TracingConfig._targetFPSDuringProcessing = 1;

            // Execute Test. The cubes fall down and get scrambled on a plane+big cube in the scene
            {
                // fixed step runs in early part of prediction, so this callback runs after physics prediction.
                TestWorld.ServerWorld.GetExistingSystemManaged<UpdateInPredictionSystem>().OnUpdateCallback += RecordServerTraces;
                TestWorld.ClientWorlds[0].GetExistingSystemManaged<UpdateInPredictionSystem>().OnUpdateCallback += RecordClientTraces;

                TracingDataAccess.Config.Data.EnableTracing = true;

                await TestWorld.TickMultipleAsync(iterationCount, frameDt);

                TracingDataAccess.Config.Data.EnableTracing = false;

                TestWorld.ServerWorld.GetExistingSystemManaged<UpdateInPredictionSystem>().OnUpdateCallback -= RecordServerTraces;
                TestWorld.ClientWorlds[0].GetExistingSystemManaged<UpdateInPredictionSystem>().OnUpdateCallback -= RecordClientTraces;
            }

            s_TickStartSimulate = NetworkTick.Invalid;

            Debug.Log($"Final tick for test. client:{TestWorld.GetNetworkTime(TestWorld.ClientWorlds[0]).ServerTick.ToFixedString()} server:{TestWorld.GetNetworkTime(TestWorld.ServerWorld).ServerTick.ToFixedString()}");

            var data = await TracingDataAccess.GetProcessedWorldsData(new CancellationToken());

            LogTraceDiffs(data);

            AssertNoDiffAboveEpsilon(data.ServerWorldData.DiffInfo, "server");
            AssertNoDiffAboveEpsilon(data.ClientWorldData.DiffInfo, "client");

            Assert.That(data.ServerWorldData.TickIDs.Count, Is.AtLeast(expectedTickCount-1), "sanity check failed, wrong expected tick count");
            Assert.That(data.ClientWorldData.FrameIDs.Count+1, Is.AtLeast(iterationCount-1), "sanity check failed, wrong frame count");

            var frameIdToSample = data.ClientWorldData.FrameIDs[0];
            var managedTickData = data.ClientWorldData.GetClientTickData(frameIdToSample, data.ClientWorldData.PerFrameData[frameIdToSample].TickIDs[0]);
            Assert.That(managedTickData.SystemsByExecutionOrder[0].GhostComponents.Count, Is.EqualTo(objCount * 2), "sanity check failed, wrong ghost count was traced");

            int velocityZeroCount = 0;
            int velocityNonZeroCount = 0;

            foreach (var tickID in data.ServerWorldData.TickIDs)
            {
                foreach (var managedSystemData in data.ServerWorldData.GetServerTickData(tickID).SystemsByExecutionOrder)
                {
                    foreach (var ghostComponentData in managedSystemData.GhostComponents)
                    {
                        var rb = (TracedGameObjectRigidbody)ghostComponentData.Value.Components[typeof(TracedGameObjectRigidbody)].AfterValue;
                        if (rb.Velocity.magnitude > 0f) velocityNonZeroCount++;
                        else velocityZeroCount++;
                    }
                }
            }

            Assert.That(velocityZeroCount, Is.Not.EqualTo(0), "Sanity check failed, recorded velocities are all non-zero");
            Assert.That(velocityNonZeroCount, Is.Not.EqualTo(0), "Sanity check failed, recorded velocities are all zero");

            // Sanity check that objects did move
            for (int i = 0; i < objCount * 2; i++)
            {
                Assert.That(PredictionCallbackHelper.ServerInstances[i].transform.position, Is.Not.EqualTo(StartPositionFromIndex(i)), $"Sanity check failed for ghost {i}, ghosts didn't move! Ghosts rigidbodies should make them move.");
                Assert.That(PredictionCallbackHelper.ServerInstances[i].transform.position.y, Is.LessThan(-2), $"Sanity check failed for ghost {i}, ghosts didn't move! Ghosts rigidbodies should make them move.");
                Assert.That(PredictionCallbackHelper.ClientInstances[i].transform.position, Is.Not.EqualTo(StartPositionFromIndex(i)), $"Sanity check failed for ghost {i}, ghosts didn't move! Ghosts rigidbodies should make them move.");
                Assert.That(PredictionCallbackHelper.ClientInstances[i].transform.position.y, Is.LessThan(-2), $"Sanity check failed for ghost {i}, ghosts didn't move! Ghosts rigidbodies should make them move.");
            }
#endif
        }

        // The physics interop's known float noise is tolerated at read time, with the same per-type
        // epsilons the direct value comparisons use.
        static void AssertNoDiffAboveEpsilon(in DiffInfo diffInfo, string worldName)
        {
            if (!diffInfo.HasDiff)
                return;
            var transformType = TypeManager.GetTypeIndex<TracedGameObjectTransform>();
            var rigidbodyType = TypeManager.GetTypeIndex<TracedGameObjectRigidbody>();
            foreach (var kvp in diffInfo.m_Aggregates)
            {
                var reasons = kvp.Key.Reasons;
                if ((reasons & DiffInfo.DiffReasons.ComponentData) != 0)
                {
                    var epsilon = 0f;
                    if (kvp.Key.Component == transformType)
                        epsilon = k_TransformEpsilon;
                    else if (kvp.Key.Component == rigidbodyType)
                        epsilon = k_VelocityEpsilon;
                    if (!(kvp.Value > epsilon))
                        reasons &= ~DiffInfo.DiffReasons.ComponentData;
                }
                Assert.That(reasons, Is.EqualTo(DiffInfo.DiffReasons.Undefined), $"{worldName} has a diff above epsilon: {kvp.Key} amount:{kvp.Value:E3}");
            }
        }

        static void LogTraceDiffs(TracingDataAccess.ProcessedWorldsData data)
        {
            // Some debug logging to help if there's a diff in traces
            if (data.ClientWorldData.DiffInfo.HasDiff)
            {
                foreach (var frameKvPair in data.ClientWorldData.PerFrameData)
                {
                    foreach (var tickKvPair in frameKvPair.Value.PerTickData)
                    {
                        var diffData = data.ClientWorldData.GetClientTickData(frameKvPair.Key, tickKvPair.Key);
                        if (diffData.HasDiff)
                        {
                            foreach (var systemData in diffData.SystemsByExecutionOrder)
                            {
                                if (!systemData.HasDiff)
                                    continue;
                                foreach (var ghostKvPair in systemData.GhostComponents)
                                {
                                    if (!ghostKvPair.Value.HasDiff)
                                        continue;
                                    foreach (var tracedComponent in ghostKvPair.Value.Components)
                                    {
                                        if (!tracedComponent.Value.HasDiff)
                                            continue;
                                        var foundServerSide = data.ServerWorldData.GetServerTickData(tickKvPair.Key)
                                            .SystemsByExecutionOrder
                                            .First(sys => sys.SystemName == systemData.SystemName)
                                            .GhostComponents[ghostKvPair.Key].Components[tracedComponent.Key].AfterValue;
                                        var foundClientSide = tracedComponent.Value.AfterValue;
                                        bool isEqual = false;
                                        if (tracedComponent.Key == typeof(TracedGameObjectTransform))
                                            isEqual = (foundClientSide as IFuzzyEquatable<TracedGameObjectTransform>).FuzzyEqual((TracedGameObjectTransform)foundServerSide, k_TransformEpsilon);
                                        else if (tracedComponent.Key == typeof(TracedGameObjectRigidbody))
                                            isEqual = (foundClientSide as IFuzzyEquatable<TracedGameObjectRigidbody>)
                                                .FuzzyEqual((TracedGameObjectRigidbody)foundServerSide,
                                                    k_VelocityEpsilon);
                                        else if (tracedComponent.Key == typeof(GhostInstance))
                                            isEqual = ((GhostInstance)foundClientSide).Equals(foundServerSide);
                                        else
                                            throw new InvalidOperationException($"invalid type {tracedComponent.Key}");
                                        if (!isEqual)
                                        {
                                            Debug.Log($"tick:{tickKvPair.Key} System {systemData.SystemName} ghost {ghostKvPair.Key} compType: {tracedComponent.Key} value: {foundClientSide.ToString()} server side: {foundServerSide.ToString()}");
                                            if (tracedComponent.Key == typeof(TracedGameObjectTransform))
                                            {
                                                var clientTransform = (TracedGameObjectTransform)foundClientSide;
                                                var serverTransform = (TracedGameObjectTransform)foundServerSide;
                                                var deltaPos = clientTransform.pos - serverTransform.pos;
                                                float maxPos = math.max(math.max(math.abs(deltaPos.x), math.abs(deltaPos.y)), math.abs(deltaPos.z));
                                                float maxRot = math.max(math.max(math.abs(clientTransform.rot.x - serverTransform.rot.x), math.abs(clientTransform.rot.y - serverTransform.rot.y)), math.max(math.abs(clientTransform.rot.z - serverTransform.rot.z), math.abs(clientTransform.rot.w - serverTransform.rot.w)));
                                                Debug.Log($"DIVERGENCE tick:{tickKvPair.Key} ghost:{ghostKvPair.Key} TRANSFORM maxPosDelta:{maxPos:E3} (x{maxPos / k_TransformEpsilon:F1} eps) maxRotDelta:{maxRot:E3} (x{maxRot / k_TransformEpsilon:F1} eps) epsilon:{k_TransformEpsilon}");
                                            }
                                            else if (tracedComponent.Key == typeof(TracedGameObjectRigidbody))
                                            {
                                                var clientRigidbody = (TracedGameObjectRigidbody)foundClientSide;
                                                var sv = (TracedGameObjectRigidbody)foundServerSide;
                                                var dvel = clientRigidbody.Velocity - sv.Velocity;
                                                var dang = clientRigidbody.AngularVelocity - sv.AngularVelocity;
                                                float maxVel = math.max(math.max(math.abs(dvel.x), math.abs(dvel.y)), math.abs(dvel.z));
                                                float maxAng = math.max(math.max(math.abs(dang.x), math.abs(dang.y)), math.abs(dang.z));
                                                Debug.Log($"DIVERGENCE tick:{tickKvPair.Key} ghost:{ghostKvPair.Key} RIGIDBODY maxVelDelta:{maxVel:E3} (x{maxVel / k_VelocityEpsilon:F1} eps) maxAngVelDelta:{maxAng:E3} (x{maxAng / k_VelocityEpsilon:F1} eps) clientSleep:{clientRigidbody.isSleep} serverSleep:{sv.isSleep} clientKinematic:{clientRigidbody.IsKinematic} serverKinematic:{sv.IsKinematic} epsilon:{k_VelocityEpsilon}");
                                            }
                                        }

                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public async Task KinematicTest()
        {
            // make sure kinematic when set user side isn't messed with by Netcode

            await TestWorld.ConnectAsync(enableGhostReplication: true);
            await TestWorld.TickAsync();

            Vector3 frozenPos = Vector3.zero;

            var serverObject = GameObject.Instantiate(m_Prefab);
            SceneManager.MoveGameObjectToScene(serverObject.gameObject, m_ServerScene);

            await TestWorld.TickMultipleAsync(5);

            var clientObject = PredictionCallbackHelper.ClientInstances[0];
            SceneManager.MoveGameObjectToScene(clientObject.gameObject, m_ClientScene);
            s_TickStartSimulate = TestWorld.ClientWorlds[0].NetworkTime.ServerTick;
            s_TickStartSimulate.Add(10);

            var tickToSetKinematic = TestWorld.GetNetworkTime(TestWorld.ClientWorlds[0]).ServerTick;
            tickToSetKinematic.Add(10);
            bool kinematicToSet = true;
            void UpdateKinematic(GameObject gameObject)
            {
                var ga = gameObject.GetComponent<GhostObject>();
                if (ga.NetworkTime.ServerTick.TickIndexForValidTick == tickToSetKinematic.TickIndexForValidTick)
                {
                    gameObject.GetComponent<Rigidbody>().isKinematic = kinematicToSet;
                    frozenPos = gameObject.transform.position;
                }
            }

            serverObject.OnPredictionEvent += UpdateKinematic;
            clientObject.OnPredictionEvent += UpdateKinematic;

            await TestWorld.TickMultipleAsync(5);

            // make sure nothing happened yet
            Assert.That(serverObject.GetComponent<Rigidbody>().isKinematic, Is.False);
            Assert.That(clientObject.GetComponent<Rigidbody>().isKinematic, Is.False);

            await TestWorld.TickMultipleAsync(10);
            // make sure kinematic is set now
            Assert.That(serverObject.transform.position, Is.EqualTo(frozenPos));
            Assert.That(clientObject.transform.position, Is.EqualTo(frozenPos));
            Assert.That(serverObject.GetComponent<Rigidbody>().isKinematic, Is.True);
            Assert.That(clientObject.GetComponent<Rigidbody>().isKinematic, Is.True);

            await TestWorld.TickMultipleAsync(30);
            // make sure it stays the same
            Assert.That(serverObject.transform.position, Is.EqualTo(frozenPos));
            Assert.That(clientObject.transform.position, Is.EqualTo(frozenPos));
            Assert.That(serverObject.GetComponent<Rigidbody>().isKinematic, Is.True);
            Assert.That(clientObject.GetComponent<Rigidbody>().isKinematic, Is.True);

            kinematicToSet = false;
            tickToSetKinematic = TestWorld.GetNetworkTime(TestWorld.ClientWorlds[0]).ServerTick;
            tickToSetKinematic.Add(10);

            Debug.Log("set kinematic false");
            await TestWorld.TickMultipleAsync(30);

            Assert.That(serverObject.transform.position, Is.Not.EqualTo(frozenPos), $"GO didn't move! server go pos is {serverObject.transform.position:F16} and frozenPos is {frozenPos:F16}");
            Assert.That(clientObject.transform.position, Is.Not.EqualTo(frozenPos), $"GO didn't move! client go pos is {clientObject.transform.position:F16} and frozenPos is {frozenPos:F16}");
            Assert.That(serverObject.GetComponent<Rigidbody>().isKinematic, Is.False);
            Assert.That(clientObject.GetComponent<Rigidbody>().isKinematic, Is.False);

            serverObject.OnPredictionEvent -= UpdateKinematic;
            clientObject.OnPredictionEvent -= UpdateKinematic;
        }

        [Test]
        [Ignore("requires engine side changes, disabling for now")]
        public async Task SleepTest([Values(0.1f, 0.005f)] float sleepThreshold)
        {
            Physics.sleepThreshold = sleepThreshold;

            void SetupScene(Scene scene)
            {
                var floor = new GameObject("floor");
                floor.transform.localScale = new Vector3(20f, 0.5f, 20f);
                floor.transform.position = new Vector3(0, 0, 0);
                floor.transform.rotation = Quaternion.Euler(0, 0, 0);
                floor.AddComponent<BoxCollider>();
                SceneManager.MoveGameObjectToScene(floor, scene);
            }
            SetupScene(m_ServerScene);
            SetupScene(m_ClientScene);

            var dt = 1/60f + 0.001f;

            await TestWorld.ConnectAsync(enableGhostReplication: true);
            await TestWorld.TickAsync();

            Vector3 frozenPos = Vector3.zero;

            var serverObjectBelow = GameObject.Instantiate(m_Prefab, Vector3.up * 0.7f, Quaternion.identity);
            var serverObjectAbove = GameObject.Instantiate(m_Prefab, Vector3.up * 2f, Quaternion.identity);
            SceneManager.MoveGameObjectToScene(serverObjectBelow.gameObject, m_ServerScene);
            SceneManager.MoveGameObjectToScene(serverObjectAbove.gameObject, m_ServerScene);

            await TestWorld.TickMultipleAsync(5);

            var clientObjectBelow = PredictionCallbackHelper.ClientInstances.First(go => go.transform.position.y < 2);
            var clientObjectAbove = PredictionCallbackHelper.ClientInstances.First(go => go != clientObjectBelow);
            SceneManager.MoveGameObjectToScene(clientObjectBelow.gameObject, m_ClientScene);
            SceneManager.MoveGameObjectToScene(clientObjectAbove.gameObject, m_ClientScene);

            // wait for the two cubes to rest on top of each other and not move
            await TestWorld.TickMultipleAsync(60, dt);

            Debug.Log($"tick is {TestWorld.GetNetworkTime(TestWorld.ServerWorld).ServerTick.ToFixedString()}");

            var oldAbovePos = clientObjectAbove.transform.position;
            var oldBelowPos = clientObjectBelow.transform.position;
            var oldServerBelowPos = serverObjectBelow.transform.position;
            var oldServerAbovePos = serverObjectAbove.transform.position;
            await TestWorld.TickMultipleAsync(120, dt);
            Assert.That(oldAbovePos, Is.EqualTo(clientObjectAbove.transform.position), $"Ghost {nameof(clientObjectAbove)} is still moving. Old: {oldAbovePos} new: {clientObjectAbove.transform.position:F16}");
            Assert.That(oldBelowPos, Is.EqualTo(clientObjectBelow.transform.position), $"Ghost {nameof(clientObjectBelow)} is still moving. Old: {oldBelowPos} new: {clientObjectBelow.transform.position:F16}");
            Assert.That(oldServerBelowPos, Is.EqualTo(serverObjectBelow.transform.position), $"Ghost {nameof(serverObjectBelow)} is still moving. Old: {oldServerBelowPos} new: {serverObjectBelow.transform.position:F16}");
            Assert.That(oldServerAbovePos, Is.EqualTo(serverObjectAbove.transform.position), $"Ghost {nameof(serverObjectAbove)} is still moving. Old: {oldServerAbovePos} new: {serverObjectAbove.transform.position:F16}");

            Assert.That(serverObjectBelow.GetComponent<Rigidbody>().IsSleeping, $"ghost {nameof(serverObjectBelow)} is not sleeping after being still");
            Assert.That(serverObjectAbove.GetComponent<Rigidbody>().IsSleeping, $"ghost {nameof(serverObjectAbove)} is not sleeping after being still");
            Assert.That(clientObjectAbove.GetComponent<Rigidbody>().IsSleeping, $"ghost {nameof(clientObjectAbove)} is not sleeping after being still");
            Assert.That(clientObjectBelow.GetComponent<Rigidbody>().IsSleeping, $"ghost {nameof(clientObjectBelow)} is not sleeping after being still");
        }

        [Test]
        public async Task MakeSurePartialTick_AppliesKinematicCorrectly()
        {
            m_UnregisteredPrefab.Ghost.MaxSendRate = 30; // makes sure there's going to be partial snapshots
            Netcode.RegisterPrefab(m_UnregisteredPrefab.gameObject);
            await TestWorld.ConnectAsync(enableGhostReplication: true);
            await TestWorld.TickAsync();

            Vector3 frozenPos = Vector3.zero;

            var server = GameObject.Instantiate(m_Prefab);
            var serverLowRate = GameObject.Instantiate(m_UnregisteredPrefab);

            Dictionary<PredictionCallbackHelper, int> updateCount = new();
            void ValidateKinematic(GameObject o)
            {
                updateCount[o.GetComponent<PredictionCallbackHelper>()]++;
                Assert.IsFalse(o.GetComponent<Rigidbody>().isKinematic, "kinematic should not be set to true while we're simulating");
            }

            foreach (var serverInstance in PredictionCallbackHelper.ServerInstances)
            {
                SceneManager.MoveGameObjectToScene(serverInstance.gameObject, m_ServerScene);
                serverInstance.OnFixedPredictionEvent += ValidateKinematic;
                updateCount[serverInstance] = 0;
            }

            await TestWorld.TickMultipleAsync(6);

            PredictionCallbackHelper client = null;
            PredictionCallbackHelper clientLowRate = null;
            foreach (var clientInstance in PredictionCallbackHelper.ClientInstances)
            {
                SceneManager.MoveGameObjectToScene(clientInstance.gameObject, m_ClientScene);
                clientInstance.OnFixedPredictionEvent += ValidateKinematic;
                if (clientInstance.Ghost.GhostId == server.Ghost.GhostId)
                    client = clientInstance;
                else if (clientInstance.Ghost.GhostId == serverLowRate.Ghost.GhostId)
                    clientLowRate = clientInstance;
                updateCount[clientInstance] = 0;
            }

            s_TickStartSimulate = TestWorld.ClientWorlds[0].NetworkTime.ServerTick;
            s_TickStartSimulate.Add(10);


            TestWorld.ClientWorlds[0].GetExistingSystemManaged<AfterSimulateFixedPredictionCallbackSystem>().OnUpdateCallback += world =>
            {
                var em = world.EntityManager;
                var query = em.CreateEntityQuery(typeof(GhostInstance), typeof(GhostRigidbodyGameObjectTracker));
                foreach (var ent in query.ToEntityArray(Allocator.Temp))
                {
                    Assert.AreNotEqual(em.IsComponentEnabled<Simulate>(ent), em.GetComponentData<GhostRigidbodyGameObjectTracker>(ent).Rigidbody.Value.isKinematic);
                }
            };

            await TestWorld.TickMultipleAsync(10);

            Assert.That(updateCount[clientLowRate], Is.LessThan(updateCount[client]), "sanity check failed, low rate didn't run as expected");
            Assert.That(updateCount[clientLowRate], Is.GreaterThan(0), "sanity check failed, low rate didn't run as expected");
            Assert.That(updateCount[client], Is.GreaterThan(0), "sanity check failed, client at normal rate didn't run as expected");
        }
    }
}
#endif

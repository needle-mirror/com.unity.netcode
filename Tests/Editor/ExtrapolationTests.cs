#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class GhostExtrapolationConverter : TestNetCodeAuthoring.IConverter
    {
        public TestExtrapolated TestExtrapolated;

        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, in TestExtrapolated);
        }
    }

    internal struct TestExtrapolated : IComponentData
    {
        [GhostField(Smoothing=SmoothingAction.InterpolateAndExtrapolate, MaxSmoothingDistance=5)]
        public float ReceivedValueIaE;
        [GhostField(Smoothing=SmoothingAction.InterpolateAndExtrapolate, MaxSmoothingDistance=0.1f)]
        public float ReceivedValueIaEWithMaxSmoothingDistance; // Special case.
        [GhostField(Smoothing=SmoothingAction.Interpolate, MaxSmoothingDistance=5)]
        public float ReceivedValueInterp;
        [GhostField(Smoothing=SmoothingAction.Clamp)]
        public float ReceivedValueClamp;
        [GhostField(Smoothing=SmoothingAction.InterpolateAndExtrapolate, MaxSmoothingDistance=5)]
        public float PredictedValueIaE;
        [GhostField(Smoothing=SmoothingAction.Interpolate, MaxSmoothingDistance=5)]
        public float PredictedValueInterp;
        [GhostField(Smoothing=SmoothingAction.Clamp)]
        public float PredictedValueClamp;
        public int? TicksSinceClampedValueChanged;
        public GhostMode GhostMode;
        public GhostOptimizationMode OptimizationMode;
    }

    internal struct ExtrapolateBackup : IComponentData
    {
        public NetworkTick Tick;
        public float Fraction;
        public float ReceivedValueIaE;
        public float ReceivedValueIaEWithMaxSmoothingDistance;
        public float ReceivedValueInterp;
        public float ReceivedValueClamp;
        public float PredictedValueIaE;
        public float PredictedValueInterp;
        public float PredictedValueClamp;
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class MoveExtrapolated : SystemBase
    {
        protected override void OnUpdate()
        {
            var dt = SystemAPI.Time.DeltaTime;
            var isServer = World.IsServer();
            foreach (var valRef in SystemAPI.Query<RefRW<TestExtrapolated>>().WithAll<Simulate>())
            {
                ref var val = ref valRef.ValueRW;
                if (isServer)
                {
                    val.ReceivedValueIaE += dt;
                    val.ReceivedValueClamp += dt;
                    val.ReceivedValueInterp += dt;
                    val.ReceivedValueIaEWithMaxSmoothingDistance += dt;
                }
                val.PredictedValueIaE += dt;
                val.PredictedValueClamp += dt;
                val.PredictedValueInterp += dt;
            }
        }
    }
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    internal partial class CheckExtrapolate : SystemBase
    {
        private const float DrawDurationSeconds = 180;
        public static uint NumStepsTested;

        protected override void OnUpdate()
        {
            var nTime = SystemAPI.GetSingleton<NetworkTime>();
            if (!nTime.ServerTick.IsValid || !SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate)) return;
            var white = new Color(1f, 1f, 1f, 0.9f);
            var pink = new Color(1f, 0.16f, 0.98f, 0.9f);
            var black = new Color(0f, 0f, 0f, 0.9f);

            foreach (var (currentRef, backupRef, clientEntity) in SystemAPI.Query<RefRW<TestExtrapolated>, RefRW<ExtrapolateBackup>>().WithEntityAccess())
            {
                ref var current = ref currentRef.ValueRW;
                ref var backup = ref backupRef.ValueRW;
                var hasNewSnapshotContainingThisGhost = current.ReceivedValueClamp != backup.ReceivedValueClamp;

                // Ignore the first few:
                if (backup.ReceivedValueIaE != default && backup.Tick.IsValid)
                {
                    // Draw bar graph showing X:time and Y:val.Value.
                    const float barScale = 0.01f;
                    var length = (nTime.InterpolationTick.TickIndexForValidTick + nTime.InterpolationTickFraction) * barScale;
                    var backupLength = (backup.Tick.TickIndexForValidTick + backup.Fraction) * barScale;

                    // This is to aid the visual debugging:
                    const float xOffset = 4f;
                    var (color, _, x) = (current.OptimizationMode, current.GhostMode) switch
                    {
                        (GhostOptimizationMode.Dynamic, GhostMode.Interpolated) => (Color.green, "green", -3 * xOffset),
                        (GhostOptimizationMode.Dynamic, GhostMode.Predicted) => (Color.cyan, "cyan", -2 * xOffset),
                        (GhostOptimizationMode.Dynamic, GhostMode.OwnerPredicted) => (Color.blue, "blue", -1 * xOffset),
                        (GhostOptimizationMode.Static, GhostMode.Interpolated) => (Color.yellow, "yellow", +0 * xOffset),
                        (GhostOptimizationMode.Static, GhostMode.Predicted) => (new Color(1f, 0.5f, 0f), "orange", +1 * xOffset),
                        (GhostOptimizationMode.Static, GhostMode.OwnerPredicted) => (Color.magenta, "magenta", +2 * xOffset),
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                    color.a = 0.5f; // Add some fading so we can more easily see if two lines overlap.
                    x += ExtrapolationTests.TMode switch
                    {
                        ExtrapolationTests.NetcodeSetupMode.OnlyInterpolate100ms => -40,
                        ExtrapolationTests.NetcodeSetupMode.SmallestInterpolationWindowAndExtrapolate100ms => 0,
                        ExtrapolationTests.NetcodeSetupMode.Interpolate50msThenExtrapolate50ms => 40,
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                    Debug.DrawLine(new Vector3(x + length, 0, 0), new Vector3(x + length, 0 + current.ReceivedValueIaE, 0), color, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, backup.ReceivedValueInterp, 0), new Vector3(x + length, current.ReceivedValueInterp, 0), white, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, backup.ReceivedValueIaEWithMaxSmoothingDistance, 0), new Vector3(x + length, current.ReceivedValueIaEWithMaxSmoothingDistance, 0), pink, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, backup.ReceivedValueClamp, 0), new Vector3(x + length, current.ReceivedValueClamp, 0), black, DrawDurationSeconds);

                    Debug.DrawLine(new Vector3(x + length, 0, 0), new Vector3(x + length, 0 + -current.PredictedValueIaE, 0), color, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, -backup.PredictedValueInterp, 0), new Vector3(x + length, -current.PredictedValueInterp, 0), white, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, -backup.ReceivedValueIaEWithMaxSmoothingDistance, 0), new Vector3(x + length, -current.ReceivedValueIaEWithMaxSmoothingDistance, 0), pink, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, -backup.PredictedValueClamp, 0), new Vector3(x + length, -current.PredictedValueClamp, 0), black, DrawDurationSeconds);

                    // Draw every time we receive a snapshot:
                    const float markerLength = 0.3f;
                    var expectedDeltaStep = tickRate.SimulationFixedTimeStep;
                    var numPredictedTicks = nTime.ServerTick.TicksSince(nTime.InterpolationTick);

                    var log = ExtrapolationTests.TestLog[clientEntity];
                    if (hasNewSnapshotContainingThisGhost)
                    {
                        Debug.DrawRay(new Vector3(x + backupLength, backup.ReceivedValueIaE, 0), new Vector3(-markerLength * 0.5f, markerLength, 0), Color.green, DrawDurationSeconds);
                        log += ($"\n\n-- New Snapshot! ?:{current.TicksSinceClampedValueChanged} ticks");

                        var isReceivingSnapshotsTooFrequently = current.TicksSinceClampedValueChanged < (tickRate.SimulationTickRate / 2) - 2;
                        if (isReceivingSnapshotsTooFrequently)
                            log += ($"\nFATAL! isReceivingSnapshotsTooFrequently:{current.TicksSinceClampedValueChanged}");
                        var interpolationBufferTooBig = numPredictedTicks > 12;
                        if (interpolationBufferTooBig)
                            log += ($"\nFATAL! interpolationBufferTooBig:{numPredictedTicks}");

                        current.TicksSinceClampedValueChanged = 0;
                    }
                    else if(current.TicksSinceClampedValueChanged.HasValue) current.TicksSinceClampedValueChanged++;

                    log += $"\nST:{nTime.ServerTick.ToFixedString()} IT:{nTime.InterpolationTick.ToFixedString()} ?:{numPredictedTicks} TSCVC:{current.TicksSinceClampedValueChanged} --";
                    NumStepsTested++;

                    // Expected behaviour:
                    var exp = ExtrapolationTests.GetExpectedResults(in current);
                    TestValue(1, exp.ExpectedRIaE, current.ReceivedValueIaE, backup.ReceivedValueIaE, ref log, "RIaE", current.TicksSinceClampedValueChanged, true);
                    TestValue(1, exp.ExpectedRIaEWithMaxSmoothingDistance, current.ReceivedValueIaEWithMaxSmoothingDistance, backup.ReceivedValueIaEWithMaxSmoothingDistance, ref log, "RInterp-MSD", current.TicksSinceClampedValueChanged, false);
                    TestValue(1, exp.ExpectedRInterp, current.ReceivedValueInterp, backup.ReceivedValueInterp, ref log, "RInterp", current.TicksSinceClampedValueChanged, false);
                    TestValue(1, exp.ExpectedRClamp, current.ReceivedValueClamp, backup.ReceivedValueClamp, ref log, "RClamp", current.TicksSinceClampedValueChanged, false);
                    TestValue(-1, exp.ExpectedPIaE, current.PredictedValueIaE, backup.PredictedValueIaE, ref log, "PIaE", current.TicksSinceClampedValueChanged, true);
                    TestValue(-1, exp.ExpectedPInterp, current.PredictedValueInterp, backup.PredictedValueInterp, ref log, "PInterp", current.TicksSinceClampedValueChanged, false);
                    TestValue(-1, exp.ExpectedPClamp, current.PredictedValueClamp, backup.PredictedValueClamp, ref log, "PClamp", current.TicksSinceClampedValueChanged, false);

                    void TestValue(float yMul, Result expectedResult, float currentVal, float previousVal, ref string log2, string name, int? ticksSinceClampedValueChangedLocal, bool isExtrapolating)
                    {
                        var result = Result.Unknown;
                        const float clampTolerance = 0.005f;
                        var delta = currentVal - previousVal;
                        var deltaToDelta = math.abs(expectedDeltaStep - delta);
                        var isSmooth = deltaToDelta <= expectedDeltaStep * ExtrapolationTests.k_StepTolerance;
                        if(isSmooth) result = Result.Smooth;
                        var modDelta = math.abs(delta) % expectedDeltaStep;
                        var deltaToModDelta = math.min(modDelta, math.abs(expectedDeltaStep - modDelta));
                        if (!isSmooth && deltaToModDelta <= clampTolerance) result = Result.Clamp;
                        if (delta < 0) result = Result.Negative;
                        log2 += $"\n\t{name}    \t >> {currentVal:0.000} {result.ToString()} ";
                        //log2 += $"%?:{(delta % expectedDeltaStep):0.000} {1f-(delta/expectedDeltaStep):p0}";
                        if (result != expectedResult && expectedResult != Result.Any)
                        {
                            log2 += $" < EXPECTED {expectedResult}";
                            Debug.DrawRay(new Vector3(x + length, yMul * currentVal, 0), new Vector3(-markerLength, yMul * markerLength, 0), Color.red, DrawDurationSeconds);
                        }
                    }
                    ExtrapolationTests.TestLog[clientEntity] = log;
                }

                // Update backup:
                backup.ReceivedValueIaE = current.ReceivedValueIaE;
                backup.ReceivedValueInterp = current.ReceivedValueInterp;
                backup.ReceivedValueIaEWithMaxSmoothingDistance = current.ReceivedValueIaEWithMaxSmoothingDistance;
                backup.ReceivedValueClamp = current.ReceivedValueClamp;
                backup.PredictedValueIaE = current.PredictedValueIaE;
                backup.PredictedValueInterp = current.PredictedValueInterp;
                backup.PredictedValueClamp = current.PredictedValueClamp;

                backup.Tick = nTime.InterpolationTick;
                backup.Fraction = nTime.InterpolationTickFraction;
            }
        }
    }

    internal enum Result
    {
        Unknown,
        /// <summary>Denotes the value has (or should) smoothly increase (by deltaTime) in a positive direction, without any noticeable jumps or negative values.</summary>
        Smooth,
        /// <summary>Denotes the value has (or should) clamp to the latest value, forming a staircase where it either doesn't change, or (rarely) jumps many ticks.</summary>
        Clamp,
        /// <summary>Denotes the value has (or should) go negative.</summary>
        Negative,
        /// <summary>Denotes any value allowed / skipped.</summary>
        Any,
    }

    internal class ExtrapolationTests
    {
        public static NetcodeSetupMode TMode;
        internal enum NetcodeSetupMode
        {
            OnlyInterpolate100ms,
            /// <summary>
            /// Note: You cannot disable interpolation entirely - even setting the window to 0ms,
            /// netcode will still require (and make use of) a couple of frames of interpolation.
            /// </summary>
            SmallestInterpolationWindowAndExtrapolate100ms,
            Interpolate50msThenExtrapolate50ms,
        }

        public const float k_StepTolerance = 0.001f;
        public static Dictionary<Entity,string> TestLog;
        /// <summary>
        /// Tests three sub-systems used by end-users to get smooth, consistent gameplay using GhostFields;
        /// <list type="bullet">
        /// <item>Client prediction.</item>
        /// <item>The client's interpolation buffer/window.</item>
        /// <item>Client-side extrapolation (when enabled via SmoothingAction).</item>
        /// </list>
        /// The test-case is the simplest form (values changing by a fixed <c>dt</c> on a client ticking at exactly 60hz),
        /// in a few scenarios (listed in <see cref="NetcodeSetupMode"/>).
        /// It also tests the correctness of <see cref="GhostFieldAttribute.Smoothing"/> and <see cref="GhostFieldAttribute.MaxSmoothingDistance"/>.
        /// </summary>
        /// <remarks>
        /// Future hardening and improvement ideas include;
        /// <list type="bullet">
        /// <item>With vs without prediction smoothing.</item>
        /// <item>Partial snapshots (for client prediction).</item>
        /// <item>Physics interactions (for client prediction in particular).</item>
        /// <item>Introducing acceleration, teleportation, and large direction changes.</item>
        /// <item>Different tick rates (e.g. 30Hz, 90Hz, variable Hz).</item>
        /// <item>This test uses partial ticks. If we force always full ticks, do we see the same smoothness?</item>
        /// </list>
        /// </remarks>
        /// <param name="mode"></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        [Test]
        public void NetcodeProducesSmoothValues([Values]NetcodeSetupMode mode)
        {
            // Setup:
            TMode = mode;
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(MoveExtrapolated), typeof(CheckExtrapolate));

            var optimizationModes = (GhostOptimizationMode[])Enum.GetValues(typeof(GhostOptimizationMode));
            var ghostModes = (GhostMode[])Enum.GetValues(typeof(GhostMode));
            var authoringGhostPrefabs = new List<GameObject>(32);
            foreach (var optimizationMode in optimizationModes)
            {
                foreach (var ghostMode in ghostModes)
                {
                    var ghostGameObject = new GameObject($"Ghost_{optimizationMode}_{ghostMode}");
                    authoringGhostPrefabs.Add(ghostGameObject);
                    ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostExtrapolationConverter
                    {
                        TestExtrapolated = new TestExtrapolated
                        {
                            GhostMode = ghostMode, OptimizationMode = optimizationMode,
                        },
                    };
                    var ghostAuthoringComponent = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                    ghostAuthoringComponent.DefaultGhostMode = ghostMode;
                    ghostAuthoringComponent.SupportedGhostModes = GhostModeMask.All;
                    ghostAuthoringComponent.OptimizationMode = optimizationMode;
                    ghostAuthoringComponent.HasOwner = true;
                    ghostAuthoringComponent.MaxSendRate = 2; // Low, to make sure interpolation & extrapolation is used.
                                                             // Note: Don't use NetworkTickRate for this, as it forces
                                                             // the interpolation window to be a minimum of 1 NetworkTickRate tick.
                }
            }
            Assert.IsTrue(testWorld.CreateGhostCollection(authoringGhostPrefabs.ToArray()));
            testWorld.CreateWorlds(true, 1);

            // Prevent batched ticks!
            var tickRate = new ClientServerTickRate {MaxSimulationStepBatchSize = 1, MaxSimulationStepsPerFrame = 1};
            tickRate.ResolveDefaults();
            testWorld.ServerWorld.EntityManager.CreateSingleton(tickRate);

            // Disable interpolation time to make sure extrapolation is used
            var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            var (interpMs, extrapMs) = mode switch
            {
                NetcodeSetupMode.OnlyInterpolate100ms => (100u, 0u),
                NetcodeSetupMode.SmallestInterpolationWindowAndExtrapolate100ms => (0u, 100u),
                NetcodeSetupMode.Interpolate50msThenExtrapolate50ms => (50u, 50u),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };
            clientTickRate.InterpolationTimeNetTicks = 0;
            clientTickRate.InterpolationTimeMS = interpMs;
            clientTickRate.MaxExtrapolationTimeSimTicks = (uint) (extrapMs / 1000f * tickRate.SimulationTickRate);
            testWorld.ClientWorlds[0].EntityManager.CreateSingleton(clientTickRate);

            // Spawn & set owner (for owner predicted):
            var serverEntitites = new FixedList4096Bytes<Entity>();
            foreach (var ghostPrefab in authoringGhostPrefabs)
            {
                var serverEnt = testWorld.SpawnOnServer(ghostPrefab);
                serverEntitites.Add(serverEnt);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{ NetworkId = 1, });
            }

            // Let the simulation run for a bit since we're testing the stability of the connection (and start-up is turbulent):
            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 256; ++i)
                testWorld.Tick();

            // Reset the values just before test start:
            using var clientEntityQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(TestExtrapolated));
            using var clientEntities = clientEntityQuery.ToEntityArray(Allocator.Persistent);
            foreach (var serverEntity in serverEntitites)
                ResetComp(testWorld.ServerWorld.EntityManager, serverEntity);
            TestLog = new Dictionary<Entity, string>(clientEntities.Length);
            foreach (var clientEntity in clientEntities)
                TestLog.Add(clientEntity, "");
            static void ResetComp(EntityManager em, Entity entity)
            {
                var comp = em.GetComponentData<TestExtrapolated>(entity);
                em.SetComponentData(entity, new TestExtrapolated
                {
                    GhostMode = comp.GhostMode,
                    OptimizationMode = comp.OptimizationMode,
                });
            }
            for (int i = 0; i < 128; ++i)
                testWorld.Tick();

            // Enable the checks via component query:
            Assert.AreEqual(serverEntitites.Length, clientEntities.Length, "Sanity");
            CheckExtrapolate.NumStepsTested = default;
            foreach (var clientEntity in clientEntities)
                testWorld.ClientWorlds[0].EntityManager.AddComponent<ExtrapolateBackup>(clientEntity);

            // Run the test over this many ticks:
            for (int i = 0; i < 200; ++i)
                testWorld.Tick();

            Assert.IsTrue(CheckExtrapolate.NumStepsTested > 180, $"We need to make sure the test has actually run! {CheckExtrapolate.NumStepsTested}");

            // As of 01/25, there are occasional smoothing issues when snapshots arrive, due to the low send rate (except for interpolated ghosts).
            // Thus, allow SOME % of errors.
            foreach (var clientEntity in clientEntities)
            {
                var current = testWorld.ClientWorlds[0].EntityManager.GetComponentData<TestExtrapolated>(clientEntity);
                //var backup = testWorld.ClientWorlds[0].EntityManager.GetComponentData<ExtrapolateBackup>(clientEntity);
                LogEach(ref current, TestLog[clientEntity]);
            }

            void LogEach(ref TestExtrapolated current, string logFs)
            {
                Assert.That(logFs.Length > 1000, "Sanity");
                var title = $"({mode},{current.OptimizationMode},{current.GhostMode}) - InterpolationBufferWindow:{clientTickRate.CalculateInterpolationBufferTimeInMs(in tickRate)}ms, ExtrapolationBufferWindow:{clientTickRate.MaxExtrapolationTimeSimTicks}ticks!";
                var log = $"{title} {logFs}";
                var foundErrors = new Regex(Regex.Escape("EXPECTED")).Matches(log).Count;
                var foundFatal = new Regex(Regex.Escape("FATAL")).Matches(log).Count;
                if (foundErrors > 0 || foundFatal > 0)
                {
                    // Too many errors means test failure!
                    if (foundErrors > 0 || foundFatal > 0)
                    {
                        var error = $"FAIL: Found {foundErrors} errors ({foundFatal} fatal) with (stepTolerance:{k_StepTolerance:0.000}) on {log}";
                        Debug.LogError(error);
                        return;
                    }
                }
                Debug.Log($"PASS: Found {foundErrors} errors with (stepTolerance:{k_StepTolerance:0.000}) on {log}");
            }
        }

        internal struct ResultGroup
        {
            public Result ExpectedRIaE;
            public Result ExpectedRInterp;
            public Result ExpectedRIaEWithMaxSmoothingDistance;
            public Result ExpectedRClamp;
            public Result ExpectedPIaE;
            public Result ExpectedPInterp;
            public Result ExpectedPClamp;
        }
        public static ResultGroup GetExpectedResults(in TestExtrapolated current)
        {
            // Nuance1: Interpolation smoothes values for `(SNAPSHOT-N) to SNAPSHOT` ticks BEFORE the NEXT clamp value (including the tick the snapshot arrived for).
            // Nuance2: Extrapolation smoothes values from `SNAPSHOT to (SNAPSHOT+N)` ticks i.e. AFTER the snapshot arrives (including the tick the snapshot arrived for).
            // Nuance3: When in the extrapolation mode, we still see ~2 ticks of interpolation for SmoothingAction.Interpolate and SmoothingAction.InterpolateAndExtrapolate,
            // on the 0th and last tick (before another snapshot arrives). This is correct & expected.
            // Nuance4: Static-optimized disables extrapolation.
            // Nuance5: 50ms of interpolation + 50ms of extrapolation leads to a different smooth & clamp pattern than previous patterns (as expected). This one is for dynamic ghosts.
            // Nuance6: Same as Note5, but for static-optimized ghosts.
            Span<(Result n1, Result n2, Result n3, Result n4, Result n5, Result n6)> nuances = stackalloc (Result, Result, Result, Result, Result, Result)[]
            {
                // Note1,           Note2,          Note3,          Note4,          Note5,          Note6
                (Result.Smooth,     Result.Smooth,  Result.Smooth,  Result.Smooth,  Result.Smooth,  Result.Smooth), // 0
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Smooth,  Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Smooth,  Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Smooth,  Result.Clamp), // 3 - Extrapolation ends at 50ms.
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Clamp,   Result.Clamp), // 6 - Extrapolation ends at 100ms.
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp), // 10
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp), // 20
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Smooth,     Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp), // 24 - Interpolation begins at 100ms.
                (Result.Smooth,     Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Smooth,     Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Smooth,     Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Smooth,  Result.Smooth), // 27 - Interpolation begins at 50ms.
                (Result.Smooth,     Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Smooth,  Result.Smooth),
                (Result.Smooth,     Result.Smooth,  Result.Smooth,  Result.Smooth,  Result.Smooth,  Result.Smooth), // 29
            };
            switch (TMode, current.OptimizationMode, current.GhostMode)
            {
                case (_, GhostOptimizationMode.Dynamic or GhostOptimizationMode.Static, GhostMode.Predicted or GhostMode.OwnerPredicted):
                    return new ResultGroup
                    {
                        ExpectedRIaE = Result.Clamp,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = Result.Clamp,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = Result.Smooth,
                        ExpectedPInterp = Result.Smooth,
                        ExpectedPClamp = Result.Smooth,
                    };
                case (NetcodeSetupMode.OnlyInterpolate100ms, _, GhostMode.Interpolated):
                    return new ResultGroup
                    {
                        ExpectedRIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n1 : Result.Any,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n1 : Result.Any,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n1 : Result.Any,
                        ExpectedPInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n1 : Result.Any,
                        ExpectedPClamp = Result.Clamp,
                    };
                case (NetcodeSetupMode.Interpolate50msThenExtrapolate50ms, GhostOptimizationMode.Dynamic, GhostMode.Interpolated):
                    return new ResultGroup
                    {
                        ExpectedRIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n5 : Result.Any,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n5 : Result.Any,
                        ExpectedPInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedPClamp = Result.Clamp,
                    };
                case (NetcodeSetupMode.Interpolate50msThenExtrapolate50ms, GhostOptimizationMode.Static, GhostMode.Interpolated):
                    return new ResultGroup
                    {
                        ExpectedRIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedPInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedPClamp = Result.Clamp,
                    };
                case (_, GhostOptimizationMode.Dynamic, GhostMode.Interpolated):
                    return new ResultGroup
                    {
                        ExpectedRIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n3 : Result.Any,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n2 : Result.Any,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n3 : Result.Any,
                        ExpectedPInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n2 : Result.Any,
                        ExpectedPClamp = Result.Clamp,
                    };
                case (_, GhostOptimizationMode.Static, GhostMode.Interpolated):
                    return new ResultGroup
                    {
                        ExpectedRIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n4 : Result.Any,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n2 : Result.Any,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n4 : Result.Any,
                        ExpectedPInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n2 : Result.Any,
                        ExpectedPClamp = Result.Clamp,
                    };
                default: return default;
            }
        }
    }
}

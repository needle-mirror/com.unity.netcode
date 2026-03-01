#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using System.Collections;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    #region Entity setup
    internal class GhostInterpolationConverter : TestNetCodeAuthoring.IConverter
    {
        public TestStaticInterpolated TestStaticInterpolated;

        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, in TestStaticInterpolated);
        }
    }
    #endregion

    internal struct TestStaticInterpolated : IComponentData
    {
        [GhostField(Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float ReceivedValueIaE;

        [GhostField(Smoothing=SmoothingAction.Interpolate)]
        public float ReceivedValueInterp;

        [GhostField]
        public int NumChanges;

        [GhostField]
        public bool IsMoving;

        public int? TicksSinceClampedValueChanged;
    }

    internal struct InterpolateBackup : IComponentData
    {
        public NetworkTick Tick;
        public float Fraction;
        public float ReceivedValueIaE;
        public float ReceivedValueInterp;
        public int NumChanges;
        public bool IsMoving;
        public float CalculatedInterpolation;
    }

    #region Server movement
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class MoveStaticInterpolated : SystemBase
    {
        protected override void OnUpdate()
        {
            var nTime = SystemAPI.GetSingleton<NetworkTime>();
            var dt = SystemAPI.Time.DeltaTime;
            foreach (var valRef in SystemAPI.Query<RefRW<TestStaticInterpolated>>().WithAll<Simulate>())
            {
                ref var val = ref valRef.ValueRW;
                if (nTime.ServerTick.TickIndexForValidTick % StaticInterpolationTests.TicksPerStateChange == 0)
                {
                    val.IsMoving = !val.IsMoving;

                    // Track the change for the frame we stopped moving
                    if (!val.IsMoving)
                        val.NumChanges++;
                }

                if (val.IsMoving)
                {
                    val.ReceivedValueIaE += dt;
                    val.ReceivedValueInterp += dt;
                    val.NumChanges++;
                }
                StaticInterpolationTests.ServerDataAtTick.Add(nTime.ServerTick, val.ReceivedValueIaE);
                StaticInterpolationTests.ServerMovementAtTick.Add(nTime.ServerTick, val.IsMoving);
            }
        }
    }
    #endregion

    #region Client entity (runs the test and validates data)
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    internal partial class CheckInterpolation : SystemBase
    {
        public static uint NumStepsTested;

        protected override void OnUpdate()
        {
            var dt = SystemAPI.Time.DeltaTime;
            var nTime = SystemAPI.GetSingleton<NetworkTime>();
            if (!nTime.InterpolationTick.IsValid || !SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate)) return;

            // Find the targeted tick as there is an off-by-one error with fractional ticks
            var currentTick = nTime.InterpolationTick;
            if (nTime.InterpolationTickFraction < 1)
            {
                currentTick.Decrement();
            }

            // The client is interpolated between this tick and the next tick. Find the data on the server for those ticks.
            if (!StaticInterpolationTests.ServerDataAtTick.TryGetValue(currentTick, out var appliedData)) return;

            var higher = currentTick;
            higher.Increment();
            var futureData = StaticInterpolationTests.ServerDataAtTick[higher];

            // Use the actual data from the server to find the expected interpolated value for this client frame.
            var testData = new SnapshotData.DataAtTick {Tick = currentTick};
            testData.PopulateInterpolationFactor(false, currentTick, higher, new NetworkTick(), nTime.InterpolationTickFraction, 0);
            var serverInterpolatedValue = math.lerp(appliedData, futureData, testData.InterpolationFactor);

            foreach (var (currentRef, backupRef, clientEntity) in SystemAPI.Query<RefRW<TestStaticInterpolated>, RefRW<InterpolateBackup>>().WithEntityAccess())
            {
                ref var current = ref currentRef.ValueRW;
                ref var backup = ref backupRef.ValueRW;
                var hasNewSnapshotContainingThisGhost = current.NumChanges != backup.NumChanges;

                // Ignore the first few:
                if (backup.ReceivedValueIaE > 0 && backup.Tick.IsValid)
                {
                    var log = StaticInterpolationTests.TestResults[clientEntity];

                    if (hasNewSnapshotContainingThisGhost)
                    {
                        log.LogBuilder.Append($"\n\n-- New Snapshot! ?:{current.TicksSinceClampedValueChanged} ticks");
                        current.TicksSinceClampedValueChanged = 0;
                    }
                    else if(current.TicksSinceClampedValueChanged.HasValue) current.TicksSinceClampedValueChanged++;

                    log.LogBuilder.Append($"\nIT:{nTime.InterpolationTick.ToFixedString()} F:{nTime.InterpolationTickFraction:0.00} TSCVC:{current.TicksSinceClampedValueChanged} Moving: {current.IsMoving} --");
                    NumStepsTested++;

                    var interpAndExtrapError = false;
                    var interpError = false;

                    // Skip testing until we have received at least one snapshot
                    if (current.TicksSinceClampedValueChanged.HasValue)
                    {
                        // Expected behaviour:
                        var exp = StaticInterpolationTests.GetExpectedResults(in currentTick, current.TicksSinceClampedValueChanged.Value);
                        interpAndExtrapError = TestValue( exp, current.ReceivedValueIaE, "RIaE");
                        interpError = TestValue( exp, current.ReceivedValueInterp, "RInterp");
                    }

                    bool TestValue(StaticInterpolationTests.Result expectedResult, float currentVal, string name)
                    {
                        var deltaFromExpected = serverInterpolatedValue - currentVal;
                        var result = StaticInterpolationTests.Result.OutsideTolerance;

                        // If the expectedResult is InsideTolerance then we don't need to check for an exact match
                        // ExactMatch counts as being inside the tolerance, we want to capture InsideTolerance as the result.
                        if (Mathf.Approximately(currentVal, serverInterpolatedValue) && expectedResult != StaticInterpolationTests.Result.InsideTolerance)
                        {
                            result = StaticInterpolationTests.Result.ExactMatch;
                        }
                        // deltaFromExpected should be positive and within what could be mis-interpolated because of changes on ticks where no snapshot was sent
                        else if (deltaFromExpected >= 0 && deltaFromExpected < dt * StaticInterpolationTests.ExpectedTicksBetweenSnapshots)
                        {
                            result = StaticInterpolationTests.Result.InsideTolerance;
                        }

                        log.LogBuilder.Append($"\n\t{name}    \t >> {currentVal:0.000} {result.ToString()} ");
                        if (result != expectedResult)
                        {
                            log.ErrorCount++;
                            log.LogBuilder.Append($" < EXPECTED {expectedResult}");
                            return true;
                        }
                        return false;
                    }

                    StaticInterpolationTests.TestResults[clientEntity] = log;
                    StaticInterpolationTests.DrawDebugFrame(in nTime, in current, in backup, serverInterpolatedValue, hasNewSnapshotContainingThisGhost, interpAndExtrapError, interpError);
                }

                // Update backup:
                backup.ReceivedValueIaE = current.ReceivedValueIaE;
                backup.ReceivedValueInterp = current.ReceivedValueInterp;
                backup.IsMoving = current.IsMoving;
                backup.NumChanges = current.NumChanges;

                backup.CalculatedInterpolation = serverInterpolatedValue;

                backup.Tick = nTime.InterpolationTick;
                backup.Fraction = nTime.InterpolationTickFraction;
            }
        }
    }
    #endregion

    [DisableSingleWorldHostTest]
    internal class StaticInterpolationTests
    {
        public static readonly Dictionary<NetworkTick, float> ServerDataAtTick = new();
        public static readonly Dictionary<NetworkTick, bool> ServerMovementAtTick = new();

        public static Dictionary<Entity, ResultsLog> TestResults;

        public struct ResultsLog
        {
            public StringBuilder LogBuilder;
            public int ErrorCount;
        }

        /// <summary>
        /// DebugMode will display frame-by-frame details as the test runs
        /// It uses Debug.DrawLine to draw a graph into the scene mode of the values as the test runs
        /// The X axis shows time passing,
        /// The Y axis shows values changing over time.
        /// </summary>
        public static bool DebugMode = false;

        #region Debug drawing

        private const float DrawDurationSeconds = 60;
        private const float barScale = 0.01f;
        private static readonly Color white = new(1f, 1f, 1f, 1f);
        private static readonly Color black = new(0f, 0f, 0f, 1f);
        private static readonly Color pink = new(1f, 0.16f, 0.98f, 1f);

        public static void DrawDebugFrame(in NetworkTime ntime, in TestStaticInterpolated current, in InterpolateBackup backup, float serverInterpolatedValue, bool hasNewSnapshot, bool interpAndExtrapError, bool interpError)
        {
            if (!DebugMode)
            {
                return;
            }

            // Draw bar graph showing X:time and Y:val.Value.
            var length = (ntime.InterpolationTick.TickIndexForValidTick + ntime.InterpolationTickFraction) * barScale;
            var backupLength = (backup.Tick.TickIndexForValidTick + backup.Fraction) * barScale;

            // This is to aid the visual debugging:
            const float xOffset = 4f;
            var x = +0 * xOffset;

            // Black line draws the server's actual movement
            Debug.DrawLine(new Vector3(x + backupLength, backup.CalculatedInterpolation, 0), new Vector3(x + length, serverInterpolatedValue, 0), black, DrawDurationSeconds);
            // White line raws the Interpolation only value. This should be the same as the yellow as static ghosts should never extrapolate
            Debug.DrawLine(new Vector3(x + backupLength, backup.ReceivedValueInterp, 0), new Vector3(x + length, current.ReceivedValueInterp, 0), white, DrawDurationSeconds);
            // Yellow vertical lines fill in the volume of the Interpolation and Extrapolation
            Debug.DrawLine(new Vector3(x + length, 0, 0), new Vector3(x + length, 0 + current.ReceivedValueIaE, 0), Color.yellow, DrawDurationSeconds);

            // Draw every time we receive a snapshot:
            const float markerLength = 0.3f;

            if (hasNewSnapshot)
                Debug.DrawRay(new Vector3(x + length, current.ReceivedValueIaE, 0), new Vector3(-markerLength * 0.5f, markerLength, 0), pink, DrawDurationSeconds);

            if (current.IsMoving != backup.IsMoving)
                Debug.DrawRay(new Vector3(x + length, current.ReceivedValueIaE, 0), new Vector3(-markerLength * 0.2f, markerLength, 0), Color.green, DrawDurationSeconds);

            if (interpAndExtrapError)
                Debug.DrawRay(new Vector3(x + length, current.ReceivedValueIaE, 0), new Vector3(-markerLength, markerLength, 0), Color.red, DrawDurationSeconds);
            if (interpError)
                Debug.DrawRay(new Vector3(x + length, current.ReceivedValueInterp, 0), new Vector3(-markerLength, markerLength, 0), Color.red, DrawDurationSeconds);
        }
        #endregion

        #region Test runtime values

        /// <summary>
        /// Number of ticks before server should change between IsMoving and !IsMoving
        /// Server will swap on <see cref="NetworkTime.ServerTick"/>%TicksPerStateChange == 0
        /// </summary>
        public static int TicksPerStateChange;

        public static int SimulationTickRate;
        /// <summary>
        /// Sets the <see cref="GhostAuthoringComponent.MaxSendRate"/> of the test object
        /// </summary>
        /// <remarks>A lower max send rate will force bigger mis-predictions, making the test more robust at catching errors</remarks>
        public static byte MaxSendRate;

        /// <summary>
        /// The number of ticks the client will render per server tick
        /// </summary>
        /// <remarks>Client renders at 60hertz</remarks>
        public static int RenderingTicksPerServerTick => 60 / SimulationTickRate;

        /// <summary>
        /// The number of full ticks between each server snapshot
        /// </summary>
        internal static int ExpectedTicksBetweenSnapshots => SimulationTickRate / MaxSendRate;

        #endregion

        public static IEnumerable TestCases
        {
            get
            {
                yield return new TestCaseData(10, 60, 60).SetName("ClientCanInterpolateAccurately");
                yield return new TestCaseData(7, 30, 15).SetName("ClientInterpolatesWithinToleranceWhenMissingData");
            }
        }

        /// <summary>
        /// Tests that static interpolation:
        /// <list type="bullet">
        /// <item>Never extrapolates</item>
        /// <item>Waits until the interpolation timeline before applying new snapshots</item>
        /// <item>Interpolates smoothly</item>
        /// </list>
        /// The server will flip between moving and not moving every <see cref="TicksPerStateChange"/> ticks.
        /// </summary>
        /// <remarks>
        /// Set <see cref="DebugMode"/> to true to enable printing debug data for this test
        /// </remarks>
        [TestCaseSource(nameof(TestCases))]
        public void InterpolationProducesSmoothValues(int ticksPerStateChange, int simulationTickRate, int maxSendRate)
        {
            ServerDataAtTick.Clear();
            ServerMovementAtTick.Clear();

            TicksPerStateChange = ticksPerStateChange;
            SimulationTickRate = simulationTickRate;
            MaxSendRate = (byte) maxSendRate;

            // Setup:
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(MoveStaticInterpolated), typeof(CheckInterpolation));

            var authoringGhostPrefabs = new List<GameObject>(32);

            var ghostGameObject = new GameObject($"Ghost_Static_Interpolation");
            authoringGhostPrefabs.Add(ghostGameObject);
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostInterpolationConverter()
            {
                TestStaticInterpolated = new TestStaticInterpolated(),
            };
            var ghostAuthoringComponent = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostAuthoringComponent.DefaultGhostMode = GhostMode.Interpolated;
            ghostAuthoringComponent.SupportedGhostModes = GhostModeMask.Interpolated;
            ghostAuthoringComponent.OptimizationMode = GhostOptimizationMode.Static;
            ghostAuthoringComponent.HasOwner = true;
            ghostAuthoringComponent.MaxSendRate = MaxSendRate;

            Assert.IsTrue(testWorld.CreateGhostCollection(authoringGhostPrefabs.ToArray()));
            testWorld.CreateWorlds(true, 1);

            // Prevent batched ticks!
            var tickRate = new ClientServerTickRate {MaxSimulationStepBatchSize = 1, MaxSimulationStepsPerFrame = 1, SimulationTickRate = SimulationTickRate};
            tickRate.ResolveDefaults();
            testWorld.ServerWorld.EntityManager.CreateSingleton(tickRate);

            // Setup the tick rate
            var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            clientTickRate.InterpolationTimeNetTicks = 0;
            clientTickRate.InterpolationTimeMS = 100u;
            clientTickRate.MaxExtrapolationTimeSimTicks = (uint) (0.1 * tickRate.SimulationTickRate);
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
            foreach (var serverEntity in serverEntitites)
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new TestStaticInterpolated());

            // Set up the test
            using var clientEntityQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(TestStaticInterpolated));
            using var clientEntities = clientEntityQuery.ToEntityArray(Allocator.Persistent);
            TestResults = new Dictionary<Entity, ResultsLog>(clientEntities.Length);
            foreach (var clientEntity in clientEntities)
                TestResults.Add(clientEntity, new ResultsLog{ LogBuilder = new StringBuilder() });

            // Run the test for 128 ticks.
            for (int i = 0; i < 128; ++i)
                testWorld.Tick();

            // Enable the checks via component query:
            Assert.AreEqual(serverEntitites.Length, clientEntities.Length, "Sanity");
            CheckInterpolation.NumStepsTested = 0;
            foreach (var clientEntity in clientEntities)
                testWorld.ClientWorlds[0].EntityManager.AddComponent<InterpolateBackup>(clientEntity);

            // Run the test over this many ticks:
            for (int i = 0; i < 200; ++i)
                testWorld.Tick();

            Assert.IsTrue(CheckInterpolation.NumStepsTested > 180, $"We need to make sure the test has actually run! {CheckInterpolation.NumStepsTested}");

            foreach (var clientEntity in clientEntities)
            {
                var results = TestResults[clientEntity];

                Assert.That(results.LogBuilder.Length > 1000, "Sanity check that the code ran");

                // Use an assert if we're not in debug mode as it should run faster than logging.
                if (!DebugMode)
                {
                    Assert.That(results.ErrorCount, Is.EqualTo(0), $"Test ran with {results.ErrorCount} errors! Use {nameof(DebugMode)} to display debug data for this test.");
                    continue;
                }

                var log = results.LogBuilder;
                log.Insert(0, $"Ticks between snapshots: {ExpectedTicksBetweenSnapshots}! Client renders per server tick: {RenderingTicksPerServerTick}! --\n");
                if (results.ErrorCount > 0)
                {
                    log.Insert(0,  $"FAIL: Found {results.ErrorCount} errors!\n");
                    Debug.LogError(log.ToString());
                    continue;
                }
                log.Insert(0,  "PASS: ");
                Debug.Log(log.ToString());
            }
        }

        internal enum Result
        {
            OutsideTolerance,
            /// <summary>Denotes the value has (or should) teleport to the latest value</summary>
            InsideTolerance,
            /// <summary>Denotes the value should exactly match the interpolation from the server's data.</summary>
            ExactMatch,
        }

        public static Result GetExpectedResults(in NetworkTick currentTick, int ticksSinceLastSnapshot)
        {
            // How many frames until we could next receive a snapshot
            // With static ghosts the server will not send a snapshot if nothing has changed.
            var nextSnapshotIn = ticksSinceLastSnapshot % math.max(ExpectedTicksBetweenSnapshots * RenderingTicksPerServerTick, 1);

            // This test setup can have the simulationTickRate set to lower than the frame rate
            // In this case we need to calculate how many server ticks until we next could receive a snapshot
            var serverTicks = (uint) nextSnapshotIn / RenderingTicksPerServerTick;

            // Find whether the server was changing data on the previous snapshot
            var lastSnapshotTick = currentTick;
            lastSnapshotTick.Subtract((uint) serverTicks);
            var movingOnLastSnapshot = ServerMovementAtTick[lastSnapshotTick];

            // Find whether the server will be changing data on the next snapshot
            var nextSnapshotTick = lastSnapshotTick;
            nextSnapshotTick.Add((uint) ExpectedTicksBetweenSnapshots);
            var movingOnNextSnapshot = ServerMovementAtTick[nextSnapshotTick];

            // If we are in-between two snapshots where the server changed between moving and not moving
            // Then the client could not match the server authoritative data as the client can be missing data
            if (movingOnLastSnapshot != movingOnNextSnapshot)
            {
                return Result.InsideTolerance;
            }

            // All the rest of the time the client should have enough data to exactly match the server data.
            return Result.ExactMatch;
        }
    }
}

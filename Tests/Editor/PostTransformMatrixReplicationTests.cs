using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    internal partial class SelectPostTransformMatrixScaleVariantSystem : DefaultVariantSystemBase
    {
        protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
        {
            defaultVariants.Add(ComponentType.ReadWrite<PostTransformMatrix>(), Rule.OnlyParents(typeof(PostTransformMatrix3DScaleVariant)));
        }
    }

    [GhostComponentVariation(typeof(PostTransformMatrix), displayName: "PTM Scale - Clamp Test", isTestVariant: true)]
    internal struct ClampScaleTestVariant
    {
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Clamp, SubType = GhostFieldSubType.PostTransformMatrixScale)]
        public float4x4 Value;
    }

    [GhostComponentVariation(typeof(PostTransformMatrix), displayName: "PTM Scale - Interpolate Test", isTestVariant: true)]
    internal struct InterpolateScaleTestVariant
    {
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Interpolate, SubType = GhostFieldSubType.PostTransformMatrixScale)]
        public float4x4 Value;
    }

    // No quantized InterpolateAndExtrapolate test variant: the shipping PostTransformMatrix3DScaleVariant already
    // declares exactly that (Quantization = 1000, InterpolateAndExtrapolate), so the tests use it directly.

    // Quantization = 0 routes to the unquantized template (GhostSnapshotValuePostTransformMatrixUnquantized).
    [GhostComponentVariation(typeof(PostTransformMatrix), displayName: "PTM Scale - Unquantized Clamp Test", isTestVariant: true)]
    internal struct UnquantizedClampScaleTestVariant
    {
        [GhostField(Quantization = 0, Smoothing = SmoothingAction.Clamp, SubType = GhostFieldSubType.PostTransformMatrixScale)]
        public float4x4 Value;
    }

    [GhostComponentVariation(typeof(PostTransformMatrix), displayName: "PTM Scale - Unquantized Interpolate Test", isTestVariant: true)]
    internal struct UnquantizedInterpolateScaleTestVariant
    {
        [GhostField(Quantization = 0, Smoothing = SmoothingAction.Interpolate, SubType = GhostFieldSubType.PostTransformMatrixScale)]
        public float4x4 Value;
    }

    [GhostComponentVariation(typeof(PostTransformMatrix), displayName: "PTM Scale - Unquantized InterpolateAndExtrapolate Test", isTestVariant: true)]
    internal struct UnquantizedInterpolateAndExtrapolateScaleTestVariant
    {
        [GhostField(Quantization = 0, Smoothing = SmoothingAction.InterpolateAndExtrapolate, SubType = GhostFieldSubType.PostTransformMatrixScale)]
        public float4x4 Value;
    }

    [GhostComponentVariation(typeof(PostTransformMatrix), displayName: "PTM Scale - Coarse Quantization Test", isTestVariant: true)]
    internal struct CoarseQuantizationScaleTestVariant
    {
        [GhostField(Quantization = 10, Smoothing = SmoothingAction.Interpolate, SubType = GhostFieldSubType.PostTransformMatrixScale)]
        public float4x4 Value;
    }

    [GhostComponentVariation(typeof(PostTransformMatrix), displayName: "PTM Scale - MaxSmoothingDistance Test", isTestVariant: true)]
    internal struct MaxSmoothingDistanceScaleTestVariant
    {
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Interpolate, MaxSmoothingDistance = 1f, SubType = GhostFieldSubType.PostTransformMatrixScale)]
        public float4x4 Value;
    }

    // Grows the ghost's 3D scale incrementally every prediction tick, on both server and client.
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class PredictedScaleGrowthSystem : SystemBase
    {
        public static readonly float3 k_ScaleStepPerTick = new(0.01f, 0.02f, 0.03f);

        protected override void OnUpdate()
        {
            foreach (var matrix in SystemAPI.Query<RefRW<PostTransformMatrix>>().WithAll<Simulate>())
                matrix.ValueRW.Value = MatrixScaleHelper.SetScale(matrix.ValueRO.Value, matrix.ValueRO.Value.Scale() + k_ScaleStepPerTick);
        }
    }

    // Server-only monotonic ramp: gives interpolated clients a continuously changing value, so "kept moving past
    // the newest snapshot" (extrapolation) is distinguishable from "froze on it".
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class ServerScaleRampSystem : SystemBase
    {
        public const float k_StepPerTick = 0.25f;

        protected override void OnUpdate()
        {
            foreach (var matrix in SystemAPI.Query<RefRW<PostTransformMatrix>>())
                matrix.ValueRW.Value = MatrixScaleHelper.SetScale(matrix.ValueRO.Value, matrix.ValueRO.Value.Scale() + k_StepPerTick);
        }
    }

    internal struct MoveGhostTag : IComponentData { }

    // Moves tagged ghosts every prediction tick, so the interpolated view and the predicted state actually diverge
    // and prediction-switch smoothing has something to smooth.
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class ScaledGhostMoveSystem : SystemBase
    {
        public const float k_StepPerTick = 0.5f;

        protected override void OnUpdate()
        {
            foreach (var trans in SystemAPI.Query<RefRW<LocalTransform>>().WithAll<MoveGhostTag, Simulate>())
                trans.ValueRW.Position += new float3(k_StepPerTick, 0f, 0f);
        }
    }

    internal class PostTransformMatrixReplicationTests
    {
        // Ticks for a server-side change to reach the client and settle.
        const int k_PredictedSettleTicks = 4;
        const int k_InterpolatedSettleTicks = 6;

        // Sub-tick sampling puts the client inside the interpolation window instead of only on its edges, which is
        // the only way a lerp is observable. Exactly 5ms, because ApplyDTClient truncates the dt to whole ms.
        const float k_SubTickDt = 0.005f;
        const int k_SubTickSettleSamples = 64;

        static readonly float3 k_PrefabScale = new(1.5f, 2.5f, 3.5f);

        // Off every quantization grid the tests use, so a variant resolved to the wrong template (quantized vs
        // unquantized) fails the bit-exact asserts.
        static readonly float3 k_UpdatedScale = new(0.2500123f, 4.0004567f, 1.0000891f);

        // scaleVariant: variant to select via a per-prefab override, or null for no override (system default).
        static Entity CreateGhost(World world, Type scaleVariant, GhostMode ghostMode)
        {
            var entityManager = world.EntityManager;
            var entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, LocalTransform.Identity);
            entityManager.AddComponentData(entity, new LocalToWorld { Value = float4x4.identity });
            entityManager.AddComponentData(entity, new PostTransformMatrix { Value = float4x4.Scale(k_PrefabScale) });
            var config = new GhostPrefabCreation.Config
            {
                Name = "PostTransformMatrixGhost",
                Importance = 1,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = ghostMode,
                OptimizationMode = GhostOptimizationMode.Dynamic,
            };
            using var overrides = new NativeParallelHashMap<GhostPrefabCreation.Component, GhostPrefabCreation.ComponentOverride>(0, Allocator.Temp);
            if (scaleVariant != null)
            {
                overrides.Add(
                    new GhostPrefabCreation.Component
                    {
                        ComponentType = ComponentType.ReadWrite<PostTransformMatrix>(), ChildIndex = 0
                    },
                    new GhostPrefabCreation.ComponentOverride
                    {
                        OverrideType = GhostPrefabCreation.ComponentOverrideType.Variant,
                        Variant = GhostVariantsUtility.UncheckedVariantHashNBC(scaleVariant,
                            ComponentType.ReadWrite<PostTransformMatrix>()),
                    });
            }

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, entity, config, overrides);
            return entity;
        }

        static Entity GetClientGhost(NetCodeTestWorld testWorld)
        {
            using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PostTransformMatrix>());
            return query.GetSingletonEntity();
        }

        static float4x4 GetClientMatrix(NetCodeTestWorld testWorld)
            => testWorld.ClientWorlds[0].EntityManager.GetComponentData<PostTransformMatrix>(GetClientGhost(testWorld)).Value;

        // SignedScaleFromScaleOnlyMatrix so tests can also verify negative (mirrored) scale replication.
        static float3 ScaleOf(float4x4 matrix) => MatrixScaleHelper.SignedScaleFromScaleOnlyMatrix(matrix);

        static float3 GetClientMatrixScale(NetCodeTestWorld testWorld) => ScaleOf(GetClientMatrix(testWorld));

        static float3 GetServerMatrixScale(NetCodeTestWorld testWorld, Entity serverGhost)
            => ScaleOf(testWorld.ServerWorld.EntityManager.GetComponentData<PostTransformMatrix>(serverGhost).Value);

        // Drives the assertions from the attribute under test instead of values duplicated in the test body.
        static GhostFieldAttribute GetDeclaredGhostField(Type variantType)
            => variantType.GetField("Value").GetCustomAttribute<GhostFieldAttribute>();

        // Mirrors the scale templates' quantize + dequantize steps bit-for-bit.
        static float3 DequantizedScale(float3 scale, int quantization)
        {
            var dequantize = 1f / quantization;
            return new float3(
                (int)math.round(scale.x * quantization) * dequantize,
                (int)math.round(scale.y * quantization) * dequantize,
                (int)math.round(scale.z * quantization) * dequantize);
        }

        // The exact client-side PostTransformMatrix after replicating serverScale onto an axis-aligned client matrix.
        static float4x4 ExpectedClientMatrix(float3 serverScale, int quantization = 1000)
            => float4x4.Scale(quantization == 0 ? serverScale : DequantizedScale(serverScale, quantization));

        static void AssertClientMatrixIs(float3 serverScale, NetCodeTestWorld testWorld, int quantization = 1000)
            => Assert.AreEqual(ExpectedClientMatrix(serverScale, quantization), GetClientMatrix(testWorld));

        static int SettleTicks(GhostMode ghostMode) => ghostMode == GhostMode.Predicted ? k_PredictedSettleTicks : k_InterpolatedSettleTicks;

        static Entity SetupAndSpawn(NetCodeTestWorld testWorld, Type scaleVariant, GhostMode ghostMode)
        {
            testWorld.CreateWorlds(true, 1, false);
            testWorld.CreateGhostCollection();
            testWorld.Connect();
            var ghostPrefab = CreateGhost(testWorld.ServerWorld, scaleVariant, ghostMode);
            CreateGhost(testWorld.ClientWorlds[0], scaleVariant, ghostMode);
            testWorld.GoInGame();
            var serverGhost = testWorld.ServerWorld.EntityManager.Instantiate(ghostPrefab);
            testWorld.TickUntilClientsHaveAllGhosts();
            return serverGhost;
        }

        static void SetServerScale(NetCodeTestWorld testWorld, Entity serverGhost, float3 scale)
            => testWorld.ServerWorld.EntityManager.SetComponentData(serverGhost, new PostTransformMatrix { Value = float4x4.Scale(scale) });

        static void SetServerScaleAndSettle(NetCodeTestWorld testWorld, Entity serverGhost, float3 scale, GhostMode ghostMode = GhostMode.Interpolated)
        {
            SetServerScale(testWorld, serverGhost, scale);
            testWorld.TickMultiple(SettleTicks(ghostMode));
        }

        // Samples the client matrix in sub-tick steps while a change propagates and returns whether any sample sat
        // strictly between the two bit-exact bounds. mustSnap fails on the first such sample.
        static bool SampleSubTicks(NetCodeTestWorld testWorld, float4x4 oldMatrix, float4x4 newMatrix, bool mustSnap, string snapFailure)
        {
            var sawInBetween = false;
            for (var i = 0; i < k_SubTickSettleSamples; i++)
            {
                testWorld.Tick(k_SubTickDt);
                var sample = GetClientMatrix(testWorld);
                var isInBetween = !sample.Equals(oldMatrix) && !sample.Equals(newMatrix);
                Assert.IsTrue(!mustSnap || !isInBetween,
                    $"Sample {i}: client scale {ScaleOf(sample)} is neither the old ({ScaleOf(oldMatrix)}) nor the new ({ScaleOf(newMatrix)}) value: {snapFailure}");
                sawInBetween |= isInBetween;
            }
            return sawInBetween;
        }

        // Existing entities-only projects with a PostTransformMatrix on their ghosts must see no bandwidth or
        // behaviour change until they opt in.
        [Test]
        [Description("PostTransformMatrix must NOT be replicated by default.")]
        public void PostTransformMatrix_IsNotReplicatedByDefault()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var serverGhost = SetupAndSpawn(testWorld, scaleVariant: null, GhostMode.Interpolated);

            // Replication never touches the client matrix: it stays bit-identical to the client prefab value.
            var expectedClientPrefabMatrix = float4x4.Scale(k_PrefabScale);
            Assert.AreEqual(expectedClientPrefabMatrix, GetClientMatrix(testWorld));
            SetServerScaleAndSettle(testWorld, serverGhost, k_UpdatedScale);
            Assert.AreEqual(expectedClientPrefabMatrix, GetClientMatrix(testWorld));
        }

        [Test]
        [Description("A per-prefab variant override opts an entities-only ghost in to 3D scale replication, on both the interpolated and predicted apply paths.")]
        public void PostTransformMatrix_ScaleReplicates_WithPerPrefabVariantOverride(
            [Values(GhostMode.Interpolated, GhostMode.Predicted)] GhostMode ghostMode)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            Type scaleVariant = typeof(PostTransformMatrix3DScaleVariant);
            var serverGhost = SetupAndSpawn(testWorld, scaleVariant, ghostMode);
            AssertClientMatrixIs(k_PrefabScale, testWorld);
            SetServerScaleAndSettle(testWorld, serverGhost, k_UpdatedScale, ghostMode);
            AssertClientMatrixIs(k_UpdatedScale, testWorld);
        }

        // The growth is incremental, so this only holds if snapshot rollback, prediction backup restore and
        // re-simulation all work for PostTransformMatrix.
        [Test]
        [Description("3D scale changes driven from inside the prediction loop must advance by exactly one step per predicted tick.")]
        public void PostTransformMatrix_ScaleChanges_ArePredictedCorrectly()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(PredictedScaleGrowthSystem));
            SetupAndSpawn(testWorld, typeof(PostTransformMatrix3DScaleVariant), GhostMode.Predicted);
            testWorld.TickMultiple(k_PredictedSettleTicks);

            for (var i = 0; i < k_PredictedSettleTicks; i++)
            {
                var before = GetClientMatrixScale(testWorld);
                testWorld.Tick();
                var after = GetClientMatrixScale(testWorld);
                var expected = before + PredictedScaleGrowthSystem.k_ScaleStepPerTick;
                // The one place here that can't be bit-exact: a snapshot arriving between the two samples rolls the
                // client onto a dequantized origin, so the re-simulated chain differs by the round-trip error.
                const float epsilon = 0.000_001f;
                Assert.AreEqual(expected.x, after.x, epsilon, "x");
                Assert.AreEqual(expected.y, after.y, epsilon, "y");
                Assert.AreEqual(expected.z, after.z, epsilon, "z");
            }
        }

        [Test]
        [Description("A DefaultVariantSystemBase opts a project in to 3D scale replication globally, without any per-prefab override.")]
        public void PostTransformMatrix_ScaleReplicates_WithDefaultVariantSystem()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(SelectPostTransformMatrixScaleVariantSystem));
            var serverGhost = SetupAndSpawn(testWorld, null, GhostMode.Interpolated);
            AssertClientMatrixIs(k_PrefabScale, testWorld);
            SetServerScaleAndSettle(testWorld, serverGhost, k_UpdatedScale, GhostMode.Interpolated);
            AssertClientMatrixIs(k_UpdatedScale, testWorld);
        }

        // Covers every smoothing type at coarse, default and unquantized precision: each combination must resolve
        // to a template at compile time and replicate at runtime.
        [Test]
        [Description("User-authored scale variants must honour BOTH declared GhostField settings: the Quantization and the Smoothing.")]
        public void PostTransformMatrix_UserVariants_HonourDeclaredQuantizationAndSmoothing(
            [Values(typeof(ClampScaleTestVariant), typeof(InterpolateScaleTestVariant), typeof(PostTransformMatrix3DScaleVariant),
                    typeof(UnquantizedClampScaleTestVariant), typeof(UnquantizedInterpolateScaleTestVariant), typeof(UnquantizedInterpolateAndExtrapolateScaleTestVariant),
                    typeof(CoarseQuantizationScaleTestVariant))] Type variantType)
        {
            var ghostField = GetDeclaredGhostField(variantType);
            var quantization = ghostField.Quantization;
            var interpolates = ghostField.Smoothing != SmoothingAction.Clamp;

            if (quantization != 0)
                Assert.AreNotEqual(k_UpdatedScale, DequantizedScale(k_UpdatedScale, quantization),
                    $"Test data must not sit on the Quantization = {quantization} grid, otherwise the lossy path is not exercised and a dropped Quantization would go unnoticed.");

            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var serverGhost = SetupAndSpawn(testWorld, variantType, GhostMode.Interpolated);

            AssertClientMatrixIs(k_PrefabScale, testWorld, quantization);
            SetServerScale(testWorld, serverGhost, k_UpdatedScale);

            // The propagation transient is the only place Smoothing is observable: all three types settle on the
            // same final value, so a variant whose Smoothing was silently dropped would otherwise still pass.
            var sawInBetween = SampleSubTicks(testWorld, ExpectedClientMatrix(k_PrefabScale, quantization), ExpectedClientMatrix(k_UpdatedScale, quantization),
                mustSnap: !interpolates, snapFailure: "Smoothing.Clamp must only ever show discrete snapshot values, so the variant was resolved to an interpolating template.");

            Assert.AreEqual(interpolates, sawInBetween,
                interpolates
                    ? $"Smoothing.{ghostField.Smoothing} never produced a value between the old and the new scale: the variant was resolved to the clamping template."
                    : "Sanity: the clamp assertion above should already have failed.");

            AssertClientMatrixIs(k_UpdatedScale, testWorld, quantization);
        }

        // Interpolate and InterpolateAndExtrapolate share a template (matching only compares the 'interpolated'
        // bit) and extrapolation is enabled separately at codegen time, so it needs its own coverage.
        [Test]
        [Description("InterpolateAndExtrapolate must keep projecting past the newest snapshot, where plain Interpolate must freeze on it.")]
        public void PostTransformMatrix_ExtrapolationSetting_IsApplied(
            [Values(typeof(InterpolateScaleTestVariant), typeof(PostTransformMatrix3DScaleVariant))] Type variantType)
        {
            var extrapolates = GetDeclaredGhostField(variantType).Smoothing == SmoothingAction.InterpolateAndExtrapolate;

            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(ServerScaleRampSystem));
            var serverGhost = SetupAndSpawn(testWorld, variantType, GhostMode.Interpolated);
            testWorld.TickMultiple(32);

            // Ticking the client alone starves it of snapshots while its interpolation target keeps advancing, so it
            // runs off the end of the received data. 10 ticks stays well inside the default
            // MaxExtrapolationTimeSimTicks (20), after which even extrapolation freezes.
            var lastServerScale = GetServerMatrixScale(testWorld, serverGhost);
            for (var i = 0; i < 9; i++)
                testWorld.TickClientWorld();
            var beforeLastTick = GetClientMatrixScale(testWorld);
            testWorld.TickClientWorld();
            var clientScale = GetClientMatrixScale(testWorld);

            if (extrapolates)
                Assert.Greater(clientScale.x, lastServerScale.x,
                    $"Extrapolation was not enabled: the client scale {clientScale} never got beyond the last server scale {lastServerScale}.");
            else
                Assert.AreEqual(beforeLastTick, clientScale,
                    $"Extrapolation was wrongly enabled: Interpolate must freeze on the newest snapshot, but the client scale moved from {beforeLastTick} to {clientScale}.");
        }

        [Test]
        [Description("A GhostField MaxSmoothingDistance makes changes larger than that distance snap instead of interpolate on interpolated clients.")]
        public void PostTransformMatrix_MaxSmoothingDistance_SnapsLargeChanges()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var serverGhost = SetupAndSpawn(testWorld, typeof(MaxSmoothingDistanceScaleTestVariant), GhostMode.Interpolated);

            // Way beyond MaxSmoothingDistance = 1, so interpolation between old and new must be suppressed.
            var newScale = new float3(5f, 6f, 7f);
            SetServerScale(testWorld, serverGhost, newScale);

            var newMatrix = ExpectedClientMatrix(newScale);
            SampleSubTicks(testWorld, ExpectedClientMatrix(k_PrefabScale), newMatrix,
                mustSnap: true, snapFailure: "an in-between value means MaxSmoothingDistance was not applied.");
            Assert.AreEqual(newMatrix, GetClientMatrix(testWorld));
        }

        // The sign is recovered from the matrix diagonal on extraction and re-applied on the receiving side.
        [Test]
        [Description("Negative (mirrored) per-axis scale must replicate, flipping from the positive prefab scale through the established baseline.")]
        public void PostTransformMatrix_NegativeScale_Replicates()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var serverGhost = SetupAndSpawn(testWorld, typeof(PostTransformMatrix3DScaleVariant), GhostMode.Interpolated);
            AssertClientMatrixIs(k_PrefabScale, testWorld);

            var mirroredScale = new float3(-1f, 2f, 3f);
            SetServerScaleAndSettle(testWorld, serverGhost, mirroredScale);
            AssertClientMatrixIs(mirroredScale, testWorld);
        }

        [Test]
        [Description("Applying a replicated scale must change only the scale part of the client's matrix, preserving locally authored translation and rotation (kept below 90° here, so the diagonal-sign convention leaves it intact).")]
        public void PostTransformMatrix_ReplicatedScale_PreservesClientAuthoredRotationAndTranslation()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var serverGhost = SetupAndSpawn(testWorld, typeof(PostTransformMatrix3DScaleVariant), GhostMode.Interpolated);

            // Authored into the client's matrix only.
            var translation = new float3(1f, 2f, 3f);
            var rotation = quaternion.Euler(0.4f, 0.2f, -0.6f);
            testWorld.ClientWorlds[0].EntityManager.SetComponentData(GetClientGhost(testWorld),
                new PostTransformMatrix { Value = float4x4.TRS(translation, rotation, k_PrefabScale) });

            SetServerScaleAndSettle(testWorld, serverGhost, k_UpdatedScale);

            var clientMatrix = GetClientMatrix(testWorld);
            // SetScale never touches the fourth column, so the authored translation survives bit-exactly.
            Assert.AreEqual(new float4(translation, 1f), clientMatrix.c3);
            // Re-applying the replicated (dequantized) scale must change nothing, which also proves the matrix's
            // scale content is exactly that value.
            Assert.AreEqual(clientMatrix, MatrixScaleHelper.SetScale(clientMatrix, DequantizedScale(k_UpdatedScale, 1000)));
            // Rotation is preserved as column directions, which pass through float normalization, so there is no
            // bit-exact analytic expected value for it: bounded check.
            Assert.Less(math.angle(rotation.value, clientMatrix.Rotation().value), 0.000_001f, "Client-authored rotation must be preserved when the replicated scale is applied.");
        }

        // Note: we can probably adapt this to also smooth PostTransformMatrix in another pass if users ask for it.
        [Test]
        [Description("Prediction switch smoothing must keep the non-uniform 3D scale for the whole transition, then land on LocalTransform x PostTransformMatrix.")]
        public void PostTransformMatrix_PredictionSwitchSmoothing_PreservesScaleAndWorldPosition([Values] bool toPredicted)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(ScaledGhostMoveSystem));
            var serverGhost = SetupAndSpawn(testWorld, typeof(PostTransformMatrix3DScaleVariant), toPredicted ? GhostMode.Interpolated : GhostMode.Predicted);

            var clientWorld = testWorld.ClientWorlds[0];
            var em = clientWorld.EntityManager;
            var clientGhost = GetClientGhost(testWorld);
            // Move the ghost so the interpolated view and predicted state diverge: smoothing gets a real offset.
            testWorld.ServerWorld.EntityManager.AddComponent<MoveGhostTag>(serverGhost);
            em.AddComponent<MoveGhostTag>(clientGhost);
            testWorld.TickMultiple(8);

            ref var queues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(clientWorld).ValueRW;
            var entry = new ConvertPredictionEntry { TargetEntity = clientGhost, TransitionDurationSeconds = 0.5f };
            if (toPredicted)
                queues.ConvertToPredictedQueue.Enqueue(entry);
            else
                queues.ConvertToInterpolatedQueue.Enqueue(entry);
            testWorld.Tick();
            Assert.IsTrue(em.HasComponent<SwitchPredictionSmoothing>(clientGhost), "Sanity: the smoothing transition should be running.");

            // Bit-exact because the ghost never rotates (its replicated rotation is exactly identity), so the
            // smoothed LocalToWorld's basis columns stay axis-aligned and their lengths are the dequantized scale.
            var expectedScale = DequantizedScale(k_PrefabScale, 1000);
            var guard = 0;
            while (em.HasComponent<SwitchPredictionSmoothing>(clientGhost))
            {
                Assert.AreEqual(expectedScale, em.GetComponentData<LocalToWorld>(clientGhost).Value.Scale());
                testWorld.Tick();
                Assert.Less(++guard, 128, "The smoothing transition never completed.");
            }

            // LocalToWorld is owned by LocalToWorldSystem again: mirror its own world-space computation.
            testWorld.Tick();
            var ltw = em.GetComponentData<LocalToWorld>(clientGhost);
            var expected = math.mul(em.GetComponentData<LocalTransform>(clientGhost).ToMatrix(), em.GetComponentData<PostTransformMatrix>(clientGhost).Value);
            Assert.AreEqual(expected, ltw.Value);
        }
    }
}

#if UNITY_EDITOR
using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Unity.Netcode.Tests
{
    // All asserts are exact (bit-identical), no epsilons:
    // - Server: the GO->entity capture writes localScale verbatim into an axis-aligned PostTransformMatrix and keeps
    //   LocalTransform.Scale at 1, so the matrix is a pure scale matrix.
    // - Replication quantizes with (int)math.round(v * Q) and dequantizes by multiplying with a codegen-emitted 1f/Q
    //   literal, which matches DequantizedScale below.
    // - These ghosts are Predicted, so the snapshot value is applied as-is (no interpolation lerp).
    // - The client GameObject takes its scale magnitudes from column lengths, exact because sqrt(x*x) == |x|.
    internal class GhostObjectPostTransformMatrixScaleTests
    {
        // Ticks for a server-side change to reach the client and settle, same budget as
        // PostTransformMatrixReplicationTests.k_PredictedSettleTicks. Every ghost here is Predicted; interpolated
        // ghosts need 6 there.
        const int k_SettleTicks = 4;

        // Demonstrates that users can author their own scale variant by reusing the PostTransformMatrixScale subtype.
        // Higher precision (~1e-5) than the built-in PostTransformMatrix3DScaleVariant (~1e-3).
        [GhostComponentVariation(typeof(PostTransformMatrix), displayName: "High Precision Scale Test", isTestVariant: true)]
        internal struct HighPrecisionPostTransformMatrixScaleVariant
        {
            [GhostField(Quantization = 100000, Smoothing = SmoothingAction.InterpolateAndExtrapolate, SubType = GhostFieldSubType.PostTransformMatrixScale)]
            public float4x4 Value;
        }

        // Mirrors the scale template's quantize + dequantize steps bit-for-bit.
        static float3 DequantizedScale(float3 scale, int quantization = 1000)
        {
            var dequantize = 1f / quantization;
            return new float3(
                (int)math.round(scale.x * quantization) * dequantize,
                (int)math.round(scale.y * quantization) * dequantize,
                (int)math.round(scale.z * quantization) * dequantize);
        }

        static float4x4 GetPtm(World world, Entity entity)
            => world.EntityManager.GetComponentData<PostTransformMatrix>(entity).Value;

        static async Task SetupAndConnect(NetCodeTestWorld testWorld)
        {
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
        }

        // variantHash 0 leaves the Variant flag unset, i.e. a PrefabType-only override.
        static PredictionCallbackHelper CreatePrefabWithPtmOverride(string name, ulong variantHash = 0, bool useUniformScale = false)
        {
            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab(name, autoRegister: false);
            prefab.gameObject.GetComponent<GhostObject>().UseUniformScale = useUniformScale;
            AddVariantOverride(prefab, typeof(PostTransformMatrix), variantHash);
            Netcode.RegisterPrefab(prefab.gameObject);
            return prefab;
        }

        // Has to live outside the async tests: `ref` locals aren't allowed in async methods on C# 9.
        static void AddVariantOverride(PredictionCallbackHelper prefab, Type componentType, ulong variantHash)
        {
            var overrides = prefab.gameObject.AddComponent<GhostAuthoringInspectionComponent>();
            ref var componentOverride = ref overrides.GetOrAddPrefabOverride(componentType,
                PrefabsRegistry.EntityGuidFromGameObject(prefab.gameObject), GhostPrefabType.All);
            componentOverride.VariantHash = variantHash;
        }

        static async Task<(PredictionCallbackHelper server, PredictionCallbackHelper client)> SpawnAndReplicate(
            NetCodeTestWorld testWorld, PredictionCallbackHelper prefab, float3 scale, quaternion? rotation = null)
        {
            var server = GameObject.Instantiate(prefab);
            if (rotation.HasValue) server.transform.localRotation = rotation.Value;
            server.transform.localScale = (Vector3)scale;
            await testWorld.TickMultipleAsync(k_SettleTicks);
            Assert.AreEqual(1, PredictionCallbackHelper.ClientInstances.Count, "client ghost count");
            return (server, PredictionCallbackHelper.ClientInstances[0]);
        }

        [Test(Description = "GhostAdapter ghosts replicate their non-uniform 3D scale by default, with no per-prefab configuration.")]
        public async Task NonUniformScale_ReplicatesEndToEnd_ByDefault()
        {
            await using var testWorld = new NetCodeTestWorld();
            await SetupAndConnect(testWorld);

            // No inspection overrides: a plain GhostAdapter prefab should sync PostTransformMatrix automatically.
            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("ptm scale ghost default");
            var scale = new float3(2f, 3f, 4f);
            var (server, client) = await SpawnAndReplicate(testWorld, prefab, scale);

            Assert.AreEqual(float4x4.Scale(scale), GetPtm(testWorld.ServerWorld, server.Ghost.Entity), "server entity");
            Assert.AreEqual(float4x4.Scale(DequantizedScale(scale)), GetPtm(testWorld.ClientWorlds[0], client.Ghost.Entity), "client entity");
            Assert.AreEqual(DequantizedScale(scale), (float3)client.transform.localScale, "client GameObject");
        }

        [Test(Description = "Negative (mirrored) scale replicates, including flipping a sign at runtime on an already-replicated ghost.")]
        public async Task NegativeScale_ReplicatesEndToEnd()
        {
            await using var testWorld = new NetCodeTestWorld();
            await SetupAndConnect(testWorld);

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("ptm negative scale ghost");
            var (server, client) = await SpawnAndReplicate(testWorld, prefab, new float3(1f, 2f, 3f));

            // Flip x at runtime, so the GO->entity capture has to change an already-captured positive scale.
            var mirroredScale = new float3(-1f, 2f, 3f);
            server.transform.localScale = (Vector3)mirroredScale;
            await testWorld.TickMultipleAsync(k_SettleTicks);

            // The GO->entity capture runs every frame, so it must be idempotent for negative scale.
            Assert.AreEqual(float4x4.Scale(mirroredScale), GetPtm(testWorld.ServerWorld, server.Ghost.Entity), "server entity");
            Assert.AreEqual(mirroredScale, (float3)server.transform.localScale, "server GameObject");

            var expectedClientScale = DequantizedScale(mirroredScale);
            Assert.AreEqual(float4x4.Scale(expectedClientScale), GetPtm(testWorld.ClientWorlds[0], client.Ghost.Entity), "client entity");

            // 1:1 with the server's authored axis signs, no mirror shuffled onto another axis.
            Assert.AreEqual(expectedClientScale, (float3)client.transform.localScale, "client GameObject scale");
            Assert.AreEqual(quaternion.identity, (quaternion)client.transform.localRotation, "client GameObject rotation");
        }

        // The only test with a non-identity rotation, which is where the entity->GameObject step has to separate rotation
        // from scale rather than read a diagonal. The ECS-side tests stop at the entity.
        [Test(Description = "A rotated ghost's rotation and scale both arrive 1:1 on the client GameObject.")]
        public async Task RotatedScale_ArrivesOneToOne([Values] bool mirrored)
        {
            await using var testWorld = new NetCodeTestWorld();
            await SetupAndConnect(testWorld);

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab($"ptm rotated scale ghost {mirrored}");

            // 180 degrees around z, written out rather than via quaternion.RotateZ(math.PI) whose components are only
            // approximately 0 and 1: these survive the rotation quantization exactly, keeping the asserts bit-exact.
            var rotation = new quaternion(0f, 0f, 1f, 0f);
            var scale = mirrored ? new float3(-2f, 3f, 4f) : new float3(2f, 3f, 4f);
            var (server, client) = await SpawnAndReplicate(testWorld, prefab, scale, rotation);

            // Only the scale differs between the two sides: the server holds the authored value, the client the
            // quantized round-trip of it.
            Assert.AreEqual(rotation, (quaternion)server.transform.localRotation, "server GameObject rotation");
            Assert.AreEqual(scale, (float3)server.transform.localScale, "server GameObject scale");
            Assert.AreEqual(rotation, (quaternion)client.transform.localRotation, "client GameObject rotation");
            Assert.AreEqual(DequantizedScale(scale), (float3)client.transform.localScale, "client GameObject scale");
        }

        // Regression: the presentation LocalToWorld decomposition used to hand a mirrored basis to orthonormalize, which
        // cannot represent a reflection, so it invented a 180 degree rotation and pushed the mirror onto z.
        [Test(Description = "With rotation excluded from replication, a mirrored scale must not make the client GameObject rotate.")]
        public async Task MirroredScale_DoesNotInventRotation_WhenRotationIsNotReplicated()
        {
            await using var testWorld = new NetCodeTestWorld();
            await SetupAndConnect(testWorld);

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("ptm mirrored scale no rotation", autoRegister: false);
            // PositionScale variant: LocalTransform.Rotation is not replicated, so the client's stays at the prefab
            // identity. The 3D scale still replicates, it lives on the separate PostTransformMatrix.
            AddVariantOverride(prefab, typeof(LocalTransform), GhostVariantsUtility.UncheckedVariantHashNBC(
                typeof(PositionScaleVariant).FullName, typeof(LocalTransform).FullName));
            Netcode.RegisterPrefab(prefab.gameObject);

            var scale = new float3(-2f, 3f, 4f);
            var (_, client) = await SpawnAndReplicate(testWorld, prefab, scale);

            // Asserted first so a failure here points at replication rather than at the GameObject step.
            Assert.AreEqual(quaternion.identity, testWorld.ClientWorlds[0].EntityManager
                .GetComponentData<LocalTransform>(client.Ghost.Entity).Rotation, "client entity rotation");
            Assert.AreEqual(float4x4.Scale(DequantizedScale(scale)), GetPtm(testWorld.ClientWorlds[0], client.Ghost.Entity), "client entity");

            Assert.AreEqual(quaternion.identity, (quaternion)client.transform.localRotation, $"client GameObject rotation expected identity but was {client.transform.localRotation.eulerAngles}");
            Assert.AreEqual(DequantizedScale(scale), (float3)client.transform.localScale, "client GameObject scale");
        }

        // The prediction loop and presentation reach the GameObject's scale by different routes (the PostTransformMatrix
        // directly vs a decomposition of the smoothed LocalToWorld). They must agree, otherwise the scale a user reads
        // in PredictionUpdate differs from the one that ends up rendered.
        [Test(Description = "Inside the prediction loop the client GameObject carries the un-smoothed scale, with the server's authored axis signs.")]
        public async Task NegativeScale_KeepsAuthoredSigns_InsidePredictionLoop()
        {
            await using var testWorld = new NetCodeTestWorld();
            await SetupAndConnect(testWorld);

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("ptm scale in prediction loop");
            var scale = new float3(-2f, 3f, 4f);
            var (_, client) = await SpawnAndReplicate(testWorld, prefab, scale);

            // No tick gating: the scale is constant, so every prediction tick (re-simulated ones included) sees it.
            client.OnPredictionEvent += go => Assert.AreEqual(DequantizedScale(scale), (float3)go.transform.localScale,
                "client GameObject inside the prediction loop");
            await testWorld.TickMultipleAsync(1);

            // Same ghost after presentation: same value, no mirror shuffled onto another axis.
            Assert.AreEqual(DequantizedScale(scale), (float3)client.transform.localScale, "client GameObject after presentation");
        }

        [Test(Description = "UseUniformScale adds no PostTransformMatrix and replicates through LocalTransform.Scale instead, from localScale.x.")]
        public async Task UseUniformScale_SkipsPostTransformMatrix_AndReplicatesUniformScale()
        {
            await using var testWorld = new NetCodeTestWorld();
            await SetupAndConnect(testWorld);

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("ptm uniform scale ghost", autoRegister: false);
            prefab.gameObject.GetComponent<GhostObject>().UseUniformScale = true;
            Netcode.RegisterPrefab(prefab.gameObject);

            var (server, client) = await SpawnAndReplicate(testWorld, prefab, new float3(2f));

            AssertUniformScaleReplicated(testWorld, server, client);
        }

        // PrefabRegistry skips its whole PostTransformMatrix block when UseUniformScale is set, so the override is
        // dropped without a diagnostic: GhostObject.GetDefaultAttachedComponents omits the type, which makes
        // GetPrefabOverrides filter the entry out, and ConvertToGhostPrefab only looks up overrides for components the
        // entity actually has.
        [Test(Description = "UseUniformScale wins over an explicit PostTransformMatrix override, even one asking for the 3D scale variant.")]
        public async Task UseUniformScale_IgnoresPostTransformMatrixOverride([Values] bool requestScaleVariant)
        {
            await using var testWorld = new NetCodeTestWorld();
            await SetupAndConnect(testWorld);

            var variantHash = requestScaleVariant
                ? GhostVariantsUtility.UncheckedVariantHashNBC(typeof(PostTransformMatrix3DScaleVariant).FullName, typeof(PostTransformMatrix).FullName)
                : 0ul;
            var prefab = CreatePrefabWithPtmOverride($"ptm uniform scale override {requestScaleVariant}", variantHash, useUniformScale: true);

            // Non-uniform on purpose: only localScale.x is used, y and z must be dropped.
            var (server, client) = await SpawnAndReplicate(testWorld, prefab, new float3(2f, 3f, 4f));

            AssertUniformScaleReplicated(testWorld, server, client);
        }

        // Expects a ghost authored with localScale.x == 2: no PostTransformMatrix anywhere, uniform scale replicated.
        static void AssertUniformScaleReplicated(NetCodeTestWorld testWorld, PredictionCallbackHelper server, PredictionCallbackHelper client)
        {
            Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<PostTransformMatrix>(server.Ghost.Entity), "server entity has no PostTransformMatrix");
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PostTransformMatrix>(client.Ghost.Entity), "client entity has no PostTransformMatrix");

            var expectedScale = DequantizedScale(new float3(2f)).x;
            Assert.AreEqual(expectedScale, testWorld.ClientWorlds[0].EntityManager
                .GetComponentData<LocalTransform>(client.Ghost.Entity).Scale, "client LocalTransform.Scale");
            Assert.AreEqual(new float3(expectedScale), (float3)client.transform.localScale, "client GameObject");
        }

        [Test(Description = "A PrefabType-only override must not block the GO-layer default: the scale variant is merged into it.")]
        public async Task NonUniformScale_StillReplicates_WithPrefabTypeOnlyOverride()
        {
            await using var testWorld = new NetCodeTestWorld();
            await SetupAndConnect(testWorld);

            var prefab = CreatePrefabWithPtmOverride("ptm scale ghost prefabtype override");
            var scale = new float3(2f, 3f, 4f);
            var (_, client) = await SpawnAndReplicate(testWorld, prefab, scale);

            Assert.AreEqual(float4x4.Scale(DequantizedScale(scale)), GetPtm(testWorld.ClientWorlds[0], client.Ghost.Entity), "client entity");
        }

        [Test(Description = "Overriding the prefab variant to DontSerialize opts out of scale replication.")]
        public async Task NonUniformScale_CanBeOptedOut_ViaDontSerializeOverride()
        {
            await using var testWorld = new NetCodeTestWorld();
            await SetupAndConnect(testWorld);

            var prefab = CreatePrefabWithPtmOverride("ptm scale ghost optout", GhostVariantsUtility.DontSerializeHash);
            var (_, client) = await SpawnAndReplicate(testWorld, prefab, new float3(2f, 3f, 4f));

            // The client keeps its local (prefab) scale of 1, on both the entity and the GameObject.
            Assert.AreEqual(float4x4.Scale(new float3(1f)), GetPtm(testWorld.ClientWorlds[0], client.Ghost.Entity), "client entity");
            Assert.AreEqual(new float3(1f), (float3)client.transform.localScale, "client GameObject");
        }

        [Test(Description = "A user-defined scale variant replicates at its own quantization precision.")]
        public async Task UserDefinedVariant_WithHigherQuantization_Replicates()
        {
            await using var testWorld = new NetCodeTestWorld();
            await SetupAndConnect(testWorld);

            var prefab = CreatePrefabWithPtmOverride("ptm scale ghost custom variant",
                GhostVariantsUtility.UncheckedVariantHashNBC(
                    typeof(HighPrecisionPostTransformMatrixScaleVariant).FullName, typeof(PostTransformMatrix).FullName));

            // Values that land on different points of the 1000 and 100000 grids, so the assert below can only pass if
            // the user's variant was actually selected.
            var scale = new float3(1.23456f, 2.34567f, 3.45678f);
            var (_, client) = await SpawnAndReplicate(testWorld, prefab, scale);

            var expectedHighPrecision = DequantizedScale(scale, 100000);
            Assert.AreNotEqual(DequantizedScale(scale), expectedHighPrecision, "test data must quantize differently on the two grids");
            Assert.AreEqual(float4x4.Scale(expectedHighPrecision), GetPtm(testWorld.ClientWorlds[0], client.Ghost.Entity), "client entity");
        }
    }
}
#endif

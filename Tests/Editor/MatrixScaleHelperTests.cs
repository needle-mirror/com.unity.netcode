using NUnit.Framework;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.Netcode.Tests
{
    internal class MatrixScaleHelperTests
    {
        [Test(Description = "SetScale replaces the per-axis scale while preserving translation and rotation (kept below 90° here, so the diagonal-sign convention leaves it intact).")]
        public void SetScale_ReplacesScale_PreservesRotationAndTranslation()
        {
            var newScale = new float3(4f, 1f, 0.25f);
            var matrix = float4x4.TRS(new float3(10f, -5f, 2.5f), quaternion.Euler(0.3f, 0.5f, -0.2f), new float3(2f, 0.5f, 3f));

            var result = MatrixScaleHelper.SetScale(matrix, newScale);

            Assert.AreEqual(matrix.Translation(), result.Translation());
            Assert.Less(math.angle(matrix.Rotation(), result.Rotation()), 0.000_001f, "rotation");
            // A bit-exact fixed point of the requested scale: re-applying changes nothing, which also proves the
            // matrix's scale content is exactly newScale.
            Assert.AreEqual(result, MatrixScaleHelper.SetScale(result, newScale));

            // Rotation survives as column directions, through float normalization, so these are bounded.
            var scale = result.Scale();
            Assert.AreEqual(newScale, scale, "scale");
        }

        [Test(Description = "A pure scale matrix round-trips exactly, mirrored axes included.")]
        public void PureScale_RoundTrips()
        {
            var scale = new float3(3f, 7f, 0.2f);
            Assert.AreEqual(scale, float4x4.Scale(scale).Scale());
            Assert.AreEqual(scale, MatrixScaleHelper.SignedScaleFromScaleOnlyMatrix(float4x4.Scale(scale)));
            Assert.AreEqual(float4x4.Scale(scale), MatrixScaleHelper.SetScale(float4x4.identity, scale));

            // Unlike TransformHelpers.Scale, the signed extraction keeps mirrored axes.
            var mirrored = new float3(-1f, 2f, -3f);
            Assert.AreEqual(mirrored, MatrixScaleHelper.SignedScaleFromScaleOnlyMatrix(float4x4.Scale(mirrored)));
        }

        // Idempotency matters because the GameObject->entity sync re-applies the same scale every frame.
        [Test(Description = "SetScale round-trips a negative scale, preserves translation, and is idempotent.")]
        public void SetScale_NegativeScale_RoundTripsAndIsIdempotent()
        {
            var mirroredScale = new float3(-1f, 2f, 3f);
            var translation = new float3(10f, -5f, 2.5f);
            var matrix = float4x4.TRS(translation, quaternion.identity, new float3(2f));

            var once = MatrixScaleHelper.SetScale(matrix, mirroredScale);

            Assert.AreEqual(mirroredScale, MatrixScaleHelper.SignedScaleFromScaleOnlyMatrix(once));
            Assert.AreEqual(once, MatrixScaleHelper.SetScale(once, mirroredScale));
            Assert.AreEqual(mirroredScale, MatrixScaleHelper.SignedScaleFromScaleOnlyMatrix(once));
        }

        [Test(Description = "SetScale falls back to axis-aligned directions on a degenerate (zero) matrix.")]
        public void SetScale_DegenerateMatrix_FallsBackToAxisAligned()
        {
            var newScale = new float3(2f, 3f, 4f);

            var result = MatrixScaleHelper.SetScale(default, newScale);

            // A pure scale matrix, except the untouched fourth column, which keeps the degenerate input's zero.
            var expected = float4x4.Scale(newScale);
            expected.c3 = default;
            Assert.AreEqual(expected, result);
            Assert.AreEqual(newScale, result.Scale());
        }
    }
}

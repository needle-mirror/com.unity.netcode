using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Scripting;

namespace Unity.Netcode
{
    /// <summary>
    /// A serialization strategy for <see cref="Unity.Transforms.PostTransformMatrix"/> that replicates only the
    /// non-uniform (3D) scale stored in the matrix, as 3 quantized floats instead of the full 16.
    /// </summary>
    /// <remarks>
    /// <para><see cref="LocalTransform.Scale"/> is uniform only, so per-axis scale lives engine-side in the optional
    /// <see cref="PostTransformMatrix"/>. This variant extracts that scale and re-applies it on the receiving side
    /// (see <see cref="MatrixScaleHelper.SetScale"/>), preserving local translation and rotation - the latter up to
    /// diagonal signs, which the replicated scale owns (see <see cref="MatrixScaleHelper"/>).</para>
    /// <para>Rotation, translation and shear are never sent. Negative (mirrored) scale is replicated; see
    /// <see cref="MatrixScaleHelper"/> for the sign convention and its limits.</para>
    /// <para>Defaults: <see cref="PostTransformMatrix"/> uses <c>DontSerializeVariant</c> so existing projects
    /// pay no extra bandwidth. Opt in per-prefab, or globally via <see cref="DefaultVariantSystemBase"/>.</para>
    /// <para>To write your own variant, follow the usual variant flow but set
    /// SubType=<see cref="GhostFieldSubType.PostTransformMatrixScale"/> to reuse this custom serialization. Replicating a
    /// subset of axes requires a custom template - see our docs. If you need more than scale replicated, reach out so
    /// we can prioritise it.</para>
    /// </remarks>
    [Preserve]
    [GhostComponentVariation(typeof(PostTransformMatrix), "PostTransformMatrix - 3D Scale")]
    [GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
    public struct PostTransformMatrix3DScaleVariant
    {
        /// <summary>
        /// The non-uniform scale encoded in the matrix is replicated with a default quantization unit of 1000
        /// (so roughly 1mm precision per axis).
        /// </summary>
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.InterpolateAndExtrapolate, SubType = GhostFieldSubType.PostTransformMatrixScale)]
        public float4x4 Value;
    }

    /// <summary>
    /// Extraction and application of the per-axis scale stored in a <see cref="PostTransformMatrix"/> or in a <see cref="LocalToWorld"/>'s <see cref="float4x4"/>
    /// <c>TransformHelpers.Scale</c> can't be used for this: it returns unsigned lengths, losing mirrored axes.
    /// </summary>
    /// <remarks>
    /// A matrix does not uniquely encode which axes are mirrored, so <see cref="SignedScaleFromScaleOnlyMatrix"/> takes
    /// the per-axis signs from the diagonal. That is faithful for scale-only matrices, and pairs with a rotation coming
    /// from elsewhere (e.g. the replicated <see cref="LocalTransform.Rotation"/>) rather than one orthonormalized out of
    /// the same matrix - a quaternion cannot carry a mirror, so that would invent a rotation instead.
    /// If the matrix does carry its own rotation, diagonal signs it turns negative (rotations past 90°) are read - and
    /// on <see cref="SetScale"/>, overwritten - as scale signs. Adapt to the convention: read/write scale through this
    /// helper and author mirroring as negative scale, not as 180° rotations.
    /// </remarks>
    public static class MatrixScaleHelper
    {
        /// <summary>
        /// The signed per-axis scale: column lengths, signs from the matrix diagonal. Only faithful for scale-only
        /// matrices (see class remarks).
        /// </summary>
        /// <param name="matrix">The matrix to extract the scale from.</param>
        /// <returns>The signed per-axis scale.</returns>
        public static float3 SignedScaleFromScaleOnlyMatrix(in float4x4 matrix)
        {
            return new float3(
                math.length(matrix.c0.xyz) * (matrix.c0.x < 0f ? -1f : 1f),
                math.length(matrix.c1.xyz) * (matrix.c1.y < 0f ? -1f : 1f),
                math.length(matrix.c2.xyz) * (matrix.c2.z < 0f ? -1f : 1f));
        }

        /// <summary>
        /// Replaces the matrix's per-axis signed scale (per <see cref="SignedScaleFromScaleOnlyMatrix"/>'s convention), preserving its
        /// rotation and translation.
        /// </summary>
        /// <param name="matrix">The matrix to re-scale.</param>
        /// <param name="newScale">The new per-axis scale. Negative values mirror the axis.</param>
        /// <returns>A matrix with the requested scale and the original rotation/translation.</returns>
        public static float4x4 SetScale(in float4x4 matrix, float3 newScale)
        {
            // Dividing a column by its current signed scale re-normalizes and un-mirrors it in one step; sharing
            // SignedScaleFromScaleOnlyMatrix's convention keeps re-applying a negative scale idempotent (no flip-flop).
            var currentScale = SignedScaleFromScaleOnlyMatrix(in matrix);
            var result = matrix;
            result.c0.xyz = ScaleColumn(matrix.c0.xyz, currentScale.x, newScale.x, new float3(1f, 0f, 0f));
            result.c1.xyz = ScaleColumn(matrix.c1.xyz, currentScale.y, newScale.y, new float3(0f, 1f, 0f));
            result.c2.xyz = ScaleColumn(matrix.c2.xyz, currentScale.z, newScale.z, new float3(0f, 0f, 1f));
            return result;
        }

        // Fallback: an axis-aligned direction for degenerate (zero length) columns, e.g. a default/zero matrix.
        static float3 ScaleColumn(float3 column, float currentSignedScale, float newSignedScale, float3 fallbackDirection)
        {
            if (math.abs(currentSignedScale) <= 1e-6f)
                return fallbackDirection * newSignedScale;
            return column / currentSignedScale * newSignedScale;
        }
    }
}

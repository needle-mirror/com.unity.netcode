using Unity.Mathematics;
using UnityEngine.Scripting;

namespace Unity.NetCode
{
    /// <summary>
    /// The default serialization strategy for the <see cref="Unity.Transforms.Translation"/> components provided by the NetCode package.
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.Translation), "Translation - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct TranslationDefaultVariant
    {
        /// <summary>
        /// The translation value is replicated with a default quantization unit of 100 (so roughly 1cm precision per component).
        /// The replicated translation value support both interpolation and extrapolation
        /// </summary>
        [GhostField(Composite=true,Quantization=100, Smoothing=SmoothingAction.InterpolateAndExtrapolate)] public float3 Value;
    }

    /// <summary>
    /// The default serialization strategy for the <see cref="Unity.Transforms.Rotation"/> components provided by the NetCode package.
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.Rotation), "Rotation - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients, SendDataForChildEntity = false)]
    public struct RotationDefaultVariant
    {
        /// <summary>
        /// The rotation quaternion is replicated and the resulting floating point data use for replication the rotation is quantized with good precision (10 or more bits per component)
        /// </summary>
        [GhostField(Composite=true,Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)] public quaternion Value;
    }
}

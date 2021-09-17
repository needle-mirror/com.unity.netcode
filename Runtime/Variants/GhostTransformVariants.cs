using Unity.Mathematics;
using UnityEngine.Scripting;

namespace Unity.NetCode
{
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.Translation))]
    [GhostComponent(PrefabType=GhostPrefabType.All, OwnerPredictedSendType=GhostSendType.All, SendDataForChildEntity = false)]
    public struct TranslationDefaultVariant
    {
        [GhostField(Composite=true,Quantization=100, Smoothing=SmoothingAction.InterpolateAndExtrapolate)] public float3 Value;
    }

    [Preserve]
    [GhostComponentVariation(typeof(Transforms.Rotation))]
    [GhostComponent(PrefabType=GhostPrefabType.All, OwnerPredictedSendType=GhostSendType.All, SendDataForChildEntity = false)]
    public struct RotationDefaultVariant
    {
        [GhostField(Composite=true,Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)] public quaternion Value;
    }
}
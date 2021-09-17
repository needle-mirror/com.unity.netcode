using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.GraphicsIntegration;

namespace Unity.NetCode
{
    /// <summary>
    /// Default serialization variant for the PhysicsVelocity. Necessary to synchronize physics
    /// </summary>
    [GhostComponentVariation(typeof(PhysicsVelocity))]
    [GhostComponent(PrefabType = GhostPrefabType.All, OwnerPredictedSendType = GhostSendType.Predicted, SendDataForChildEntity = false)]
    public struct PhysicsVelocityDefaultVariant
    {
        [GhostField(Quantization = 1000)] public float3 Linear;
        [GhostField(Quantization = 1000)] public float3 Angular;
    }


    /// <summary>
    /// Default serialization variant for the PhysicsGraphicalSmoothing which disables smoothing on interpolated clients.
    /// Ghost are controled by the server rather than physics on interpolated clients, which makes the physics smoothing incorrect.
    /// </summary>
    [GhostComponentVariation(typeof(PhysicsGraphicalSmoothing))]
    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted, SendDataForChildEntity = false)]
    public struct PhysicsGraphicalSmoothingDefaultVariant
    {
    }
}

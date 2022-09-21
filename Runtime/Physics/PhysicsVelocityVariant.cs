using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.GraphicsIntegration;

namespace Unity.NetCode
{
    /// <summary>
    /// Default serialization variant for the PhysicsVelocity. Necessary to synchronize physics
    /// </summary>
    [GhostComponentVariation(typeof(PhysicsVelocity), nameof(PhysicsVelocity))]
    [GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
    public struct PhysicsVelocityDefaultVariant
    {
        /// <summary>
        /// The rigid body linear velocity in world space. Measured in m/s.
        /// </summary>
        [GhostField(Quantization = 1000)] public float3 Linear;
        /// <summary>
        /// The body angular velocity in world space. Measured in radiant/s
        /// </summary>
        [GhostField(Quantization = 1000)] public float3 Angular;
    }


    /// <summary>
    /// Default serialization variant for the PhysicsGraphicalSmoothing which disables smoothing on interpolated clients.
    /// Ghost are controled by the server rather than physics on interpolated clients, which makes the physics smoothing incorrect.
    /// </summary>
    [GhostComponentVariation(typeof(PhysicsGraphicalSmoothing), nameof(PhysicsGraphicalSmoothing))]
    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    public struct PhysicsGraphicalSmoothingDefaultVariant
    {
    }
}

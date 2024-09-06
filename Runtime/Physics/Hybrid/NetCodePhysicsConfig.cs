using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// Component used to enable predicted physics automatic world changing(<see cref="PredictedPhysicsNonGhostWorld"/>) and lag compensation (<see cref="EnableLagCompensation"/>) and
    /// tweak their settings.
    /// At conversion time, a singleton entity is added to the scene/subscene if either one of, or both of the features are enabled, and
    /// the <see cref="PredictedPhysicsNonGhostWorld"/>, <see cref="EnableLagCompensation"/> components are automatically added to it based on these settings.
    /// </summary>
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.NetCodePhysicsConfig)]
    public sealed class NetCodePhysicsConfig : MonoBehaviour
    {
        /// <summary>
        /// Set to true to enable the use of the LagCompensation system. Server and Client will start recording the physics world state in the PhysicsWorldHistory buffer,
        /// which size can be further configured for by changing the ServerHistorySize and ClientHistorySize properites;
        /// </summary>
        [Tooltip("Enable/Disable the LagCompensation system. Server and Client will start recording the physics world state in the PhysicsWorldHistory buffer")]
        public bool EnableLagCompensation;
        /// <summary>
        /// The number of physics world states that are backed up on the server. This cannot be more than the maximum capacity. Leaving it at zero will give you the default (max capacity).
        /// </summary>
        [Tooltip("The number of physics world states that are backed up on the server. This cannot be more than the maximum capacity, and must be 0 (OFF/DISABLED) or a power of two.\n\nLeaving it at zero will give the default (max capacity).")]
        public int ServerHistorySize;
        /// <summary>
        /// The number of physics world states that are backed up on the client. This cannot be more than the maximum capacity. Leaving it at zero will give you the default (of one).
        /// </summary>
        [Tooltip("The number of physics world states that are backed up on the client. This cannot be more than the maximum capacity, and must be 0 (OFF/DISABLED) or a power of two.\n\nLeaving it at zero will disable it.")]
        public int ClientHistorySize;

        /// <summary>
        /// When using predicted physics all dynamic physics objects in the main physics world on the client
        /// mus be ghosts. Setting this will move any non-ghost in the default physics world to another world.
        /// </summary>
        [Tooltip("The physics world index to use for all dynamic physics objects which are not ghosts.")]
        public uint ClientNonGhostWorldIndex = 0;

        /// <inheritdoc cref="LagCompensationConfig.DeepCopyDynamicColliders"/>>
        [Tooltip("Denotes whether or not Netcode will deep copy dynamic colliders into the Lag Compensation CollisionWorld ring buffer used for Lag Compensation.\n\nRecommendation & Default: True.\n\nEnable this if you get exceptions when querying since-destroyed entities.")]
        public bool DeepCopyDynamicColliders = true;

        /// <inheritdoc cref="LagCompensationConfig.DeepCopyStaticColliders"/>>
        [Tooltip("Denotes whether or not Netcode will deep copy static colliders into the Lag Compensation CollisionWorld ring buffer used for Lag Compensation.\n\nEnable if you need perfectly accurate lag compensation query results with static colliders, which is typically only necessary if they occasionally change.\n\nRecommendation & Default: False.\n\nInstead: Run two queries - one against static geometry - then another against the dynamic entities in the historic buffer.")]
        public bool DeepCopyStaticColliders;
    }

    class NetCodePhysicsConfigBaker : Baker<NetCodePhysicsConfig>
    {
        public override void Bake(NetCodePhysicsConfig authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            if (authoring.EnableLagCompensation)
            {
                AddComponent(entity, new LagCompensationConfig
                {
                    ServerHistorySize = authoring.ServerHistorySize,
                    ClientHistorySize = authoring.ClientHistorySize,
                    DeepCopyStaticColliders = authoring.DeepCopyStaticColliders,
                    DeepCopyDynamicColliders = authoring.DeepCopyDynamicColliders,
                });
            }
            if (authoring.ClientNonGhostWorldIndex != 0)
                AddComponent(entity, new PredictedPhysicsNonGhostWorld{Value = authoring.ClientNonGhostWorldIndex});
        }
    }
}

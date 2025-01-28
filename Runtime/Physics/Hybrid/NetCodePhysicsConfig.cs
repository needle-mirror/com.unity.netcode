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
        /// Configure how the PhysicsSystemGroup should update inside the <see cref="PredictedFixedStepSimulationSystemGroup"/>.
        /// By default, this option is set to <see cref="PhysicGroupRunMode.LagCompensationEnabledOrKinematicGhosts"/> (preserve the original behavior).
        /// However, in general, a more correct settings would be to either use <see cref="PhysicGroupRunMode.LagCompensationEnabledOrAnyPhysicsEntities"/>, or <see cref="PhysicGroupRunMode.AlwaysRun"/>.
        /// </summary>
        /// <remarks>
        /// For the client, in particular, because physics can update only if the prediction loop runs,
        /// in order to have this settings be used, it is necessary to configure the PredictedSimulationSystemGroup to always update
        /// (by using the <see cref="ClientTickRate.PredictionLoopUpdateMode"/> property and set that to <see cref="PredictionLoopUpdateMode.AlwaysRun"/>).
        /// </remarks>
        [Tooltip("Configure how the PhysicsSystemGroup should update inside the <b>PredictedFixedStepSimulationSystemGroup</b>.\nBy default, this option is set to <b>PhysicGroupRunMode.LagCompensationEnabledOrKinematicGhosts</b> (preserve the original behavior).\nHowever, in general, a more correct settings would be to either use <b>PhysicGroupRunMode.LagCompensationEnabledOrAnyPhysicsEntities</b>, or <b>PhysicGroupRunMode.AlwaysRun</b>.\n\n<b>For the client, in particular, because physics can update only if the prediction loop runs, in order to have this settings be used, <color=yellow>it is necessary to configure the PredictedSimulationSystemGroup to always update (by using the ClientTickRate.PredictionLoopUpdateMode property and set that to PredictionLoopUpdateMode.AlwaysRun</color>).</b>")]
        public PhysicGroupRunMode PhysicGroupRunMode;
        /// <summary>
        /// Set to true to enable the use of the LagCompensation system. Server and Client will start recording the physics world state in the PhysicsWorldHistory buffer,
        /// which size can be further configured for by changing the ServerHistorySize and ClientHistorySize properties.
        /// </summary>
        [Tooltip("Enable/Disable the LagCompensation system. Server and Client will start recording the physics world state in the PhysicsWorldHistory buffer")]
        public bool EnableLagCompensation;
        /// <inheritdoc cref="LagCompensationConfig.ServerHistorySize"/>
        [Tooltip("The number of physics world states that are backed up on the server. This cannot be more than the maximum capacity (32), and must be a power of two.\n\nLeaving it at zero will give you the default value (16).")]
        public int ServerHistorySize;
        /// <inheritdoc cref="LagCompensationConfig.ClientHistorySize"/>
        [Tooltip("The number of physics world states that are backed up on the client. This cannot be more than the maximum capacity (32), and must be a power of two.\n\nThe default value is 1, but setting it to 0 will disable recording the physics history on the client, reducing CPU and memory consumption.")]
        public int ClientHistorySize = 1;

        /// <summary>
        /// When using predicted physics all dynamic physics objects in the main physics world on the client
        /// must be ghosts. Setting this will move any non-ghost in the default physics world to another world.
        /// </summary>
        [Tooltip("The physics world index to use for all dynamic physics objects which are not ghosts.")]
        public uint ClientNonGhostWorldIndex = 0;

        /// <inheritdoc cref="LagCompensationConfig.DeepCopyDynamicColliders"/>
        [Tooltip("Denotes whether or not Netcode will deep copy dynamic colliders into the Lag Compensation CollisionWorld ring buffer used for Lag Compensation.\n\nRecommendation & Default: True.\n\nEnable this if you get exceptions when querying since-destroyed entities.")]
        public bool DeepCopyDynamicColliders = true;

        /// <inheritdoc cref="LagCompensationConfig.DeepCopyStaticColliders"/>
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
            AddComponent(entity, new PhysicsGroupConfig()
            {
                PhysicsRunMode = authoring.PhysicGroupRunMode
            });
            if (authoring.ClientNonGhostWorldIndex != 0)
                AddComponent(entity, new PredictedPhysicsNonGhostWorld{Value = authoring.ClientNonGhostWorldIndex});
        }
    }
}

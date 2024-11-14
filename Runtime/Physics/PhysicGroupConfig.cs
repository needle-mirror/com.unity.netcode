using System;
using System.Runtime.CompilerServices;
using Unity.Entities;
using UnityEngine.Serialization;

[assembly: InternalsVisibleTo("Unity.NetCode.Physics.Hybrid")]

namespace Unity.NetCode
{
    /// <summary>
    /// Instrument how and when the <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/> inside
    /// the <see cref="PredictedFixedStepSimulationSystemGroup"/> should run.
    /// </summary>
    public enum PhysicGroupRunMode
    {
        /// <summary>
        /// The default option for both the server and client. The <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/> requires
        /// entities with <see cref="PredictedGhost"/> and <see cref="Unity.Physics.PhysicsVelocity"/> components to run.
        /// On the server, If no entities match this query, and the lag compensation is active, the physics group will still run.
        /// </summary>
        /// <remarks>
        /// Be aware of the fact that, when using this setting (which is the default) on the client, when no predicted ghosts are present, the prediction loop does not run, and therefore neither does any part of the physics simulation.
        /// In order to change that, set the <see cref="ClientTickRate.PredictionLoopUpdateMode"/> to <see cref="PredictionLoopUpdateMode.AlwaysRun"/>.
        /// <br/>
        /// If no matching entities, and lag compensation is enabled, the physics loop will only run when the <see cref="NetworkTime.IsFirstTimeFullyPredictingTick"/> is true.
        /// <br/>
        /// If all predicted ghost entities have been destroyed, and lag compensation is not enabled, the collision world information will become stale, meaning that while it contains the latest
        /// computed broadphase tree, we are no longer computing any new broadphase trees. And therefore, entity references stored inside these old broadphase trees may have become invalidated, as well as any references to the associated collider blobs.
        /// </remarks>
        LagCompensationEnabledOrKinematicGhosts,
        /// <summary>
        /// A more relaxed option for both server and client. The <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/> requires
        /// entities with <see cref="Unity.Physics.PhysicsVelocity"/> or <see cref="Unity.Physics.Collider"/> components to run.
        /// In case no entities match this query, but the lag compensation is active, the physics group will update.
        /// </summary>
        /// <remarks>
        /// Be aware of the fact that, when using this setting on the client, when no physics ghosts are present, the prediction loop does not run, and therefore neither does any part of the physics simulation.
        /// In order to change that, set the <see cref="ClientTickRate.PredictionLoopUpdateMode"/> to <see cref="PredictionLoopUpdateMode.AlwaysRun"/>.
        /// <br/>
        /// If no matching entities, and lag compensation is enabled, the physics loop will only run when the <see cref="NetworkTime.IsFirstTimeFullyPredictingTick"/> is true.
        /// <br/>
        /// If all physics entities have been destroyed, and lag compensation is not enabled, the collision world information will become stale, meaning that while it contains the latest
        /// computed broadphase tree, we are no longer computing any new broadphase trees. And therefore, entity references stored inside these old broadphase trees may have become invalidated, as well as any references to the associated collider blobs.
        /// </remarks>
        LagCompensationEnabledOrAnyPhysicsEntities,
        /// <summary>
        /// Allow the physics group to run, even if there aren't physics entities, predicted ghost entities, or lag compensation enabled.
        /// If no physics entities exists, the physics loop run only when the <see cref="NetworkTime.IsFirstTimeFullyPredictingTick"/> is true.
        /// </summary>
        AlwaysRun,
    }
    /// <summary>
    /// Singleton component that allows users to configure whether or not the <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/> runs inside the prediction loop.
    /// </summary>
    internal struct PhysicsGroupConfig : IComponentData
    {
        /// <summary>
        /// Denotes whether or not the physics group should run, even if predicted ghosts are not present in the world.
        /// By default, this settings is <see cref="PhysicGroupRunMode.RequirePredictedGhostsOrLagCompensation"/>.
        /// </summary>
        public PhysicGroupRunMode PhysicsRunMode;
    }
}

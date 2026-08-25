
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Client-only, allows to change how ghost are simulated at runtime by switching in between
    /// interpolated and predicted mode.
    /// <para>
    /// In order to use prediction-switching, ghosts must have their `Supported Ghost Modes` set to `All`
    /// (via <see cref="GhostObject"/>) and the <see cref="GhostObject.DefaultGhostMode"/> should
    /// </para>
    /// <para>When the ghost mode changes, the `timeline` for that specific ghost is affected.
    /// Predicted ghost are ahead of the server, (roughly one round trip time / SimulationTickRate ticks).
    /// Interpolated ghosts are instead running behind the server timeline/last-received tick, by a variable margin
    /// (this depends both on configuration (see <see cref="ClientTickRate"/>) and network condition). By default, it'll usually be 2/3 ticks
    /// behind the last received data.</para>
    /// <para>Because of that, we provide a way to smooth that transition, at the moment limited only to `visuals`,
    /// by interpolating the visual transform (entities' LocalToWorld) toward the new desired value
    /// (<see cref="SwitchPredictionSmoothing"/> component).
    /// </para>
    /// <para>
    /// From a GhostBehaviour point of view, the ghost will be seen as predicted/interpolated (and the component data changed) starting
    /// from the first LateUpdate after the call.
    /// The first <see cref="GhostBehaviour.PredictionUpdate"/> after a ghost has been converted is going to be invoked or skipped accordingly to
    /// requested mode.
    /// When smoothing is applied, for the whole duration of the transition, the GameObject.Transform is affected as following,
    /// trying to mimic GameObject physics' Rigidbody's interpolation behaviour:
    /// <list type="bullet">
    /// <li>In the first LateUpdate after the conversion, the GameObject.Transform (position, rotation and scale) will match the interpolated position.</li>
    /// <li>For all the subsequent GameObject.Update until the transition end, the position, rotation and scale match the interpolated position.</li>
    /// <li>Inside the <see cref="GhostBehaviour.PredictionUpdate"/> the gameObject.transform's position, rotation and scale match the actual gameplay predicted transform values. In the background, those are stored in LocalTransform entities side.</li>
    /// </list>
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>PredictionSwitching is a simple wrapping interface, built on top of the <see cref="GhostPredictionSwitchingQueues"/>.
    /// It is used internally to schedule the mode changes. The <see cref="GhostPredictionSwitchingSystem"/> is responsible to convert
    /// the entity from one mode to the other by:
    /// <list type="bullet">
    /// <li>Adding/Removing components, based on the <see cref="GhostComponentAttribute.PrefabType"/> rule</li>
    /// <li>Adding/Removing the <see cref="PredictedGhost"/> component, that state how the ghost should be simulated</li>
    /// </list>
    /// </para>
    /// </remarks>
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    struct PredictionSwitching
    {
        private EntityQuery m_PredictionQueueEntity;
        private EntityQueryMask m_PredictedGhostMask;
        private EntityQueryMask m_CanSwitchToPredicted;
        private EntityQueryMask m_CanSwitchToInterpolated;
        private EntityQueryMask m_IsSmoothing;

        internal PredictionSwitching(EntityManager entityManager)
        {
            m_PredictionQueueEntity = entityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAllRW<GhostPredictionSwitchingQueues>());
            m_PredictedGhostMask = entityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAll<PredictedGhost>()).GetEntityQueryMask();
            using var builder = new EntityQueryBuilder(Allocator.Temp);
            builder.WithPresent<PredictedGhost>().WithAbsent<PredictionSwitchingSmoothing>();
            m_CanSwitchToInterpolated = builder.Build(entityManager).GetEntityQueryMask();
            builder.Reset();
            builder.WithAbsent<PredictedGhost>().WithAbsent<PredictionSwitchingSmoothing>();
            m_CanSwitchToPredicted = builder.Build(entityManager).GetEntityQueryMask();
            builder.Reset();
            builder.WithPresent<PredictionSwitchingSmoothing>();
            m_IsSmoothing = builder.Build(entityManager).GetEntityQueryMask();
        }
        /// <summary>
        /// Check if the ghost can be converted to a predicted ghost by verifying that:
        /// - the ghost is interpolated
        /// - the ghost is not smoothing a previous mode transition.
        /// </summary>
        /// <param name="ghost"></param>
        /// <returns>true if the interpolated ghost can be converted to be predicted.</returns>
        [ExcludeFromBurstCompatTesting("Use managed types")]
        public bool CanSwitchToPredicted(GhostObject ghost)
        {
            return m_CanSwitchToPredicted.MatchesIgnoreFilter(ghost.Entity);
        }

        /// <summary>
        /// Check if the ghost can be converted to a interpolated ghost by verifying that:
        /// - the ghost is predicted
        /// - the ghost is not smoothing a previous mode transition.
        /// </summary>
        /// <param name="ghost"></param>
        /// <returns>true if the predicted ghost can be converted to interpolated.</returns>
        [ExcludeFromBurstCompatTesting("Use managed types")]
        public bool CanSwitchToInterpolated(GhostObject ghost)
        {
            return m_CanSwitchToInterpolated.MatchesIgnoreFilter(ghost.Entity);
        }

        /// <summary>
        /// Convert an interpolated ghost to a predicted ghost. The ghost must support both interpolated and predicted mode,
        /// and it cannot be owner predicted. Owner predicted ghost are automatically changing prediction mode when the owner is changed.
        /// </summary>
        /// <remarks>
        /// If this method is called from a MonoBehaviour, and the entity is associated to a GhostObject,
        /// all the <see cref="GhostBehaviour.PredictionUpdate"/> are going to be invoked this frame.
        /// If this method is called from a System, running after the <see cref="GhostPredictionSmoothingSystem"/>, the ghost conversion will occur
        /// the next frame, thus the ghost will be still be seen as interpolated for the whole world update.
        /// <para>
        /// From a GhostBehaviour perspective the ghost is going to be seen as:
        /// - `Interpolated` in the next Update
        /// - `Predicted` in the next LateUpdate
        /// </para>
        /// </remarks>
        /// <param name="ghost"></param>
        /// <param name="transitionDuration"></param>
        /// <returns></returns>
        [ExcludeFromBurstCompatTesting("Use managed types")]
        public bool ToPredicted(GhostObject ghost, float transitionDuration=0)
        {
            if (!CheckModeSwitchingIsAllowed(ghost))
                return false;

            ToPredicted(ghost.Entity, transitionDuration);
            return true;
        }

        /// <summary>
        /// Convert a predicted ghost to an interpolated ghost. The ghost must support both interpolated and predicted mode,
        /// and it cannot be owner predicted. Owner predicted ghost are automatically changing prediction mode when the owner is changed.
        /// </summary>
        /// <remarks>
        /// If this method is called from a MonoBehaviour, and the entity is associated to a GhostObject,
        /// all the <see cref="GhostBehaviour.PredictionUpdate"/> are not going to be invoked this frame.
        /// If this method is called from a System, running after the <see cref="GhostPredictionSmoothingSystem"/>, the ghost conversion will occur
        /// the next frame, thus the ghost will be still be seen as predicted for the whole world update.
        /// <para>
        /// From a GhostBehaviour perspective the ghost is going to be seen as:
        /// - `Predicted` in the next Update
        /// - `Interpolated` in the next LateUpdate
        /// </para>
        /// </remarks>
        /// <param name="ghost"></param>
        /// <param name="transitionDuration"></param>
        /// <returns></returns>
        [ExcludeFromBurstCompatTesting("Use managed types")]
        public bool ToInterpolated(GhostObject ghost, float transitionDuration=0)
        {
            if (!CheckModeSwitchingIsAllowed(ghost))
                return false;

            ToInterpolated(ghost.Entity, transitionDuration);
            return true;
        }

        /// <summary>
        /// Denote if the ghost has been converted to either interpolated or predicted and if it
        /// is still smoothing the transition (visually).
        /// </summary>
        /// <param name="ghost"></param>
        /// <returns></returns>
        [ExcludeFromBurstCompatTesting("Use managed types")]
        public bool IsSmoothingModeChange(GhostObject ghost)
        {
            if (ghost.World == null || !ghost.World.IsCreated)
                return false;
            return m_IsSmoothing.MatchesIgnoreFilter(ghost.Entity);
        }

        bool CheckModeSwitchingIsAllowed(GhostObject ghost)
        {
            if (ghost.World == null || !ghost.World.IsCreated)
                return false;

            if (!ghost.World.IsClient())
            {
                UnityEngine.Debug.LogError($"It is invalid to switch prediction mode for ghost {ghost.name} with entity:" +
                                           $"{ghost.Entity} being a server game-object bound to world {ghost.World.Name}.");
                return false;
            }

            if (ghost.SupportedGhostModes != GhostModeMask.All)
            {
                UnityEngine.Debug.LogError($"It is invalid to switch prediction for ghost {ghost.name} " +
                                           $"because the ghost has been authored to support only {ghost.SupportedGhostModes} mode." +
                                           $"The ghost mode must be set to {GhostModeMask.All} in the GhostAuthoringComponent to enable prediction switching");
                return false;
            }

            if (ghost.DefaultGhostMode == GhostMode.OwnerPredicted)
            {
                UnityEngine.Debug.LogError($"It is invalid to switch prediction for ghost {ghost.name} " +
                                           $"because the ghost has been authored to use {GhostMode.OwnerPredicted} mode." +
                                           $"OwnerPredicted ghost change prediction mode based on the owner automatically.");
                return false;
            }
            return true;
        }
        /// <summary>
        /// Convert an interpolated ghost to a predicted ghost. The ghost must support both interpolated and predicted mode,
        /// and it cannot be owner predicted. Owner predicted ghost are automatically changing prediction mode when the owner is changed.
        /// </summary>
        /// <remarks>
        /// If this method is called from a MonoBehaviour, and the entity is associated to a GhostObject,
        /// all the <see cref="GhostBehaviour.PredictionUpdate"/> are going to be invoked this frame.
        /// If this method is called from a System, running after the <see cref="GhostPredictionSmoothingSystem"/>, the ghost conversion will occur
        /// the next frame, thus the ghost will be still be seen as interpolated for the whole world update.
        /// <para>
        /// From a GhostBehaviour perspective the ghost is going to be seen as:
        /// - `Interpolated` in the next Update
        /// - `Predicted` in the next LateUpdate
        /// </para>
        /// </remarks>
        /// <param name="entity"></param>
        /// <param name="transitionDuration"></param>
        /// <returns></returns>
        public bool ToPredicted(Entity entity, float transitionDuration=0)
        {
            if (m_PredictedGhostMask.MatchesIgnoreFilter(entity))
                return true;

            m_PredictionQueueEntity.GetSingletonRW<GhostPredictionSwitchingQueues>().ValueRW.
                ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = entity,
                    TransitionDurationSeconds = transitionDuration
                });
            return true;
        }

        /// <summary>
        /// Convert an predicted ghost to an interpolated ghost. The ghost must support both interpolated and predicted mode,
        /// and it cannot be owner predicted. Owner predicted ghost are automatically changing prediction mode when the owner is changed.
        /// </summary>
        /// <remarks>
        /// If this method is called from a MonoBehaviour, and the entity is associated to a GhostObject,
        /// all the <see cref="GhostBehaviour.PredictionUpdate"/> are not going to be invoked this frame.
        /// If this method is called from a System, running after the <see cref="GhostPredictionSmoothingSystem"/>, the ghost conversion will occur
        /// the next frame, thus the ghost will be still be seen as predicted for the whole world update.
        /// <para>
        /// From a GhostBehaviour perspective the ghost is going to be seen as:
        /// - `Predicted` in the next Update
        /// - `Interpolated` in the next LateUpdate
        /// </para>
        /// </remarks>
        /// <param name="entity"></param>
        /// <param name="transitionDuration"></param>
        /// <returns></returns>
        public bool ToInterpolated(Entity entity, float transitionDuration=0)
        {
            if (!m_PredictedGhostMask.MatchesIgnoreFilter(entity))
                return true;

            m_PredictionQueueEntity.GetSingletonRW<GhostPredictionSwitchingQueues>().ValueRW.
                ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = entity,
                    TransitionDurationSeconds = transitionDuration
                });
            return true;
        }

        /// <summary>
        /// Denote if the ghost has been converted to either interpolated or predicted and if it
        /// is still smoothing the transition (visually).
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        public bool IsSmoothingModeChange(Entity entity)
        {
            return m_IsSmoothing.MatchesIgnoreFilter(entity);
        }
    }

#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    static class GhostObjectPredictionSwitchExtension
    {
        /// <inheritdoc cref="PredictionSwitching.ToPredicted(GhostObject, float)"/>
        /// <param name="transitionTime"></param>
        /// <returns>true if the ghost can switch mode.</returns>
        public static bool ConvertToPredicted(this GhostObject self, float transitionTime = 0f)
        {
            if (!self.World.ExistsAndIsCreated() || self.World.IsServer())
                return false;

            if (!self.World.PredictionSwitching.CanSwitchToPredicted(self))
                return false;

            self.World.PredictionSwitching.ToPredicted(self.Entity, transitionTime);
            return true;
        }

        /// <inheritdoc cref="PredictionSwitching.ToInterpolated(GhostObject, float)"/>
        /// <param name="transitionTime"></param>
        /// <returns>true if the ghost can switch mode.</returns>
        public static bool ConvertToInterpolated(this GhostObject self, float transitionTime = 0f)
        {
            if (!self.World.ExistsAndIsCreated() || self.World.IsServer())
                return false;

            if (!self.World.PredictionSwitching.CanSwitchToInterpolated(self))
                return false;

            self.World.PredictionSwitching.ToInterpolated(self.Entity, transitionTime);
            return true;
        }
    }
}

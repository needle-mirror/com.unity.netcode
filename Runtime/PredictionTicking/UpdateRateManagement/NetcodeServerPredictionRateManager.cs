using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Prediction group's rate manager. Since the parent simulation group is in charge of tick rate, this is mostly a passthrough in charge of setting the right flags on networkTime
    /// </summary>
    class NetcodeServerPredictionRateManager : IRateManager
    {
        private EntityQuery m_NetworkTimeQuery;
        private TickRateManagerStrategy m_Runner;

        const NetworkTimeFlags k_ServerPredictionFlags = NetworkTimeFlags.IsInPredictionLoop |
            NetworkTimeFlags.IsFirstPredictionTick |
            NetworkTimeFlags.IsFinalPredictionTick |
            NetworkTimeFlags.IsFinalFullPredictionTick |
            NetworkTimeFlags.IsFirstTimeFullyPredictingTick;

        internal NetcodeServerPredictionRateManager(ComponentSystemGroup group)
        {
            m_NetworkTimeQuery = group.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            m_Runner = new RunOnce() {ShouldRun = (_)=>true, OnEnterSystemGroup = OnEnterPredictionLoopForFirstTime, OnExitSystemGroup = OnExitPredictionLoop};
        }

        void OnEnterPredictionLoopForFirstTime(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            networkTime.Flags |= k_ServerPredictionFlags;
        }

        void OnExitPredictionLoop(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            // Reset all the prediction flags. They are not valid outside the prediction loop
            networkTime.Flags &= ~k_ServerPredictionFlags;
        }

        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            return m_Runner.Update(group);
        }
        public float Timestep
        {
            get
            {
                throw new System.NotImplementedException();
            }
            set
            {
                throw new System.NotImplementedException();
            }
        }
    }
}

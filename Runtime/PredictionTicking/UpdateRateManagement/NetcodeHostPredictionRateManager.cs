using Unity.Entities;

namespace Unity.NetCode
{
    class NetcodeHostPredictionRateManager : IRateManager
    {
        private EntityQuery m_NetworkTimeQuery;
        private EntityQuery m_ClientServerTickRateQuery;
        private TickRateManagerStrategy m_Runner;
        private NetcodeTimeTracker m_TimeTracker;

        const NetworkTimeFlags k_ServerPredictionFlags = NetworkTimeFlags.IsInPredictionLoop |
            NetworkTimeFlags.IsFirstPredictionTick |
            NetworkTimeFlags.IsFinalPredictionTick |
            NetworkTimeFlags.IsFinalFullPredictionTick |
            NetworkTimeFlags.IsFirstTimeFullyPredictingTick;

        internal NetcodeHostPredictionRateManager(ComponentSystemGroup group, NetcodeTimeTracker timeTracker)
        {
            m_NetworkTimeQuery = group.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            m_ClientServerTickRateQuery = group.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ClientServerTickRate>());
            m_TimeTracker = timeTracker;
            m_Runner = new RunMultiple() { ShouldRunFirstTime = ShouldRun, ShouldContinueRun = ShouldRun, OnEnterSystemGroup = OnEnterPredictionLoopForFirstTime, OnExitSystemGroup = OnExitPredictionLoop,  OnSubsequentRuns = OnSubsequentLoops};
        }

        bool ShouldRun(ComponentSystemGroup group)
        {
            // This is initialized from parent simulation system group
            // This only applies on host where prediction group runs multiple times inside a single SimulationSystemGroup run.
            return m_TimeTracker.RemainingTicksToRun > 0;
        }

        void OnEnterPredictionLoopForFirstTime(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            m_ClientServerTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            // We want current tick to be accurate even before running the prediction system group. So the first UpdateNetworkTime is run by the parent SimulationSystemGroup, not this group
            m_TimeTracker.RemainingTicksToRun--;
            var dt = m_TimeTracker.GetDeltaTimeForCurrentTick(tickRate);
            // Host side this is only done in the prediction, as we want the real frame deltaTime outside prediction, for other client systems like interpolation
            m_TimeTracker.PushTime(group, dt, networkTime);

            networkTime.Flags |= k_ServerPredictionFlags;
        }

        void OnSubsequentLoops(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            m_ClientServerTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            m_TimeTracker.PopTime(group);
            m_TimeTracker.UpdateNetworkTime(group, tickRate, ref networkTime);
            m_TimeTracker.RemainingTicksToRun--;
            var dt = m_TimeTracker.GetDeltaTimeForCurrentTick(tickRate);
            m_TimeTracker.PushTime(group, dt, networkTime);
        }

        void OnExitPredictionLoop(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;

            m_TimeTracker.PopTime(group);
            // Reset all the prediction flags. They are not valid outside the prediction loop
            networkTime.Flags &= ~k_ServerPredictionFlags;
        }

        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            return m_Runner.Update(group);

            // containing system group already updated with appropriate initial time setup.
            // if I'm before the prediction loop, frame systems should have "here's the tick that's about to get simulated this frame"
            // if I'm after, frame systems should have "here's the tick that just got simulated". Input target tick should be +1 since we're accumulating inputs for the next tick
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

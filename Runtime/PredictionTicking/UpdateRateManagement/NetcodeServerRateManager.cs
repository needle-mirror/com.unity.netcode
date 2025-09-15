using System;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// Server world's main update rate manager. Determines whether simulation system group should run or not, depending on tick rate and
    /// accumulator logic.
    /// Host side, Simulation Group runs at frame rate. Prediction Group runs at tick rate
    /// DGS side, Simulation Group runs at tick rate, Prediction Group runs at same tick rate (pass through).
    /// </summary>
    public class NetcodeServerRateManager : IRateManager
    {
        private EntityQuery m_NetworkTimeQuery;
        private EntityQuery m_ClientSeverTickRateQuery;

        private RunMultiple m_Runner;
        internal NetcodeTimeTracker TimeTracker;
        ComponentSystemGroup m_Group;

        internal NetcodeServerRateManager(ComponentSystemGroup group)
        {
            m_Group = group;

            // Create the queries for singletons
            m_NetworkTimeQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            m_ClientSeverTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ClientServerTickRate>());

            m_Runner = new RunMultiple() { ShouldRunFirstTime = ShouldEnterSystemGroupFirstTime, ShouldContinueRun = ShouldContinueRun, OnEnterSystemGroup = OnEnterServerFrame, OnSubsequentRuns = OnSubsequentRuns, OnExitSystemGroup = OnExitServerFrame};
            TimeTracker = new NetcodeTimeTracker(group);
        }

        bool ShouldEnterSystemGroupFirstTime(ComponentSystemGroup group)
        {
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;

            var updateCountThisFrame = TimeTracker.RefreshUpdateCount(group.World.Time.DeltaTime, tickRate.SimulationFixedTimeStep, tickRate.MaxSimulationStepsPerFrame, tickRate.MaxSimulationStepBatchSize);

            networkTime.NumPredictedTicksExpected = updateCountThisFrame.TotalSteps;
            var shouldRun = TimeTracker.InitializeNetworkTimeForFrame(group, tickRate, updateCountThisFrame);
            return shouldRun;
        }

        bool ShouldContinueRun(ComponentSystemGroup group)
        {
            return TimeTracker.RemainingTicksToRun > 0;
        }

        void OnEnterServerFrame(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            // No pop needed when running for the first time
            TimeTracker.UpdateNetworkTime(group, tickRate, ref networkTime);
            TimeTracker.RemainingTicksToRun--;
            var dt = TimeTracker.GetDeltaTimeForCurrentTick(tickRate);
            TimeTracker.PushTime(group, dt, networkTime);
        }

        void OnSubsequentRuns(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            TimeTracker.PopTime(group);

            TimeTracker.UpdateNetworkTime(group, tickRate, ref networkTime);
            // TODO-2.0 make breaking change and have this be frame time? In sleep mode this wouldn't change much and in busyWait, it'd be the actual frame time, not tickDeltaTime
            TimeTracker.RemainingTicksToRun--;
            var dt = TimeTracker.GetDeltaTimeForCurrentTick(tickRate);
            TimeTracker.PushTime(group, dt, networkTime);
        }

        void OnExitServerFrame(ComponentSystemGroup group)
        {
            // To stay consistent with previous server logic, pushing and popping time in server group
            TimeTracker.PopTime(group);
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            networkTime.NumPredictedTicksExpected = 0;
        }

        /// <summary>
        /// Internal (do not use) method called on system group edges, to determine if should enter or exit
        /// </summary>
        /// <param name="group">group</param>
        /// <returns>If the group should update</returns>
        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            return m_Runner.Update(group);
        }

        /// <summary>
        /// Internal
        /// </summary>
        /// <exception cref="NotImplementedException">NotImplementedException</exception>
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

        /// <summary>
        /// <para>
        /// IMPORTANT: This method is obsolete and you should prefer using <see cref="NetworkTime.IsOffFrame"/> instead. This method will be removed in upcoming versions.
        /// </para>
        /// <para>
        /// Utility method to help determine if the server <see cref="SimulationSystemGroup"/> will update this frame or not. This should only be valid when <see cref="ClientServerTickRate.TargetFrameRateMode"/> is set to <see cref="ClientServerTickRate.FrameRateMode.BusyWait"/>
        /// This can be useful if your host's rate mode is set to BusyWait and you want to do client operations during frames where your server isn't ticking.
        /// Ex: for a tick rate of 60Hz and a frame rate of 120Hz, a client hosted server would execute 2 frames for every tick. In other words, your game would be
        /// less busy one frame out of two. This can be used to do extra operations.
        /// This method can be accessible through the server's rate manager.
        /// </para>
        /// </summary>
        /// <example>
        /// [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
        /// [UpdateInGroup(typeof(InitializationSystemGroup))]
        /// public partial class DoExtraWorkSystem : SystemBase
        /// {
        ///     protected override void OnUpdate()
        ///     {
        ///         var serverRateManager = ClientServerBootstrap.ServerWorld.GetExistingSystemManaged&lt;SimulationSystemGroup&gt;().RateManager as NetcodeServerRateManager;
        ///         if (!serverRateManager.WillUpdate())
        ///             DoExtraWork(); // We know this frame will be less busy, we can do extra work
        ///     }
        /// }
        /// </example>
        /// <returns>Whether the server's simulation system group will update this frame or not</returns>
        [Obsolete("Prefer using NetworkTime.IsOffFrame")]
        public bool WillUpdate()
        {
            return WillUpdateInternal();
        }

        /// <summary>
        /// Non obsolete method for our own internal usage. Should keep this even once we remove the above obsolete method.
        /// </summary>
        /// <returns></returns>
        internal bool WillUpdateInternal()
        {
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            if (TimeTracker.ShouldSleep(tickRate))
            {
                Debug.LogWarning($"Testing if will update when {nameof(ClientServerTickRate.TargetFrameRateMode)} is set to {nameof(ClientServerTickRate.FrameRateMode.Sleep)}. This will always return true.");
            }

            return TimeTracker.GetUpdateCountReadonly(m_Group.World.Time.DeltaTime, tickRate.SimulationFixedTimeStep, tickRate.MaxSimulationStepsPerFrame, tickRate.MaxSimulationStepBatchSize).TotalSteps > 0;
        }
    }
}

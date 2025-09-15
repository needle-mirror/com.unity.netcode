using System;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Assertions;

namespace Unity.NetCode
{
    /// <summary>
    /// Same as <see cref="NetcodeServerRateManager"/>, but there's a bit more work to do to setup <see cref="NetworkTime.ServerTick"/> when in non-predicting frames (in other words; off frames).
    /// Off frames should have their <see cref="NetworkTime.InputTargetTick"/> set to +1, as they are accumulating inputs for the next tick.
    /// ServerTick should remain the same, so that we know the current state is associated with which tick.
    /// </summary>
    /// Example
    /// | Tick 10 |       |       |          |       |       | Tick 11          |           |           |
    /// | frame   | frame | frame | frame    | frame | frame | frame            | frame     |           |
    /// |         |       |       | input 11 |       |       | consume input 11 |           |           |
    /// |         |       |       |          |       |       | lerp 10.2        | lerp 10.4 | lerp 10.6 |
    class NetcodeHostRateManager : IRateManager
    {
        EntityQuery m_NetworkTimeQuery;
        EntityQuery m_ClientSeverTickRateQuery;
        RunOnce m_Runner;
        internal NetcodeTimeTracker TimeTracker;
        ComponentSystemGroup m_Group;

        internal NetcodeHostRateManager(ComponentSystemGroup group)
        {
            m_Group = group;
            m_ClientSeverTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ClientServerTickRate>());
            m_NetworkTimeQuery = group.World.EntityManager.CreateEntityQuery(typeof(NetworkTime));

            m_Runner = new RunOnce() { ShouldRun = (_)=>true, OnEnterSystemGroup = OnEnterSimulationGroup, OnExitSystemGroup = OnExitSimulationGroup};
            TimeTracker = new NetcodeTimeTracker(group);
        }

        void OnEnterSimulationGroup(ComponentSystemGroup group)
        {
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;

            if (tickRate.TargetFrameRateMode == ClientServerTickRate.FrameRateMode.Sleep)
            {
                Debug.LogError($"{nameof(tickRate.TargetFrameRateMode)} set to {nameof(ClientServerTickRate.FrameRateMode.Sleep)} is invalid on a single world host and will be ignored");
                // TODO-Release should handle this for battery based devices like mobile
            }

            // doing a precheck. Other methods do initialization steps we don't want.
            // This needs to update accumulatedTime, even if we're not actually executing a tick this frame
            var updateCountThisFrame = TimeTracker.RefreshUpdateCount(group.World.Time.DeltaTime, tickRate.SimulationFixedTimeStep, tickRate.MaxSimulationStepsPerFrame, tickRate.MaxSimulationStepBatchSize);
            networkTime.NumPredictedTicksExpected = updateCountThisFrame.TotalSteps;
            if (updateCountThisFrame.TotalSteps > 0)
            {
                // tick is gonna happen this frame, doing the precalculation step for prediction group so that whole frame is setup with current network time context
                var shouldRunTick = TimeTracker.InitializeNetworkTimeForFrame(group, tickRate, updateCountThisFrame);
                Assert.IsTrue(shouldRunTick, "sanity check failed! we're assuming we are running a tick here");

                TimeTracker.UpdateNetworkTime(group, tickRate, ref networkTime);
            }
        }

        void OnExitSimulationGroup(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            networkTime.NumPredictedTicksExpected = 0;
        }

        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            // We need network tick context for the current frame
            // there's a chance no tick runs at all this frame
            return m_Runner.Update(group);
        }

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

        public float Timestep {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }
    }
}

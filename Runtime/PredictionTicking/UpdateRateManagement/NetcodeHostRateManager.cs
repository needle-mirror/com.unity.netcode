using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
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
    /// | Tick 10 |       |          |       |       | Tick 11          |           |           |
    /// | frame   | frame | frame    | frame | frame | frame            | frame     |           |
    /// |         |       | input 11 |       |       | consume input 11 |           |           |
    /// |         |       |          |       |       | lerp 10.2        | lerp 10.4 | lerp 10.6 |
    class NetcodeHostRateManager : IRateManager
    {
        EntityQuery m_NetworkTimeQuery;
        EntityQuery m_ClientSeverTickRateQuery;
        EntityQuery m_ClientTickRateQuery;
        RunOnce m_Runner;
        internal NetcodeTimeTracker TimeTracker;
        ComponentSystemGroup m_Group;

        internal NetcodeHostRateManager(ComponentSystemGroup group)
        {
            m_Group = group;
            m_ClientSeverTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ClientServerTickRate>());
            m_ClientTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ClientTickRate>());
            m_NetworkTimeQuery = group.World.EntityManager.CreateEntityQuery(typeof(NetworkTime));

            m_Runner = new RunOnce() { ShouldRun = (_)=>true, OnEnterSystemGroup = OnEnterSimulationGroup, };
            TimeTracker = new NetcodeTimeTracker(group);
        }

        void OnEnterSimulationGroup(ComponentSystemGroup group)
        {
            Netcode.Instance.m_ActiveWorld = (NetcodeWorld)group.World;
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();
            if (!m_ClientTickRateQuery.TryGetSingleton<ClientTickRate>(out var clientTickRate))
                clientTickRate = NetworkTimeSystem.DefaultClientTickRate;

            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;

            // TODO - Add support for ClampPartialTicksThreshold.
            var updateCountThisFrame = TimeTracker.RefreshUpdateCount(group.World.Time.DeltaTime, tickRate.SimulationFixedTimeStep, tickRate.MaxSimulationStepsPerFrame, tickRate.MaxSimulationStepBatchSize);
            networkTime.NumPredictedTicksExpected = updateCountThisFrame.TotalSteps;
            networkTime.PredictedTickIndex = 0;
            var shouldRunTick = TimeTracker.InitializeNetworkTimeForFrame(group, tickRate, updateCountThisFrame);
            if (updateCountThisFrame.TotalSteps > 0)
            {
                Assert.IsTrue(shouldRunTick, "sanity check failed! we're assuming we are running a tick here");
                TimeTracker.UpdateNetworkTime(group, tickRate, ref networkTime);
            }
            else Assert.IsFalse(shouldRunTick, "sanity check failed! we're assuming we are not running a tick");

            UpdateHostInterpolation(ref networkTime, tickRate, clientTickRate, TimeTracker);
        }

        internal static void UpdateHostInterpolation(ref NetworkTime networkTime, in ClientServerTickRate tickRate,
            in ClientTickRate clientTickRate, NetcodeTimeTracker timeTracker)
        {
            // The InterpolationTick is simply one tick behind.
            networkTime.InterpolationTick = networkTime.ServerTick;
            networkTime.InterpolationTick.Subtract(1);
            networkTime.InterpolationTickFraction = (timeTracker.AccumulatedTime / tickRate.SimulationFixedTimeStep);
            // TODO - Note that the InterpolationTickFraction could be bumped forward by a fraction of a tick - without
            // introducing extrapolation - if we can be confident the off ticks wont push the fraction over 1.
            // E.g. |-------------------|
            //      ^a        ^b
            // If "on frame" a always occurs at 0.0f, and "on frame" b always occurs at 0.5f, we could bump the
            // InterpolationTickFraction by 0.5f on average, without introducing extrapolation. Though, as frame timings
            // are not consistent, we'd need to update this value dynamically, with smoothing.

            // Off frames bump the input latency by one tick.
            // But other than that nuance, we don't need to worry about ping affecting the effective number of forced input latency ticks.
            // It "just works" with single world host.
            networkTime.EffectiveInputLatencyTicks = clientTickRate.ForcedInputLatencyTicks + (networkTime.IsOffFrame ? 1u : 0u);
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

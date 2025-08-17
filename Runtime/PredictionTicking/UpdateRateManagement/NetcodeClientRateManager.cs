using Unity.Core;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// Store the previous tick and fraction. Used by client to calculated the network elapsed deltatime
    /// </summary>
    internal struct PreviousServerTick : IComponentData
    {
        public NetworkTick Value;
        public float Fraction;
    }

    class NetcodeClientRateManager : IRateManager
    {
        private EntityQuery m_NetworkTimeQuery;
        private EntityQuery m_UnscaledTimeQuery;
        private EntityQuery m_PreviousServerTickQuery;
        private EntityQuery m_ClientSeverTickRateQuery;
        private EntityQuery m_NetworkStreamInGameQuery;
        private EntityQuery m_NetworkTimeSystemDataQuery;
        private EntityQuery m_NetDebugQuery;
        private readonly PredictedFixedStepSimulationSystemGroup m_PredictedFixedStepSimulationSystemGroup;

        private bool m_DidPushTime;
        internal NetcodeClientRateManager(ComponentSystemGroup group)
        {
            // Create the queries for singletons
            m_NetworkTimeQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            m_UnscaledTimeQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<UnscaledClientTime>());
            m_PreviousServerTickQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<PreviousServerTick>());
            m_ClientSeverTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ClientServerTickRate>());
            m_NetworkStreamInGameQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamInGame>());
            m_NetworkTimeSystemDataQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkTimeSystemData>());
            m_NetDebugQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetDebug>());
            m_PredictedFixedStepSimulationSystemGroup = group.World.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>();

            var netTimeEntity = group.World.EntityManager.CreateEntity(
                ComponentType.ReadWrite<NetworkTime>(),
                ComponentType.ReadWrite<UnscaledClientTime>(),
                ComponentType.ReadWrite<PreviousServerTick>(),
                ComponentType.ReadWrite<GhostSnapshotLastBackupTick>());
            group.World.EntityManager.SetName(netTimeEntity, "NetworkTimeSingleton");

            m_UnscaledTimeQuery.SetSingleton(new UnscaledClientTime
            {
                UnscaleElapsedTime = group.World.Time.ElapsedTime,
            });
        }
        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            if (m_DidPushTime)
            {
                group.World.PopTime();
                m_DidPushTime = false;
                return false;
            }

            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();
            if (m_PredictedFixedStepSimulationSystemGroup != null)
                m_PredictedFixedStepSimulationSystemGroup.ConfigureTimeStep(tickRate);

            var networkTimeSystemData = m_NetworkTimeSystemDataQuery.GetSingleton<NetworkTimeSystemData>();
            // Calculate update time based on values received from the network time system
            var curServerTick = networkTimeSystemData.predictTargetTick;
            var curInterpoationTick = networkTimeSystemData.interpolateTargetTick;
            var serverTickFraction = networkTimeSystemData.subPredictTargetTick;
            var interpolationTickFraction = networkTimeSystemData.subInterpolateTargetTick;

            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            ref var unscaledTime = ref m_UnscaledTimeQuery.GetSingletonRW<UnscaledClientTime>().ValueRW;
            ref var previousServerTick = ref m_PreviousServerTickQuery.GetSingletonRW<PreviousServerTick>().ValueRW;
            var currentTime = group.World.Time;

            // If the tick is within ±5% of a frame from matching a tick - just use the actual tick instead
            if (curServerTick.IsValid && tickRate.ClampPartialTicksThreshold > 0)
            {
                var fClamp = tickRate.ClampPartialTicksThreshold * 0.01f;
                var oneOverFClamp = 1 - fClamp;
                if (serverTickFraction < fClamp)
                    serverTickFraction = 1;
                else
                    curServerTick.Increment();
                if (serverTickFraction > oneOverFClamp)
                    serverTickFraction = 1;
                if (interpolationTickFraction < fClamp)
                    interpolationTickFraction = 1;
                else
                    curInterpoationTick.Increment();
                if (interpolationTickFraction > oneOverFClamp)
                    interpolationTickFraction = 1;
            }

            networkTime.SimulationStepBatchSize = 1;
            float networkDeltaTime = currentTime.DeltaTime;
            if (curServerTick.IsValid && previousServerTick.Value.IsValid)
            {
                var deltaTicks = curServerTick.TicksSince(previousServerTick.Value);
                networkDeltaTime = (deltaTicks + serverTickFraction - previousServerTick.Fraction) * tickRate.SimulationFixedTimeStep;
                networkTime.SimulationStepBatchSize = (int)deltaTicks;
                // If last tick was fractional - consider this as re-doing that tick since it will be re-predicted
                if (previousServerTick.Fraction < 1)
                    ++networkTime.SimulationStepBatchSize;
            }

            if (networkDeltaTime <= 0)
            {
                m_NetDebugQuery.GetSingleton<NetDebug>().DebugLog($"[{group.World.Name}] Netcode's network delta time is negative: {networkDeltaTime}. To avoid undefined behaviour, the frame will be skipped.");
                return false;
            }

            if (!m_NetworkStreamInGameQuery.HasSingleton<NetworkStreamInGame>())
            {
                previousServerTick.Value = NetworkTick.Invalid;
                previousServerTick.Fraction = 0f;
                networkDeltaTime = currentTime.DeltaTime;
            }
            else
            {
                previousServerTick.Value = curServerTick;
                previousServerTick.Fraction = serverTickFraction;
            }

            unscaledTime.UnscaleElapsedTime = currentTime.ElapsedTime;
            unscaledTime.UnscaleDeltaTime = currentTime.DeltaTime;
            networkTime.ElapsedNetworkTime += networkDeltaTime;
            networkTime.ServerTick = curServerTick;
            networkTime.EffectiveInputLatencyTicks = networkTimeSystemData.effectiveForcedInputLatencyTicks;
            networkTime.ServerTickFraction = serverTickFraction;
            networkTime.InterpolationTick = curInterpoationTick;
            networkTime.InterpolationTickFraction = interpolationTickFraction;

            group.World.PushTime(new TimeData(networkTime.ElapsedNetworkTime, networkDeltaTime));
            m_DidPushTime = true;
            return true;
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

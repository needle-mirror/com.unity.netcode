#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
#if NETCODE_DEBUG
using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;

namespace Unity.NetCode
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [BurstCompile]
    public partial struct WarnAboutBatchedTicksSystem : ISystem
    {
        private float m_RollingAverage;
        private bool m_ShowDetailedWarning;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_RollingAverage = 1.0f;
            m_ShowDetailedWarning = true;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var netDebug = SystemAPI.GetSingletonRW<NetDebug>();
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            if (!netDebug.ValueRO.WarnBatchedTicks)
                return;

            if (networkTime.SimulationStepBatchSize > 1)
            {
                netDebug.ValueRW.DebugLog( $"Server tick batching has occured. {networkTime.SimulationStepBatchSize} ticks have been batched into 1." );
            }

            float k_RollingWindow = (float)netDebug.ValueRO.WarnBatchedTicksRollingWindowSize;
            m_RollingAverage = (m_RollingAverage - (m_RollingAverage / k_RollingWindow)) + ((float)networkTime.SimulationStepBatchSize / k_RollingWindow);

            if ( m_RollingAverage >= netDebug.ValueRO.WarnAboveAverageBatchedTicksPerFrame )
            {
                FixedString64Bytes detailsString = (m_ShowDetailedWarning ? "" : " (see first warning for more details)");

                netDebug.ValueRW.LogWarning($"Server Tick Batching has occurred due to the server falling behind its desired `SimulationTickRate`. An average of {m_RollingAverage:G3} ticks per frame has been detected for the last ~{netDebug.ValueRO.WarnBatchedTicksRollingWindowSize} frames. Click <a href=\"{NetCodeHyperLinkArguments.s_OpenPlayModeTools}\" highlight=\"{NetCodeHyperLinkArguments.s_HighlightWarnBatchedTicks}\">here</a> to disable this warning.{detailsString}" );

                if ( m_ShowDetailedWarning )
                {
                    m_ShowDetailedWarning = false;
                    netDebug.ValueRW.LogWarning($"Expect client input loss, and a reduction in gameplay, physics, prediction and interpolation quality. Server Tick Batching should only occur in exceptional situations, as a defensive mechanism to prevent a death spiral. i.e. If encountered frequently - with optimizations (like Burst) enabled - this indicates unacceptably poor server performance, as frequent batching makes most games effectively unplayable.");
                }

                m_RollingAverage = 1.0f;
            }
        }
    }
}
#endif

#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Add this component to a singleton entity to configure the NetCode package logging level and to enable/disable packet dumps.
    /// </summary>
    public struct NetCodeDebugConfig : IComponentData
    {
        /// <summary>
        /// The logging level used by netcode. The default is <see cref="NetDebug.LogLevelType.Notify"/>.
        /// </summary>
        public NetDebug.LogLevelType LogLevel;
        /// <summary>
        /// Enable/disable packet dumps. Packet dumps are meant to be enabled mostly for debugging purpose,
        /// being very expensive in both CPU and memory.
        /// </summary>
        public bool DumpPackets;
    }

#if NETCODE_DEBUG
    /// <summary>
    /// System that copy the <see cref="NetCodeDebugConfig"/> to the <see cref="NetDebug"/> singleton.
    /// When the <see cref="NetCodeDebugConfig.DumpPackets"/> is set to true, a <see cref="EnablePacketLogging"/> component is added to all connection.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    internal partial struct DebugConnections : ISystem
    {
        EntityQuery m_ConnectionsQueryWithout;
        EntityQuery m_ConnectionsQueryWith;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_ConnectionsQueryWithout = state.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<NetworkStreamConnection>().WithNone<EnablePacketLogging>());
            m_ConnectionsQueryWith = state.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<NetworkStreamConnection>().WithAll<EnablePacketLogging>());
            state.RequireForUpdate<NetCodeDebugConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsThinClient())
                return;

            var debugConfig = SystemAPI.GetSingleton<NetCodeDebugConfig>();
            var targetLogLevel = debugConfig.LogLevel;
            var shouldDumpPackets = debugConfig.DumpPackets;

#if UNITY_EDITOR
            if (MultiplayerPlayModePreferences.ApplyLoggerSettings)
            {
                targetLogLevel = MultiplayerPlayModePreferences.TargetLogLevel;
                shouldDumpPackets = MultiplayerPlayModePreferences.TargetShouldDumpPackets;
            }
#endif

            SystemAPI.GetSingletonRW<NetDebug>().ValueRW.LogLevel = targetLogLevel;

            if (shouldDumpPackets)
            {
                state.EntityManager.AddComponent<EnablePacketLogging>(m_ConnectionsQueryWithout);
            }
            else
            {
                state.EntityManager.RemoveComponent<EnablePacketLogging>(m_ConnectionsQueryWith);
            }
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            m_ConnectionsQueryWithout.Dispose();
            m_ConnectionsQueryWith.Dispose();
        }
    }
#endif
}

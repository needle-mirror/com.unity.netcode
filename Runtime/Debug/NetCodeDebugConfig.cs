#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
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
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    internal partial class DebugConnections : SystemBase
    {
        BeginSimulationEntityCommandBufferSystem m_CmdBuffer;

        protected override void OnCreate()
        {
            m_CmdBuffer = World.GetOrCreateSystemManaged<BeginSimulationEntityCommandBufferSystem>();
            RequireForUpdate<NetCodeDebugConfig>();
        }

        protected override void OnUpdate()
        {
            if (World.IsThinClient())
                return;

            var debugConfig = GetSingleton<NetCodeDebugConfig>();
            var targetLogLevel = debugConfig.LogLevel;

#if UNITY_EDITOR
            if (MultiplayerPlayModePreferences.ApplyLoggerSettings)
                targetLogLevel = MultiplayerPlayModePreferences.TargetLogLevel;
#endif

            GetSingletonRW<NetDebug>().ValueRW.LogLevel = targetLogLevel;

            var cmdBuffer = m_CmdBuffer.CreateCommandBuffer();
            if (debugConfig.DumpPackets)
            {
                Entities.WithNone<EnablePacketLogging>().ForEach((Entity entity, in NetworkStreamConnection conn) =>
                {
                    cmdBuffer.AddComponent<EnablePacketLogging>(entity);
                }).Schedule();
            }
            else
            {
                Entities.ForEach((Entity entity, in NetworkStreamConnection conn, in EnablePacketLogging logging) =>
                {
                    cmdBuffer.RemoveComponent<EnablePacketLogging>(entity);
                }).Schedule();
            }

            m_CmdBuffer.AddJobHandleForProducer(Dependency);
        }
    }
#endif
}

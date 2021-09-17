#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using Unity.Entities;

namespace Unity.NetCode
{
    public struct NetCodeDebugConfig : IComponentData
    {
        public NetDebug.LogLevelType LogLevel;
        public bool DumpPackets;
    }

#if NETCODE_DEBUG
    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    public partial class DebugConnections : SystemBase
    {
        BeginSimulationEntityCommandBufferSystem m_CmdBuffer;
        NetDebugSystem m_NetDebugSystem;

        protected override void OnCreate()
        {
            m_CmdBuffer = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_NetDebugSystem = World.GetOrCreateSystem<NetDebugSystem>();
            RequireSingletonForUpdate<NetCodeDebugConfig>();
        }

        protected override void OnUpdate()
        {
            if (HasSingleton<ThinClientComponent>())
                return;

            var debugConfig = GetSingleton<NetCodeDebugConfig>();
            m_NetDebugSystem.LogLevel = debugConfig.LogLevel;

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

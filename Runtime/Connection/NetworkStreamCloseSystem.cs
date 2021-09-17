using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    public partial class NetworkStreamCloseSystem : SystemBase
    {
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private NetDebugSystem m_NetDebugSystem;

        protected override void OnCreate()
        {
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_NetDebugSystem = World.GetOrCreateSystem<NetDebugSystem>();
        }

        protected override void OnUpdate()
        {
            var commandBuffer = m_Barrier.CreateCommandBuffer();
            var netDebug = m_NetDebugSystem.NetDebug;
            FixedString32Bytes worldName = World.Name;
            Entities.ForEach((Entity entity, in NetworkStreamConnection con, in NetworkStreamDisconnected disconnection) =>
            {
                var id = -1;
                if (HasComponent<NetworkIdComponent>(entity))
                {
                    var netIdDataFromEntity = GetComponentDataFromEntity<NetworkIdComponent>(true);
                    id = netIdDataFromEntity[entity].Value;
                }
                commandBuffer.DestroyEntity(entity);
                netDebug.DebugLog(FixedString.Format("[{0}][Connection] Cleaning up connection NetworkId={1} InternalId={2} Reason={3}", worldName, id, con.Value.InternalId, DisconnectReasonEnumToString.Convert((int)disconnection.Reason)));
            }).Schedule();
            m_Barrier.AddJobHandleForProducer(Dependency);
        }
    }
}

using Unity.Entities;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    public class NetworkStreamCloseSystem : SystemBase
    {
        private BeginSimulationEntityCommandBufferSystem m_Barrier;

        protected override void OnCreate()
        {
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
        }

        protected override void OnUpdate()
        {
            var commandBuffer = m_Barrier.CreateCommandBuffer();
            Entities.WithAll<NetworkStreamDisconnected>().ForEach((Entity entity, in NetworkStreamConnection con) =>
            {
                commandBuffer.DestroyEntity(entity);
            }).Schedule();
            m_Barrier.AddJobHandleForProducer(Dependency);
        }
    }
}

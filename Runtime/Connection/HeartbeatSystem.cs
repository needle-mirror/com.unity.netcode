using Unity.Entities;
using Unity.Jobs;

namespace Unity.NetCode
{
    public struct HeartbeatComponent : IRpcCommand
    {
    }

    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    public class HeartbeatSendSystem : SystemBase
    {
        private uint m_LastSend;
        private BeginSimulationEntityCommandBufferSystem m_CommandBufferSystem;
        private EntityQuery m_ConnectionQuery;

        protected override void OnCreate()
        {
            m_CommandBufferSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_ConnectionQuery = EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkIdComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>(), ComponentType.Exclude<NetworkStreamInGame>());
        }

        protected override void OnUpdate()
        {
            uint now = NetworkTimeSystem.TimestampMS;
            // Send a heartbeat every 10 seconds, but only to connections which are not ingame since ingame connections already has a constant stream of data
            if (now - m_LastSend >= 10000)
            {
                if (!m_ConnectionQuery.IsEmptyIgnoreFilter)
                {
                    var commandBuffer = m_CommandBufferSystem.CreateCommandBuffer();
                    var request = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent<HeartbeatComponent>(request);
                    // Target = Entity.Null which means broadcast, client only ever has a single connection
                    commandBuffer.AddComponent<SendRpcCommandRequestComponent>(request);
                }

                m_LastSend = now;
            }
        }
    }

    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Server)]
    public class HeartbeatReplySystem : SystemBase
    {
        private BeginSimulationEntityCommandBufferSystem m_CommandBufferSystem;

        protected override void OnCreate()
        {
            m_CommandBufferSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
        }

        protected override void OnUpdate()
        {
            var commandBuffer = m_CommandBufferSystem.CreateCommandBuffer();
            Entities.ForEach(
                (Entity entity, ref HeartbeatComponent heartbeat, ref ReceiveRpcCommandRequestComponent recv) =>
                {
                    // Re-use the same request entity, just add the send component to send it back
                    commandBuffer.AddComponent(entity,
                        new SendRpcCommandRequestComponent {TargetConnection = recv.SourceConnection});
                }).Schedule();
            m_CommandBufferSystem.AddJobHandleForProducer(Dependency);
        }
    }

    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    public class HeartbeatReceiveSystem : SystemBase
    {
        private BeginSimulationEntityCommandBufferSystem m_CommandBufferSystem;

        protected override void OnCreate()
        {
            m_CommandBufferSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
        }

        protected override void OnUpdate()
        {
            var commandBuffer = m_CommandBufferSystem.CreateCommandBuffer();
            Entities.WithNone<SendRpcCommandRequestComponent>().ForEach(
                (Entity entity, ref HeartbeatComponent heartbeat) =>
                {
                    // Just make sure the request is destroyed
                    commandBuffer.DestroyEntity(entity);
                }).Schedule();
            m_CommandBufferSystem.AddJobHandleForProducer(Dependency);
        }
    }
}

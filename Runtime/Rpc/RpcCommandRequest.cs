using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Unity.NetCode
{
    public struct SendRpcCommandRequestComponent : IComponentData
    {
        public Entity TargetConnection;
    }
    public struct ReceiveRpcCommandRequestComponent : IComponentData
    {
        public Entity SourceConnection;
    }

    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    [UpdateBefore(typeof(RpcSystem))]
    public class RpcCommandRequestSystemGroup : ComponentSystemGroup
    {
    }

    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    public class RpcCommandRequestSystem<TActionRequest> : JobComponentSystem
        where TActionRequest : struct, IRpcCommand
    {
        struct SendRpc : IJobForEachWithEntity<SendRpcCommandRequestComponent, TActionRequest>
        {
            public EntityCommandBuffer.Concurrent commandBuffer;
            public BufferFromEntity<OutgoingRpcDataStreamBufferComponent> rpcFromEntity;
            public RpcQueue<TActionRequest> rpcQueue;
            [DeallocateOnJobCompletion][ReadOnly] public NativeArray<Entity> connections;

            public void Execute(Entity entity, int index, [ReadOnly] ref SendRpcCommandRequestComponent dest,
                [ReadOnly] ref TActionRequest action)
            {
                if (dest.TargetConnection != Entity.Null)
                {
                    var buffer = rpcFromEntity[dest.TargetConnection];
                    rpcQueue.Schedule(buffer, action);
                }
                else
                {
                    for (var i = 0; i < connections.Length; ++i)
                    {
                        var buffer = rpcFromEntity[connections[i]];
                        rpcQueue.Schedule(buffer, action);
                    }
                }

                commandBuffer.DestroyEntity(index, entity);
            }
        }

        private BeginSimulationEntityCommandBufferSystem m_CommandBufferSystem;
        private RpcQueue<TActionRequest> m_RpcQueue;
        private EntityQuery m_ConnectionsQuery;

        protected override void OnCreate()
        {
            var rpcSystem = World.GetOrCreateSystem<RpcSystem>();
            rpcSystem.RegisterRpc<TActionRequest>();
            m_CommandBufferSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_RpcQueue = rpcSystem.GetRpcQueue<TActionRequest>();
            m_ConnectionsQuery = EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkIdComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var sendJob = new SendRpc
            {
                commandBuffer = m_CommandBufferSystem.CreateCommandBuffer().ToConcurrent(),
                rpcFromEntity = GetBufferFromEntity<OutgoingRpcDataStreamBufferComponent>(),
                rpcQueue = m_RpcQueue,
                connections = m_ConnectionsQuery.ToEntityArray(Allocator.TempJob, out var connectionsHandle)
            };
            var handle = sendJob.ScheduleSingle(this, JobHandle.CombineDependencies(inputDeps, connectionsHandle));
            m_CommandBufferSystem.AddJobHandleForProducer(handle);
            return handle;
        }
    }
}

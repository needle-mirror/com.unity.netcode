using System;
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
        public RpcCommandRequestSystemGroup()
        {
            UseLegacySortOrder = false;
        }
    }

    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    public class RpcCommandRequestSystem<TActionRequest> : JobComponentSystem
        where TActionRequest : struct, IRpcCommand
    {
        struct SendRpc : IJobChunk
        {
            public EntityCommandBuffer.Concurrent commandBuffer;
            [ReadOnly] public ArchetypeChunkEntityType entitiesType;
            [ReadOnly] public ArchetypeChunkComponentType<SendRpcCommandRequestComponent> rpcRequestType;
            [ReadOnly] public ArchetypeChunkComponentType<TActionRequest> actionRequestType;
            public BufferFromEntity<OutgoingRpcDataStreamBufferComponent> rpcFromEntity;
            public RpcQueue<TActionRequest> rpcQueue;
            [ReadOnly] public NativeArray<Entity> connections;

            public void LambdaMethod(Entity entity, int orderIndex, in SendRpcCommandRequestComponent dest, in TActionRequest action)
            {
                commandBuffer.DestroyEntity(orderIndex, entity);
                if (connections.Length > 0)
                {
                    if (dest.TargetConnection != Entity.Null)
                    {
                        if (!rpcFromEntity.Exists(dest.TargetConnection))
                        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            throw new InvalidOperationException("Cannot send RPC with no remote connection.");
#else
                            return;
#endif
                        }
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
                }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                else
                {
                    throw new InvalidOperationException("Cannot send RPC with no remote connection.");
                }
#endif
            }

            public void Execute(ArchetypeChunk chunk, int orderIndex, int firstEntityIndex)
            {
                var entities = chunk.GetNativeArray(entitiesType);
                var rpcRequests = chunk.GetNativeArray(rpcRequestType);
                if (ComponentType.ReadOnly<TActionRequest>().IsZeroSized)
                {
                    TActionRequest action = default;
                    for (int i = 0; i < chunk.Count; ++i)
                    {
                        LambdaMethod(entities[i], orderIndex, rpcRequests[i], action);
                    }
                }
                else
                {
                    var actions = chunk.GetNativeArray(actionRequestType);
                    for (int i = 0; i < chunk.Count; ++i)
                    {
                        LambdaMethod(entities[i], orderIndex, rpcRequests[i], actions[i]);
                    }
                }
            }
        }

        private BeginSimulationEntityCommandBufferSystem m_CommandBufferSystem;
        private RpcQueue<TActionRequest> m_RpcQueue;
        private EntityQuery m_ConnectionsQuery;
        private EntityQuery m_entityQuery;

        protected override void OnCreate()
        {
            var rpcSystem = World.GetOrCreateSystem<RpcSystem>();
            rpcSystem.RegisterRpc<TActionRequest>();
            m_CommandBufferSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_RpcQueue = rpcSystem.GetRpcQueue<TActionRequest>();
            m_ConnectionsQuery = EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkIdComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());
            m_entityQuery = GetEntityQuery(ComponentType.ReadOnly<SendRpcCommandRequestComponent>(),
                ComponentType.ReadOnly<TActionRequest>());
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var connections = m_ConnectionsQuery.ToEntityArrayAsync(Allocator.TempJob, out var connectionsHandle);
            var sendJob = new SendRpc
            {
                commandBuffer = m_CommandBufferSystem.CreateCommandBuffer().ToConcurrent(),
                entitiesType = GetArchetypeChunkEntityType(),
                rpcRequestType = GetArchetypeChunkComponentType<SendRpcCommandRequestComponent>(true),
                actionRequestType = GetArchetypeChunkComponentType<TActionRequest>(true),
                rpcFromEntity = GetBufferFromEntity<OutgoingRpcDataStreamBufferComponent>(),
                rpcQueue = m_RpcQueue,
                connections = connections
            };
            var handle = sendJob.ScheduleSingle(m_entityQuery, JobHandle.CombineDependencies(inputDeps, connectionsHandle));
            handle = connections.Dispose(handle);
            m_CommandBufferSystem.AddJobHandleForProducer(handle);
            return handle;
        }
    }
}

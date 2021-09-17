using System;
using System.Runtime.CompilerServices;
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

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        /// <inheritdoc cref="Consume"/>
        public ushort Age;

#endif
        /// <inheritdoc cref="Consume"/>
        public bool IsConsumed
        {
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                return Age == ushort.MaxValue;
#else
                return false;
#endif
            }
        }

        /// <summary>
        ///     <see cref="ReceiveRpcCommandRequestComponent"/> has a <see cref="WarnAboutStaleRpcSystem"/> which will log a warning if this <see cref="Age"/> value exceeds <see cref="WarnAboutStaleRpcSystem.MaxRpcAgeFrames"/>.
        ///     Counts simulation frames.
        ///     0 is the simulation frame it is received on.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Consume()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            Age = ushort.MaxValue;
#endif
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(RpcSystem))]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    public class RpcCommandRequestSystemGroup : ComponentSystemGroup
    {
        EntityQuery m_Query;
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SendRpcCommandRequestComponent>());
        }
        protected override void OnUpdate()
        {
            if (!m_Query.IsEmptyIgnoreFilter)
                base.OnUpdate();
        }
    }

    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    public abstract partial class RpcCommandRequestSystem<TActionSerializer, TActionRequest> : SystemBase
        where TActionRequest : struct, IComponentData
        where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
    {
        public struct SendRpcData
        {
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            [ReadOnly] public EntityTypeHandle entitiesType;
            [ReadOnly] public ComponentTypeHandle<SendRpcCommandRequestComponent> rpcRequestType;
            [ReadOnly] public ComponentTypeHandle<TActionRequest> actionRequestType;
            [ReadOnly] public ComponentDataFromEntity<GhostComponent> ghostFromEntity;
            public BufferFromEntity<OutgoingRpcDataStreamBufferComponent> rpcFromEntity;
            public RpcQueue<TActionSerializer, TActionRequest> rpcQueue;
            [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<Entity> connections;

            public void LambdaMethod(Entity entity, int orderIndex, in SendRpcCommandRequestComponent dest, in TActionRequest action)
            {
                commandBuffer.DestroyEntity(orderIndex, entity);
                if (connections.Length > 0)
                {
                    if (dest.TargetConnection != Entity.Null)
                    {
                        if (!rpcFromEntity.HasComponent(dest.TargetConnection))
                        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            throw new InvalidOperationException("Cannot send RPC with no remote connection.");
#else
                            return;
#endif
                        }
                        var buffer = rpcFromEntity[dest.TargetConnection];
                        rpcQueue.Schedule(buffer, ghostFromEntity, action);
                    }
                    else
                    {
                        for (var i = 0; i < connections.Length; ++i)
                        {
                            var buffer = rpcFromEntity[connections[i]];
                            rpcQueue.Schedule(buffer, ghostFromEntity, action);
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

            public void Execute(ArchetypeChunk chunk, int orderIndex)
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
        private RpcQueue<TActionSerializer, TActionRequest> m_RpcQueue;
        private EntityQuery m_entityQuery;
        private EntityQuery m_ConnectionsQuery;

        protected override void OnCreate()
        {
            var rpcSystem = World.GetOrCreateSystem<RpcSystem>();
            rpcSystem.RegisterRpc<TActionSerializer, TActionRequest>();
            m_CommandBufferSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_RpcQueue = rpcSystem.GetRpcQueue<TActionSerializer, TActionRequest>();
            m_entityQuery = GetEntityQuery(ComponentType.ReadOnly<SendRpcCommandRequestComponent>(),
                ComponentType.ReadOnly<TActionRequest>());
            m_ConnectionsQuery = EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkIdComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());
            RequireForUpdate(m_entityQuery);
        }

        protected SendRpcData InitJobData()
        {
            var connections = m_ConnectionsQuery.ToEntityArrayAsync(Allocator.TempJob, out var connectionsHandle);
            var sendJob = new SendRpcData
            {
                commandBuffer = m_CommandBufferSystem.CreateCommandBuffer().AsParallelWriter(),
                entitiesType = GetEntityTypeHandle(),
                rpcRequestType = GetComponentTypeHandle<SendRpcCommandRequestComponent>(true),
                actionRequestType = GetComponentTypeHandle<TActionRequest>(true),
                ghostFromEntity = GetComponentDataFromEntity<GhostComponent>(true),
                rpcFromEntity = GetBufferFromEntity<OutgoingRpcDataStreamBufferComponent>(),
                rpcQueue = m_RpcQueue,
                connections = connections
            };
            Dependency = JobHandle.CombineDependencies(Dependency, connectionsHandle);
            return sendJob;
        }
        protected void ScheduleJobData<T>(in T sendJob) where T: struct, IJobEntityBatch
        {
            var handle = sendJob.Schedule(m_entityQuery, Dependency);
            m_CommandBufferSystem.AddJobHandleForProducer(handle);
            Dependency = handle;
        }
    }
}

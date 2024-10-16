#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// Temporary type, used to upgrade to new component type, to be removed before final 1.0
    /// </summary>
    [Obsolete("SendRpcCommandRequestComponent has been deprecated. Use SendRpcCommandRequest instead (UnityUpgradable) -> SendRpcCommandRequest", true)]
    public struct SendRpcCommandRequestComponent : IComponentData
    {}
    /// <summary>
    /// Temporary type, used to upgrade to new component type, to be removed before final 1.0
    /// </summary>
    [Obsolete("ReceiveRpcCommandRequestComponent has been deprecated. Use ReceiveRpcCommandRequest instead (UnityUpgradable) -> ReceiveRpcCommandRequest", true)]
    public struct ReceiveRpcCommandRequestComponent : IComponentData
    {}

    /// <summary>
    /// A component used to signal that an RPC is supposed to be sent to a remote connection and should *not* be processed.
    /// </summary>
    public struct SendRpcCommandRequest : IComponentData
    {
        /// <summary>
        /// The "NetworkConnection" entity that this RPC should be sent specifically to, or Entity.Null to broadcast to all connections.
        /// </summary>
        public Entity TargetConnection;
    }
    /// <summary>
    /// A component used to signal that an RPC has been received from a remote connection and should be processed.
    /// </summary>
    public struct ReceiveRpcCommandRequest : IComponentData
    {
        /// <summary>
        /// The connection which sent the RPC being processed.
        /// </summary>
        public Entity SourceConnection;

#if NETCODE_DEBUG
        /// <inheritdoc cref="Consume"/>
        public ushort Age;

#endif
        /// <inheritdoc cref="Consume"/>
        public bool IsConsumed
        {
            get
            {
#if NETCODE_DEBUG
                return Age == ushort.MaxValue;
#else
                return false;
#endif
            }
        }

        /// <summary>
        ///     <see cref="ReceiveRpcCommandRequest"/> has a <see cref="WarnAboutStaleRpcSystem"/> which will log a warning if this <see cref="Age"/> value exceeds <see cref="WarnAboutStaleRpcSystem.MaxRpcAgeFrames"/>.
        ///     Counts simulation frames.
        ///     0 is the simulation frame it is received on.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Consume()
        {
#if NETCODE_DEBUG
            Age = ushort.MaxValue;
#endif
        }
    }

    /// <summary>
    /// A group used to make sure all processing on command request entities happens in the correct place.
    /// This is used by code-gen and should only be used directly when implementing custom command request processors.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation,
        WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(RpcSystem))]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    public partial class RpcCommandRequestSystemGroup : ComponentSystemGroup
    {
        EntityQuery m_Query;
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(ComponentType.ReadOnly<SendRpcCommandRequest>());
        }
        protected override void OnUpdate()
        {
            if (!m_Query.IsEmptyIgnoreFilter)
                base.OnUpdate();
        }
    }

    /// <summary>
    /// Helper struct for implementing systems to process RPC command request entities.
    /// This is generally used by code-gen, and should only be used directly in special cases.
    /// </summary>
    /// <typeparam name="TActionSerializer">Unmanaged type of <see cref="IRpcCommandSerializer{TActionRequest}"/></typeparam>
    /// <typeparam name="TActionRequest">Unmanaged type of <see cref="IComponentData"/></typeparam>
    public struct RpcCommandRequest<TActionSerializer, TActionRequest>
        where TActionRequest : unmanaged, IComponentData
        where TActionSerializer : unmanaged, IRpcCommandSerializer<TActionRequest>
    {
        /// <summary>
        /// <para>A struct that can be embedded into your system job, and should be used to delegate the rpc handling.
        /// Example of use:</para>
        /// <code>
        /// [BurstCompile]
        /// struct SendRpc : IJobChunk
        /// {
        ///     public RpcCommandRequest{MyRpcCommand, MyRpcCommand}.SendRpcData data;
        ///     public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        ///     {
        ///         data.Execute(chunk, unfilteredChunkIndex);
        ///     }
        /// }
        /// </code>
        /// <para>Always use the <see cref="RpcCommandRequest{TActionSerializer,TActionRequest}.InitJobData"/> method to construct
        /// a valid instance.</para>
        /// </summary>
        public struct SendRpcData
        {
            internal EntityCommandBuffer.ParallelWriter commandBuffer;
            [ReadOnly] internal EntityTypeHandle entitiesType;
            [ReadOnly] internal ComponentTypeHandle<SendRpcCommandRequest> rpcRequestType;
            [ReadOnly] internal ComponentTypeHandle<TActionRequest> actionRequestType;
            [ReadOnly] internal ComponentLookup<GhostInstance> ghostFromEntity;
            [ReadOnly] internal ComponentLookup<NetworkId> networkIdLookup;
            [ReadOnly] internal ComponentLookup<NetworkStreamConnection> networkStreamConnectionLookup;
            internal BufferLookup<OutgoingRpcDataStreamBuffer> rpcFromEntity;
            internal RpcQueue<TActionSerializer, TActionRequest> rpcQueue;
            [ReadOnly] internal NativeList<Entity> connections;
            internal NetDebug netDebug;
            internal byte requireConnectionApproval;
            internal byte isApprovalRpc;
            internal byte isServer;
            internal FixedString128Bytes worldName;


            // Process all send requests
            void LambdaMethod(Entity entity, int orderIndex, in SendRpcCommandRequest dest, in TActionRequest action)
            {
                commandBuffer.DestroyEntity(orderIndex, entity);
                if (dest.TargetConnection != Entity.Null)
                {
                    ValidateAndQueueRpc(dest.TargetConnection, action);
                }
                else
                {
                    if (connections.Length == 0)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (isServer != 0)
                            netDebug.LogWarning($"[{worldName}] Cannot broadcast RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' as no remote connections. I.e. No `NetworkStreamConnection` entities found, as no clients connected to this server.");
                        else
                            netDebug.LogWarning($"[{worldName}] Cannot send RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' with no remote connection. I.e. No `NetworkStreamConnection` entity, as this client world is not connected (nor connecting) to any server.");
#endif
                        return;
                    }
                    for (var i = 0; i < connections.Length; ++i)
                    {
                        ValidateAndQueueRpc(connections[i], action);
                    }
                }
            }

            private void ValidateAndQueueRpc(Entity connectionEntity, in TActionRequest action)
            {
                // TODO - Distinguish between entity deleted and Entity never was a NetworkStreamConnection "NetworkConnection" entity.
                // One is an error, the other is a warning.
                if (!networkStreamConnectionLookup.TryGetComponent(connectionEntity, out var networkStreamConnection))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    netDebug.LogWarning($"[{worldName}] Cannot send RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to {connectionEntity.ToFixedString()} as it does not have a `NetworkStreamConnection` entity. It's either recently deleted, or you assigned an invalid entity.");
#endif
                    return;
                }

                var isHandshakeOrApproval = networkStreamConnection.IsHandshakeOrApproval;
                if (!isHandshakeOrApproval)
                {
                    var isConnected = networkStreamConnection.CurrentState == ConnectionState.State.Connected && networkIdLookup.HasComponent(connectionEntity);
                    if (!isConnected)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        netDebug.LogError($"[{worldName}] Cannot send RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to {connectionEntity.ToFixedString()} as {networkStreamConnection.Value.ToFixedString()} is in state `{networkStreamConnection.CurrentState.ToFixedString()}`!");
#endif
                        return;
                    }
                }

                if (!rpcFromEntity.TryGetBuffer(connectionEntity, out var buffer))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    netDebug.LogError($"Cannot send RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to {connectionEntity.ToFixedString()} as {networkStreamConnection.Value.ToFixedString()} has no `OutgoingRpcDataStreamBuffer` RPC buffer!");
#endif
                    return;
                }

                if (isHandshakeOrApproval)
                {
                    if (isApprovalRpc == 0)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        netDebug.LogError($"[{worldName}] Cannot send non-approval RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to {connectionEntity.ToFixedString()} as {networkStreamConnection.Value.ToFixedString()} is in state `{networkStreamConnection.CurrentState.ToFixedString()}`! Wait for handshake and approval to complete!");
#endif
                        return;
                    }
                }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if(requireConnectionApproval == 0 && isApprovalRpc == 1 && !netDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning)
                    netDebug.LogWarning($"[{worldName}] Sending approval RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to {connectionEntity.ToFixedString()} ('{networkStreamConnection.Value.ToFixedString()}') but connection approval is disabled. RPC will still be sent. If intentional, suppress via `NetDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning`.");
#endif

                rpcQueue.Schedule(buffer, ghostFromEntity, action);
            }

            /// <summary>
            /// Call this from an <see cref="IJobChunk.Execute"/> method to handle the rpc requests.
            /// </summary>
            /// <param name="chunk"></param>
            /// <param name="orderIndex"></param>
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                var entities = chunk.GetNativeArray(entitiesType);
                var rpcRequests = chunk.GetNativeArray(ref rpcRequestType);
                if (ComponentType.ReadOnly<TActionRequest>().IsZeroSized)
                {
                    TActionRequest action = default;
                    for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                    {
                        LambdaMethod(entities[i], orderIndex, rpcRequests[i], action);
                    }
                }
                else
                {
                    var actions = chunk.GetNativeArray(ref actionRequestType);
                    for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                    {
                        LambdaMethod(entities[i], orderIndex, rpcRequests[i], actions[i]);
                    }
                }
            }
        }

        private RpcQueue<TActionSerializer, TActionRequest> m_RpcQueue;
        private EntityQuery m_ConnectionsQuery;
        private EntityQuery m_CommandBufferQuery;
        private EntityQuery m_NetDebugQuery;
        private EntityQuery m_NetworkStreamDriver;
        /// <summary>
        /// The query to use when scheduling the processing job.
        /// </summary>
        public EntityQuery Query;

        EntityTypeHandle m_EntityTypeHandle;
        ComponentTypeHandle<SendRpcCommandRequest> m_SendRpcCommandRequestComponentHandle;
        ComponentTypeHandle<TActionRequest> m_TActionRequestHandle;
        ComponentLookup<GhostInstance> m_GhostComponentFromEntity;
        ComponentLookup<NetworkId> m_NetworkIdLookup;
        ComponentLookup<NetworkStreamConnection> m_NetworkStreamConnectionLookup;
        BufferLookup<OutgoingRpcDataStreamBuffer> m_OutgoingRpcDataStreamBufferComponentFromEntity;
        bool m_IsApprovalRpc;

        /// <summary>
        /// Initialize the helper struct, should be called from OnCreate in an ISystem.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<RpcCollection>();
            var collectionQuery = state.GetEntityQuery(builder);
            var rpcCollection = collectionQuery.GetSingleton<RpcCollection>();
            rpcCollection.RegisterRpc<TActionSerializer, TActionRequest>();
            m_RpcQueue = rpcCollection.GetRpcQueue<TActionSerializer, TActionRequest>();
            builder.Reset();
            builder.WithAll<SendRpcCommandRequest, TActionRequest>();
            Query = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetworkStreamConnection>();
            m_ConnectionsQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<BeginSimulationEntityCommandBufferSystem.Singleton>();
            builder.WithOptions(EntityQueryOptions.IncludeSystems);
            m_CommandBufferQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetDebug>();
            m_NetDebugQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetworkStreamDriver>();
            m_NetworkStreamDriver = state.GetEntityQuery(builder);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_SendRpcCommandRequestComponentHandle = state.GetComponentTypeHandle<SendRpcCommandRequest>(true);
            m_TActionRequestHandle = state.GetComponentTypeHandle<TActionRequest>(true);
            m_GhostComponentFromEntity = state.GetComponentLookup<GhostInstance>(true);
            m_NetworkIdLookup = state.GetComponentLookup<NetworkId>(true);
            m_NetworkStreamConnectionLookup = state.GetComponentLookup<NetworkStreamConnection>(true);
            m_OutgoingRpcDataStreamBufferComponentFromEntity = state.GetBufferLookup<OutgoingRpcDataStreamBuffer>();

            var componentsManagedType = ComponentType.ReadWrite<TActionRequest>().GetManagedType();
            if (RpcCollection.IsApprovalRpcType(componentsManagedType))
                m_IsApprovalRpc = true;

            state.RequireForUpdate(Query);
        }

        /// <summary>
        /// Initialize the internal state of a processing job. Should be called from OnUpdate of an ISystem.
        /// </summary>
        /// <param name="state">Raw entity system state.</param>
        /// <returns><see cref="SendRpcData"/> initialized using <paramref name="state"/></returns>
        public SendRpcData InitJobData(ref SystemState state)
        {
            m_EntityTypeHandle.Update(ref state);
            m_SendRpcCommandRequestComponentHandle.Update(ref state);
            m_TActionRequestHandle.Update(ref state);
            m_GhostComponentFromEntity.Update(ref state);
            m_NetworkIdLookup.Update(ref state);
            m_NetworkStreamConnectionLookup.Update(ref state);
            m_OutgoingRpcDataStreamBufferComponentFromEntity.Update(ref state);
            var connections = m_ConnectionsQuery.ToEntityListAsync(state.WorldUpdateAllocator,
                out var connectionsHandle);
            var sendJob = new SendRpcData
            {
                commandBuffer = m_CommandBufferQuery.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                entitiesType = m_EntityTypeHandle,
                rpcRequestType = m_SendRpcCommandRequestComponentHandle,
                actionRequestType = m_TActionRequestHandle,
                ghostFromEntity = m_GhostComponentFromEntity,
                rpcFromEntity = m_OutgoingRpcDataStreamBufferComponentFromEntity,
                networkIdLookup = m_NetworkIdLookup,
                networkStreamConnectionLookup = m_NetworkStreamConnectionLookup,
                rpcQueue = m_RpcQueue,
                connections = connections,
                netDebug = m_NetDebugQuery.GetSingleton<NetDebug>(),
                requireConnectionApproval = m_NetworkStreamDriver.GetSingleton<NetworkStreamDriver>().RequireConnectionApproval ? (byte)1 : (byte)0,
                isApprovalRpc = m_IsApprovalRpc ? (byte)1 : (byte)0,
                isServer = state.WorldUnmanaged.IsServer() ? (byte)1 : (byte)0,
                worldName = state.WorldUnmanaged.Name,
            };
            state.Dependency = JobHandle.CombineDependencies(state.Dependency, connectionsHandle);
            return sendJob;
        }
    }
}

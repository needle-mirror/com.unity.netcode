#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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
        ///     <see cref="ReceiveRpcCommandRequest"/> has a <see cref="WarnAboutStaleRpcSystem"/> which will log a warning if this <see cref="Age"/> value exceeds <see cref="NetDebug.MaxRpcAgeFrames"/>.
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
            internal EntityCommandBuffer.ParallelWriter commandBuffer; // begin simulation
            [ReadOnly] internal EntityTypeHandle entitiesType;
            [ReadOnly] internal ComponentTypeHandle<SendRpcCommandRequest> rpcRequestType;
            [ReadOnly] internal ComponentTypeHandle<TActionRequest> actionRequestType;
            [ReadOnly] internal ComponentLookup<GhostInstance> ghostFromEntity;
            [ReadOnly] internal ComponentLookup<NetworkId> networkIdLookup;
            [ReadOnly] internal ComponentLookup<NetworkStreamConnection> networkStreamConnectionLookup;
            [ReadOnly] internal ComponentLookup<LocalConnection> localConnectionLookup;
            [ReadOnly] internal NativeList<RpcCollection.RpcData> execute;
            [ReadOnly] internal NativeParallelHashMap<ulong, int> hashToIndex;
            internal BufferLookup<OutgoingRpcDataStreamBuffer> rpcFromEntity;
            internal RpcQueue<TActionSerializer, TActionRequest> rpcQueue;
            [ReadOnly] internal NativeList<Entity> connections;
            internal NetDebug netDebug;
            internal byte requireConnectionApproval;
            internal byte isApprovalRpc;
            internal byte isServer;
            internal byte isHost;
            internal FixedString128Bytes worldName;
            internal NativeArray<NetCodeConnectionEvent>.ReadOnly connectionEventsForTick;

            // Process all send requests
            void LambdaMethod(Entity entity, int orderIndex, in SendRpcCommandRequest dest, in TActionRequest action)
            {
                commandBuffer.DestroyEntity(orderIndex, entity);
                if (dest.TargetConnection != Entity.Null)
                {
                    ValidateIncorrectApprovalUsage(dest.TargetConnection, false);
                    ValidateAndQueueRpc(dest.TargetConnection, false, action, orderIndex);
                }
                else
                {
                    if (connections.Length == 0)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        var msg = isServer != 0
                            ? $"[{worldName}] Cannot broadcast RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' as no remote connections. I.e. No `NetworkStreamConnection` entities found, as no clients connected to this server."
                            : $"[{worldName}] Cannot send RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to the server as not connected to one! I.e. No `NetworkStreamConnection` entity, as this client world is not connected (nor connecting) to any server.";
                        if (!AnyDisconnectEvents(connectionEventsForTick))
                            netDebug.LogWarning(msg);
                        else netDebug.DebugLog(msg);
                        static bool AnyDisconnectEvents(NativeArray<NetCodeConnectionEvent>.ReadOnly eventsForTickLocal)
                        {
                            foreach (var evt in eventsForTickLocal)
                                if (evt.State == ConnectionState.State.Disconnected)
                                    return true;
                            return false;
                        }
#endif
                        return;
                    }

                    ValidateIncorrectApprovalUsage(connections[0], isServer != 0);
                    for (var i = 0; i < connections.Length; ++i)
                    {
                        ValidateAndQueueRpc(connections[i], isServer != 0, action, orderIndex);
                    }
                }
            }

            private void ValidateAndQueueRpc(Entity connectionEntity, bool isBroadcast, TActionRequest action, int orderIndex)
            {
                // We want the action parameter to be passed by copy, to reduce risk with unsafe operations below which copies by pointer. (this line actionDataOverridePtr = (IntPtr)UnsafeUtility.AddressOf(ref action),)

                // TODO-release come back to this with new approval flows and fix above flows too with no connections
                // TODO-release MTT-13314 handle users calling Schedule (see schedule call below) directly and bypassing the update of the RPC entity. This should work for single world host as well
                if (isHost == 1 && localConnectionLookup.HasComponent(connectionEntity))
                {
                    // Single world host passthrough: if there is an entity with an RPC buffer but is the local connection for the host
                    // immediately create the entity here as if was received by the server.
                    unsafe
                    {
                        RpcExecutor.Parameters parameters = new RpcExecutor.Parameters()
                        {
                            CommandBuffer = commandBuffer,
                            Connection = connectionEntity,
                            JobIndex = orderIndex,
                            actionDataOverridePtr = (IntPtr)UnsafeUtility.AddressOf(ref action),
                            IsPassthroughRPC = true,
                            NetDebug = netDebug,
                            WorldName = worldName,
                            IsServer = isServer == 1
                        };

                        var rpcHash = TypeManager.GetTypeInfo<TActionRequest>().StableTypeHash;
                        hashToIndex.TryGetValue(rpcHash, out var rpcIndex);
                        // If users have their own custom RPC serialization/execution, we need to call it too. Since this triggers normal flows, this also handles Remotes
                        // triggering the appropriate callback.
                        // In ExecuteCreateRequestComponent there should be an edge case handling serializing or not and create the appropriate action component
                        execute[rpcIndex].Execute.Ptr.Invoke(ref parameters);
                        return;
                    }
                }

                // TODO - If cleanup components are removed (and/or structural changes disallowed),
                // add error if you assign an incorrect Entity to the TargetConnection by checking entityExists.
                if (!networkStreamConnectionLookup.TryGetComponent(connectionEntity, out var networkStreamConnection)
                    || !rpcFromEntity.TryGetBuffer(connectionEntity, out var buffer))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (isBroadcast || FindDidJustDisconnect(connectionEntity))
                        netDebug.DebugLog($"{Prefix(true, connectionEntity)} as they just disconnected.");
                    else
                        netDebug.LogWarning($"{Prefix(false, connectionEntity)} as its connection entity ({connectionEntity.ToFixedString()}) does not have a `NetworkStreamConnection` or `OutgoingRpcDataStreamBuffer` component (anymore?). Did you assign the correct entity?");
#endif
                    return;
                }

                var isHandshakeOrApproval = networkStreamConnection.IsHandshakeOrApproval;
                if (isHandshakeOrApproval)
                {
                    if (isApprovalRpc == 0)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        FixedString512Bytes msg = $"{Prefix(isBroadcast, connectionEntity)} as it is not an Approval RPC, and its {networkStreamConnection.Value.ToFixedString()} - on {connectionEntity.ToFixedString()} - is in state `{networkStreamConnection.CurrentState.ToFixedString()}`!";
                        if (isBroadcast)
                            netDebug.DebugLog(msg);
                        else
                        {
                            msg.Append((FixedString128Bytes)" You MUST wait for Handshake and Approval to complete, OR convert this RPC to an `IApprovalRpcCommand`!");
                            netDebug.LogError(msg);
                        }
#endif
                        return;
                    }
                }
                else
                {
                    var isConnected = networkStreamConnection.CurrentState == ConnectionState.State.Connected && networkIdLookup.HasComponent(connectionEntity);
                    if (!isConnected)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        FixedString512Bytes msg = $"{Prefix(isBroadcast, connectionEntity)} as its {networkStreamConnection.Value.ToFixedString()} - on {connectionEntity.ToFixedString()} - is in state `{networkStreamConnection.CurrentState.ToFixedString()}`!";
                        if (isBroadcast)
                            netDebug.DebugLog(msg);
                        else netDebug.LogError(msg);
#endif
                        return;
                    }
                }

                rpcQueue.Schedule(buffer, ghostFromEntity, action);
            }

            private bool FindDidJustDisconnect(Entity entity)
            {
                foreach (var evt  in connectionEventsForTick)
                {
                    if (evt.State == ConnectionState.State.Disconnected && evt.ConnectionEntity == entity)
                        return true;
                }
                return false;
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void ValidateIncorrectApprovalUsage(Entity connectionEntity, bool isBroadcast)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (requireConnectionApproval == 0 && isApprovalRpc == 1 && !netDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning)
                {
                    FixedString512Bytes msg = isBroadcast
                        ? $"[{worldName}] Broadcasting approval RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' but connection approval is disabled. We will still attempt to broadcast the RPC."
                        : $"[{worldName}] Sending approval RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to {Target(connectionEntity)} but connection approval is disabled. We will still attempt to send the RPC.";
                    msg.Append((FixedString128Bytes)" If intentional, suppress via `NetDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning`.");
                    netDebug.LogWarning(msg);
                }
#endif
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            FixedString512Bytes Prefix(bool isBroadcast, Entity connectionEntity)
            {
                return isBroadcast
                    ? $"[{worldName}] Broadcast of RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' will skip client connection {connectionEntity.ToFixedString()}"
                    : $"[{worldName}] Cannot send RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to {Target(connectionEntity)}";
            }

            private FixedString128Bytes Target(Entity connectionEntity) => isServer == 0 ? $"the server" : $"TargetConnection:{connectionEntity.ToFixedString()}";
#endif

            /// <summary>
            /// Call this from a <see cref="IJobChunk.Execute"/> method to handle the rpc requests.
            /// </summary>
            /// <param name="chunk">Chunk</param>
            /// <param name="orderIndex">Order index</param>
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
        ComponentLookup<LocalConnection> m_LocalConnectionLookup;
        EntityQuery m_RpcCollectionQuery;
        BufferLookup<OutgoingRpcDataStreamBuffer> m_OutgoingRpcDataStreamBufferComponentFromEntity;
        bool m_IsApprovalRpc;

        /// <summary>
        /// Initialize the helper struct, should be called from OnCreate in an ISystem.
        /// </summary>
        /// <param name="state"><see cref="SystemState"/></param>
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<RpcCollection>();
            m_RpcCollectionQuery = state.GetEntityQuery(builder);
            var rpcCollection = m_RpcCollectionQuery.GetSingleton<RpcCollection>();
            rpcCollection.RegisterRpc<TActionSerializer, TActionRequest>();
            m_RpcQueue = rpcCollection.GetRpcQueue<TActionSerializer, TActionRequest>();
            builder.Reset();
            builder.WithAll<SendRpcCommandRequest, TActionRequest>();
            Query = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<OutgoingRpcDataStreamBuffer>();
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
            m_LocalConnectionLookup = state.GetComponentLookup<LocalConnection>(true);
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
            var connections = m_ConnectionsQuery.ToEntityListAsync(state.WorldUpdateAllocator,
                out var connectionsHandle);
            m_EntityTypeHandle.Update(ref state);
            m_SendRpcCommandRequestComponentHandle.Update(ref state);
            m_TActionRequestHandle.Update(ref state);
            m_GhostComponentFromEntity.Update(ref state);
            m_NetworkIdLookup.Update(ref state);
            m_NetworkStreamConnectionLookup.Update(ref state);
            m_LocalConnectionLookup.Update(ref state);
            m_OutgoingRpcDataStreamBufferComponentFromEntity.Update(ref state);
            var nsd = m_NetworkStreamDriver.GetSingleton<NetworkStreamDriver>();
            var rpcCollection = m_RpcCollectionQuery.GetSingleton<RpcCollection>();
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
                localConnectionLookup = m_LocalConnectionLookup,
                execute = rpcCollection.m_RpcData,
                hashToIndex = rpcCollection.m_RpcTypeHashToIndex,
                rpcQueue = m_RpcQueue,
                connections = connections,
                connectionEventsForTick = nsd.ConnectionEventsForTick,
                netDebug = m_NetDebugQuery.GetSingleton<NetDebug>(),
                requireConnectionApproval = nsd.RequireConnectionApproval ? (byte)1 : (byte)0,
                isApprovalRpc = m_IsApprovalRpc ? (byte)1 : (byte)0,
                isServer = state.WorldUnmanaged.IsServer() ? (byte)1 : (byte)0,
                isHost = state.WorldUnmanaged.IsHost() ? (byte)1 : (byte)0,
                worldName = state.WorldUnmanaged.Name,
            };
            state.Dependency = JobHandle.CombineDependencies(state.Dependency, connectionsHandle);
            return sendJob;
        }
    }
}

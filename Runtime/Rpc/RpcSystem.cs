using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using System.Runtime.InteropServices;
using Unity.Assertions;
using Unity.Burst.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Networking.Transport.Error;

namespace Unity.NetCode
{
    /// <summary>
    /// Struct that can be used to simplify writing systems and jobs that deserialize and execute received rpc commands.
    /// </summary>
    public struct RpcExecutor
    {
        /// <summary>
        /// Struct used as argument to the rpc execute method (see the <see cref="ExecuteDelegate"/> delegate).
        /// Contains the input data stream, the receiving connection, and other useful data that can be used
        /// to decode and write your rpc logic.
        /// </summary>
        public struct Parameters
        {
            /// <summary>
            /// The data-stream that contains the rpc data.
            /// </summary>
            public DataStreamReader Reader;
            /// <summary>
            /// The connection that received the rpc.
            /// </summary>
            public Entity Connection;
            /// <summary>
            /// On clients this will be the singleton entity which stores the client connection uniqueId. If Entity.Null
            /// it means no such entity has been created yet (and it should be created).
            /// </summary>
            internal Entity ClientConnectionUniqueIdEntity;
            /// <summary>
            /// On clients this will be the current unique ID of the client connection to the server, if any has been
            /// set already. Will be 0 otherwise.
            /// </summary>
            internal uint ClientCurrentConnectionUniqueId;
            /// <summary>
            /// The cached component state of said <see cref="Connection"/>, written back automatically!
            /// </summary>
            internal NetworkStreamConnection ConnectionStateRef;
            /// <summary>
            /// A command buffer that be used to make structural changes.
            /// </summary>
            public EntityCommandBuffer.ParallelWriter CommandBuffer;
            /// <summary>
            /// The sort order that must be used to add commands to command buffer.
            /// </summary>
            public int JobIndex;
            /// <summary>
            /// A pointer to a <see cref="RpcDeserializerState"/> instance.
            /// </summary>
            internal IntPtr State;
            /// <summary>
            /// Logger.
            /// </summary>
            public NetDebug NetDebug;
            /// <summary>
            /// Cache of this components value.
            /// </summary>
            public NetworkProtocolVersion ProtocolVersion;
            /// <summary>
            /// Cache of this World's name.
            /// </summary>
            public FixedString128Bytes WorldName;
            /// <summary>
            /// True if this world is using <see cref="RpcCollection.DynamicAssemblyList"/>.
            /// </summary>
            [MarshalAs(UnmanagedType.U1)]
            public bool UseDynamicAssemblyList;
            /// <summary>
            /// Is this executing in a server world.
            /// </summary>
            [MarshalAs(UnmanagedType.U1)]
            public bool IsServer;

            /// <summary>
            /// Whether this RPC is a loopback RPC that's bypassing serialization. Your RPC execution code shouldn't need to serialize in this case for performance reasons and should just read data from <see cref="GetPassthroughActionData"/>
            /// </summary>
            // TODO-release new doc entry for adding this use case (plus some samples)
            [MarshalAs(UnmanagedType.U1)]
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
            public bool IsPassthroughRPC;
#else
            internal bool IsPassthroughRPC;
#endif

            /// <summary>
            /// Ptr to the action data passthrough. Useful for single world host where we bypass the serialization flow
            /// </summary>
            internal IntPtr actionDataOverridePtr;

            /// <summary>
            /// An instance of <see cref="RpcDeserializerState"/> that can be used to deserialize the rpcs.
            /// </summary>
            public RpcDeserializerState DeserializerState
            {
                get { unsafe { return UnsafeUtility.AsRef<RpcDeserializerState>((void*)State); } }
            }

            // TODO-release better name
            /// <summary>
            /// In a single world host scenario, rpc data doesn't need to be deserialized and is instead already available here, bypassing serialization/deserialization logic
            /// </summary>
            /// <typeparam name="TActionData">RPC component type</typeparam>
            /// <returns></returns>
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
            public unsafe TActionData GetPassthroughActionData<TActionData>() where TActionData : unmanaged, IComponentData
#else
            internal unsafe TActionData GetPassthroughActionData<TActionData>() where TActionData : unmanaged, IComponentData
#endif
            {
                return UnsafeUtility.AsRef<TActionData>((void*)actionDataOverridePtr);
            }
        }

        /// <summary>
        /// <para>The reference to static burst-compatible method that is invoked when an rpc has been received.
        /// For example:
        /// </para>
        /// <code>
        ///     [BurstCompile(DisableDirectCall = true)]
        ///     [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        ///     private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        /// </code>
        /// </summary>
        /// <remarks>
        /// The <c>DisableDirectCall = true</c> was necessary to workaround an issue with burst and function delegate.
        /// If you are implementing your custom rpc serializer, please remember to disable the direct call.
        /// </remarks>
        /// <param name="parameters">Parameters for custom rpc serializer</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ExecuteDelegate(ref Parameters parameters);

        /// <summary>
        /// <para>Helper method that can be used to implement the execute method for the <see cref="IRpcCommandSerializer{T}"/>
        /// interface.
        /// By calling the ExecuteCreateRequestComponent, a new entity (with a <typeparamref name="TActionRequest"/> and
        /// a <see cref="ReceiveRpcCommandRequest"/> component) is created.
        /// It is the users responsibility to write a system that consumes the created rpcs entities. For example:
        /// </para>
        /// <code>
        /// public struct MyRpcConsumeSystem : ISystem
        /// {
        ///    private Query rcpQuery;
        ///    public void OnCreate(ref SystemState state)
        ///    {
        ///        var builder = new EntityQueryBuilder(Allocator.Temp).WithAll&lt;MyRpc, ReceiveRpcCommandRequestComponent&gt;();
        ///        rcpQuery = state.GetEntityQuery(builder);
        ///    }
        ///    public void OnUpdate(ref SystemState state)
        ///    {
        ///         foreach(var rpc in SystemAPI.Query&lt;MyRpc&gt;().WithAll&lt;ReceiveRpcCommandRequestComponent&gt;())
        ///         {
        ///             //do something with the rpc
        ///         }
        ///         //Consumes all of them
        ///         state.EntityManager.DestroyEntity(rpcQuery);
        ///    }
        /// }
        /// </code>
        /// </summary>
        /// <param name="parameters">Container for <see cref="EntityCommandBuffer"/>, JobIndex, as well as connection entity.</param>
        /// <typeparam name="TActionSerializer">Struct of type <see cref="IRpcCommandSerializer{TActionRequest}"/>.</typeparam>
        /// <typeparam name="TActionRequest">Unmanaged type of <see cref="IComponentData"/>.</typeparam>
        /// <returns>Created entity for RPC request. Name of the Entity is set as 'NetCodeRPC'.</returns>
        public static Entity ExecuteCreateRequestComponent<TActionSerializer, TActionRequest>(ref Parameters parameters)
            where TActionRequest : unmanaged, IComponentData
            where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
        {
            var rpcData = default(TActionRequest);

            if (parameters.IsPassthroughRPC)
            {
                rpcData = parameters.GetPassthroughActionData<TActionRequest>();
            }
            else
            {
                var rpcSerializer = default(TActionSerializer);
                rpcSerializer.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);
            }

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, new ReceiveRpcCommandRequest {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, rpcData);

#if !DOTS_DISABLE_DEBUG_NAMES
            FixedString64Bytes truncatedName = new FixedString64Bytes();
            truncatedName.CopyFromTruncated((FixedString512Bytes)$"NetcodeRPC_{ComponentType.ReadWrite<TActionRequest>().ToFixedString()}");
            parameters.CommandBuffer.SetName(parameters.JobIndex, entity, truncatedName);
#endif
            return entity;
        }
    }

    /// <summary>
    /// <para>
    /// The system responsible for sending and receiving RPCs.
    /// </para>
    /// <para>
    /// The RpcSystem flushes all the outgoing RPCs scheduled in the <see cref="OutgoingRpcDataStreamBuffer"/> for all the active connections.
    /// Multiple RPCs can be raised by a world (to be sent in a single frame) to each connection. Therefore, in order to reduce the number of in-flight reliable messages,
    /// the system tries to coalesce multiple RPCs into a single packet.
    /// </para>
    /// <para>
    /// Because packet queue size is limited (<see cref="NetworkParameterConstants.SendQueueCapacity"/> and <see cref="NetworkConfigParameter"/>), the
    /// number of available packets may not be sufficient to flush the queue entirely. In that case, the pending messages are going to attempt to be
    /// sent during the next frame (recursively) (or when a resource is available).
    /// </para>
    /// <para>
    /// When an rpc packet is received, it is first handled by the <see cref="NetworkStreamReceiveSystem"/>, which decodes the incoming network packet
    /// and appends it to the <see cref="IncomingRpcDataStreamBuffer"/> for the connection that received the message.
    /// The RpcSystem will then dequeue all the received messages, and dispatch them by invoking their execute method (<see cref="IRpcCommandSerializer{T}"/>
    /// and <see cref="RpcExecutor"/>).
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    [BurstCompile]
    public partial struct RpcSystem : ISystem
    {
        /// <summary>
        /// During the initial handshake, the client and server exchanges their respective <see cref="NetworkProtocolVersion"/> by using
        /// a internal rpc.
        /// When received, the RpcSystem will perform a protocol check, that verifies that the versions are compatible.
        /// If the verification fails, a new entity with a <see cref="ProtocolVersionError"/> component is created;
        /// the generated error is then handled by the <see cref="RpcSystemErrors"/> system.
        /// </summary>
        internal struct ProtocolVersionError : IComponentData
        {
            public Entity connection;
            public NetworkProtocolVersion remoteProtocol;
        }

        private NativeList<RpcCollection.RpcData> m_RpcData;
        private NativeParallelHashMap<ulong, int> m_RpcTypeHashToIndex;
        private NativeReference<byte> m_DynamicAssemblyList;

        private EntityQuery m_RpcBufferGroup;

        private EntityTypeHandle m_EntityTypeHandle;
        private ComponentTypeHandle<NetworkStreamConnection> m_NetworkStreamConnectionHandle;
        private BufferTypeHandle<IncomingRpcDataStreamBuffer> m_IncomingRpcDataStreamBufferComponentHandle;
        private BufferTypeHandle<OutgoingRpcDataStreamBuffer> m_OutgoingRpcDataStreamBufferComponentHandle;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<OutgoingRpcDataStreamBuffer>() == 1);
            UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<IncomingRpcDataStreamBuffer>() == 1);
#endif

            m_RpcData = new NativeList<RpcCollection.RpcData>(16, Allocator.Persistent);
            m_RpcTypeHashToIndex = new NativeParallelHashMap<ulong, int>(16, Allocator.Persistent);
            m_DynamicAssemblyList = new NativeReference<byte>(Allocator.Persistent);
            var rpcSingleton = state.EntityManager.CreateEntity(ComponentType.ReadWrite<RpcCollection>());
            state.EntityManager.SetName(rpcSingleton, "RpcCollection-Singleton");
            state.EntityManager.SetComponentData(rpcSingleton, new RpcCollection
            {
                m_DynamicAssemblyList = m_DynamicAssemblyList,
                m_RpcData = m_RpcData,
                m_RpcTypeHashToIndex = m_RpcTypeHashToIndex,
                m_IsFinal = 0
            });

            m_RpcBufferGroup = state.GetEntityQuery(
                ComponentType.ReadWrite<IncomingRpcDataStreamBuffer>(),
                ComponentType.ReadWrite<OutgoingRpcDataStreamBuffer>(),
                ComponentType.ReadWrite<NetworkStreamConnection>() // single world host has a connection with no NetworkStreamConnection. TODO-release handle disconnected clients
                );
            state.RequireForUpdate(m_RpcBufferGroup);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_NetworkStreamConnectionHandle = state.GetComponentTypeHandle<NetworkStreamConnection>();
            m_IncomingRpcDataStreamBufferComponentHandle = state.GetBufferTypeHandle<IncomingRpcDataStreamBuffer>();
            m_OutgoingRpcDataStreamBufferComponentHandle = state.GetBufferTypeHandle<OutgoingRpcDataStreamBuffer>();

            var rpcCollection = SystemAPI.GetSingleton<RpcCollection>();
            rpcCollection.RegisterRpc<RequestProtocolVersionHandshake>();
            rpcCollection.RegisterRpc<ServerRequestApprovalAfterHandshake>();
            rpcCollection.RegisterRpc<ServerApprovedConnection>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            m_RpcData.Dispose();
            m_RpcTypeHashToIndex.Dispose();
            m_DynamicAssemblyList.Dispose();
        }

        /// <summary>
        /// Calls RPC's execute methods. By default, those will have deserialization logic from <see cref="RpcExecutor.ExecuteCreateRequestComponent"/>
        /// </summary>
        [BurstCompile]
        struct RpcExecJob : IJobChunk
        {
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            [ReadOnly] public EntityTypeHandle entityType;
            public ComponentTypeHandle<NetworkStreamConnection> connectionType;
            public BufferTypeHandle<IncomingRpcDataStreamBuffer> inBufferType;
            public BufferTypeHandle<OutgoingRpcDataStreamBuffer> outBufferType;
            public Entity connectionUniqueIdEntity;
            public uint connectionUniqueId;
            [ReadOnly] public NativeList<RpcCollection.RpcData> execute;
            [ReadOnly] public NativeParallelHashMap<ulong, int> hashToIndex; // TODO - int > ushort.
            [ReadOnly] public NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly ghostMap;

            public uint localTime;

            public ConcurrentDriverStore concurrentDriverStore;
            public NetworkProtocolVersion jobProtocolVersion;
            public byte dynamicAssemblyList;
            public FixedString128Bytes worldName;
            public NetDebug netDebug;
            public byte isServer;

            public unsafe void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // This job is not written to support queries with enableable component types.
                Assert.IsFalse(useEnabledMask);

                var entities = chunk.GetNativeArray(entityType);
                var rpcInBuffer = chunk.GetBufferAccessor(ref inBufferType);
                var rpcOutBuffer = chunk.GetBufferAccessor(ref outBufferType);
                var connections = chunk.GetNativeArray(ref connectionType);
                var deserializeState = new RpcDeserializerState
                {
                    ghostMap = ghostMap,
                    CompressionModel = StreamCompressionModel.Default, // TODO - Hook-up when (eventually) customizable.
                };
                for (int i = 0; i < rpcInBuffer.Length; ++i)
                {
                    var connectionEntity = entities[i];
                    var conn = connections[i];
                    var concurrentDriver = concurrentDriverStore.GetConcurrentDriver(conn.DriverId);
                    ref var driver = ref concurrentDriver.driver;
                    var conState = concurrentDriver.driver.GetConnectionState(conn.Value);

                    // If we're now in a disconnected state check if the protocol version RPC is in the incoming buffer so we can process it and report an error if it's mismatched (reason for the disconnect)
                    if (conState == NetworkConnection.State.Disconnected && rpcInBuffer[i].Length > 0)
                    {
                        ushort rpcIndex = 0;
                        if (dynamicAssemblyList == 1)
                        {
                            var rpcHashPeek = *(ulong*) rpcInBuffer[i].GetUnsafeReadOnlyPtr();
                            if (hashToIndex.TryGetValue(rpcHashPeek, out var rpcIndexInt))
                                rpcIndex = (ushort) rpcIndexInt;
                            else rpcIndex = ushort.MaxValue;
                        }
                        else
                        {
                            rpcIndex = *(ushort*) rpcInBuffer[i].GetUnsafeReadOnlyPtr();
                        }

                        if (rpcIndex < execute.Length && execute[rpcIndex].IsApprovalType == 1)
                            netDebug.DebugLog($"[{worldName}] {conn.Value.ToFixedString()} in disconnected state but allowing {execute[rpcIndex].ToFixedString()} to get processed, as is approval RPC!");
                        else
                            continue;
                    }
                    else if (conState != NetworkConnection.State.Connected)
                    {
                        // We're not connected at the transport level yet, so we'll wait until we are before processing
                        // outgoing and incoming RPCs. Note: We don't discard them in this case either, we just hold
                        // onto them.
                        continue;
                    }

                    var dynArray = rpcInBuffer[i];
                    var parameters = new RpcExecutor.Parameters
                    {
                        Reader = dynArray.AsDataStreamReader(),
                        CommandBuffer = commandBuffer,
                        State = (IntPtr)UnsafeUtility.AddressOf(ref deserializeState),
                        Connection = connectionEntity,
                        ClientConnectionUniqueIdEntity = connectionUniqueIdEntity,
                        ClientCurrentConnectionUniqueId = connectionUniqueId,
                        JobIndex = unfilteredChunkIndex,
                        ConnectionStateRef = conn,
                        NetDebug = netDebug,
                        ProtocolVersion = jobProtocolVersion,
                        UseDynamicAssemblyList = dynamicAssemblyList != 0,
                        WorldName = worldName,
                        IsServer = isServer == 1
                    };
                    int msgHeaderLen = RpcCollection.GetInnerRpcMessageHeaderLength(dynamicAssemblyList == 1);
                    while (parameters.Reader.GetBytesRead() < parameters.Reader.Length)
                    {
                        int rpcIndex;
                        if (dynamicAssemblyList == 1)
                        {
                            ulong rpcHash = parameters.Reader.ReadULong();
                            if (!hashToIndex.TryGetValue(rpcHash, out rpcIndex))
                            {
                                netDebug.LogError(
                                    $"[{worldName}] RpcSystem received rpc with invalid hash ({rpcHash}) from {conn.Value.ToFixedString()}");
                                commandBuffer.AddComponent(unfilteredChunkIndex, connectionEntity,
                                    new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.InvalidRpc});
                                break;
                            }
                        }
                        else
                        {
                            rpcIndex = parameters.Reader.ReadUShort();
                        }

                        var rpcSizeBits = parameters.Reader.ReadUShort();
                        var rpcSizeBytes = (rpcSizeBits + 7) >> 3;

                        // Normal RPCs are not allowed during the approval connection phase
                        // On clients both ProtocolVersion and NetworkID RPCs should be ok as they are sent by server after approval is done
                        // as part of the next phase (handshake)
                        if (conn.IsHandshakeOrApproval)
                        {
                            if (execute[rpcIndex].IsApprovalType == 0)
                            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                netDebug.LogError($"[{worldName}] RpcSystem received non-approval RPC {execute[rpcIndex].ToFixedString()} while in the {conn.CurrentState.ToFixedString()} connection state, from {conn.Value.ToFixedString()}. Make sure you only send non-approval RPCs once the connection is approved. Disconnecting.");
#endif
                                commandBuffer.AddComponent(unfilteredChunkIndex, connectionEntity,
                                    new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.InvalidRpc});
                                break;
                            }
                        }

                        var rpcBitStart = parameters.Reader.GetBitsRead();
                        if (Hint.Unlikely(rpcIndex >= execute.Length))
                        {
                            netDebug.LogError($"[{worldName}] RpcSystem received invalid rpc (index {rpcIndex} out of range) from {conn.Value.ToFixedString()}!");
                            commandBuffer.AddComponent(unfilteredChunkIndex, connectionEntity,
                                new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.InvalidRpc});
                            break;
                        }

                        execute[rpcIndex].Execute.Ptr.Invoke(ref parameters);
                        // TODO - Possible defensive guard here: We can check to see if execute[rpcIndex].Execute.Ptr.Invoke encountered a fatal error, and early out.

                        // Validate rpcSizeBits matches our deserialization:
                        var rpcBitsRead = parameters.Reader.GetBitsRead() - rpcBitStart;
                        if (parameters.Reader.HasFailedReads || rpcSizeBits != rpcBitsRead)
                        {
                            var rpcBytesRead = (rpcBitsRead + 7) >> 3;
                            netDebug.LogError($"[{worldName}] RpcSystem failed to deserialize RPC '{execute[rpcIndex].ToFixedString()}', as bits read ({rpcBitsRead} [{rpcBytesRead}B] did not match expected ({rpcSizeBits} [{rpcSizeBytes}B])! Be aware that the incorrectly deserialized RPC may have still executed, but this connection will soon be closed.");
                            commandBuffer.AddComponent(unfilteredChunkIndex, entities[i], new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.InvalidRpc});
                            break;
                        }

                        parameters.Reader.Flush(); // We have to pad any unused bits,
                                                   // as we byte-align each packed RPC.

                        // Write ConnectionStateRef back in:
                        conn = parameters.ConnectionStateRef;
                        connections[i] = parameters.ConnectionStateRef;
                    }

                    dynArray.Clear();

                    var sendBuffer = rpcOutBuffer[i];
                    while (sendBuffer.Length > 0)
                    {
                        // The writer will return a buffer with a size defined by the Transport.
                        // I.e. It's not netcode who decides the max RPC size.
                        int result;
                        if ((result = driver.BeginSend(concurrentDriver.reliablePipeline, conn.Value, out var rpcPacketWriter)) < 0)
                        {
                            if(result == (int)StatusCode.NetworkSendQueueFull)
                                netDebug.DebugLog($"[{worldName}] RpcSystem BeginSend encountered StatusCode.NetworkSendQueueFull (-5), which is an expected StatusCode when sending many reliable RPCs within a short duration (the NetworkConfigParameter.sendQueue is full). Will re-attempt on future ticks, until all have succeeded.\nhttps://docs.unity3d.com/Packages/com.unity.transport@2.2/manual/faq.html#what-does-error-networksendqueuefull-mean");
                            else netDebug.LogWarning($"[{worldName}] RPCSystem failed to BeginSend message with StatusCode: {result}. Retrying next tick!");
                            break;
                        }

                        rpcPacketWriter.WriteByte((byte) NetworkStreamProtocol.Rpc);
                        rpcPacketWriter.WriteUInt(localTime);
                        var headerLengthBytes = rpcPacketWriter.Length;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.Assert(headerLengthBytes == RpcCollection.k_RpcCommonHeaderLengthBytes);
#endif

                        // If we have too many RPCs queued in our sendBuffer, send as many as we can:
                        if (sendBuffer.Length + headerLengthBytes > rpcPacketWriter.Capacity)
                        {
                            var sendArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(sendBuffer.GetUnsafePtr(), sendBuffer.Length, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            var safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(sendBuffer.AsNativeArray());
                            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref sendArray, safety);
#endif
                            var reader = new DataStreamReader(sendArray);
                            ushort rpcIndex;
                            ulong rpcHash;
                            if (dynamicAssemblyList == 1)
                            {
                                rpcHash = reader.ReadULong();
                                if (hashToIndex.TryGetValue(rpcHash, out var rpcIndexInt))
                                    rpcIndex = (ushort) rpcIndexInt;
                                else throw new InvalidOperationException($"[{worldName}][RpcSystem] Attempting to send RPC with hash '{rpcHash}' that is unknown to our own collection!");
                            }
                            else
                            {
                                rpcHash = 0;
                                rpcIndex = reader.ReadUShort();
                            }

                            var payloadLengthBits = reader.ReadUShort();
                            var payloadLengthBytes = ((payloadLengthBits + 7) >> 3);
                            var rpcLengthBytes = payloadLengthBytes + msgHeaderLen;
                            var totalLengthBytes = rpcLengthBytes + headerLengthBytes;
                            if (totalLengthBytes > rpcPacketWriter.Capacity)
                            {
                                sendBuffer.Clear();
                                driver.AbortSend(rpcPacketWriter);
                                // Could not fit a single message in the packet, this is a serious error
                                var rpcName = rpcIndex < execute.Length ? execute[rpcIndex].ToFixedString() : $"Rpc[{rpcHash}, ??, index: {rpcIndex}]";
                                throw new InvalidOperationException($"[{worldName}][RpcSystem] RPC '{rpcName}' was too big to be sent! It was {totalLengthBytes} bytes [netcode header: {headerLengthBytes}B, rpc message header: {msgHeaderLen}B, payload: {payloadLengthBits} bits], but UTP only offered a packet buffer of {rpcPacketWriter.Capacity}B! Reduce the size of this RPC payload!");
                            }

                            rpcPacketWriter.WriteBytesUnsafe((byte*) sendBuffer.GetUnsafePtr(), rpcLengthBytes);

                            // Now try to fit as many more messages in this packet as we can:
                            while (true)
                            {
                                var curTmpDataLength = rpcPacketWriter.Length - headerLengthBytes;
                                var subArray = sendArray.GetSubArray(curTmpDataLength, sendArray.Length - curTmpDataLength);
                                reader = new DataStreamReader(subArray);
                                if (dynamicAssemblyList == 1)
                                    reader.ReadULong();
                                else
                                    reader.ReadUShort();
                                var innerPayloadLengthBits = reader.ReadUShort();
                                var innerPayloadLengthBytes = ((innerPayloadLengthBits+7) >> 3);
                                var innerRpcLengthBytes = innerPayloadLengthBytes + msgHeaderLen;
                                if (rpcPacketWriter.Length + innerRpcLengthBytes > rpcPacketWriter.Capacity)
                                    break;
                                rpcPacketWriter.WriteBytesUnsafe((byte*) subArray.GetUnsafeReadOnlyPtr(), innerRpcLengthBytes);
                            }
                        }
                        else
                            rpcPacketWriter.WriteBytesUnsafe((byte*) sendBuffer.GetUnsafePtr(), sendBuffer.Length);

                        // If sending failed we stop and wait until next frame
                        if ((result = driver.EndSend(rpcPacketWriter)) <= 0)
                        {
                            if (result == (int) StatusCode.NetworkSendQueueFull)
                                netDebug.DebugLog($"[{worldName}] RpcSystem EndSend encountered StatusCode.NetworkSendQueueFull (-5), which is an expected StatusCode when sending many reliable RPCs within a short duration (hitting the outbound ReliableUtility.Parameters.WindowSize capacity). Will re-attempt on future ticks, until all have succeeded.\nhttps://docs.unity3d.com/Packages/com.unity.transport@2.2/manual/faq.html#what-does-error-networksendqueuefull-mean");
                            else netDebug.LogWarning($"[{worldName}] An error occured during RpcSystem EndSend with StatusCode: {result}, UTP Buffer Capacity: {rpcPacketWriter.Capacity}. Retrying next tick!");
                            break;
                        }

                        var tmpDataLength = rpcPacketWriter.Length - headerLengthBytes;
                        if (tmpDataLength < sendBuffer.Length)
                        {
                            // Compact the buffer, removing the rpcs we did send
                            for (int cpy = tmpDataLength; cpy < sendBuffer.Length; ++cpy)
                                sendBuffer[cpy - tmpDataLength] = sendBuffer[cpy];
                            sendBuffer.ResizeUninitialized(sendBuffer.Length - tmpDataLength);
                        }
                        else
                            sendBuffer.Clear();
                    }
                }
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Deserialize the command type from the reader stream
            // Execute the RPC
            ref readonly var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRO;
            SystemAPI.TryGetSingleton(out NetworkProtocolVersion protocolVersion);
            var connectionUniqueIdEntity = Entity.Null;
            ConnectionUniqueId connectionUniqueId = default;
            if (!state.WorldUnmanaged.IsServer())
            {
                SystemAPI.TryGetSingletonEntity<ConnectionUniqueId>(out connectionUniqueIdEntity);
                SystemAPI.TryGetSingleton(out connectionUniqueId);
            }

            m_EntityTypeHandle.Update(ref state);
            m_NetworkStreamConnectionHandle.Update(ref state);
            m_IncomingRpcDataStreamBufferComponentHandle.Update(ref state);
            m_OutgoingRpcDataStreamBufferComponentHandle.Update(ref state);
            var execJob = new RpcExecJob
            {
                commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                entityType = m_EntityTypeHandle,
                connectionType = m_NetworkStreamConnectionHandle,
                inBufferType = m_IncomingRpcDataStreamBufferComponentHandle,
                outBufferType = m_OutgoingRpcDataStreamBufferComponentHandle,
                connectionUniqueIdEntity = connectionUniqueIdEntity,
                connectionUniqueId = connectionUniqueId.Value,
                execute = m_RpcData,
                hashToIndex = m_RpcTypeHashToIndex,
                ghostMap = SystemAPI.GetSingleton<SpawnedGhostEntityMap>().Value,
                localTime = NetworkTimeSystem.TimestampMS,
                concurrentDriverStore = networkStreamDriver.ConcurrentDriverStore,
                jobProtocolVersion = protocolVersion,
                dynamicAssemblyList = m_DynamicAssemblyList.Value,
                netDebug = SystemAPI.GetSingleton<NetDebug>(),
                worldName = state.WorldUnmanaged.Name,
                isServer = state.WorldUnmanaged.IsServer() ? (byte)1 : (byte)0
            };
            state.Dependency = execJob.ScheduleParallel(m_RpcBufferGroup, state.Dependency);
            state.Dependency = networkStreamDriver.DriverStore.ScheduleFlushSendAllDrivers(state.Dependency);
        }
    }

    /// <summary>
    /// <para>A system responsible for handling all the <see cref="RpcSystem.ProtocolVersionError"/> created by the
    /// <see cref="RpcSystem"/> while receiving rpcs.
    /// </para>
    /// <para>
    /// The connection that generated the <see cref="RpcSystem.ProtocolVersionError"/> will be disconnected, by adding
    /// a <see cref="NetworkStreamRequestDisconnect"/> component, and a verbose error message containing the following
    /// is reported to the application:
    /// </para>
    /// <para> - The local protocol.</para>
    /// <para> - The remote protocol.</para>
    /// <para> - The list of all registered rpc.</para>
    /// <para> - The list of all registered serializer.</para>
    /// </summary>
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [BurstCompile]
    public partial struct RpcSystemErrors : ISystem
    {
        private EntityQuery m_ProtocolErrorQuery;
        private ComponentLookup<NetworkStreamConnection> m_NetworkStreamConnectionFromEntity;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            m_ProtocolErrorQuery = state.GetEntityQuery(ComponentType.ReadOnly<RpcSystem.ProtocolVersionError>());
            state.RequireForUpdate(m_ProtocolErrorQuery);
            state.RequireForUpdate<GhostCollection>();

            m_NetworkStreamConnectionFromEntity = state.GetComponentLookup<NetworkStreamConnection>(true);
        }

        [BurstCompile]
        partial struct ReportRpcErrors : IJobEntity
        {
            public EntityCommandBuffer commandBuffer;
            [ReadOnly] public ComponentLookup<NetworkStreamConnection> connections;
            public NativeArray<FixedString128Bytes> rpcs;
            public NativeArray<FixedString128Bytes> componentInfo;
            public NetDebug netDebug;
            public NetworkProtocolVersion localProtocol;
            public FixedString128Bytes worldName;
            public void Execute(Entity entity, in RpcSystem.ProtocolVersionError rpcError)
            {
                FixedString128Bytes connection = "unknown connection";
                if (rpcError.connection != Entity.Null)
                {
                    commandBuffer.AddComponent(rpcError.connection,
                        new NetworkStreamRequestDisconnect
                            { Reason = NetworkStreamDisconnectReason.InvalidRpc });
                    connection = connections[rpcError.connection].Value.ToFixedString();
                }

                var errorHeader = (FixedString512Bytes)$"[{worldName}] RpcSystem received bad protocol version from {connection}";
                errorHeader.Append((FixedString32Bytes)"\nLocal protocol: ");
                errorHeader.Append(localProtocol.ToFixedString());
                errorHeader.Append((FixedString32Bytes)"\nRemote protocol: ");
                errorHeader.Append(rpcError.remoteProtocol.ToFixedString());
                errorHeader.Append((FixedString512Bytes)"\nSee the following errors for more information.");
                netDebug.LogError(errorHeader);

                if (localProtocol.NetCodeVersion != rpcError.remoteProtocol.NetCodeVersion)
                {
                    netDebug.LogError((FixedString512Bytes)"The NetCode version mismatched between remote and local. Ensure that you are using the same version of Netcode for Entities on both client and server.");
                }

                if (localProtocol.GameVersion != rpcError.remoteProtocol.GameVersion)
                {
                    netDebug.LogError((FixedString512Bytes)"The Game version mismatched between remote and local. Ensure that you are using the same version of the game on both client and server.");
                }

                if (localProtocol.RpcCollectionVersion != rpcError.remoteProtocol.RpcCollectionVersion)
                {
                    netDebug.LogError((FixedString512Bytes)"The RPC Collection mismatched between remote and local. Compare the following list of RPCs against the set produced by the remote, to find which RPCs are misaligned. You can also enable `RpcCollection.DynamicAssemblyList` to relax this requirement (which is recommended during development, see documentation for more details).");
                }

                if (localProtocol.ComponentCollectionVersion != rpcError.remoteProtocol.ComponentCollectionVersion)
                {
                    netDebug.LogError((FixedString512Bytes)"The Component Collection mismatched between remote and local. Compare the following list of Components against the set produced by the remote, to find which components are misaligned. You can also enable `RpcCollection.DynamicAssemblyList` to relax this requirement (which is recommended during development, see documentation for more details).");
                }


                var s = (FixedString512Bytes)"RPC List (for above 'bad protocol version' error): ";
                s.Append(rpcs.Length);
                netDebug.LogError(s);

                for (int i = 0; i < rpcs.Length; ++i)
                    netDebug.LogError($"RpcHash[{i}] = {rpcs[i]}");

                s = (FixedString512Bytes)"Component serializer data (for above 'bad protocol version' error): ";
                s.Append(componentInfo.Length);
                netDebug.LogError(s);

                for (int i = 0; i < componentInfo.Length; ++i)
                    netDebug.LogError($"ComponentHash[{i}] = {componentInfo[i]}");

                commandBuffer.DestroyEntity(entity);
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            m_NetworkStreamConnectionFromEntity.Update(ref state);

            var collectionRpcs = SystemAPI.GetSingleton<RpcCollection>().Rpcs;
            var rpcs = CollectionHelper.CreateNativeArray<FixedString128Bytes>(collectionRpcs.Length, state.WorldUpdateAllocator);
            for (int i = 0; i < collectionRpcs.Length; ++i)
            {
                var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(collectionRpcs[i].TypeHash);
                rpcs[i] = new FixedString128Bytes(TypeManager.GetTypeInfo(typeIndex).DebugTypeName);
            }
            FixedString128Bytes serializerHashString = default;
            var ghostSerializerCollection = SystemAPI.GetSingletonBuffer<GhostComponentSerializer.State>();
            var componentInfo = CollectionHelper.CreateNativeArray<FixedString128Bytes>(ghostSerializerCollection.Length, state.WorldUpdateAllocator);
            for (int serializerIndex = 0; serializerIndex < ghostSerializerCollection.Length; ++serializerIndex)
            {
                GhostCollectionSystem.GetSerializerHashString(ghostSerializerCollection[serializerIndex],
                    ref serializerHashString);
                componentInfo[serializerIndex] = serializerHashString;
                serializerHashString.Clear();
            }

            var reportJob = new ReportRpcErrors
            {
                commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged),
                connections = m_NetworkStreamConnectionFromEntity,
                rpcs = rpcs,
                componentInfo = componentInfo,
                netDebug = SystemAPI.GetSingleton<NetDebug>(),
                localProtocol = SystemAPI.GetSingleton<NetworkProtocolVersion>(),
                worldName = state.WorldUnmanaged.Name
            };

            state.Dependency = reportJob.Schedule(state.Dependency);
        }
    }
}

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using System.Runtime.InteropServices;

namespace Unity.NetCode
{
    public struct RpcExecutor
    {
        public struct Parameters
        {
            public DataStreamReader Reader;
            public Entity Connection;
            public EntityCommandBuffer.ParallelWriter CommandBuffer;
            public int JobIndex;
            internal IntPtr State;
            public RpcDeserializerState DeserializerState
            {
                get { unsafe { return UnsafeUtility.AsRef<RpcDeserializerState>((void*)State); } }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ExecuteDelegate(ref Parameters parameters);

        /// <summary>
        /// Helper method used to create a new entity for an RPC request T.
        /// </summary>
        public static Entity ExecuteCreateRequestComponent<TActionSerializer, TActionRequest>(ref Parameters parameters)
            where TActionRequest : struct, IComponentData
            where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
        {
            var rpcData = default(TActionRequest);

            var rpcSerializer = default(TActionSerializer);
            rpcSerializer.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);
            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, new ReceiveRpcCommandRequestComponent {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, rpcData);

#if !DOTS_DISABLE_DEBUG_NAMES
            parameters.CommandBuffer.SetName(parameters.JobIndex, entity, "NetCodeRPC");
#endif
            return entity;
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    public partial class RpcSystem : SystemBase
    {
        public enum ErrorCodes
        {
            ProtocolMismatch = -1,
            InvalidRpc = -2,
            VersionNotReceived = -3
        }

        struct RpcReceiveError
        {
            public Entity connection;
            public ErrorCodes error;
        }

        struct RpcData : IComparable<RpcData>
        {
            public ulong TypeHash;
            public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> Execute;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            public ComponentType RpcType;
#endif
            public int CompareTo(RpcData other)
            {
                if (TypeHash < other.TypeHash)
                    return -1;
                if (TypeHash > other.TypeHash)
                    return 1;
                return 0;
            }
        }

        /// <summary>
        /// Treat the set of assemblies loaded on the client / server as dynamic or different. This is required if
        /// assemblies containing ghost component serializers or RPC serializers are removed when building standalone.
        /// This property is only read during system creation, so it must be set from the ClientServerBootstrap.
        /// </summary>
        public static bool DynamicAssemblyList;
        private bool m_DynamicAssemblyList;
        private NetworkStreamReceiveSystem m_ReceiveSystem;
        private GhostSimulationSystemGroup m_GhostSimulationGroup;
        private EntityQuery m_RpcBufferGroup;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private NativeList<RpcData> m_RpcData;
        private NativeParallelHashMap<ulong, int> m_RpcTypeHashToIndex;
        private NativeQueue<RpcReceiveError> m_RpcErrors;
        private bool m_CanRegister;
        private NetDebugSystem m_NetDebugSystem;

        public RpcSystem()
        {
            m_CanRegister = true;
            m_RpcData = new NativeList<RpcData>(16, Allocator.Persistent);
            m_RpcTypeHashToIndex = new NativeParallelHashMap<ulong, int>(16, Allocator.Persistent);
            m_DynamicAssemblyList = DynamicAssemblyList;
        }

        protected override void OnCreate()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<OutgoingRpcDataStreamBufferComponent>() == 1);
            UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<IncomingRpcDataStreamBufferComponent>() == 1);
#endif
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_RpcBufferGroup = GetEntityQuery(
                ComponentType.ReadWrite<IncomingRpcDataStreamBufferComponent>(),
                ComponentType.ReadWrite<OutgoingRpcDataStreamBufferComponent>(),
                ComponentType.ReadWrite<NetworkStreamConnection>(),
                ComponentType.ReadOnly<NetworkSnapshotAckComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());
            RequireForUpdate(m_RpcBufferGroup);

            RegisterRpc(ComponentType.ReadWrite<RpcSetNetworkId>(), default(RpcSetNetworkId).CompileExecute());
            m_ReceiveSystem = World.GetOrCreateSystem<NetworkStreamReceiveSystem>();
            m_GhostSimulationGroup = World.GetExistingSystem<GhostSimulationSystemGroup>();
            m_RpcErrors = new NativeQueue<RpcReceiveError>(Allocator.Persistent);
            m_NetDebugSystem = World.GetOrCreateSystem<NetDebugSystem>();
        }

        protected override void OnDestroy()
        {
            m_RpcData.Dispose();
            m_RpcTypeHashToIndex.Dispose();
            m_RpcErrors.Dispose();
        }

        public void RegisterRpc<TActionSerializer, TActionRequest>()
            where TActionRequest : struct, IComponentData
            where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
        {
            RegisterRpc(ComponentType.ReadWrite<TActionRequest>(), default(TActionSerializer).CompileExecute());
        }
        public void RegisterRpc(ComponentType type, PortableFunctionPointer<RpcExecutor.ExecuteDelegate> exec)
        {
            if (!m_CanRegister)
                throw new InvalidOperationException("Cannot register new RPCs after the RpcSystem has started running");

            if (!exec.Ptr.IsCreated)
            {
                throw new InvalidOperationException($"Cannot register RPC for type {type.GetManagedType()}: Ptr property is not created (null)" +
                                                    "Check CompileExecute() and verify you are initializing the PortableFunctionPointer with a valid static function delegate, decorated with [BurstCompile(DisableDirectCall = true)] attribute");
            }

            var hash = TypeManager.GetTypeInfo(type.TypeIndex).StableTypeHash;
            if (hash == 0)
                throw new InvalidOperationException(String.Format("Unexpected 0 hash for type {0}", type.GetManagedType()));
            if (m_RpcTypeHashToIndex.TryGetValue(hash, out var index))
            {
                var rpcData = m_RpcData[index];
                if (rpcData.TypeHash != 0)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (rpcData.RpcType == type)
                        throw new InvalidOperationException(
                            String.Format("Registering RPC {0} multiple times is not allowed", type.GetManagedType()));
                    throw new InvalidOperationException(
                        String.Format("Type hash collision between types {0} and {1}", type.GetManagedType(), rpcData.RpcType.GetManagedType()));
#else
                    throw new InvalidOperationException(
                        String.Format("Hash collision or multiple registrations for {0}", type.GetManagedType()));
#endif
                }

                rpcData.TypeHash = hash;
                rpcData.Execute = exec;
                m_RpcData[index] = rpcData;
            }
            else
            {
                m_RpcTypeHashToIndex.Add(hash, m_RpcData.Length);
                m_RpcData.Add(new RpcData
                {
                    TypeHash = hash,
                    Execute = exec,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    RpcType = type
#endif
                });
            }
        }
        [BurstCompile]
        struct RpcExecJob : IJobChunk
        {
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            [ReadOnly] public EntityTypeHandle entityType;
            public ComponentTypeHandle<NetworkStreamConnection> connectionType;
            public BufferTypeHandle<IncomingRpcDataStreamBufferComponent> inBufferType;
            public BufferTypeHandle<OutgoingRpcDataStreamBufferComponent> outBufferType;
            public NativeQueue<RpcReceiveError>.ParallelWriter errors;
            [ReadOnly] public NativeList<RpcData> execute;
            [ReadOnly] public NativeParallelHashMap<ulong, int> hashToIndex;
            [ReadOnly] public NativeParallelHashMap<SpawnedGhost, Entity> ghostMap;

            [ReadOnly] public ComponentTypeHandle<NetworkSnapshotAckComponent> ackType;
            public uint localTime;

            public NetworkDriver.Concurrent driver;
            public NetworkPipeline reliablePipeline;

            public NetworkProtocolVersion protocolVersion;
            public bool dynamicAssemblyList;
            public NetDebug netDebug;

            public unsafe void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var entities = chunk.GetNativeArray(entityType);
                var rpcInBuffer = chunk.GetBufferAccessor(inBufferType);
                var rpcOutBuffer = chunk.GetBufferAccessor(outBufferType);
                var connections = chunk.GetNativeArray(connectionType);
                var acks = chunk.GetNativeArray(ackType);
                var deserializeState = new RpcDeserializerState {ghostMap = ghostMap};
                for (int i = 0; i < rpcInBuffer.Length; ++i)
                {
                    if (driver.GetConnectionState(connections[i].Value) != NetworkConnection.State.Connected)
                        continue;

                    var dynArray = rpcInBuffer[i];
                    var parameters = new RpcExecutor.Parameters
                    {
                        Reader = dynArray.AsDataStreamReader(),
                        CommandBuffer = commandBuffer,
                        State = (IntPtr)UnsafeUtility.AddressOf(ref deserializeState),
                        Connection = entities[i],
                        JobIndex = chunkIndex
                    };
                    int msgHeaderLen = dynamicAssemblyList ? 10 : 4;
                    while (parameters.Reader.GetBytesRead() < parameters.Reader.Length)
                    {
                        int rpcIndex = 0;
                        if (dynamicAssemblyList)
                        {
                            ulong rpcHash = parameters.Reader.ReadULong();
                            if (rpcHash == 0)
                            {
                                rpcIndex = ushort.MaxValue;
                                protocolVersion.RpcCollectionVersion = 0;
                                protocolVersion.ComponentCollectionVersion = 0;
                            }
                            else if (rpcHash != 0 && !hashToIndex.TryGetValue(rpcHash, out rpcIndex))
                            {
                                errors.Enqueue(new RpcReceiveError
                                {
                                    connection = entities[i],
                                    error = ErrorCodes.InvalidRpc
                                });
                                break;
                            }
                        }
                        else
                        {
                            rpcIndex = parameters.Reader.ReadUShort();
                        }

                        var rpcSize = parameters.Reader.ReadUShort();
                        if (rpcIndex == ushort.MaxValue)
                        {
                            // Special value for NetworkProtocolVersion
                            var netCodeVersion = parameters.Reader.ReadInt();
                            var gameVersion = parameters.Reader.ReadInt();
                            var rpcVersion = parameters.Reader.ReadULong();
                            var componentVersion = parameters.Reader.ReadULong();
                            if (netCodeVersion != protocolVersion.NetCodeVersion ||
                                gameVersion != protocolVersion.GameVersion ||
                                rpcVersion != protocolVersion.RpcCollectionVersion ||
                                componentVersion != protocolVersion.ComponentCollectionVersion)
                            {
                                errors.Enqueue(new RpcReceiveError
                                {
                                    connection = entities[i],
                                    error = ErrorCodes.ProtocolMismatch
                                });
                                break;
                            }
                            //The connection has received the version. RpcSystem can't accept any rpc's if the NetworkProtocolVersion
                            //has not been received first.
                            var connection = connections[i];
                            connection.ProtocolVersionReceived = 1;
                            connections[i] = connection;
                        }
                        else if (rpcIndex >= execute.Length)
                        {
                            //If this is the server, we must disconnect the connection
                            errors.Enqueue(new RpcReceiveError
                            {
                                connection = entities[i],
                                error = ErrorCodes.InvalidRpc
                            });
                            break;
                        }
                        else if (connections[i].ProtocolVersionReceived == 0)
                        {
                            errors.Enqueue(new RpcReceiveError
                            {
                                connection = entities[i],
                                error = ErrorCodes.VersionNotReceived
                            });
                            break;
                        }
                        else
                        {
                            execute[rpcIndex].Execute.Ptr.Invoke(ref parameters);
                        }
                    }

                    dynArray.Clear();

                    var sendBuffer = rpcOutBuffer[i];
                    var ack = acks[i];
                    while (sendBuffer.Length > 0)
                    {
                        if (driver.BeginSend(reliablePipeline, connections[i].Value, out var tmp) == 0)
                        {
                            tmp.WriteByte((byte) NetworkStreamProtocol.Rpc);
                            tmp.WriteUInt(localTime);
                            uint returnTime = ack.LastReceivedRemoteTime;
                            if (returnTime != 0)
                                returnTime += (localTime - ack.LastReceiveTimestamp);
                            tmp.WriteUInt(returnTime);
                            var headerLength = tmp.Length;

                            // If sending failed we stop and wait until next frame
                            if (sendBuffer.Length + headerLength > tmp.Capacity)
                            {
                                var sendArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(sendBuffer.GetUnsafePtr(), sendBuffer.Length, Allocator.Invalid);
    #if ENABLE_UNITY_COLLECTIONS_CHECKS
                                var safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(sendBuffer.AsNativeArray());
                                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref sendArray, safety);
    #endif
                                var reader = new DataStreamReader(sendArray);
                                if (dynamicAssemblyList)
                                    reader.ReadULong();
                                else
                                    reader.ReadUShort();
                                var len = reader.ReadUShort() + msgHeaderLen;
                                if (len + headerLength > tmp.Capacity)
                                {
                                    sendBuffer.Clear();
                                    // Could not fit a single message in the packet, this is a serious error
                                    throw new InvalidOperationException("An RPC was too big to be sent, reduce the size of your RPCs");
                                }
                                tmp.WriteBytes((byte*) sendBuffer.GetUnsafePtr(), len);
                                // Try to fit a few more messages in this packet
                                while (true)
                                {
                                    var curTmpDataLength = tmp.Length - headerLength;
                                    var subArray = sendArray.GetSubArray(curTmpDataLength, sendArray.Length - curTmpDataLength);
                                    reader = new DataStreamReader(subArray);
                                    if (dynamicAssemblyList)
                                        reader.ReadULong();
                                    else
                                        reader.ReadUShort();
                                    len = reader.ReadUShort() + msgHeaderLen;
                                    if (tmp.Length + len > tmp.Capacity)
                                        break;
                                    tmp.WriteBytes((byte*) subArray.GetUnsafeReadOnlyPtr(), len);
                                }
                            }
                            else
                                tmp.WriteBytes((byte*) sendBuffer.GetUnsafePtr(), sendBuffer.Length);
                            // If sending failed we stop and wait until next frame
                            var result = 0;
                            if ((result = driver.EndSend(tmp)) <= 0)
                            {
                                netDebug.LogWarning(FixedString.Format("An error occured during EndSend. ErrorCode: {0}", result));
                                break;
                            }
                            var tmpDataLength = tmp.Length - headerLength;
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
        }

        [BurstCompile]
        struct RpcErrorReportingJob : IJob
        {
            public EntityCommandBuffer commandBuffer;
            public NativeQueue<RpcReceiveError> errors;
            [ReadOnly] public ComponentDataFromEntity<NetworkStreamConnection> connections;
            public NetDebug netDebug;

            public void Execute()
            {
                while (errors.TryDequeue(out var rpcError))
                {
                    //Because errors are per connection it is safe to add the component like that here
                    switch (rpcError.error)
                    {
                        case ErrorCodes.InvalidRpc:
                        case ErrorCodes.VersionNotReceived:
                            netDebug.LogError(FixedString.Format("RpcSystem received invalid rpc from connection {0}", connections[rpcError.connection].Value.InternalId));
                            commandBuffer.AddComponent(rpcError.connection,
                                new NetworkStreamRequestDisconnect
                                    {Reason = NetworkStreamDisconnectReason.InvalidRpc});
                            break;
                        case ErrorCodes.ProtocolMismatch:
                            commandBuffer.AddComponent(rpcError.connection,
                                new NetworkStreamRequestDisconnect
                                    {Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
                            netDebug.LogError(FixedString.Format("RpcSystem received bad protocol version from connection {0}", connections[rpcError.connection].Value.InternalId));
                            break;
                    }
                }
            }
        }

        public static unsafe void SendProtocolVersion(DynamicBuffer<OutgoingRpcDataStreamBufferComponent> buffer, NetworkProtocolVersion version)
        {
            bool dynamicAssemblyList = (version.RpcCollectionVersion == 0);
            int msgHeaderLen = dynamicAssemblyList ? 10 : 4;
            DataStreamWriter writer = new DataStreamWriter(UnsafeUtility.SizeOf<NetworkProtocolVersion>() + msgHeaderLen + 1, Allocator.Temp);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (buffer.Length != 0)
                throw new InvalidOperationException("Protocol version must be the very first RPC sent");
#endif
            if (dynamicAssemblyList)
                writer.WriteULong(0);
            else
                writer.WriteUShort(ushort.MaxValue);
            var lenWriter = writer;
            writer.WriteUShort((ushort)0);
            writer.WriteInt(version.NetCodeVersion);
            writer.WriteInt(version.GameVersion);
            if (dynamicAssemblyList)
            {
                writer.WriteULong(0);
                writer.WriteULong(0);
            }
            else
            {
                writer.WriteULong(version.RpcCollectionVersion);
                writer.WriteULong(version.ComponentCollectionVersion);
            }
            lenWriter.WriteUShort((ushort)(writer.Length - msgHeaderLen - 1));
            var prevLen = buffer.Length;
            buffer.ResizeUninitialized(buffer.Length + writer.Length);
            byte* ptr = (byte*) buffer.GetUnsafePtr();
            ptr += prevLen;
            UnsafeUtility.MemCpy(ptr, writer.AsNativeArray().GetUnsafeReadOnlyPtr(), writer.Length);
        }
        public ulong CalculateVersionHash()
        {
            if (m_RpcData.Length >= ushort.MaxValue)
                throw new InvalidOperationException(String.Format("RpcSystem does not support more than {0} RPCs", ushort.MaxValue));
            for (int i = 0; i < m_RpcData.Length; ++i)
            {
                if (m_RpcData[i].TypeHash == 0)
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    throw new InvalidOperationException(String.Format("Missing RPC registration for {0} which is used to send data", m_RpcData[i].RpcType.GetManagedType()));
#else
                    throw new InvalidOperationException("Missing RPC registration for RPC which is used to send");
#endif
            }
            m_RpcData.Sort();
            m_RpcTypeHashToIndex.Clear();
            for (int i = 0; i < m_RpcData.Length; ++i)
            {
                m_RpcTypeHashToIndex.Add(m_RpcData[i].TypeHash, i);
            }

            ulong hash = m_RpcData[0].TypeHash;
            for (int i = 0; i < m_RpcData.Length; ++i)
                hash = TypeHash.CombineFNV1A64(hash, m_RpcData[i].TypeHash);
            m_CanRegister = false;
            return m_DynamicAssemblyList ? 0 : hash;
        }
        protected override void OnUpdate()
        {
            // Deserialize the command type from the reader stream
            // Execute the RPC
            Dependency = JobHandle.CombineDependencies(Dependency, m_ReceiveSystem.LastDriverWriter, m_GhostSimulationGroup.LastGhostMapWriter);
            var execJob = new RpcExecJob
            {
                commandBuffer = m_Barrier.CreateCommandBuffer()
                    .AsParallelWriter(),
                entityType = GetEntityTypeHandle(),
                connectionType = GetComponentTypeHandle<NetworkStreamConnection>(),
                inBufferType = GetBufferTypeHandle<IncomingRpcDataStreamBufferComponent>(),
                outBufferType = GetBufferTypeHandle<OutgoingRpcDataStreamBufferComponent>(),
                errors = m_RpcErrors.AsParallelWriter(),
                execute = m_RpcData,
                hashToIndex = m_RpcTypeHashToIndex,
                ghostMap = m_GhostSimulationGroup.SpawnedGhostEntityMap,
                ackType = GetComponentTypeHandle<NetworkSnapshotAckComponent>(true),
                localTime = NetworkTimeSystem.TimestampMS,
                driver = m_ReceiveSystem.ConcurrentDriver,
                reliablePipeline = m_ReceiveSystem.ReliablePipeline,
                protocolVersion = GetSingleton<NetworkProtocolVersion>(),
                dynamicAssemblyList = m_DynamicAssemblyList,
                netDebug = m_NetDebugSystem.NetDebug
            };
            Dependency = execJob.Schedule(m_RpcBufferGroup, Dependency);
            m_GhostSimulationGroup.LastGhostMapWriter = Dependency;
            m_Barrier.AddJobHandleForProducer(Dependency);
            Dependency = new RpcErrorReportingJob
            {
                commandBuffer = m_Barrier.CreateCommandBuffer(),
                connections = GetComponentDataFromEntity<NetworkStreamConnection>(),
                errors = m_RpcErrors,
                netDebug = m_NetDebugSystem.NetDebug
            }.Schedule(Dependency);
            m_Barrier.AddJobHandleForProducer(Dependency);
            Dependency = m_ReceiveSystem.Driver.ScheduleFlushSend(Dependency);
            m_ReceiveSystem.LastDriverWriter = Dependency;
        }

        public RpcQueue<TActionSerializer, TActionRequest> GetRpcQueue<TActionSerializer, TActionRequest>()
            where TActionRequest : struct, IComponentData
            where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
        {
            if (!m_RpcTypeHashToIndex.IsCreated)
                throw new InvalidOperationException($"The RPCSystem has not been created or has been destroyed");

            var hash = TypeManager.GetTypeInfo(TypeManager.GetTypeIndex<TActionRequest>()).StableTypeHash;
            if (hash == 0)
                throw new InvalidOperationException(String.Format("Unexpected 0 hash for type {0}", typeof(TActionRequest)));
            int index;
            if (!m_RpcTypeHashToIndex.TryGetValue(hash, out index))
            {
                if (!m_CanRegister)
                    throw new InvalidOperationException("Cannot register new RPCs after the RpcSystem has started running");
                index = m_RpcData.Length;
                m_RpcTypeHashToIndex.Add(hash, index);
                m_RpcData.Add(new RpcData
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    RpcType = ComponentType.ReadWrite<TActionRequest>()
#endif
                });
            }
            return new RpcQueue<TActionSerializer, TActionRequest>
            {
                rpcType = hash,
                rpcTypeHashToIndex = m_RpcTypeHashToIndex,
                dynamicAssemblyList = m_DynamicAssemblyList
            };
        }
    }
}

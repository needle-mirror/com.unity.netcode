using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.NetCode
{

    public struct RpcExecutor
    {
        public struct Parameters
        {
            public DataStreamReader Reader;
            public DataStreamReader.Context ReaderContext;
            public Entity Connection;
            public EntityCommandBuffer.Concurrent CommandBuffer;
            public int JobIndex;
        }
        public delegate void ExecuteDelegate(ref Parameters parameters);

        public static void ExecuteCreateRequestComponent<T>(ref Parameters parameters)
            where T: struct, IRpcCommand
        {
            var rpcData = default(T);
            rpcData.Deserialize(parameters.Reader, ref parameters.ReaderContext);
            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, new ReceiveRpcCommandRequestComponent {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, rpcData);
        }
    }

    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    public class RpcSystem : JobComponentSystem
    {
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
        private NetworkStreamReceiveSystem m_ReceiveSystem;
        private EntityQuery m_RpcBufferGroup;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private NativeList<RpcData> m_RpcData;
        private NativeHashMap<ulong, int> m_RpcTypeHashToIndex;
        private bool m_CanRegister;

        protected override void OnCreate()
        {
            m_CanRegister = true;
            m_RpcData = new NativeList<RpcData>(16, Allocator.Persistent);
            m_RpcTypeHashToIndex = new NativeHashMap<ulong, int>(16, Allocator.Persistent);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            Debug.Assert(UnsafeUtility.SizeOf<OutgoingRpcDataStreamBufferComponent>() == 1);
            Debug.Assert(UnsafeUtility.SizeOf<IncomingRpcDataStreamBufferComponent>() == 1);
#endif
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_RpcBufferGroup = GetEntityQuery(
                ComponentType.ReadWrite<IncomingRpcDataStreamBufferComponent>(),
                ComponentType.ReadWrite<OutgoingRpcDataStreamBufferComponent>(),
                ComponentType.ReadWrite<NetworkStreamConnection>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());
            RequireForUpdate(m_RpcBufferGroup);

            RegisterRpc(ComponentType.ReadWrite<RpcSetNetworkId>(), default(RpcSetNetworkId).CompileExecute());
            m_ReceiveSystem = World.GetOrCreateSystem<NetworkStreamReceiveSystem>();
        }

        protected override void OnDestroy()
        {
            m_RpcData.Dispose();
            m_RpcTypeHashToIndex.Dispose();
        }

        public void RegisterRpc<T>()
            where T: struct, IRpcCommand
        {
            RegisterRpc(ComponentType.ReadWrite<T>(), default(T).CompileExecute());
        }
        public void RegisterRpc(ComponentType type, PortableFunctionPointer<RpcExecutor.ExecuteDelegate> exec)
        {
            if (!m_CanRegister)
                throw new InvalidOperationException("Cannot register new RPCs after the RpcSystem has started running");
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
            public EntityCommandBuffer.Concurrent commandBuffer;
            [ReadOnly] public ArchetypeChunkEntityType entityType;
            [ReadOnly] public ArchetypeChunkComponentType<NetworkStreamConnection> connectionType;
            public ArchetypeChunkBufferType<IncomingRpcDataStreamBufferComponent> inBufferType;
            public ArchetypeChunkBufferType<OutgoingRpcDataStreamBufferComponent> outBufferType;
            [ReadOnly] public NativeList<RpcData> execute;
            [ReadOnly] public NativeHashMap<ulong, int> hashToIndex;

            public UdpNetworkDriver.Concurrent driver;
            public NetworkPipeline reliablePipeline;

            public NetworkProtocolVersion protocolVersion;

            public unsafe void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var entities = chunk.GetNativeArray(entityType);
                var rpcInBuffer = chunk.GetBufferAccessor(inBufferType);
                var rpcOutBuffer = chunk.GetBufferAccessor(outBufferType);
                var connections = chunk.GetNativeArray(connectionType);
                for (int i = 0; i < rpcInBuffer.Length; ++i)
                {
                    if (driver.GetConnectionState(connections[i].Value) != NetworkConnection.State.Connected)
                        continue;

                    var dynArray = rpcInBuffer[i];
                    var reader = DataStreamUnsafeUtility.CreateReaderFromExistingData((byte*) dynArray.GetUnsafePtr(),
                        dynArray.Length);
                    var parameters = new RpcExecutor.Parameters
                    {
                        Reader = reader,
                        ReaderContext = default(DataStreamReader.Context),
                        CommandBuffer = commandBuffer,
                        Connection = entities[i],
                        JobIndex = chunkIndex

                    };
                    while (reader.GetBytesRead(ref parameters.ReaderContext) < reader.Length)
                    {
                        var rpcIndex = reader.ReadUShort(ref parameters.ReaderContext);
                        if (rpcIndex == ushort.MaxValue)
                        {
                            // Special value for NetworkProtocolVersion
                            var netCodeVersion = reader.ReadInt(ref parameters.ReaderContext);
                            var gameVersion = reader.ReadInt(ref parameters.ReaderContext);
                            var rpcVersion = reader.ReadULong(ref parameters.ReaderContext);
                            if (netCodeVersion != protocolVersion.NetCodeVersion ||
                                gameVersion != protocolVersion.GameVersion ||
                                rpcVersion != protocolVersion.RpcCollectionVersion)
                            {
                                commandBuffer.AddComponent(chunkIndex, entities[i], new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                throw new InvalidOperationException("Network protocol mismatch");
#else
                                return;
#endif
                            }
                        }
                        else if (rpcIndex >= execute.Length)
                        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            throw new InvalidOperationException("Received an invalid RPC");
#else
                            return;
#endif
                        }
                        else
                            execute[rpcIndex].Execute.Ptr.Invoke(ref parameters);
                    }

                    dynArray.Clear();

                    var sendBuffer = rpcOutBuffer[i];
                    if (sendBuffer.Length > 0)
                    {
                        DataStreamWriter tmp = new DataStreamWriter(sendBuffer.Length, Allocator.Temp);
                        tmp.WriteBytes((byte*) sendBuffer.GetUnsafePtr(), sendBuffer.Length);
                        driver.Send(reliablePipeline, connections[i].Value, tmp);
                        sendBuffer.Clear();
                    }

                }
            }
        }

        public static unsafe void SendProtocolVersion(DynamicBuffer<OutgoingRpcDataStreamBufferComponent> buffer, NetworkProtocolVersion version)
        {
            DataStreamWriter writer = new DataStreamWriter(UnsafeUtility.SizeOf<NetworkProtocolVersion>() + 2 + 1, Allocator.Temp);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (buffer.Length != 0)
                throw new InvalidOperationException("Protocol version must be the very first RPC sent");
#endif
            writer.Write((byte) NetworkStreamProtocol.Rpc);
            writer.Write(ushort.MaxValue);
            writer.Write(version.NetCodeVersion);
            writer.Write(version.GameVersion);
            writer.Write(version.RpcCollectionVersion);
            var prevLen = buffer.Length;
            buffer.ResizeUninitialized(buffer.Length + writer.Length);
            byte* ptr = (byte*) buffer.GetUnsafePtr();
            ptr += prevLen;
            UnsafeUtility.MemCpy(ptr, writer.GetUnsafeReadOnlyPtr(), writer.Length);
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
            return hash;
        }
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            // Deserialize the command type from the reader stream
            // Execute the RPC
            inputDeps = JobHandle.CombineDependencies(inputDeps, m_ReceiveSystem.LastDriverWriter);
            var execJob = new RpcExecJob
            {
                commandBuffer = m_Barrier.CreateCommandBuffer().ToConcurrent(),
                entityType = GetArchetypeChunkEntityType(),
                connectionType = GetArchetypeChunkComponentType<NetworkStreamConnection>(),
                inBufferType = GetArchetypeChunkBufferType<IncomingRpcDataStreamBufferComponent>(),
                outBufferType = GetArchetypeChunkBufferType<OutgoingRpcDataStreamBufferComponent>(),
                execute = m_RpcData,
                hashToIndex = m_RpcTypeHashToIndex,
                driver = m_ReceiveSystem.ConcurrentDriver,
                reliablePipeline = m_ReceiveSystem.ReliablePipeline,
                protocolVersion = GetSingleton<NetworkProtocolVersion>()
            };
            var handle = execJob.Schedule(m_RpcBufferGroup, inputDeps);
            m_Barrier.AddJobHandleForProducer(handle);
            m_ReceiveSystem.LastDriverWriter = handle;
            return handle;
        }

        public RpcQueue<T> GetRpcQueue<T>() where T : struct, IRpcCommand
        {
            var hash = TypeManager.GetTypeInfo(TypeManager.GetTypeIndex<T>()).StableTypeHash;
            if (hash == 0)
                throw new InvalidOperationException(String.Format("Unexpected 0 hash for type {0}", typeof(T)));
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
                    RpcType = ComponentType.ReadWrite<T>()
#endif
                });
            }
            return new RpcQueue<T>
            {
                rpcType = hash,
                rpcTypeHashToIndex = m_RpcTypeHashToIndex
            };
        }
    }
}

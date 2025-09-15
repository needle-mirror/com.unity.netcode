using System;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// The RpcCollection is the set of all available RPCs. It is created by the RpcSystem.
    /// It is used to register RPCs and to get queues for sending RPCs. In most cases you
    /// do not need to use it directly, the generated code will use it to setup the RPC
    /// components.
    /// </summary>
    public struct RpcCollection : IComponentData
    {
        internal struct RpcData : IComparable<RpcData>
        {
            public ulong TypeHash;
            public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> Execute;
            public byte IsApprovalType;
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
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

            [GenerateTestsForBurstCompatibility]
            public FixedString512Bytes ToFixedString()
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
                return (FixedString512Bytes)$"Rpc[{TypeHash}, {RpcType.ToFixedString()}]";
                #else
                return (FixedString512Bytes)$"Rpc[{TypeHash}, ???]";
                #endif
            }
            /// <inheritdoc cref="ToFixedString"/>
            public override string ToString() => ToFixedString().ToString();
        }
        /// <summary>
        /// <para>
        /// Allows the set assemblies loaded on the client and server to differ. This is useful during development when
        /// assemblies containing ghost component serializers or RPCs are removed when building standalone.
        /// This usually happens during development when you are connecting a standalone player to the Editor.
        /// For example, tests are usually not included in a standalone build, but they are still compiled and
        /// registered in the Editor, which causes a mismatch in the set of assemblies.
        /// </para>
        /// <para>
        /// If set to false (default), the RPC system triggers an RPC version error when connecting to a server with
        /// a different set of assemblies. This is more strict and acts as a validation step during handshake.
        /// </para>
        /// <para>
        /// If set to true, six bytes is added to the header of each RPC.
        /// The RPC system doesn't trigger an RPC version error when connecting to
        /// a server with a different set of assemblies. Instead, an error will be triggered if an invalid RPC or serialized component is
        /// received.
        /// </para>
        /// </summary>
        public bool DynamicAssemblyList
        {
            get { return m_DynamicAssemblyList.Value == 1; }
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (m_IsFinal == 1)
                    throw new InvalidOperationException("DynamicAssemblyList must be set before the RpcSystem.OnUpdate is called!");
#endif
                m_DynamicAssemblyList.Value = value ? (byte)1u : (byte)0u;
            }
        }

        /// <summary>
        /// The RPC "common header" format is 9 bytes:
        /// - Message Type: byte
        /// - LocalTime: int (a.k.a. `remoteTime` on the receiver)
        ///
        /// And then, for each RPC, the header is:
        /// - RpcHash: [short|long] (based on DynamicAssemblyList)
        /// - Size: ushort
        /// - Payload : x bytes
        ///
        /// So for a single message we have:
        /// - 9 (common header) + 4 => 13 bytes (no DynamicAssemblyList)
        /// - 9 (common header) + 10 => 19 bytes (with DynamicAssemblyList)
        /// </summary>
        /// <param name="dynamicAssemblyList">Whether or not your project is using <see cref="DynamicAssemblyList"/>.</param>
        /// <returns>If <see cref="DynamicAssemblyList"/>, 15 bytes, otherwise 9 bytes.</returns>
        public static int GetRpcHeaderLength(bool dynamicAssemblyList) => k_RpcCommonHeaderLengthBytes + GetInnerRpcMessageHeaderLength(dynamicAssemblyList);

        /// <inheritdoc cref="GetRpcHeaderLength"/>>
        internal const int k_RpcCommonHeaderLengthBytes = 5;

        /// <summary>
        /// If <see cref="DynamicAssemblyList"/>, 10 bytes, otherwise 4 bytes.
        /// </summary>
        /// <param name="dynamicAssemblyList">Whether or not your project is using <see cref="DynamicAssemblyList"/>.</param>
        /// <returns>If <see cref="DynamicAssemblyList"/>, 10 bytes, otherwise 4 bytes.</returns>
        internal static int GetInnerRpcMessageHeaderLength(bool dynamicAssemblyList) => dynamicAssemblyList ? 10 : 4;

        /// <summary>
        /// Register a new RPC type which can be sent over the network. This must be called before
        /// any connections are established.
        /// </summary>
        /// <typeparam name="TActionSerializer">A struct of type IRpcCommandSerializer.</typeparam>
        /// <typeparam name="TActionRequest">A struct of type IComponent.</typeparam>
        public void RegisterRpc<TActionSerializer, TActionRequest>()
            where TActionRequest : struct, IComponentData
            where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
        {
            RegisterRpc(ComponentType.ReadWrite<TActionRequest>(), default(TActionSerializer).CompileExecute());
        }

        /// <summary>
        /// Register a new RPC type which can be sent over the network. This must be called before
        /// any connections are established.
        /// </summary>
        /// <typeparam name="TActionRequestAndSerializer">A struct of type IComponentData, with IRpcCommandSerializer too.</typeparam>
        public void RegisterRpc<TActionRequestAndSerializer>()
            where TActionRequestAndSerializer : struct, IComponentData, IRpcCommandSerializer<TActionRequestAndSerializer>
        {
            RegisterRpc(ComponentType.ReadWrite<TActionRequestAndSerializer>(), default(TActionRequestAndSerializer).CompileExecute());
        }

        /// <summary>
        /// Register a new RPC type which can be sent over the network. This must be called before
        /// any connections are established.
        /// </summary>
        /// <param name="type">Type to register.</param>
        /// <param name="exec">Callback for RPC to execute.</param>
        public void RegisterRpc(ComponentType type, PortableFunctionPointer<RpcExecutor.ExecuteDelegate> exec)
        {
            if (m_IsFinal == 1)
                throw new InvalidOperationException("Cannot register new RPCs after the RpcSystem has started running");

            if (!exec.Ptr.IsCreated)
            {
                throw new InvalidOperationException($"Cannot register RPC for type {type.GetManagedType()}: Ptr property is not created (null)" +
                                                    "Check CompileExecute() and verify you are initializing the PortableFunctionPointer with a valid static function delegate, decorated with [BurstCompile(DisableDirectCall = true)] attribute");
            }

            var hash = TypeManager.GetTypeInfo(type.TypeIndex).StableTypeHash;
            if (hash == 0)
                throw new InvalidOperationException(String.Format("Unexpected 0 hash for type {0}", type.GetManagedType()));

            byte isApprovalType = 0;
            if (IsApprovalRpcType(type))
                isApprovalType = 1;

            if (m_RpcTypeHashToIndex.TryGetValue(hash, out var index))
            {
                var rpcData = m_RpcData[index];
                if (rpcData.TypeHash != 0)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (rpcData.RpcType == type)
                        throw new InvalidOperationException($"Registering RPC {type.ToFixedString()} multiple times is not allowed! Existing: {rpcData.RpcType.ToFixedString()}!");
                    throw new InvalidOperationException($"StableTypeHash collision between types {type.ToFixedString()} and {rpcData.RpcType.ToFixedString()} while registering RPC!");
#else
                    throw new InvalidOperationException($"Hash collision or multiple registrations for {type.ToFixedString()} while registering RPC! Existing: {rpcData.TypeHash}!");
#endif
                }

                rpcData.IsApprovalType = isApprovalType;
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
                    IsApprovalType = isApprovalType,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    RpcType = type
#endif
                });
            }
        }

        internal static bool IsApprovalRpcType(ComponentType type)
        {
            // TODO - Infer via code-gen, rather than runtime reflection!
            return typeof(IApprovalRpcCommand).IsAssignableFrom(type.GetManagedType());
        }

        /// <summary>
        /// Get an RpcQueue which can be used to send RPCs.
        /// </summary>
        /// <typeparam name="TActionRequestAndSerializer">Struct of type <see cref="TActionRequestAndSerializer"/>
        /// implementing <see cref="IRpcCommandSerializer{TActionRequestAndSerializer}"/>.</typeparam>
        /// <returns><see cref="RpcQueue{TActionRequestAndSerializer,TActionRequestAndSerializer}"/> to be used to send RPCs.</returns>
        public RpcQueue<TActionRequestAndSerializer, TActionRequestAndSerializer> GetRpcQueue<TActionRequestAndSerializer>()
            where TActionRequestAndSerializer : struct, IComponentData, IRpcCommandSerializer<TActionRequestAndSerializer>
        {
            return GetRpcQueue<TActionRequestAndSerializer, TActionRequestAndSerializer>();
        }

        /// <summary>
        /// Get an RpcQueue which can be used to send RPCs.
        /// </summary>
        /// <typeparam name="TActionSerializer">Struct of type <see cref="IRpcCommandSerializer{TActionRequest}"/></typeparam>
        /// <typeparam name="TActionRequest">Struct of type <see cref="IComponentData"/></typeparam>
        /// <returns><see cref="RpcQueue{TActionSerializer,TActionRequest}"/> to be used to send RPCs.</returns>
        public RpcQueue<TActionSerializer, TActionRequest> GetRpcQueue<TActionSerializer, TActionRequest>()
            where TActionRequest : struct, IComponentData
            where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
        {
            var hash = TypeManager.GetTypeInfo(TypeManager.GetTypeIndex<TActionRequest>()).StableTypeHash;
            if (hash == 0)
                throw new InvalidOperationException(String.Format("Unexpected 0 hash for type {0}", typeof(TActionRequest)));
            int index;
            if (!m_RpcTypeHashToIndex.TryGetValue(hash, out index))
            {
                if (m_IsFinal == 1)
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
        /// <summary>
        /// Internal method to calculate the hash of all types when sending version. When calling this you
        /// must have write access to the singleton since it changes internal state.
        /// </summary>
        internal ulong CalculateVersionHash()
        {
            Debug.Assert(m_IsFinal == 0);
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

#if ENABLE_UNITY_RPC_REGISTRATION_LOGGING
#if UNITY_DOTS_DEBUG
                UnityEngine.Debug.Log(String.Format("NetCode RPC Method hash 0x{0:X} index {1} type {2}", m_RpcData[i].TypeHash, i, m_RpcData[i].RpcType));
#else
                UnityEngine.Debug.Log(String.Format("NetCode RPC Method hash {0} index {1}", m_RpcData[i].TypeHash, i));
#endif
#endif
            }

            ulong hash = m_RpcData[0].TypeHash;
            for (int i = 0; i < m_RpcData.Length; ++i)
                hash = TypeHash.CombineFNV1A64(hash, m_RpcData[i].TypeHash);
            m_IsFinal = 1;
            return hash;
        }

        internal NativeList<RpcData> Rpcs => m_RpcData;

        internal NativeList<RpcData> m_RpcData;
        internal NativeParallelHashMap<ulong, int> m_RpcTypeHashToIndex;
        internal NativeReference<byte> m_DynamicAssemblyList;

        internal byte m_IsFinal;
    }
}

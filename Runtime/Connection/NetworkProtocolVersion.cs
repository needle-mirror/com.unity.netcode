using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.HostMigration;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>The NetworkProtocolVersion is a singleton entity that is automatically created by the
    /// <see cref="GhostCollectionSystem"/> (once the GhostCollection is ready), and is used to verify client and
    /// server compatibility.</para>
    /// <para>
    /// The protocol version is composed by different part:</para>
    /// <para>- The NetCode package version.</para>
    /// <para>- A user defined <see cref="GameProtocolVersion"/> game version, that identify the version of your game</para>
    /// <para>- A unique hash of all the <see cref="IRpcCommand"/> and <see cref="ICommandData"/> that is used to verify both client and server
    /// recognize the same rpc and command and that can serialize/deserialize them in the same way</para>
    /// <para>- A unique hash of all the replicated <see cref="IComponentData"/> and <see cref="IBufferElementData"/> that is used to verify
    /// both client and server can serialize/deserialize all the replicated component present in the ghosts</para>
    /// <para>
    /// When a client tries to connect to the server, as part of the initial handshake, they exchange their protocol version
    /// to validate they are both using same version. If the version mismatch, the connection is forcibly closed.
    /// </para>
    /// </summary>
    public struct NetworkProtocolVersion : IComponentData
    {
        /// <summary>
        /// The integer used to determine a compatible version of the NetCode package.
        /// </summary>
        /// <remarks>
        /// Note: When we increment this, it implies netcode is not compatible with a previous version.
        /// However, there is no guarantee you'll get a graceful error if connecting to an incompatible version,
        /// because, if we change the serialization of the protocol version (e.g. by changing RPC header size), we
        /// almost certainly cannot deserialize this value correctly anyway.
        /// <br/><b>NOTE: Netcode makes no guarantees that any major, minor, or patch versions of netcode are
        /// compatible with each other. We only guarantee that the exact version is compatible with itself.</b>
        /// </remarks>
        public const int k_NetCodeVersion = 2;

        /// <summary>
        /// The NetCode package version
        /// </summary>
        public int NetCodeVersion;
        /// <summary>
        /// The user specific game version the server and client are using. 0 by default, unless the <see cref="GameProtocolVersion"/> is used
        /// to customise it.
        /// </summary>
        public int GameVersion;
        /// <summary>
        /// A unique hash computed of all the RPC and commands, used to check if the server and client have the same messages and
        /// with compatible data and serialization.
        /// </summary>
        public ulong RpcCollectionVersion;
        /// <summary>
        /// A unique hash calculated on all the serialized components that can be used to check if the client
        /// can properly decode the snapshots.
        /// </summary>
        public ulong ComponentCollectionVersion;

        /// <summary>
        /// Denotes if these two are matching, while respecting <see cref="RpcCollection.DynamicAssemblyList"/> rules.
        /// </summary>
        /// <param name="other"></param>
        /// <param name="useDynamicAssemblyList">
        /// DynamicAssemblyList means we don't care about RpcCollectionVersion and ComponentCollectionVersion.
        /// I.e. They can be different, AS LONG AS each in-use RPC and ghost has a hash that is known to both
        /// the client and server.</param>
        /// <returns></returns>
        internal bool IsCorrect(NetworkProtocolVersion other, bool useDynamicAssemblyList)
        {
            var matchesRequiredFields = NetCodeVersion == other.NetCodeVersion && GameVersion == other.GameVersion;
            if (useDynamicAssemblyList) return matchesRequiredFields;
            return matchesRequiredFields && RpcCollectionVersion == other.RpcCollectionVersion
                                         && ComponentCollectionVersion == other.ComponentCollectionVersion;
        }

        /// <summary>Helper.</summary>
        /// <returns>"NPV[NetCodeVersion:0, GameVersion:0, RpcCollection:00000000000, ComponentCollection:00000000000]"</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString() => $"NPV[NetCodeVersion:{NetCodeVersion}, GameVersion:{GameVersion}, RpcCollection:{RpcCollectionVersion}, ComponentCollection:{ComponentCollectionVersion}]";

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();

        /// <summary>Ensures we're not writing invalid values.</summary>
        [Conditional("UNITY_ASSERTIONS")]
        internal void AssertIsValid()
        {
            UnityEngine.Debug.Assert(NetCodeVersion != 0, nameof(NetCodeVersion));
            // It's valid for the GameVersion to be 0!
            UnityEngine.Debug.Assert(RpcCollectionVersion != 0, nameof(RpcCollectionVersion));
            UnityEngine.Debug.Assert(ComponentCollectionVersion != 0, nameof(ComponentCollectionVersion));
        }
    }

    /// <summary>
    /// The game specific version to use for protocol validation when the client and server connects.
    /// If a singleton with this component does not exist 0 will be used instead.
    /// Protocol validation will still validate the <see cref="NetworkProtocolVersion.NetCodeVersion"/>,
    /// <see cref="NetworkProtocolVersion.RpcCollectionVersion"/> and <see cref="NetworkProtocolVersion.ComponentCollectionVersion"/>.
    /// </summary>
    public struct GameProtocolVersion : IComponentData
    {
        /// <summary>
        /// An user defined integer that identify the current game version.
        /// </summary>
        public int Version;
    }

     /// <summary>
    /// System RPC: Sent from each World, as soon as the transport layer indicates a successful connection has been
    /// established. Each state what they believe the <see cref="NetworkProtocolVersion"/> is.
    /// If they align, the server will reply with <see cref="ServerApprovedConnection"/>, or
    /// <see cref="ServerRequestApprovalAfterHandshake"/> if the approval flow is enabled.
    /// </summary>
    [BurstCompile]
    internal struct RequestProtocolVersionHandshake : IApprovalRpcCommand, IRpcCommandSerializer<RequestProtocolVersionHandshake>
    {
        public NetworkProtocolVersion Data;
        public uint ConnectionUniqueId;

        /// <summary>
        /// Do not change (except in the very rare case that the RPC serialization fundamentally changes),
        /// otherwise we create junk protocol version errors!
        /// </summary>
        private const int NetcodeVersionBaseline = 2;
        private const int GameVersionBaseline = 0;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in RequestProtocolVersionHandshake data)
        {
            data.Data.AssertIsValid();
            var compressionModel = StreamCompressionModel.Default;
            writer.WritePackedIntDelta(data.Data.NetCodeVersion, NetcodeVersionBaseline, compressionModel);
            writer.WritePackedIntDelta(data.Data.GameVersion, GameVersionBaseline, compressionModel);
            writer.WriteULong(data.Data.RpcCollectionVersion);
            writer.WriteULong(data.Data.ComponentCollectionVersion);
            writer.WriteUInt(data.ConnectionUniqueId);
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref RequestProtocolVersionHandshake data)
        {
            var compressionModel = StreamCompressionModel.Default;
            data.Data.NetCodeVersion = reader.ReadPackedIntDelta(NetcodeVersionBaseline, compressionModel);
            data.Data.GameVersion = reader.ReadPackedIntDelta(GameVersionBaseline, compressionModel);
            data.Data.RpcCollectionVersion = reader.ReadULong();
            data.Data.ComponentCollectionVersion = reader.ReadULong();
            data.ConnectionUniqueId = reader.ReadUInt();
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            // Received protocol version, see if it's right:
            parameters.ProtocolVersion.AssertIsValid();
            var rpcData = default(RequestProtocolVersionHandshake);
            rpcData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);

            var protocolVersionIsCorrect = rpcData.Data.IsCorrect(parameters.ProtocolVersion, parameters.UseDynamicAssemblyList);
            parameters.NetDebug.DebugLog($"[{parameters.WorldName}][Connection] Received protocol version {parameters.ConnectionStateRef.Value.ToFixedString()} UDAL:{parameters.UseDynamicAssemblyList} Connection[UniqueId:{rpcData.ConnectionUniqueId}] IsCorrect:{protocolVersionIsCorrect}\n - Ours:{parameters.ProtocolVersion.ToFixedString()}\n - Them:{rpcData.Data.ToFixedString()}");
            if (protocolVersionIsCorrect)
            {
                // Signal that we can `Handshake` this connection!
                parameters.ConnectionStateRef.ProtocolVersionReceived = 1;
                // If the client is reporting a unique connection ID it means it is reconnecting, this is assigned to the
                // connection entity of the client on the server. When assigning new unique IDs later the server will see
                // the client already has one and skips it.
                if (rpcData.ConnectionUniqueId != 0)
                {
                    if (parameters.IsServer)
                    {
                        parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new ConnectionUniqueId() { Value = rpcData.ConnectionUniqueId });
                        parameters.CommandBuffer.AddComponent<MigrateComponents>(parameters.JobIndex, parameters.Connection);
                    }
                    parameters.CommandBuffer.AddComponent<NetworkStreamIsReconnected>(parameters.JobIndex, parameters.Connection);
                }
                return;
            }

            // Error flow:
            var connectionEntity = parameters.Connection;
            var pveEntity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, pveEntity, new RpcSystem.ProtocolVersionError
            {
                connection = connectionEntity,
                remoteProtocol = rpcData.Data,
            });
        }
        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }

    /// <summary>
    /// System RPC: The server will reply with this RPC if the client knew the <see cref="NetworkProtocolVersion"/>
    /// (via <see cref="RequestProtocolVersionHandshake"/>), but only if approval is needed.
    /// If approval is NOT needed, it'll jump straight to <see cref="ServerApprovedConnection"/>.
    /// </summary>
    [BurstCompile]
    internal struct ServerRequestApprovalAfterHandshake : IApprovalRpcCommand, IRpcCommandSerializer<ServerRequestApprovalAfterHandshake>
    {
        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in ServerRequestApprovalAfterHandshake data)
        {
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref ServerRequestApprovalAfterHandshake data)
        {
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            // RPC arrived on the client, client must enter approval state.
            var rpcData = default(ServerRequestApprovalAfterHandshake);
            rpcData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);

            // Validate this is allowed to execute but after deserialization to prevent deserialization errors
            if (parameters.IsServer)
            {
                parameters.NetDebug.LogError($"[{parameters.WorldName}][Connection] Server received internal client-only RPC request '{ComponentType.ReadWrite<ServerRequestApprovalAfterHandshake>().ToFixedString()}' from client. This is not allowed, and the client connection will be disconnected.");
                parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new NetworkStreamRequestDisconnect
                {
                    Reason = NetworkStreamDisconnectReason.InvalidRpc,
                });
                return;
            }

            parameters.NetDebug.DebugLog($"[{parameters.WorldName}][Connection] Client received valid protocol version from server, handshake complete!");
            parameters.ConnectionStateRef.CurrentState = ConnectionState.State.Approval;
            parameters.ConnectionStateRef.CurrentStateDirty = true;
            parameters.CommandBuffer.SetName(parameters.JobIndex, parameters.Connection, "NetworkConnection (Approval)");
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }
}

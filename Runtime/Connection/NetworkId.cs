using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Temporary type, used to upgrade to new component type, to be removed before final 1.0
    /// </summary>
    [Obsolete("NetworkIdComponent has been deprecated. Use NetworkId instead (UnityUpgradable) -> NetworkId", true)]
    public struct NetworkIdComponent : IComponentData
    {}

    /// <summary>
    /// The connection identifier assigned by the server to the incoming client connection.
    /// The NetworkIdComponent is used as temporary client identifier for the current session. When a client disconnects,
    /// its network id can be reused by the server, and assigned to a new, incoming connection (on a a "first come, first serve" basis).
    /// Thus, there is no guarantee that a disconnecting client will receive the same network id once reconnected.
    /// As such, the network identifier should never be used to persist - and then retrieve - information for a given client/player.
    /// </summary>
    public struct NetworkId : IComponentData, IEquatable<NetworkId>
    {
        /// <summary>
        /// The network identifier assigned by the server. A valid identifier it is always greater than 0.
        /// </summary>
        public int Value;

        /// <summary>
        /// Returns 'NID[value]'.
        /// </summary>
        /// <returns>Returns 'NID[value]'.</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString32Bytes ToFixedString()
        {
            var s = new FixedString32Bytes((FixedString32Bytes)"NID[");
            s.Append(Value);
            s.Append(']');
            return s;
        }

        /// <inheritdoc cref="ToFixedString"/>>
        public override string ToString() => ToFixedString().ToString();

        /// <inheritdoc cref="IEquatable{T}.Equals(object)"/>
        public static bool operator ==(NetworkId left, NetworkId right) => left.Equals(right);

        /// <inheritdoc cref="IEquatable{T}.Equals(object)"/>
        public static bool operator !=(NetworkId left, NetworkId right) => !left.Equals(right);

        /// <inheritdoc cref="IEquatable{T}.Equals(object)"/>
        public bool Equals(NetworkId other) => this.Value == other.Value;

        /// <inheritdoc cref="IEquatable{T}.Equals(object)"/>
        public override bool Equals(object obj) => obj is NetworkId other && Equals(other);

        /// <inheritdoc cref="object.GetHashCode"/>
        public override int GetHashCode() => Value;
    }

    /// <summary>
    /// System RPC sent from the server to client to assign a <see cref="NetCode.NetworkId"/> to a newly accepted connection.
    /// I.e. <see cref="ConnectionState.State.Handshake"/> and <see cref="ConnectionState.State.Approval"/> (if enabled) succeeded!
    /// </summary>
    /// <remarks>
    /// Also responsible for telling the client some additional server configuration information.
    /// Previously called `RpcSetNetworkId`.
    /// </remarks>
    [BurstCompile]
    internal struct ServerApprovedConnection : IApprovalRpcCommand, IRpcCommandSerializer<ServerApprovedConnection>
    {
        private const uint NetworkIdBaseline = 2;
        public int NetworkId;
        public uint UniqueId;
        public ClientServerTickRateRefreshRequest RefreshRequest;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in ServerApprovedConnection data)
        {
            UnityEngine.Debug.Assert(data.NetworkId != 0);

            writer.WritePackedUIntDelta((uint)data.NetworkId, NetworkIdBaseline, state.CompressionModel);
            writer.WriteUInt(data.UniqueId);
            data.RefreshRequest.Serialize(ref writer, in state.CompressionModel);
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref ServerApprovedConnection data)
        {
            data.NetworkId = (int) reader.ReadPackedUIntDelta(NetworkIdBaseline, state.CompressionModel);
            data.UniqueId = reader.ReadUInt();
            data.RefreshRequest.Deserialize(ref reader, in state.CompressionModel);
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            // Client received confirmation that they've successfully connected to the server!
            var rpcData = default(ServerApprovedConnection);
            rpcData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);

            // Validate this is allowed to execute but after deserialization to prevent deserialization errors
            if (parameters.IsServer)
            {
                parameters.NetDebug.LogError($"[{parameters.WorldName}][Connection] Server received internal client-only RPC request '{ComponentType.ReadWrite<ServerApprovedConnection>().ToFixedString()}' from client. This is not allowed, and the client connection will be disconnected.");
                parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new NetworkStreamRequestDisconnect
                {
                    Reason = NetworkStreamDisconnectReason.InvalidRpc,
                });
                return;
            }

            // Set the connection unique ID as commanded by the server
            if (parameters.ClientConnectionUniqueIdEntity == Entity.Null)
            {
                var uniqueIdEntity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
                parameters.CommandBuffer.AddComponent(parameters.JobIndex, uniqueIdEntity, new ConnectionUniqueId() {Value = rpcData.UniqueId});
            }
            else
            {
                parameters.CommandBuffer.SetComponent(parameters.JobIndex, parameters.ClientConnectionUniqueIdEntity, new ConnectionUniqueId() { Value = rpcData.UniqueId });
                if (parameters.ClientCurrentConnectionUniqueId == rpcData.UniqueId)
                {
                    parameters.CommandBuffer.AddComponent<NetworkStreamIsReconnected>(parameters.JobIndex, parameters.Connection);
                }
            }

            parameters.CommandBuffer.AddComponent<ConnectionApproved>(parameters.JobIndex, parameters.Connection);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new NetworkId {Value = rpcData.NetworkId});
            parameters.CommandBuffer.AddComponent<LocalConnection>(parameters.JobIndex, parameters.Connection);
            var ent = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, ent, rpcData.RefreshRequest);
            parameters.CommandBuffer.SetName(parameters.JobIndex, parameters.Connection, new FixedString64Bytes(FixedString.Format("NetworkConnection ({0})", rpcData.NetworkId)));
            parameters.NetDebug.DebugLog($"[{parameters.WorldName}][Connection] Client {parameters.Connection.ToFixedString()} received approval from server, we were assigned NetworkId:{rpcData.NetworkId} UniqueId:{rpcData.UniqueId}.");
            parameters.ConnectionStateRef.CurrentState = ConnectionState.State.Connected;
            parameters.ConnectionStateRef.ProtocolVersionReceived = 1;
            parameters.ConnectionStateRef.ConnectionApprovalTimeoutStart = 0;
            parameters.ConnectionStateRef.CurrentStateDirty = true;
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }
}

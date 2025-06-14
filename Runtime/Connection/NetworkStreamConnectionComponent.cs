using System;
using System.Runtime.InteropServices;
using Unity.Entities;

using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Error;

namespace Unity.NetCode
{
    ///<summary>
    /// <para>A connection is represented by an entity having a NetworkStreamConnection.
    /// The component hold a reference to the underlying transport <see cref="NetworkConnection"/> and the <see cref="NetworkDriver"/>
    /// that created it.
    /// All connections share a common set of components:</para>
    /// <para>- <see cref="NetworkId"/></para>
    /// <para>- <see cref="IncomingRpcDataStreamBuffer"/></para>
    /// <para>- <see cref="OutgoingCommandDataStreamBuffer"/></para>
    /// <para>- <see cref="OutgoingRpcDataStreamBuffer"/></para>
    /// <para>- <see cref="PrespawnSectionAck"/></para>
    /// <para>- <see cref="CommandTarget"/></para>
    /// <para>Client connections also have a <see cref="IncomingSnapshotDataStreamBuffer"/> to handle server ghost snapshots.</para>
    ///</summary>
    /// <remarks>Never destroy this entity yourself. You'll receive an error if you attempt to do so.</remarks>
    public struct NetworkStreamConnection : ICleanupComponentData
    {
        /// <summary>
        /// The underlying transport <see cref="NetworkConnection"/>
        /// </summary>
        public NetworkConnection Value;

        /// <summary>
        /// The driver identifier that create the connection. Can be used to retrieve the <see cref="NetworkDriver"/> or the
        /// <see cref="NetworkDriverStore.NetworkDriverInstance"/> from the <see cref="NetworkDriverStore"/>.
        /// </summary>
        public int DriverId;

        /// <summary>
        /// Flag used to mark if the connection has exchanged the protocol version.
        /// 1 indicates that the remote version protocol has been received AND accepted (i.e. it's valid)!
        /// </summary>
        public int ProtocolVersionReceived;

        /// <summary>
        /// Cache of the last state pulled from the driver.
        /// </summary>
        /// <remarks>May be stale, as only refreshed once per <see cref="SimulationSystemGroup"/> tick.</remarks>
        public ConnectionState.State CurrentState;

        /// <summary>
        /// Server-only: The timestamp at which the connection was accepted on the server.
        /// This is then used to detect if a connection handshake or approval timeout has occured.
        /// </summary>
        internal uint ConnectionApprovalTimeoutStart;

        /// <summary>
        /// In cases where we update the state outside the event raising logic, this way it'll get picked up by the next update
        /// </summary>
        [MarshalAs(UnmanagedType.U1)]
        internal bool CurrentStateDirty;

        /// <summary>Helper.</summary>
        internal bool IsHandshakeOrApproval => CurrentState is ConnectionState.State.Handshake or ConnectionState.State.Approval;
    }

    /// <summary>
    /// A component used to signal that a connection should send and receive snapshots and commands.
    /// Before adding this component the connection only processes RPCs. Must be Added by game logic to start sending snapshots and commands.
    /// </summary>
    public struct NetworkStreamInGame : IComponentData
    {
    }

#if ENABLE_HOST_MIGRATION
    /// <summary>
    /// This tag is added to connections which have been reconnected (client reconnects to a server after diconnecting).
    /// It is added on both the server and client side.
    /// </summary>
    public struct NetworkStreamIsReconnected : IComponentData { }
#endif

    /// <summary>
    /// The unique ID assigned to the connection entity in this world, to be sent to the server in case of re-connects.
    /// It needs to be a separate singleton entity as the connection entity itself will be destroyed during disconnect.
    /// </summary>
    struct ConnectionUniqueId : IComponentData
    {
        public uint Value;
    }

    /// <summary>
    /// Add this component to a ServerWorld network connection entity, to denote that it has been approved by your logic.
    /// It will be added automatically on the client, once the connection is approved.
    /// </summary>
    /// <remarks>
    /// Typically: The flow is that you'll send a game-specific <see cref="IApprovalRpcCommand"/> to the server,
    /// containing a authentication secret fetched from your game backend. The server RPC handling logic then needs
    /// to authenticate this secret, and - if valid - add this component to denote (to netcode) that this connection is
    /// approved.
    /// <br/>If the client fails authentication, either allow them to timeout, or manually disconnect them with
    /// <see cref="NetworkStreamDisconnectReason.ApprovalFailure"/>.
    /// </remarks>
    /// <seealso cref="ClientServerTickRate.HandshakeApprovalTimeoutMS"/>
    public struct ConnectionApproved : IComponentData
    {
    }

    /// <summary>
    /// A per-connection component, which is used by the <see cref="GhostSendSystem"/> (on the server) to force a non-default packet size for snapshots.
    /// Must be added to the NetworkConnection entity for a connection, by your game logic.
    /// </summary>
    /// <remarks>
    /// Helps enforce a specific KBPS target.
    /// For example: A value of 416 bytes * 60hz (via <see cref="ClientServerTickRate.SimulationTickRate"/>) = ~200kbit/s.
    /// Note, however, that this:
    /// - Does not include or affect RPCs, commands, control messages, or UDP header overhead.
    /// - Does include UTP packet header overhead.
    /// </remarks>
    public struct NetworkStreamSnapshotTargetSize : IComponentData
    {
        /// <summary>
        /// The desired packet size to use for the snapshot. By default, the packet size is the <see cref="NetworkParameterConstants.MaxMessageSize"/>
        /// minus some headers.
        /// It is possible to specify a packet size larger than a single <see cref="NetworkParameterConstants.MaxMessageSize"/>, in which case the
        /// snapshot data is sent using a pipeline that support fragmentation (see <see cref="NetworkDriverStore.NetworkDriverInstance.unreliableFragmentedPipeline"/>.
        /// The upper bound limit for this value is payload capacity of the fragmentation pipeline (see <see cref="Unity.Networking.Transport.Utilities.FragmentationUtility"/>).
        /// </summary>
        /// <remarks>
        /// There is a minimum snapshot size, which ensures that some new and destroyed entities get replicated,
        /// and ensures that at least one ghost is replicated in every snapshot.
        /// See <see cref="GhostChunkSerializer"/> for this behaviour.
        /// </remarks>
        public int Value;
    }

    /// <inheritdoc cref="DisconnectReason"/>
    /// <remarks>Maps directly to <see cref="DisconnectReason"/>, with NetCode specific additions.</remarks>
    public enum NetworkStreamDisconnectReason
    {
        /// <inheritdoc cref="DisconnectReason.Default"/>
        ConnectionClose = DisconnectReason.Default,
        /// <inheritdoc cref="DisconnectReason.Timeout"/>
        Timeout = DisconnectReason.Timeout,
        /// <inheritdoc cref="DisconnectReason.MaxConnectionAttempts"/>
        MaxConnectionAttempts = DisconnectReason.MaxConnectionAttempts,
        /// <inheritdoc cref="DisconnectReason.ClosedByRemote"/>
        ClosedByRemote = DisconnectReason.ClosedByRemote,
        /// <summary>NetCode-specific: Denotes that we've detected an unknown or unexpected ghost hash, implying that this is an incompatible server/client pair.</summary>
        BadProtocolVersion = 4,
        /// <summary>NetCode-specific: Denotes that we've detected a hash miss-match in an RPC, or an unknown RPC. Implies that this is an incompatible server/client pair.</summary>
        InvalidRpc = 5,
        /// <inheritdoc cref="DisconnectReason.AuthenticationFailure"/>
        AuthenticationFailure = DisconnectReason.AuthenticationFailure,
        /// <inheritdoc cref="DisconnectReason.ProtocolError"/>
        ProtocolError = DisconnectReason.ProtocolError,
        /// <summary>
        /// NetCode-specific: Denotes that the client's internal netcode logic has failed to send the
        /// <see cref="RequestProtocolVersionHandshake"/> to the server within the given <see cref="ClientServerTickRate.HandshakeApprovalTimeoutMS"/>.
        /// </summary>
        /// <remarks>Is your approval timeout too short? If not, may indicate a netcode error.</remarks>
        HandshakeTimeout = 100,
        /// <summary>NetCode-specific: Denotes that the client has failed approval, by inputting invalid credentials.</summary>
        /// <remarks>Must be invoked by your own code, when handling <see cref="IApprovalRpcCommand"/> RPCs.</remarks>
        ApprovalFailure = 101,
        /// <summary>
        /// NetCode-specific: Denotes that the client has failed to be approved by the server within the given
        /// <see cref="ClientServerTickRate.HandshakeApprovalTimeoutMS"/>.
        /// </summary>
        /// <remarks>In other words: The client failed to send a <see cref="IApprovalRpcCommand"/> RPC when asked to.</remarks>
        ApprovalTimeout = 102,
    }

    /// <summary>
    /// An optional cleanup component that can be added to a newly created connection to monitor its state changes.
    /// Must be added and removed by the gameplay logic. When the <see cref="ConnectionState"/> is present, the NetCode package
    /// will update the component when the connection state changes.
    /// By adding the ConnectionState state component, the connection <see cref="NetworkId"/> and <see cref="DisconnectReason"/>
    /// are retained until the game don't remove the state component.
    /// </summary>
    /// <remarks>
    /// We are considering deprecating or replacing this. Prefer <see cref="NetworkStreamDriver.ConnectionEventsForTick"/> as it:
    /// <list type="bullet">
    /// <item>Supports multiple consumers (i.e. subscribers in the pub/sub model).</item>
    /// <item>Reduces boilerplate.</item>
    /// </list>
    /// </remarks>
    public struct ConnectionState : ICleanupComponentData
    {
        /// <summary>
        /// The current state of the connection.
        /// </summary>
        public enum State
        {
            /// <summary>
            /// Default, connection not created or not initialized.
            /// </summary>
            Unknown,
            /// <summary>
            /// The connection has been closed.
            /// </summary>
            Disconnected,
            /// <summary>
            /// Client-Only, the connection is trying to contact the server and establish a communication channel.
            /// </summary>
            Connecting,
            /// <summary>
            /// The client connected to the server and is exchanging some initial messages, such as verify the <see cref="NetworkProtocolVersion"/>
            /// and <see cref="GameProtocolVersion"/> are compatible.
            /// </summary>
            Handshake,
            /// <summary>
            /// Connection is connected on the transport level but needs to be approved before continuing to the handshake.
            /// </summary>
            Approval,
            /// <summary>
            /// The connection has been established, the handshake is completed, and the connection is now fully connected.
            /// A <see cref="NetworkId"/> component is added to the Network Connection as we enter this state.
            /// </summary>
            Connected,
        }

        /// <summary>
        /// The current state of the connection. Updated internally by the <see cref="NetworkStreamReceiveSystem"/>.
        /// </summary>
        public State CurrentState;
        /// <summary>
        /// The id assigned to the connection. Identical to the <see cref="NetCode.NetworkId"/> value.
        /// </summary>
        public int NetworkId;
        /// <summary>
        /// Set when the connection is in <see cref="State.Disconnected"/> state, the reason why the connection has been terminated.
        /// </summary>
        public NetworkStreamDisconnectReason DisconnectReason;

        /// <summary>
        /// <para>Check if two connection state are equals. They are if:</para>
        /// <para>- The <see cref="State"/> is the same.</para>
        /// <para>- The <see cref="NetworkId"/> is the same.</para>
        /// <para>- The <see cref="DisconnectReason"/> is the same.</para>
        /// </summary>
        /// <param name="other">The component to compare</param>
        /// <returns>Whether the two connection state are equal.</returns>
        public bool Equals(ConnectionState other) => CurrentState == other.CurrentState && NetworkId == other.NetworkId && DisconnectReason == other.DisconnectReason;
    }

    /// <summary>
    /// A component used to signal that the game logic wants to close the connection
    /// </summary>
    public struct NetworkStreamRequestDisconnect : IComponentData
    {
        /// <summary>
        /// Optional, the reason for the disconnection. The default is <see cref="NetworkStreamDisconnectReason.ConnectionClose"/>.
        /// </summary>
        public NetworkStreamDisconnectReason Reason;
    }
    /// <summary>
    /// A component that can be added to a new entity to create a new connection instead of calling <see cref="NetworkStreamDriver.Connect"/>
    /// </summary>
    public struct NetworkStreamRequestConnect : IComponentData
    {
        /// <summary>
        /// The remote server address.
        /// </summary>
        public NetworkEndpoint Endpoint;
    }

    /// <summary>
    /// A component that can be added to a new entity to start listening to a new connection instead of calling <see cref="NetworkStreamDriver.Listen"/>
    /// </summary>
    public struct NetworkStreamRequestListen : IComponentData
    {
        /// <summary>
        /// The remote server address.
        /// </summary>
        public NetworkEndpoint Endpoint;
    }

    /// <summary>
    /// Optional cleanup component that can be added to the <see cref="NetworkStreamRequestListen"/> entity when the request is created.
    /// Used to monitor the state of the request. When present, the component is update by the <see cref="NetworkStreamListenSystem"/> system when the
    /// request is handled.
    /// </summary>
    /// <remarks>
    /// Being a cleanup component it is responsibility of the request creator to proper handle the request entity life-cycle.
    /// </remarks>
    public struct NetworkStreamRequestListenResult : ICleanupComponentData
    {
        /// <summary>
        /// The status of the listen request./
        /// </summary>
        public enum State
        {
            /// <summary>
            /// The listen request is still pending.
            /// </summary>
            Pending = 0,
            /// <summary>
            /// The listen request has been successfully handled.
            /// </summary>
            Succeeded,
            /// <summary>
            /// The listen request failed. Errors should be present in the log.
            /// </summary>
            Failed,
            /// <summary>
            /// The listen request has been refused. The driver was already listenining.
            /// </summary>
            RefusedAlreadyListening,
            /// <summary>
            /// The listen request has been refused because multiple requests were present
            /// </summary>
            RefusedMultipleRequests,
        }
        /// <summary>
        /// The remote server address for that request/.
        /// </summary>
        public NetworkEndpoint Endpoint;
        /// <summary>
        /// The request status.
        /// </summary>
        public State RequestState;
    }

    /// <summary>
    /// Temporary type, used to upgrade to new component type, to be removed before final 1.0
    /// </summary>
    [Obsolete("IncomingCommandDataStreamBufferComponent has been deprecated. Use IncomingCommandDataStreamBuffer instead (UnityUpgradable) -> IncomingCommandDataStreamBuffer", true)]
    public struct IncomingCommandDataStreamBufferComponent : IBufferElementData
    {
        /// <summary>
        /// The buffer content
        /// </summary>
        public byte Value;
    }
    /// <summary>
    /// Temporary type, used to upgrade to new component type, to be removed before final 1.0
    /// </summary>
    [Obsolete("OutgoingCommandDataStreamBufferComponent has been deprecated. Use OutgoingCommandDataStreamBuffer instead (UnityUpgradable) -> OutgoingCommandDataStreamBuffer", true)]
    public struct OutgoingCommandDataStreamBufferComponent : IBufferElementData
    {
        /// <summary>
        /// The buffer content
        /// </summary>
        public byte Value;
    }
    /// <summary>
    /// Temporary type, used to upgrade to new component type, to be removed before final 1.0
    /// </summary>
    [Obsolete("IncomingSnapshotDataStreamBufferComponent has been deprecated. Use IncomingSnapshotDataStreamBuffer instead (UnityUpgradable) -> IncomingSnapshotDataStreamBuffer", true)]
    public struct IncomingSnapshotDataStreamBufferComponent : IBufferElementData
    {
        /// <summary>
        /// The buffer content
        /// </summary>
        public byte Value;
    }

    /// <summary>
    /// This buffer stores a single incoming command packet. One per NetworkStream (client).
    /// A command packet contains commands for CommandSendSystem.k_InputBufferSendSize (default 4) ticks where 3 of them are delta compressed.
    /// It also contains some timestamps etc for ping calculations.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct IncomingCommandDataStreamBuffer : IBufferElementData
    {
        /// <summary>
        /// The buffer content
        /// </summary>
        public byte Value;
    }
    /// <summary>
    /// This buffer stores a single outgoing command packet without the headers for timestamps and ping.
    /// A command packet contains commands for CommandSendSystem.k_InputBufferSendSize (default 4) ticks where 3 of them are delta compressed.
    /// It also contains some timestamps etc for ping calculations.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct OutgoingCommandDataStreamBuffer : IBufferElementData
    {
        /// <summary>
        /// The buffer content
        /// </summary>
        public byte Value;
    }

    /// <summary>
    /// One per NetworkConnection.
    /// Stores the incoming, yet-to-be-processed snapshot stream data for a connection.
    /// Each snapshot is designed to fit inside <see cref="NetworkParameterConstants.MaxMessageSize"/>,
    /// so expect this to be MaxMessageSize or less.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct IncomingSnapshotDataStreamBuffer : IBufferElementData
    {
        /// <summary>
        /// The buffer content
        /// </summary>
        public byte Value;
    }

    internal static class NetCodeBufferComponentExtensions
    {
        public static unsafe DataStreamReader AsDataStreamReader<T>(this DynamicBuffer<T> self)
            where T: unmanaged, IBufferElementData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<T>() != 1)
                throw new System.InvalidOperationException("Can only convert DynamicBuffers of size 1 to DataStreamWriters");
#endif
            var na = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(self.GetUnsafeReadOnlyPtr(), self.Length, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(self.AsNativeArray());
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref na, safety);
#endif
            return new DataStreamReader(na);
        }
        public static unsafe void Add<T>(this DynamicBuffer<T> self, ref DataStreamReader reader)
            where T: unmanaged, IBufferElementData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<T>() != 1)
                throw new System.InvalidOperationException("Can only Add to DynamicBuffers of size 1 from DataStreamReaders");
#endif
            var oldLen = self.Length;
            var length = reader.Length - reader.GetBytesRead();
            self.ResizeUninitialized(oldLen + length);
            reader.ReadBytesUnsafe((byte*)self.GetUnsafePtr() + oldLen, length);
        }
    }
}

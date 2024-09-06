using Unity.Collections;
using Unity.Entities;


namespace Unity.NetCode
{
    /// <summary>
    /// An interface that should be used to declare a <see cref="NetworkStreamProtocol.Rpc"/> struct.
    /// <para>
    /// RPCs are "one-shot" messages that can be sent and received by both the client and server, and can be used for different
    /// purposes. E.g. Implementing a lobby, loading level logic, requesting to spawn a player etc.
    /// Unlike ghost <see cref="SnapshotData"/>, rpc messages are sent <b>using a dedicated reliable channel</b> and
    /// are therefore guaranteed to be received.
    /// </para>
    /// <para>
    /// As they're reliable messages, RPCs are not meant to be used as a replacement for ghosts, nor for sending data that will change frequently,
    /// nor player commands (<see cref="ICommandData"/> and <see cref="IInputComponentData"/>). <b>Why not?</b>
    /// 1) There is a maximum number of reliable packets that can be in-flight at any given time.
    /// 2) Latency is introduced by the ordering guarantee of the reliability pipeline.
    /// </para>
    /// <para>
    /// An RPC struct can contain any number of burst-compatible fields. However, once serialized, its size must fit into a single packet.
    /// Large messages are not supported (<see cref="NetworkParameterConstants.MaxMessageSize"/> and account for header sizes).
    /// </para>
    /// <para>
    /// It is possible to partially mitigate this limitation by creating a custom <see cref="INetworkStreamDriverConstructor"/> and
    /// setting a larger MaxMessageSize (but that will only work in favourable conditions and networks (ensure thorough testing!)) or by
    /// adding a <see cref="FragmentationPipelineStage"/> stage into the reliable pipeline (channel).
    /// </para>
    /// <para>
    /// <b>Usage: </b> To send an RPC declared using the <see cref="IRpcCommand"/> interface, you should create a new entity with your rpc message component,
    /// <i>as well as a <see cref="SendRpcCommandRequest"/> (which will notify the NetCode system that it exists, and send it)</i>.
    /// It is best to do this with an archetype to avoid runtime structural changes:
    ///
    /// <code>
    /// m_RpcArchetype = EntityManager.CreateArchetype(..);
    ///
    /// var ent = EntityManager.CreateEntity(m_RpcArchetype);
    /// EntityManager.SetComponentData(new MyRpc { SomeData = 5 });
    /// </code>
    /// </para>
    /// <para>
    /// RPCs declared using the <see cref="IRpcCommand"/> will have serialization and other boilerplate code for handling
    /// the <see cref="SendRpcCommandRequest"/> request automatically generated.
    /// For example:
    /// <code>
    /// public struct MyRpc : IRpcCommand
    /// {
    ///    public int SomeData;
    /// }
    /// </code>
    /// will generate the following systems and structs:</para>
    /// <para>- A struct implementing the <see cref="IRpcCommandSerializer{T}"/> for your rpc type.</para>
    /// <para>- A system responsible for consuming the <see cref="SendRpcCommandRequest"/> requests, and queuing
    /// the messages into the <see cref="OutgoingRpcDataStreamBuffer"/> stream (for the outgoing connection), invoked via
    /// <see cref="RpcQueue{TActionSerializer,TActionRequest}"/>.
    /// </para>
    /// <para>
    /// Because the serialization is generated by our source generator, only types recognized by the code-generation system
    /// (and that are available to use inside commands and rpcs) are going to be serialized.
    /// See <see cref="Unity.NetCode.Generators.TypeRegistryEntry"/> for other details.
    /// </para>
    /// <para>
    /// The <see cref="OutgoingRpcDataStreamBuffer"/> is processed at the end of the simulation frame by the
    /// <see cref="RpcSystem"/>, and all messages in queue attempt to be sent over the network (assuming the reliable
    /// buffer is not full, as mentioned).
    /// </para>
    /// <para>
    /// <b>To distinguish between a "broadcast" RPC and an "RPC sent to a specific client", see <see cref="SendRpcCommandRequest"/>.</b>
    /// </para>
    /// </summary>
    /// <remarks>
    /// RPCs do not make any guarantees regarding arrival relative to ghost snapshots.
    /// E.g. If you send an RPC first, and then send a snapshot, you must assume that they'll be received in <i>any</i> order.
    /// However, <b>all RPC network messages are received in the exact same order that they are "sent" (NOT "raised"!).</b>
    /// </remarks>
    public interface IRpcCommand : IComponentData
    {}

    /// <summary>
    /// An interface which can be used to implement RPCs for use in the connection approval flow.
    /// Only <see cref="IApprovalRpcCommand"/> commands are allowed to be sent and received, while in
    /// <see cref="ConnectionState.State.Handshake"/> and/or <see cref="ConnectionState.State.Approval"/> states.
    /// <br/>Connection approval can be optionally required for all incoming connection on a server. The connection flow
    /// can only proceed when the server received an Approval RPC payload which it can validate.
    /// Approval tokens are game-specific, thus netcode expects user-code to add an <see cref="ConnectionApproved"/>
    /// to the connection entity, once a valid <see cref="IApprovalRpcCommand"/> has been received by the server.
    /// </summary>
    public interface IApprovalRpcCommand : IComponentData
    {}

    /// <summary>
    /// Interop struct used to pass additional data to the <see cref="IRpcCommandSerializer{T}.Serialize"/> method.
    /// </summary>
    public struct RpcSerializerState
    {
        /// <summary>
        /// Read-only accessor to retrieve the <see cref="GhostInstance"/> from an entity.
        /// Used to serialize the a replicated ghost entity reference.
        /// </summary>
        public ComponentLookup<GhostInstance> GhostFromEntity;

        /// <summary>
        /// Read-only map for retrieving the <see cref="StreamCompressionModel"/> assigned to be used for RPCs.
        /// </summary>
        public StreamCompressionModel CompressionModel;
    }

    /// <summary>
    /// Interop struct used to pass additional data to the <see cref="IRpcCommandSerializer{T}.Deserialize"/> method.
    /// </summary>
    public struct RpcDeserializerState
    {
        /// <summary>
        /// Read-only map for retrieving the entity bound the given <see cref="SpawnedGhost"/>.
        /// Used to deserialize replicated ghost entity references.
        /// </summary>
        public NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly ghostMap;

        /// <summary>
        /// Read-only map for retrieving the <see cref="StreamCompressionModel"/> assigned to be used for RPCs.
        /// </summary>
        public StreamCompressionModel CompressionModel;
    }

    /// <summary>
    /// Interface that must be implemented by a burst-compatible struct to serialize/deserialize the
    /// specified <typeparam name="T">rpc</typeparam> type.
    /// <para>A common pattern is to make the struct declaring the rpc to also implement the serialize/deserialize interface.
    /// For example:
    /// <para><code>
    /// struct MyRpc : IComponentData, IRpcCommandSerializer{MyRpc}
    /// {
    ///     public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in MyRpc data)
    ///     { ... }
    ///     public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref MyRpc data)
    ///     { ... }
    ///     PortableFunctionPointer{RpcExecutor.ExecuteDelegate} CompileExecute()
    ///     { ... }
    /// }
    /// </code></para>
    /// </para>
    /// When declaring an rpc using the <see cref="IRpcCommand"/> interface, it is not necessary to implement the
    /// `IRpcCommandSerializer` interface yourself; the code-generation will automatically create a struct implementing the interface
    /// and all necessary boilerplate code.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IRpcCommandSerializer<T> where T: struct, IComponentData
    {
        /// <summary>
        /// Method called by the <see cref="RpcSystem"/> when an rpc is dequeued from the
        /// <see cref="OutgoingRpcDataStreamBuffer"/> (to be sent over the network).
        /// The serialization code is automatically generated when your struct implements the
        /// <see cref="IRpcCommand"/> interface.
        /// You must implement this method yourself when you opt-in for manual serialization.
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="state"></param>
        /// <param name="data"></param>
        void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in T data);
        /// <summary>
        /// Method called by the <see cref="RpcSystem"/> when an rpc is dequeued from the
        /// <see cref="IncomingRpcDataStreamBuffer"/>. Copies the data from the
        /// <paramref name="reader"/> to the output <paramref name="data"/>.
        /// The deserialization code is automatically generated when your struct implements the
        /// <see cref="IRpcCommand"/> interface.
        /// You must implement this method yourself when you opt-in for manual serialization.
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="state"></param>
        /// <param name="data"></param>
        void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref T data);
        /// <summary>
        /// Invoked when the rpc is registered to the <see cref="RpcSystem"/> at runtime.
        /// Should return a valid burst-compatible function pointer of a static method
        /// that will be called after the rpc has been deserialized to actually "execute" the command.
        /// By declaring rpcs using <see cref="IRpcCommand"/>, this method is automatically generated.
        /// See <see cref="RpcExecutor"/> for further information on how to use it to implement your
        /// custom execute method.
        /// </summary>
        /// <returns></returns>
        PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute();
    }
}

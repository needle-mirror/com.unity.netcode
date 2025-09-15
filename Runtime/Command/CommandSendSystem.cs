#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Mathematics;

namespace Unity.NetCode
{
    /// <summary>
    /// This singleton is used by code-gen. It stores a mapping of which ticks the client
    /// has changes to inputs so steps in the prediction loop can be batched when inputs
    /// are not changing.
    /// </summary>
    public struct UniqueInputTickMap : IComponentData
    {
        /// <summary>
        /// The set of ticks where inputs were changed compared to the frame before it. The value is not used but usually set to the same tick as the key.
        /// </summary>
        public NativeParallelHashMap<NetworkTick, NetworkTick>.ParallelWriter Value;
        internal NativeParallelHashMap<NetworkTick, NetworkTick> TickMap;
    }

    /// <summary>
    /// The parent group for all input gathering systems. Only present in client worlds
    /// (and local worlds, to allow singleplayer to use the same input gathering system).
    /// It runs before the <see cref="CommandSendSystemGroup"/> to remove any latency between
    /// input gathering and command submission.
    /// All systems that translate user input (for example, using the <see cref="UnityEngine.Input"/> into
    /// <see cref="ICommandData"/> command data must update in this group).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.LocalSimulation, WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    public partial class GhostInputSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// The parent group for all generated systems that copy data from the an <see cref="IInputComponentData"/> to the
    /// underlying <see cref="InputBufferData{T}"/>, that is the ring buffer that will contains the generated user commands.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation,
        WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostInputSystemGroup), OrderLast = true)]
    public partial class CopyInputToCommandBufferSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// The parent group for all generated systems that copy data from and underlying <see cref="InputBufferData{T}"/>
    /// to its parent <see cref="IInputComponentData"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation,
                       WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    public partial class CopyCommandBufferToInputSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// This group contains all core-generated system that are used to compare commands for sake of identifing the ticks the client
    /// has changed input (see <see cref="m_UniqueInputTicks"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation, WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostInputSystemGroup))]
    public partial class CompareCommandSystemGroup : ComponentSystemGroup
    {
        private NativeParallelHashMap<NetworkTick, NetworkTick> m_UniqueInputTicks;
        /// <summary>
        /// Create the <see cref="UniqueInputTickMap"/> singleton and store a reference to the
        /// UniqueInputTicks hash map
        /// </summary>
        protected override void OnCreate()
        {
            if (World.IsHost())
            {
                base.OnCreate();
                Enabled = false;
                return;
            }
            m_UniqueInputTicks = new NativeParallelHashMap<NetworkTick, NetworkTick>(CommandDataUtility.k_CommandDataMaxSize * 4, Allocator.Persistent);
            var singletonEntity = EntityManager.CreateEntity(ComponentType.ReadWrite<UniqueInputTickMap>());
            EntityManager.SetName(singletonEntity, "UniqueInputTickMap-Singleton");
            EntityManager.SetComponentData(singletonEntity, new UniqueInputTickMap{Value = m_UniqueInputTicks.AsParallelWriter(), TickMap = m_UniqueInputTicks});

            base.OnCreate();
        }
        /// <summary>
        /// Dispose all the allocated resources.
        /// </summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();

            m_UniqueInputTicks.Dispose();
        }
    }

    /// <summary>
    /// Parent group of all systems that serialize <see cref="ICommandData"/> structs into the
    /// <see cref="OutgoingCommandDataStreamBuffer"/> buffer.
    /// The serialized commands are then sent later by the <see cref="CommandSendPacketSystem"/>.
    /// Only present in client world.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation, WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostInputSystemGroup))]
    // dependency just for acking
    [UpdateAfter(typeof(GhostReceiveSystem))]
    public partial class CommandSendSystemGroup : ComponentSystemGroup
    {
        /// <summary>
        /// The maximum serialized size of an individual Command payload, including command headers.
        /// Thus, verified after delta-compression.
        /// </summary>
        public const int k_MaxCommandSerializedPayloadBytes = 1024;

        /// <summary>
        /// The maximum number of commands you can send from the client to the server - for a given ghost - in a single packet.
        /// <see cref="k_MaxInputBufferSendBits"/>. 2^5 = (0,31), but we can exclude zero, so it's (1,32).
        /// </summary>
        /// <remarks>The number of commands that will be sent is `<see cref="ClientTickRate.TargetCommandSlack"/> + <see cref="ClientTickRate.NumAdditionalCommandsToSend"/>`.</remarks>
        public const int k_MaxInputBufferSendSize = 1 << k_MaxInputBufferSendBits;

        /// <summary>
        /// How many bits are allocated to sending the length of the buffer?
        /// <see cref="k_MaxInputBufferSendBits"/>
        /// </summary>
        internal const int k_MaxInputBufferSendBits = 5;

        /// <summary>
        /// How many bits are used for sending the tick delta, for each previous tick in the buffer?
        /// Note: The highest value is a sentinel value, reserved for 'use Huffman'.
        /// </summary>
        internal const int k_TickDeltaBits = 2;

        private NetworkTick m_LastInputTargetTick;

        protected override void OnCreate()
        {
            base.OnCreate();
            if (World.IsHost())
                Enabled = false;
        }
        protected override void OnUpdate()
        {
            var clientNetTime = SystemAPI.GetSingleton<NetworkTime>();
            var inputTargetTick = clientNetTime.InputTargetTick;
            // Make sure we only send a single ack per tick - only triggers when using dynamic timestep
            if (inputTargetTick.IsValid && inputTargetTick != m_LastInputTargetTick)
                base.OnUpdate();
            m_LastInputTargetTick = inputTargetTick;
        }
    }

    /// <summary>
    /// <para>System responsible for building and sending the command packet to the server.
    /// As part of the command protocol:</para>
    /// <para>- Flushes all the serialized commands present in the <see cref="OutgoingCommandDataStreamBuffer"/>.</para>
    /// <para>- Acks the latest received snapshot to the server.</para>
    /// <para>- Sends the client local and remote time (used to calculate the Round Trip Time) back to the server.</para>
    /// <para>- Sends the loaded ghost prefabs to the server.</para>
    /// <para>- Calculates the current client interpolation delay (used for lag compensation).</para>
    /// </summary>
    [UpdateInGroup(typeof(CommandSendSystemGroup), OrderLast = true)]
    [BurstCompile]
    internal partial struct CommandSendPacketSystem : ISystem
    {
        private StreamCompressionModel m_CompressionModel;
        private EntityQuery m_connectionQuery;
        //The packet header is composed by //tatal 29 bytes
        private const int k_CommandHeadersBytes =
            1 + // the protocol id
            4 + //last received snapshot tick from server
            4 + //received snapshost mask
            4 + //the local time (used for RTT calc)
            4 + //the delta in between the local time and the last received remote time. Used to calculate the elapsed RTT and remove the time spent on client to resend the ack.
            4 + //the interpolation delay
            2 + //the loaded prefabs
            1 +  //byte denoting if the command tick is for a full or partial tick,
            4; //the first command tick

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkStreamConnection, NetworkStreamInGame, NetworkSnapshotAck>()
                .WithAllRW<OutgoingCommandDataStreamBuffer>();
            m_connectionQuery = state.GetEntityQuery(builder);
            m_CompressionModel = StreamCompressionModel.Default;

            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate(m_connectionQuery);
        }

        [BurstCompile]
        [WithAll(typeof(NetworkStreamInGame))]
        partial struct CommandSendPacket : IJobEntity
        {
            public ConcurrentDriverStore concurrentDriverStore;
            public NetDebug netDebug;
#if UNITY_EDITOR || NETCODE_DEBUG
            public NativeArray<uint> netStats;
#endif
            public uint localTime;
            public int numLoadedPrefabs;
            public NetworkTick inputTargetTick;
            public float inputTargetTickFraction;
            public uint interpolationDelay;
            public unsafe void Execute(DynamicBuffer<OutgoingCommandDataStreamBuffer> rpcData,
                    in NetworkStreamConnection connection, in NetworkSnapshotAck ack)
            {
                if (!connection.Value.IsCreated)
                    return;

                var concurrentDriver = concurrentDriverStore.GetConcurrentDriver(connection.DriverId);
                var requiredPayloadSize = k_CommandHeadersBytes + rpcData.Length;
                int maxSnapshotSizeWithoutFragmentation = concurrentDriver.driver.m_DriverSender.m_SendQueue.PayloadCapacity - concurrentDriver.driver.MaxHeaderSize(concurrentDriver.unreliablePipeline);
                var pipelineToUse = requiredPayloadSize > maxSnapshotSizeWithoutFragmentation ? concurrentDriver.unreliableFragmentedPipeline : concurrentDriver.unreliablePipeline;
                int result;
                if ((result = concurrentDriver.driver.BeginSend(pipelineToUse, connection.Value, out var writer, requiredPayloadSize)) < 0)
                {
                    netDebug.LogWarning($"CommandSendPacket BeginSend failed with errorCode: {result} on {connection.Value.ToFixedString()}!");
                    rpcData.Clear();
                    return;
                }
                //If you modify any of the following writes (add/remote/type) you shoul update the
                //k_commandHeadersBytes constant.
                writer.WriteByte((byte)NetworkStreamProtocol.Command);
                writer.WriteUInt(ack.LastReceivedSnapshotByLocal.SerializedData);
                writer.WriteUInt(ack.ReceivedSnapshotByLocalMask);
                writer.WriteUInt(localTime);
                uint returnTime = ack.CalculateReturnTime(localTime);
                writer.WriteUInt(returnTime);
                writer.WriteUInt(interpolationDelay);
                writer.WriteUShort((ushort)numLoadedPrefabs);
                writer.WriteByte((byte)(inputTargetTickFraction < 1f ? 0 : 1));
                writer.WriteUInt(inputTargetTick.SerializedData);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Assertions.Assert.AreEqual(writer.Length, k_CommandHeadersBytes);
#endif
                writer.WriteBytesUnsafe((byte*)rpcData.GetUnsafeReadOnlyPtr(), rpcData.Length);
                rpcData.Clear();

#if UNITY_EDITOR || NETCODE_DEBUG
                netStats[0] = inputTargetTick.SerializedData;
                netStats[1] = (uint)writer.Length;
#endif

                if(writer.HasFailedWrites)
                    netDebug.LogError($"CommandSendPacket job triggered Writer.HasFailedWrites on {connection.Value.ToFixedString()}, despite allocating the collection based on needed size!");
                if ((result = concurrentDriver.driver.EndSend(writer)) <= 0)
                    netDebug.LogError($"CommandSendPacket EndSend failed with errorCode: {result} on {connection.Value.ToFixedString()}!");
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var clientNetTime = SystemAPI.GetSingleton<NetworkTime>();
            var inputTargetTick = clientNetTime.InputTargetTick;
            // The time left util interpolation is at the given tick, the delta should be increased by this
            var subTickDeltaAdjust = 1 - clientNetTime.InterpolationTickFraction;
            // The time left util we are actually at the server tick, the delta should be reduced by this
            subTickDeltaAdjust -= 1 - clientNetTime.ServerTickFraction;
            var interpolationDelay = clientNetTime.ServerTick.TicksSince(clientNetTime.InterpolationTick);
            if (subTickDeltaAdjust >= 1)
                ++interpolationDelay;
            else if (subTickDeltaAdjust < 0)
                --interpolationDelay;
            interpolationDelay = math.max(interpolationDelay, 0);

            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            var sendJob = new CommandSendPacket
            {
                concurrentDriverStore = networkStreamDriver.ConcurrentDriverStore,
                netDebug = SystemAPI.GetSingleton<NetDebug>(),
#if UNITY_EDITOR || NETCODE_DEBUG
                netStats = SystemAPI.GetSingletonRW<GhostStatsCollectionCommand>().ValueRO.Value,
#endif
                localTime = NetworkTimeSystem.TimestampMS,
                numLoadedPrefabs = SystemAPI.GetSingleton<GhostCollection>().NumLoadedPrefabs,
                inputTargetTick = inputTargetTick,
                inputTargetTickFraction = clientNetTime.ServerTickFraction,
                interpolationDelay = (uint)interpolationDelay
            };
            state.Dependency = sendJob.Schedule(state.Dependency);
            state.Dependency = networkStreamDriver.DriverStore.ScheduleFlushSendAllDrivers(state.Dependency);
        }
    }

    /// <summary>
    /// Helper struct for implementing systems to send commands.
    /// This is generally used by code-gen and should only be used directly in special cases.
    /// </summary>
    /// <typeparam name="TCommandDataSerializer">Unmanaged CommandDataSerializer of type ICommandDataSerializer.</typeparam>
    /// <typeparam name="TCommandData">Unmanaged CommandData of type ICommandData.</typeparam>
    public struct CommandSendSystem<TCommandDataSerializer, TCommandData>
        where TCommandData : unmanaged, ICommandData
        where TCommandDataSerializer : unmanaged, ICommandDataSerializer<TCommandData>
    {
        /// <summary>
        /// Helper struct used by code-generated command job to serialize the <see cref="ICommandData"/> into the
        /// <see cref="OutgoingCommandDataStreamBuffer"/> for the client connection.
        /// </summary>
        public struct SendJobData
        {
            /// <summary>
            /// The readonly <see cref="CommandTarget"/> type handle for accessing the chunk data.
            /// </summary>
            [ReadOnly] public ComponentTypeHandle<CommandTarget> commmandTargetType;
            /// <summary>
            /// <see cref="OutgoingCommandDataStreamBuffer"/> buffer type handle for accessing the chunk data.
            /// This is the output of buffer for the job
            /// </summary>
            public BufferTypeHandle<OutgoingCommandDataStreamBuffer> outgoingCommandBufferType;
            /// <summary>
            /// <see cref="EnablePacketLogging"/> type handle, for packet dumping.
            /// </summary>
            public ComponentTypeHandle<EnablePacketLogging> enablePacketLoggingType;
            /// <summary>
            /// Accessor for retrieving the input buffer from the target entity.
            /// </summary>
            [ReadOnly] public BufferLookup<TCommandData> inputFromEntity;
            /// <summary>
            /// Reaonly <see cref="GhostInstance"/> type handle for accessing the chunk data.
            /// </summary>
            [ReadOnly] public ComponentLookup<GhostInstance> ghostFromEntity;
            /// <summary>
            /// Readonly accessor to retrieve the <see cref="GhostOwner"/> from the target ghost entity.
            /// </summary>
            [ReadOnly] public ComponentLookup<GhostOwner> ghostOwnerFromEntity;
            /// <summary>
            /// Readonly accessor to retrieve the <see cref="AutoCommandTarget"/> from the target ghost entity.
            /// </summary>
            [ReadOnly] public ComponentLookup<AutoCommandTarget> autoCommandTargetFromEntity;
            /// <summary>
            /// The compression model used to delta encode the old inputs. The first input (the one for the current tick)
            /// is serialized as it is. The older ones, are serialized as delta in respect the first one to reduce the bandwidth.
            /// </summary>
            public StreamCompressionModel compressionModel;
            /// <summary>
            /// The server tick the command should be executed on the server.
            /// </summary>
            public NetworkTick inputTargetTick;
            /// <summary>
            /// The last server tick for which we send this command
            /// </summary>
            public NetworkTick prevInputTargetTick;
            /// <summary>
            /// The list of all ghost entities with a <see cref="AutoCommandTarget"/> component.
            /// </summary>
            [ReadOnly] public NativeList<Entity> autoCommandTargetEntities;
            /// <summary>
            /// The stable type hash for the command type. Serialized and used on the server side to match and verify the correctness
            /// of the input data sent.
            /// </summary>
            public ulong stableHash;
            /// <summary>
            /// For how many ticks should we send input?
            /// Value corresponds to the last n ticks, starting at the current tick.
            /// </summary>
            public uint numCommandsToSend;

            void Serialize(DynamicBuffer<OutgoingCommandDataStreamBuffer> rpcData, Entity targetEntity, bool isAutoTarget, ref EnablePacketLogging enablePacketLogging)
            {
                var inputBuffer = inputFromEntity[targetEntity];
                // Check if the buffer has any data for the ticks we are trying to send, first chck if it has data at all
                if (!inputBuffer.GetDataAtTick(inputTargetTick, out var baselineInputData))
                {
#if NETCODE_DEBUG
                    if (enablePacketLogging.IsPacketCacheCreated)
                        enablePacketLogging.LogToPacket($"\n[CSS][{default(TCommandDataSerializer).ToFixedString()}:{stableHash}] No data for {targetEntity.ToFixedString()} on inputTargetTick: {inputTargetTick.ToFixedString()}, ignoring.\n");
#endif
                    return;
                }
                // Next check if we have previously sent the latest input, and the latest data we have would not fit in the buffer
                // The check for previously sent is important to handle really bad client performance
                if (prevInputTargetTick.IsValid && !baselineInputData.Tick.IsNewerThan(prevInputTargetTick) && inputTargetTick.TicksSince(baselineInputData.Tick) >= CommandDataUtility.k_CommandDataMaxSize)
                {
#if NETCODE_DEBUG
                    if (enablePacketLogging.IsPacketCacheCreated)
                        enablePacketLogging.LogToPacket($"\n[CSS][{default(TCommandDataSerializer).ToFixedString()}:{stableHash}] Already sent input for {targetEntity.ToFixedString()} on inputTargetTick: {baselineInputData.Tick}, ignoring.\n");
#endif
                    return;
                }

                var oldLen = rpcData.Length;
                const int maxHeaderSize = sizeof(ulong) + //command hash
                                       sizeof(ushort) + //serialised size
                                       sizeof(int) + //ghost id | 0
                                       sizeof(uint) + //spawnTick | 0
                                       sizeof(byte) + // numCommandsToSend (5 bits technically!)
                                       sizeof(int); // Current Tick

                rpcData.ResizeUninitialized(oldLen + CommandSendSystemGroup.k_MaxCommandSerializedPayloadBytes + maxHeaderSize);
                var writer = new DataStreamWriter(rpcData.Reinterpret<byte>().AsNativeArray().GetSubArray(oldLen,
                    CommandSendSystemGroup.k_MaxCommandSerializedPayloadBytes));

                writer.WriteULong(stableHash);
                var lengthWriter = writer;
                writer.WriteUShort(0);
                var startLength = writer.Length;
                GhostInstance ghostComponent;
                if (isAutoTarget)
                {
                    ghostComponent = ghostFromEntity[targetEntity];
                    writer.WriteInt(ghostComponent.ghostId);
                    writer.WriteUInt(ghostComponent.spawnTick.SerializedData);
                }
                else
                {
                    ghostComponent = default;
                    writer.WriteInt(0);
                    writer.WriteUInt(0);
                }

                // Num Commands To Send:
                writer.WriteRawBits(numCommandsToSend - 1, CommandSendSystemGroup.k_MaxInputBufferSendBits);

                // Write the first input:
                var serializer = default(TCommandDataSerializer);
                var serializerState = new RpcSerializerState
                {
                    GhostFromEntity = ghostFromEntity,
                    CompressionModel = compressionModel,
                };
                writer.WriteUInt(baselineInputData.Tick.SerializedData);

#if NETCODE_DEBUG
                var firstSerializeLengthInBits = writer.LengthInBits;
#endif
                serializer.Serialize(ref writer, serializerState, baselineInputData, default, compressionModel);
#if NETCODE_DEBUG
                firstSerializeLengthInBits = writer.LengthInBits - firstSerializeLengthInBits;
#endif

                // Target tick is the most recent tick which is older than the one we just sampled
                var targetTick = baselineInputData.Tick;
                if (targetTick.IsValid)
                {
                    targetTick.Decrement();
                }

                // Write the next n, delta-compressed:
                TCommandData inputData = baselineInputData;


#if NETCODE_DEBUG
                var payloadBits = firstSerializeLengthInBits;
                var payloadTickBits = 32;

                if (enablePacketLogging.IsPacketCacheCreated)
                {
                    enablePacketLogging.LogToPacket($"[CSS][{serializer.ToFixedString()}:{stableHash}] Sent for inputTargetTick: {inputTargetTick.ToFixedString()} | {targetEntity.ToFixedString()} on {ghostComponent.ToFixedString()} | isAutoTarget:{isAutoTarget}\n\t| stableHash: {CommandDataUtility.FormatBitsBytes(64)}\n\t| commandSize: {CommandDataUtility.FormatBitsBytes(16)}\n\t| autoCommandTargetGhost: {CommandDataUtility.FormatBitsBytes(64)}\n\t| numCommandsToSend({numCommandsToSend}): {CommandDataUtility.FormatBitsBytes(CommandSendSystemGroup.k_MaxInputBufferSendBits)}");
                    enablePacketLogging.LogToPacket($"\t[b]=[{baselineInputData.Tick.ToFixedString()}|{baselineInputData.ToFixedString()}] (tick: {CommandDataUtility.FormatBitsBytes(32)}) (data: {CommandDataUtility.FormatBitsBytes(firstSerializeLengthInBits)})");
                }
#endif
                var assumedTickIndex = baselineInputData.Tick;
                for (uint inputIndex = 1; inputIndex < numCommandsToSend; ++inputIndex)
                {
                    var prevInputData = inputData;
                    var changeBit = GetDataAtTickAndCmp(targetTick, ref prevInputData, inputBuffer, ref inputData, serializer);
#if NETCODE_DEBUG
                    var tickBits = writer.LengthInBits;
#endif
                    WriteTickDeltaCompressed(ref assumedTickIndex, ref writer, inputData);
#if NETCODE_DEBUG
                    FixedString512Bytes debug = default;
                    if (enablePacketLogging.IsPacketCacheCreated)
                    {
                        tickBits = writer.LengthInBits - tickBits;
                        payloadTickBits += tickBits;
                        debug.Append((FixedString512Bytes) $"\t[{inputIndex}]=[{inputData.Tick.ToFixedString()}|{inputData.ToFixedString()}] (cb: {changeBit}) (t?: {CommandDataUtility.FormatBitsBytes(tickBits)})");
                    }
#endif

                    // If unchanged, skip Serializing entirely, using a 1bit change-mask flag:
                    writer.WriteRawBits(changeBit, 1);

                    if (changeBit != 0)
                    {
#if NETCODE_DEBUG
                        var successiveSerializeLengthInBits = writer.LengthInBits;
#endif

                        serializer.Serialize(ref writer, serializerState, inputData, baselineInputData, compressionModel);
#if NETCODE_DEBUG
                        if (enablePacketLogging.IsPacketCacheCreated)
                        {
                            var dataBits = writer.LengthInBits - successiveSerializeLengthInBits;
                            payloadBits += dataBits;
                            debug.Append((FixedString512Bytes) $" (data: {CommandDataUtility.FormatBitsBytes(dataBits)})");
                        }
#endif
                    }

#if NETCODE_DEBUG
                    if (enablePacketLogging.IsPacketCacheCreated)
                    {
                        if (writer.HasFailedWrites)
                            debug.Append((FixedString32Bytes) "\nHasFailedWrites!");
                        enablePacketLogging.LogToPacket(debug);
                    }
#endif
                    targetTick = inputData.Tick;
                    if (targetTick.IsValid)
                    {
                        targetTick.Decrement();
                    }
                }

                var flush = writer.LengthInBits;
                writer.Flush();
                flush = writer.LengthInBits - flush;

                if (writer.HasFailedWrites)
                {
                    //TODO further improvement
                    //Ideally here we want to print the original TCommandData type. However, for IInputCommands this is pretty much impossible at this point (unless we percolate down the original component type)
                    //since the type information is lost.
                    UnityEngine.Debug.LogError($"CommandSendSystem failed to serialize '{ComponentType.ReadWrite<TCommandData>().ToFixedString()}' as the serialized payload is too large (limit: {CommandSendSystemGroup.k_MaxCommandSerializedPayloadBytes})! For redundancy, we pack the command for the current server tick and the last {numCommandsToSend} (configurable) values (delta-compressed) inside the payload. Please try to keep ICommandData or IInputComponentData small (tens of bytes). Remember they are serialized at the `SimulationTickRate` and can consume a lot of the client outgoing and server ingress bandwidth.\nContents:'{inputData.ToFixedString()}'.");
                }

#if NETCODE_DEBUG
                var totalCommandBits = writer.LengthInBits; // Writer is invalidated after this.
#endif
                var totalCommandBytes = (ushort)(writer.Length - startLength);
                lengthWriter.WriteUShort(totalCommandBytes);
                rpcData.ResizeUninitialized(oldLen + writer.Length);

#if NETCODE_DEBUG
                if (enablePacketLogging.IsPacketCacheCreated)
                    enablePacketLogging.LogToPacket($"\t| payloadTicks: {CommandDataUtility.FormatBitsBytes(payloadTickBits)}\n\t| payload: {CommandDataUtility.FormatBitsBytes(payloadBits)}\n\t| changeBits: {CommandDataUtility.FormatBitsBytes((int) (numCommandsToSend-1))}\n\t| flush: {CommandDataUtility.FormatBitsBytes(flush)}\n\t---\n\t{CommandDataUtility.FormatBitsBytes(totalCommandBits)}\n");
#endif
            }

            /// <summary>
            /// First we assume the previous tick is -1 from the current input's tick.
            /// We send 2 bits in the common case (a delta of -1, -2, or -3, or 0, 1, or 2 after the -1).
            /// We then fallback on huffman if -4 (i.e. -3) or worse.
            /// </summary>
            /// <param name="assumedTickIndex"></param>
            /// <param name="writer"></param>
            /// <param name="inputData"></param>
            private void WriteTickDeltaCompressed(ref NetworkTick assumedTickIndex, ref DataStreamWriter writer, in TCommandData inputData)
            {
                const int outOfRange = 3;
                if (Hint.Likely(assumedTickIndex.IsValid && inputData.Tick.IsValid))
                {
                    // The common case is a delta of either 1, 2, or 3 ticks.
                    // Thus, we allocate 0, 1, and 2 to this delta, then only fallback to Huffman if we need to.
                    var deltaTicks = assumedTickIndex.TicksSince(inputData.Tick);
                    if (Hint.Likely(deltaTicks >= 1 && deltaTicks < 3))
                    {
                        writer.WriteRawBits((uint) deltaTicks - 1, CommandSendSystemGroup.k_TickDeltaBits);
                    }
                    else
                    {
                        deltaTicks = outOfRange;
                        writer.WriteRawBits((uint) deltaTicks, CommandSendSystemGroup.k_TickDeltaBits);
                        // Subtract 4 from the PREVIOUS VALUE because it can't be -1, -2, or -3.
                        if(assumedTickIndex.IsValid) assumedTickIndex.Subtract(4);
                        writer.WritePackedUIntDelta(inputData.Tick.SerializedData, assumedTickIndex.SerializedData, compressionModel);
                    }
                }
                else
                {
                    writer.WriteRawBits(outOfRange, CommandSendSystemGroup.k_TickDeltaBits);
                    writer.WritePackedUIntDelta(inputData.Tick.SerializedData, assumedTickIndex.SerializedData, compressionModel);
                }

                assumedTickIndex = inputData.Tick;
            }

            /// <summary>
            /// Returns 1 if <see cref="prevInputData"/> is the same as the input at <see cref="targetTick"/>.
            /// Returns 0 if no data or invalid tick.
            /// </summary>
            private static uint GetDataAtTickAndCmp(NetworkTick targetTick, ref TCommandData prevInputData,
                DynamicBuffer<TCommandData> input, ref TCommandData inputData, TCommandDataSerializer serializer)
            {
                if (!targetTick.IsValid)
                    return 0;
                return input.GetDataAtTick(targetTick, out inputData)
                    ? serializer.CalculateChangeMask(in inputData, in prevInputData)
                    : 0u;
            }

            /// <summary>
            /// <para>Lookup all the ghost entities for which commands need to be serialized for the current
            /// tick and enqueue them into the <see cref="OutgoingCommandDataStreamBuffer"/>.
            /// Are considered as potential ghost targets:</para>
            /// <para>- the entity referenced by the <see cref="CommandTarget"/></para>
            /// <para>- All ghosts owned by the player (see <see cref="GhostOwner"/>) that present
            /// an enabled <see cref="AutoCommandTarget"/> components.</para>
            /// </summary>
            /// <param name="chunk">The chunk that contains the connection entities</param>
            /// <param name="orderIndex">unsed, the sorting index enequeing operation in the the entity command buffer</param>
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                var commandTargets = chunk.GetNativeArray(ref commmandTargetType);
                var rpcDatas = chunk.GetBufferAccessor(ref outgoingCommandBufferType);
#if NETCODE_DEBUG
                var enablePacketLoggings = chunk.Has(ref enablePacketLoggingType) ? chunk.GetNativeArray(ref enablePacketLoggingType) : default;
#endif

                for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                {
                    var targetEntity = commandTargets[i].targetEntity;
#if NETCODE_DEBUG
                    var enablePacketLogging = enablePacketLoggings.IsCreated ? enablePacketLoggings[i] : default;
#else
                    var enablePacketLogging = default(EnablePacketLogging);
#endif

                    bool sentTarget = false;
                    for (int ent = 0; ent < autoCommandTargetEntities.Length; ++ent)
                    {
                        var autoTarget = autoCommandTargetEntities[ent];
                        if (autoCommandTargetFromEntity[autoTarget].Enabled &&
                            inputFromEntity.HasBuffer(autoTarget))
                        {
                            Serialize(rpcDatas[i], autoTarget, true, ref enablePacketLogging);
                            sentTarget |= (autoTarget == targetEntity);
                        }
                    }
                    if (!sentTarget && inputFromEntity.HasBuffer(targetEntity))
                        Serialize(rpcDatas[i], targetEntity, false, ref enablePacketLogging);
                }
            }
        }

        /// <summary>
        /// The query to use when scheduling the processing job.
        /// </summary>
        public EntityQuery Query => m_connectionQuery;
        private EntityQuery m_connectionQuery;
        private EntityQuery m_autoTargetQuery;
        private EntityQuery m_networkTimeQuery;
        private EntityQuery m_clientTickRateQuery;
        private StreamCompressionModel m_CompressionModel;
        private NetworkTick m_PrevInputTargetTick;

        private ComponentTypeHandle<CommandTarget> m_CommandTargetComponentHandle;
        private ComponentTypeHandle<EnablePacketLogging> m_EnablePacketLoggingTypeComponentHandle;
        private BufferTypeHandle<OutgoingCommandDataStreamBuffer> m_OutgoingCommandDataStreamBufferComponentHandle;
        private BufferLookup<TCommandData> m_TCommandDataFromEntity;
        private ComponentLookup<GhostInstance> m_GhostComponentFromEntity;
        private ComponentLookup<GhostOwner> m_GhostOwnerLookup;
        private ComponentLookup<AutoCommandTarget> m_AutoCommandTargetFromEntity;
        /// <summary>
        /// Initialize the helper struct, should be called from OnCreate in an ISystem.
        /// </summary>
        /// <param name="state"><see cref="SystemState"/></param>
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkStreamInGame, CommandTarget>();
            m_connectionQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<GhostInstance, GhostOwner, GhostOwnerIsLocal, TCommandData, AutoCommandTarget>();
            m_autoTargetQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetworkTime>();
            m_networkTimeQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<ClientTickRate>();
            m_clientTickRateQuery = state.GetEntityQuery(builder);

            m_CompressionModel = StreamCompressionModel.Default;
            m_CommandTargetComponentHandle = state.GetComponentTypeHandle<CommandTarget>(true);
            m_EnablePacketLoggingTypeComponentHandle = state.GetComponentTypeHandle<EnablePacketLogging>(false);
            m_OutgoingCommandDataStreamBufferComponentHandle = state.GetBufferTypeHandle<OutgoingCommandDataStreamBuffer>();
            m_TCommandDataFromEntity = state.GetBufferLookup<TCommandData>(true);
            m_GhostComponentFromEntity = state.GetComponentLookup<GhostInstance>(true);
            m_GhostOwnerLookup = state.GetComponentLookup<GhostOwner>(true);
            m_AutoCommandTargetFromEntity = state.GetComponentLookup<AutoCommandTarget>(true);

            state.RequireForUpdate(m_connectionQuery);
            state.RequireForUpdate(m_networkTimeQuery);
            state.RequireForUpdate<GhostCollection>();
        }

        /// <summary>
        /// Initialize the internal state of a processing job, should be called from OnUpdate of an ISystem.
        /// </summary>
        /// <param name="state">Raw entity system state.</param>
        /// <returns>Constructed <see cref="SendJobData"/> with initialized state.</returns>
        public SendJobData InitJobData(ref SystemState state)
        {
            m_CommandTargetComponentHandle.Update(ref state);
            m_EnablePacketLoggingTypeComponentHandle.Update(ref state);
            m_OutgoingCommandDataStreamBufferComponentHandle.Update(ref state);
            m_TCommandDataFromEntity.Update(ref state);
            m_GhostComponentFromEntity.Update(ref state);
            m_GhostOwnerLookup.Update(ref state);
            m_AutoCommandTargetFromEntity.Update(ref state);

            var clientNetTime = m_networkTimeQuery.GetSingleton<NetworkTime>();
            var inputTargetTick = clientNetTime.InputTargetTick;
            var targetEntities = m_autoTargetQuery.ToEntityListAsync(state.WorldUpdateAllocator, out var autoHandle);

            // NumAdditionalCommandsToSend is really important! Why?
            // TargetCommandSlack tries to ensure our inputs arrive on the server N ticks before they need to be consumed.
            // This is good, as it ensures we don't commonly "drop" inputs (i.e. inputs don't commonly arrive too late
            // to be processed by the DGS's server-authoritative simulation).
            // However, the client timeline can fall out of sync with the servers timeline by more
            // than 16.67ms (i.e. one tick at 60Hz).
            // Thus, if we didn't include an additional 1 tick in each packet, *and* we fall out of sync with the
            // server (a very common case), we are now losing entire input packets (even with no packet loss at all).
            if (!m_clientTickRateQuery.TryGetSingleton(out ClientTickRate clientTickRate))
                clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            var numCommandsToSend = Mathematics.math.clamp(clientTickRate.TargetCommandSlack + clientTickRate.NumAdditionalCommandsToSend, 1u, CommandSendSystemGroup.k_MaxInputBufferSendSize);

            var sendJob = new SendJobData
            {
                commmandTargetType = m_CommandTargetComponentHandle,
                enablePacketLoggingType = m_EnablePacketLoggingTypeComponentHandle,
                outgoingCommandBufferType = m_OutgoingCommandDataStreamBufferComponentHandle,
                inputFromEntity = m_TCommandDataFromEntity,
                ghostFromEntity = m_GhostComponentFromEntity,
                ghostOwnerFromEntity = m_GhostOwnerLookup,
                autoCommandTargetFromEntity = m_AutoCommandTargetFromEntity,
                compressionModel = m_CompressionModel,
                inputTargetTick = inputTargetTick,
                prevInputTargetTick = m_PrevInputTargetTick,
                autoCommandTargetEntities = targetEntities,
                stableHash = TypeManager.GetTypeInfo<TCommandData>().StableTypeHash,
                numCommandsToSend = numCommandsToSend,
            };
            m_PrevInputTargetTick = inputTargetTick;
            state.Dependency = JobHandle.CombineDependencies(state.Dependency, autoHandle);
            return sendJob;
        }

        /// <summary>
        /// Utility method to check if the processing job needs to run, used as an early-out in OnUpdate of an ISystem.
        /// </summary>
        /// <param name="state">Raw entity system state.</param>
        /// <returns>Whether the processing job needs to run.</returns>
        public bool ShouldRunCommandJob(ref SystemState state)
        {
            // If there are auto command target entities always run the job
            if (!m_autoTargetQuery.IsEmptyIgnoreFilter)
                return true;
            // Otherwise only run if CommandTarget exists and has this component type
            if (!m_connectionQuery.TryGetSingleton<CommandTarget>(out var commandTarget))
                return false;
            if (!state.EntityManager.HasComponent<TCommandData>(commandTarget.targetEntity))
                return false;

            return true;
        }
    }
}

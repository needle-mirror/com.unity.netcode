#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using Unity.Entities;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.NetCode
{
    /// <summary>
    /// Group that contains all systems that receives commands. Only present in server world.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation, WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateAfter(typeof(NetworkStreamReceiveSystem))]
    public partial class CommandReceiveSystemGroup : ComponentSystemGroup
    {
    }

    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(CommandReceiveSystemGroup), OrderLast = true)]
    [BurstCompile]
    internal partial struct CommandReceiveClearSystem : ISystem
    {
        EntityQuery m_NetworkTimeSingleton;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_NetworkTimeSingleton = state.GetEntityQuery(ComponentType.ReadOnly<NetworkTime>());
        }
        [BurstCompile]
        partial struct CommandReceiveClearJob : IJobEntity
        {
            public NetworkTick _currentTick;

            public void Execute(DynamicBuffer<IncomingCommandDataStreamBuffer> buffer, ref NetworkSnapshotAck snapshotAck)
            {
                buffer.Clear();
                if (snapshotAck.MostRecentFullCommandTick.IsValid)
                {
                    int age = _currentTick.TicksSince(snapshotAck.MostRecentFullCommandTick);
                    age *= 256;
                    snapshotAck.ServerCommandAge = (snapshotAck.ServerCommandAge * 7 + age) / 8;
                }
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = m_NetworkTimeSingleton.GetSingleton<NetworkTime>();
            var currentTick = networkTime.ServerTick;

            var commandReceiveClearJob = new CommandReceiveClearJob() { _currentTick = currentTick };
            commandReceiveClearJob.ScheduleParallel();
        }
    }

    /// <summary>
    /// Helper struct for implementing systems to receive commands.
    /// This is generally used by code-gen and should only be used directly in special cases.
    /// </summary>
    /// <typeparam name="TCommandDataSerializer">Unmanaged CommandDataSerializer of type ICommandDataSerializer.</typeparam>
    /// <typeparam name="TCommandData">Unmanaged CommandData of type ICommandData.</typeparam>
    public struct CommandReceiveSystem<TCommandDataSerializer, TCommandData>
        where TCommandData : unmanaged, ICommandData
        where TCommandDataSerializer : unmanaged, ICommandDataSerializer<TCommandData>
    {
        /// <summary>
        /// Helper struct used by code-gen for implementing the Execute method of the the generated receiving job.
        /// The ReceiveJobData implement the command deserialization logic, by reading from the data stream the
        /// serialized commands and enqueuing them into the taget entity command buffer.
        /// As part of the command deserialization, if a <see cref="CommandDataInterpolationDelay"/> component is present
        /// on target entity, it will be updated with the latest reported interpolation delay.
        /// </summary>
        public struct ReceiveJobData
        {
            /// <summary>
            /// The output command buffer where the deserialized command are added.
            /// </summary>
            public BufferLookup<TCommandData> commandData;
            /// <summary>
            /// Accessor for retrieving the optional <see cref="CommandDataInterpolationDelay"/> component from the target entity.
            /// </summary>
            public ComponentLookup<CommandDataInterpolationDelay> delayFromEntity;
            /// <summary>
            /// Accessor for retrieving the optional <see cref="GhostOwner"/> component,
            /// and used for lookup the entity target when using <see cref="AutoCommandTarget"/>.
            /// </summary>
            [ReadOnly] public ComponentLookup<GhostOwner> ghostOwnerFromEntity;
            /// <summary>
            /// Accessor for retrieving the optional <see cref="AutoCommandTarget"/> component.
            /// </summary>
            [ReadOnly] public ComponentLookup<AutoCommandTarget> autoCommandTargetFromEntity;
            /// <summary>
            /// The compression model used for decoding the delta compressed commands.
            /// </summary>
            public StreamCompressionModel compressionModel;
            /// <summary>
            /// Read-only type handle for reading the data from the <see cref="IncomingCommandDataStreamBuffer"/> buffer.
            /// </summary>
            [ReadOnly] public BufferTypeHandle<IncomingCommandDataStreamBuffer> cmdBufferType;
            /// <summary>
            /// Type handle for <see cref="EnablePacketLogging"/>, which allows us to dump command info to disk.
            /// </summary>
            public ComponentTypeHandle<EnablePacketLogging> enablePacketLoggingType;
            /// <summary>
            /// Type handle to get the <see cref="NetworkSnapshotAck"/> for the connection.
            /// </summary>
            public ComponentTypeHandle<NetworkSnapshotAck> snapshotAckType;
            /// <summary>
            /// Read-only type handle to get the <see cref="NetworkId"/> for the connection.
            /// </summary>
            [ReadOnly] public ComponentTypeHandle<NetworkId> networkIdType;
            /// <summary>
            /// Read-only type handle to get the <see cref="CommandTarget"/> for the connection.
            /// </summary>
            [ReadOnly] public ComponentTypeHandle<CommandTarget> commmandTargetType;
            /// <summary>
            /// A readonly mapping to retrieve a ghost entity instance from a <see cref="SpawnedGhost"/> identity.
            /// See <see cref="SpawnedGhostEntityMap"/> for more information.
            /// </summary>
            [ReadOnly] public NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly ghostMap;
            /// <summary>
            /// The current server tick
            /// </summary>
            public NetworkTick serverTick;
            /// <summary>
            /// The <see cref="NetDebug"/> singleton component instance.
            /// </summary>
            public NetDebug netDebug;
            /// <summary>
            /// The stable hash for the <see cref="ICommandData"/> type. Used to verify the commands are
            /// consistent.
            /// </summary>
            public ulong stableHash;

            /// <summary>
            /// Deserialize all commands present in the packet, and put all the inputs into the entity <see cref="ICommandData"/> buffer.
            /// </summary>
            /// <param name="reader"></param>
            /// <param name="targetEntity"></param>
            /// <param name="tick"></param>
            /// <param name="snapshotAck"></param>
            /// <param name="numCommandsSent"></param>
            /// <param name="reusableTempBuffer"></param>
            /// <param name="arrivalStats"></param>
            /// <param name="enablePacketLogging"></param>
            /// <param name="readerStartBit"></param>
            /// <param name="spawnedGhost"></param>
            internal void Deserialize(ref DataStreamReader reader, Entity targetEntity,
                uint tick, in NetworkSnapshotAck snapshotAck, uint numCommandsSent, Span<TCommandData> reusableTempBuffer,
                ref CommandArrivalStatistics arrivalStats, ref EnablePacketLogging enablePacketLogging,
                int readerStartBit, in SpawnedGhost spawnedGhost)
            {
                if (delayFromEntity.HasComponent(targetEntity))
                    delayFromEntity[targetEntity] = new CommandDataInterpolationDelay{ Delay = snapshotAck.RemoteInterpolationDelay };

                var deserializeState = new RpcDeserializerState
                {
                    ghostMap = ghostMap,
                    CompressionModel = compressionModel,
                };
                var command = commandData[targetEntity];
                var baselineReceivedCommand = default(TCommandData);
                var serializer = default(TCommandDataSerializer);

                // Deserialize the first, delta-compressed against zero (default).
                baselineReceivedCommand.Tick = new NetworkTick{SerializedData = reader.ReadUInt()};
                serializer.Deserialize(ref reader, deserializeState, ref baselineReceivedCommand, default, compressionModel);
                // Store received commands in the network command buffer
                reusableTempBuffer[0] = baselineReceivedCommand;

                var earlyByTicks = baselineReceivedCommand.Tick.TicksSince(serverTick);
                var isFirstLate = earlyByTicks < 0;
                if (isFirstLate) arrivalStats.NumArrivedTooLate++;
#if NETCODE_DEBUG
                if (enablePacketLogging.IsPacketCacheCreated)
                {
                    enablePacketLogging.LogToPacket($"[CRS][{serializer.ToFixedString()}:{stableHash}] Received command packet from {targetEntity.ToFixedString()} on GhostInst[type:??|id:{spawnedGhost.ghostId},st:{spawnedGhost.spawnTick.ToFixedString()}] targeting tick {baselineReceivedCommand.Tick.ToFixedString()}:\n\t| arrivalTick: {serverTick.ToFixedString()}\n\t| margin: {earlyByTicks}");
                    FixedString512Bytes baselineLog = $"\t[b]=[{baselineReceivedCommand.Tick.ToFixedString()}|{baselineReceivedCommand.ToFixedString()}]";
                    if (isFirstLate) baselineLog.Append((FixedString32Bytes) " Late!");
                    if (reader.HasFailedReads) baselineLog.Append((FixedString32Bytes) " HasFailedReads!");
                    enablePacketLogging.LogToPacket(baselineLog);
                }
#endif

                // Deserialize the next n:
                var assumedTickIndex = baselineReceivedCommand.Tick;
                for (uint inputIndex = 1; inputIndex < numCommandsSent; ++inputIndex)
                {
                    var receivedCommand = default(TCommandData);
                    receivedCommand.Tick = ReadTickDeltaCompressed(ref reader, ref assumedTickIndex);

                    // If this flag is false, input i-1 is equal to input i.
                    // Note that these are backwards, so i-1 is actually the input for the NEXT tick.
                    var changeBit = reader.ReadRawBits(1);
                    if (changeBit == 0)
                    {
                        // Invalid ticks technically always have a zero changeBit.
                        var copyOfNextInput = receivedCommand.Tick.IsValid
                            ? reusableTempBuffer[(int) (inputIndex - 1)]
                            : default;
                        copyOfNextInput.Tick = receivedCommand.Tick;
                        reusableTempBuffer[(int) inputIndex] = copyOfNextInput;
                    }
                    else
                    {
                        serializer.Deserialize(ref reader, deserializeState, ref receivedCommand, baselineReceivedCommand,
                            compressionModel);
                        reusableTempBuffer[(int) inputIndex] = receivedCommand;
                    }

                    // Determine if this input is too late:
                    // NOTE: This is missing the first input, which itself could be late, technically.
                    bool isLate = receivedCommand.Tick.IsValid && receivedCommand.Tick.TicksSince(serverTick) < 0;
                    if (isLate) arrivalStats.NumArrivedTooLate++;

#if NETCODE_DEBUG
                    if (enablePacketLogging.IsPacketCacheCreated)
                    {
                        FixedString512Bytes debug = $"\t[{inputIndex}]=[{reusableTempBuffer[(int) inputIndex].Tick.ToFixedString()}|{reusableTempBuffer[(int) inputIndex].ToFixedString()}] (cb:{changeBit})";
                        if (isLate) debug.Append((FixedString32Bytes) " Late!");
                        if (reader.HasFailedReads) debug.Append((FixedString32Bytes) " HasFailedReads!");
                        enablePacketLogging.LogToPacket(debug);
                    }
#endif
                }

                var totalBitsRead = reader.GetBitsRead() - readerStartBit;
                totalBitsRead = ((totalBitsRead + 7) / 8) * 8; // Flush to make identical with sender!
#if NETCODE_DEBUG
                if(enablePacketLogging.IsPacketCacheCreated)
                    enablePacketLogging.LogToPacket($"\t---\n\t{CommandDataUtility.FormatBitsBytes(totalBitsRead)}\n");
#endif

                // Add the command in the order they were produced, instead of the order they were sent:
                for (int i = (int) numCommandsSent - 1; i >= 0; --i)
                {
                    if (!reusableTempBuffer[i].Tick.IsValid)
                        continue;
                    var input = reusableTempBuffer[i];
                    // This is a special case, since this could be the latest tick we have for the current server tick
                    // it must be stored somehow. Trying to get the data for previous tick also needs to return
                    // what we actually used previous tick. So we fake the tick of the most recent input we got
                    // to point at the current server tick, even though it was actually for a tick we already
                    // simulated
                    // If it turns out there is another tick which is newer and should be used for serverTick
                    // that must be included in this packet and will overwrite the state for serverTick
                    if (serverTick.IsNewerThan(reusableTempBuffer[i].Tick))
                        input.Tick = serverTick;
                    var didReplaceExisting = command.AddCommandData(input);
                    if (didReplaceExisting) arrivalStats.NumRedundantResends++;
                }

                // Stats:
                {
                    arrivalStats.NumCommandPacketsArrived++;
                    arrivalStats.NumCommandsArrived += numCommandsSent;
                    arrivalStats.AvgCommandPayloadSizeInBits = arrivalStats.AvgCommandPayloadSizeInBits == 0
                        ? totalBitsRead
                        : math.lerp(arrivalStats.AvgCommandPayloadSizeInBits, totalBitsRead, 0.125f);
                }
            }

            /// <summary>
            /// Compare this against the writer method in CommandSendSystem!
            /// </summary>
            /// <param name="reader"></param>
            /// <param name="assumedTickIndex"></param>
            /// <returns></returns>
            private NetworkTick ReadTickDeltaCompressed(ref DataStreamReader reader, ref NetworkTick assumedTickIndex)
            {
                if (Hint.Likely(assumedTickIndex.IsValid))
                {
                    var delta = reader.ReadRawBits(CommandSendSystemGroup.k_TickDeltaBits) + 1;
                    if (Hint.Likely(delta <= 3))
                    {
                        assumedTickIndex.Subtract(delta);
                        return assumedTickIndex;
                    }
                    // Subtract 4 from the PREVIOUS VALUE because it can't be -1, -2, or -3.
                    if(assumedTickIndex.IsValid) assumedTickIndex.Subtract(4);
                    assumedTickIndex.SerializedData = reader.ReadPackedUIntDelta(assumedTickIndex.SerializedData, compressionModel);
                    return assumedTickIndex;
                }

                reader.ReadRawBits(CommandSendSystemGroup.k_TickDeltaBits);
                assumedTickIndex.SerializedData = reader.ReadPackedUIntDelta(assumedTickIndex.SerializedData, compressionModel);
                return assumedTickIndex;
            }

            /// <summary>
            /// Decode the commands present in the <see cref="IncomingCommandDataStreamBuffer"/> for all
            /// the connections present in the chunk and lookup for the target entity where the command should be
            /// enqueued by either using the <see cref="CommandTarget"/> target entity or via
            /// <see cref="AutoCommandTarget"/> if enabled.
            /// </summary>
            /// <param name="chunk">Chunk containing commands to decode</param>
            /// <param name="orderIndex">Order index</param>
            public unsafe void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                var snapshotAcks = chunk.GetNativeArray(ref snapshotAckType);
                var snapshotAcksWritePtr = (NetworkSnapshotAck*)snapshotAcks.GetUnsafePtr();
                var networkIds = chunk.GetNativeArray(ref networkIdType);
                var commandTargets = chunk.GetNativeArray(ref commmandTargetType);
                var cmdBuffers = chunk.GetBufferAccessor(ref cmdBufferType);
#if NETCODE_DEBUG
                var enablePacketLoggings = chunk.Has(ref enablePacketLoggingType)
                    ? chunk.GetNativeArray(ref enablePacketLoggingType)
                    : default(NativeArray<EnablePacketLogging>);
#else
                var enablePacketLoggings = default(NativeArray<EnablePacketLogging>);
#endif
                Span<TCommandData> reusableTempBuffer = stackalloc TCommandData[CommandSendSystemGroup.k_MaxInputBufferSendSize];

                for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                {
                    var owner = networkIds[i].Value;
                    ref var snapshotAck = ref snapshotAcksWritePtr[i];
                    var buffer = cmdBuffers[i];
                    if (buffer.Length < 4)
                        continue;

                    DataStreamReader reader = buffer.AsDataStreamReader();
                    var tick = reader.ReadUInt();
                    while (reader.GetBytesRead() + 10 <= reader.Length)
                    {
                        var readerStartBit = reader.GetBitsRead();

                        var hash = reader.ReadULong();
                        var commandPayloadLength = reader.ReadUShort();
                        var startPos = reader.GetBytesRead();
                        if (hash == stableHash)
                        {
                            // Read ghost id
                            var ghostId = reader.ReadInt();
                            var spawnTick = new NetworkTick {SerializedData = reader.ReadUInt()};
                            var spawnedGhost = new SpawnedGhost {ghostId = ghostId, spawnTick = spawnTick};

                            var numCommandsSent = reader.ReadRawBits(CommandSendSystemGroup.k_MaxInputBufferSendBits) + 1;

                            var targetEntity = commandTargets[i].targetEntity;
                            if (ghostId != 0)
                            {
                                targetEntity = Entity.Null;
                                if (ghostMap.TryGetValue(spawnedGhost, out var ghostEnt))
                                {
                                    if (ghostOwnerFromEntity.HasComponent(ghostEnt) && autoCommandTargetFromEntity.HasComponent(ghostEnt))
                                    {
                                        var ghostOwner = ghostOwnerFromEntity[ghostEnt].NetworkId;
                                        if (ghostOwner == owner)
                                        {
                                            if (autoCommandTargetFromEntity[ghostEnt].Enabled)
                                            {
                                                targetEntity = ghostEnt;
                                            }
                                            else LogToPacket(enablePacketLoggings, i, $"[CRS][{default(TCommandDataSerializer).ToFixedString()}] Client {owner} sent input for ghostId (id:{ghostId},spawnTick:{spawnTick.ToFixedString()}) but AutoCommandTarget is Disabled on Server.");
                                        }
                                        else LogToPacket(enablePacketLoggings, i, $"[CRS][{default(TCommandDataSerializer).ToFixedString()}] Client {owner} sent input for ghostId (id:{ghostId},spawnTick:{spawnTick.ToFixedString()}) which is owned by another player ({ghostOwner})!");
                                    }
                                    else LogToPacket(enablePacketLoggings, i, $"[CRS][{default(TCommandDataSerializer).ToFixedString()}] Client {owner} sent input for ghostId (id:{ghostId},spawnTick:{spawnTick.ToFixedString()}) which hasn't got the GhostOwner + AutoCommandTarget combination of components!");
                                }
                                else LogToPacket(enablePacketLoggings, i, $"[CRS][{default(TCommandDataSerializer).ToFixedString()}] Client {owner} sent input for ghostId (id:{ghostId},spawnTick:{spawnTick.ToFixedString()}) which does not exist on the server!");
                            }

                            if (commandData.HasBuffer(targetEntity))
                            {
#if NETCODE_DEBUG
                                var enablePacketLogging = enablePacketLoggings.IsCreated ? enablePacketLoggings[i] : default;
#else
                                var enablePacketLogging = default(EnablePacketLogging);
#endif
                                Deserialize(ref reader, targetEntity, tick, snapshotAck, numCommandsSent, reusableTempBuffer, ref snapshotAck.CommandArrivalStatistics, ref enablePacketLogging, readerStartBit, in spawnedGhost);

                                // Validate received BYTE count (don't do bits, as we don't send the exact bit count):
                                var actualBitsRead = reader.GetBytesRead() - startPos;
                                if (reader.HasFailedReads || actualBitsRead != commandPayloadLength)
                                {
                                    netDebug.LogError($"Failed to correctly deserialize command '{ComponentType.ReadWrite<TCommandData>().ToFixedString()}' on {targetEntity.ToFixedString()} from NID[{owner}]! Expected: {commandPayloadLength} bytes, actual {actualBitsRead} bytes, reader.HasFailedReads: {reader.HasFailedReads}!");
                                    // TODO - Check frequency of this error in production. Would we prefer to kick this player?
                                }
                            }
                        }

                        reader.SeekSet(startPos + commandPayloadLength);
                    }
                }
            }

            [Conditional("NETCODE_DEBUG")]
            // ReSharper disable UnusedParameter.Local
            private void LogToPacket(in NativeArray<EnablePacketLogging> enablePacketLoggings, int index, in FixedString512Bytes msg)
            {
                // ReSharper enable UnusedParameter.Local
#if NETCODE_DEBUG
                if (!enablePacketLoggings.IsCreated) return;
                var epl = enablePacketLoggings[index];
                if (!epl.IsPacketCacheCreated) return;
                epl.LogToPacket(msg);
#endif
            }
        }

        /// <summary>
        /// The query to use when scheduling the processing job.
        /// </summary>
        public EntityQuery Query => m_entityQuery;
        private EntityQuery m_entityQuery;
        private EntityQuery m_SpawnedGhostEntityMapQuery;
        private EntityQuery m_NetworkTimeQuery;
        private EntityQuery m_NetDebugQuery;
        private StreamCompressionModel m_CompressionModel;

        private BufferLookup<TCommandData> m_TCommandDataFromEntity;
        private ComponentLookup<CommandDataInterpolationDelay> m_CommandDataInterpolationDelayFromEntity;
        private ComponentLookup<GhostOwner> m_GhostOwnerLookup;
        private ComponentLookup<AutoCommandTarget> m_AutoCommandTargetFromEntity;
        private BufferTypeHandle<IncomingCommandDataStreamBuffer> m_IncomingCommandDataStreamBufferComponentHandle;
        private ComponentTypeHandle<NetworkSnapshotAck> m_NetworkSnapshotAckComponentHandle;
        private ComponentTypeHandle<EnablePacketLogging> m_EnablePacketLoggingTypeComponentHandle;
        private ComponentTypeHandle<NetworkId> m_NetworkIdComponentHandle;
        private ComponentTypeHandle<CommandTarget> m_CommandTargetComponentHandle;

        /// <summary>
        /// Invoked by code-gen from job system
        /// </summary>
        /// <param name="state"><see cref="SystemState"/></param>
        public void OnCreate(ref SystemState state)
        {
            m_CompressionModel = StreamCompressionModel.Default;
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkStreamInGame, IncomingCommandDataStreamBuffer, NetworkSnapshotAck>()
                .WithAllRW<CommandTarget>();
            m_entityQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<SpawnedGhostEntityMap>();
            m_SpawnedGhostEntityMapQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetworkTime>();
            m_NetworkTimeQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetDebug>();
            m_NetDebugQuery = state.GetEntityQuery(builder);

            m_TCommandDataFromEntity = state.GetBufferLookup<TCommandData>();
            m_CommandDataInterpolationDelayFromEntity = state.GetComponentLookup<CommandDataInterpolationDelay>();
            m_GhostOwnerLookup = state.GetComponentLookup<GhostOwner>(true);
            m_AutoCommandTargetFromEntity = state.GetComponentLookup<AutoCommandTarget>(true);
            m_IncomingCommandDataStreamBufferComponentHandle = state.GetBufferTypeHandle<IncomingCommandDataStreamBuffer>(true);
            m_NetworkSnapshotAckComponentHandle = state.GetComponentTypeHandle<NetworkSnapshotAck>(false);
            m_EnablePacketLoggingTypeComponentHandle = state.GetComponentTypeHandle<EnablePacketLogging>(false);
            m_NetworkIdComponentHandle = state.GetComponentTypeHandle<NetworkId>(true);
            m_CommandTargetComponentHandle = state.GetComponentTypeHandle<CommandTarget>(true);

            state.RequireForUpdate(m_entityQuery);
            state.RequireForUpdate<TCommandData>();
        }

        /// <summary>
        /// Initialize the internal state of a processing job, should be called from OnUpdate of an ISystem.
        /// </summary>
        /// <param name="state">Raw entity system state.</param>
        /// <returns>Constructed <see cref="ReceiveJobData"/> with initialized state.</returns>
        public ReceiveJobData InitJobData(ref SystemState state)
        {
            m_TCommandDataFromEntity.Update(ref state);
            m_CommandDataInterpolationDelayFromEntity.Update(ref state);
            m_GhostOwnerLookup.Update(ref state);
            m_AutoCommandTargetFromEntity.Update(ref state);
            m_IncomingCommandDataStreamBufferComponentHandle.Update(ref state);
            m_NetworkSnapshotAckComponentHandle.Update(ref state);
            m_EnablePacketLoggingTypeComponentHandle.Update(ref state);
            m_NetworkIdComponentHandle.Update(ref state);
            m_CommandTargetComponentHandle.Update(ref state);
            var recvJob = new ReceiveJobData
            {
                commandData = m_TCommandDataFromEntity,
                delayFromEntity = m_CommandDataInterpolationDelayFromEntity,
                ghostOwnerFromEntity = m_GhostOwnerLookup,
                autoCommandTargetFromEntity = m_AutoCommandTargetFromEntity,
                compressionModel = m_CompressionModel,
                cmdBufferType = m_IncomingCommandDataStreamBufferComponentHandle,
                snapshotAckType = m_NetworkSnapshotAckComponentHandle,
                enablePacketLoggingType = m_EnablePacketLoggingTypeComponentHandle,
                networkIdType = m_NetworkIdComponentHandle,
                commmandTargetType = m_CommandTargetComponentHandle,
                ghostMap = m_SpawnedGhostEntityMapQuery.GetSingleton<SpawnedGhostEntityMap>().Value,
                serverTick = m_NetworkTimeQuery.GetSingleton<NetworkTime>().ServerTick,
                netDebug = m_NetDebugQuery.GetSingleton<NetDebug>(),
                stableHash = TypeManager.GetTypeInfo<TCommandData>().StableTypeHash
            };
            return recvJob;
        }
    }
}

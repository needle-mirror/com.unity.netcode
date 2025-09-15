#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Burst.CompilerServices;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.NetCode
{
    internal enum SerializeEnitiesResult
    {
        Unknown = 0,
        Ok,
        /// <summary>Likely due to the fact that we filled the packet!</summary>
        Failed,
        /// <summary>Fatal exception occurred.</summary>
        Abort,
    }
    internal unsafe struct GhostChunkSerializer
    {
        public DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
        public DynamicBuffer<GhostCollectionPrefabSerializer> GhostTypeCollection;
        public DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;
        public ComponentTypeHandle<PreSpawnedGhostIndex> PrespawnIndexType;
        public EntityStorageInfoLookup childEntityLookup;
        public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
        public BufferTypeHandle<PrespawnGhostBaseline> prespawnBaselineTypeHandle;
        public EntityTypeHandle entityType;
        public ComponentTypeHandle<GhostInstance> ghostComponentType;
        public ComponentTypeHandle<GhostCleanup> ghostSystemStateType;
        public ComponentTypeHandle<PreSerializedGhost> preSerializedGhostType;
        public ComponentTypeHandle<GhostChildEntity> ghostChildEntityComponentType;
        public BufferTypeHandle<GhostGroup> ghostGroupType;
        public NetworkSnapshotAck snapshotAck;
        public UnsafeHashMap<ArchetypeChunk, GhostChunkSerializationState> chunkSerializationData;
        public DynamicComponentTypeHandle* ghostChunkComponentTypesPtr;
        public int ghostChunkComponentTypesLength;
        public NetworkTick currentTick;
        public int expectedSnapshotRttInSimTicks;
        public StreamCompressionModel compressionModel;
        public GhostSerializerState serializerState;
        public int NetworkId;
        public NativeParallelHashMap<RelevantGhostForConnection, int> relevantGhostForConnection;
        public GhostRelevancyMode relevancyMode;
        public EntityQueryMask userGlobalRelevantMask;
        public EntityQueryMask internalGlobalRelevantMask;
        public UnsafeList<PendingGhostDespawn>* pendingDespawns;
        public ConnectionStateData.GhostStateList ghostStateData;
        public uint CurrentSystemVersion;
        public NetDebug netDebug;
#if NETCODE_DEBUG
        public PacketDumpLogger netDebugPacket;
        public FixedString512Bytes netDebugPacketDebug;
        public FixedString128Bytes netDebugPacketResult;
        public FixedString64Bytes ghostTypeName;
        public NativeList<UnsafeGhostStatsSnapshot.PerComponentStats> componentStats;
#endif

        [ReadOnly] public NativeParallelHashMap<ArchetypeChunk, SnapshotPreSerializeData> SnapshotPreSerializeData;
        public GhostSendSystemData systemData;

        private NativeArray<byte> tempRelevancyPerEntity;
        private NativeList<SnapshotBaseline> tempAvailableBaselines;

        private byte** tempBaselinesPerEntity;
        private byte** tempComponentDataPerEntity;
        private int* tempComponentDataLenPerEntity;
        private int* tempDynamicDataLenPerEntity;
        private int* tempSameBaselinePerEntity;
        private DataStreamWriter tempWriter;
        private int* tempEntityStartBit;
        private byte* tempZeroBaseline;

        struct CurrentSnapshotState
        {
            public Entity* SnapshotEntity;
            public void* SnapshotData;
            //can be null in certain conditions:
            // GhostGroup (temporary)
            // Spawn chunks
            public byte* SnapshotDynamicData;
            public int SnapshotDynamicDataCapacity;
            // Total chunck dynamic buffers data to serialize.
            // currentDynamicDataCapacity and snapshotDynamicDataSize can be different (currentDynamicDataCapacity is usually larger).
            // Spawn chunks does not allocate a full history buffer and so currentDynamicDataCapacity equals 0 and a temporary
            // data buffer is created instead
            public int SnapshotDynamicDataSize;

            public NativeList<SnapshotBaseline> AvailableBaselines;
            public byte NumInFlightBaselines;
            public byte* relevancyData;
            public byte AlreadyUsedChunk;
        }
        unsafe struct SnapshotBaseline
        {
            public uint tick;
            public byte* snapshot;
            public Entity* entity;
            //dynamic buffer data storage associated with the snapshot
            public byte *dynamicData;
        }

        public void AllocateTempData(int maxGhostsPerChunk, int dataStreamCapacity)
        {
            tempAvailableBaselines =
                new NativeList<GhostChunkSerializer.SnapshotBaseline>(GhostSystemConstants.SnapshotHistorySize, Allocator.Temp);
            tempRelevancyPerEntity = new NativeArray<byte>(maxGhostsPerChunk, Allocator.Temp);

            int maxComponentCount = 0;
            int maxSnapshotSize = 0;
            for (int i = 0; i < GhostTypeCollection.Length; ++i)
            {
                ref readonly var ghostCollectionPrefabSerializer = ref GhostTypeCollection.ElementAtRO(i);
                maxComponentCount = math.max(maxComponentCount, ghostCollectionPrefabSerializer.NumComponents);
                maxSnapshotSize = math.max(maxSnapshotSize, GhostComponentSerializer.SnapshotSizeAligned(ghostCollectionPrefabSerializer.SnapshotSize));
            }

            tempBaselinesPerEntity = (byte**)UnsafeUtility.Malloc(maxGhostsPerChunk*4*UnsafeUtility.SizeOf<IntPtr>(), 16, Allocator.Temp);
            tempComponentDataPerEntity = (byte**)UnsafeUtility.Malloc(maxGhostsPerChunk*UnsafeUtility.SizeOf<IntPtr>(), 16, Allocator.Temp);
            tempComponentDataLenPerEntity = (int*)UnsafeUtility.Malloc(maxGhostsPerChunk*4, 16, Allocator.Temp);
            tempDynamicDataLenPerEntity = (int*)UnsafeUtility.Malloc(maxGhostsPerChunk*4, 16, Allocator.Temp);
            tempSameBaselinePerEntity = (int*)UnsafeUtility.Malloc(maxGhostsPerChunk*4, 16, Allocator.Temp);
            tempWriter = new DataStreamWriter(math.max(dataStreamCapacity, 1024), Allocator.Temp);
            tempEntityStartBit = (int*)UnsafeUtility.Malloc(8*maxGhostsPerChunk+8*maxGhostsPerChunk*maxComponentCount, 16, Allocator.Temp);
            tempZeroBaseline = (byte*)UnsafeUtility.Malloc(maxSnapshotSize, 16, Allocator.Temp);
            UnsafeUtility.MemSet(tempZeroBaseline, 0, maxSnapshotSize);
        }

        private void SetupDataAndAvailableBaselines(ref CurrentSnapshotState currentSnapshot, ref GhostChunkSerializationState chunkState, ArchetypeChunk chunk, int snapshotSize, int writeIndex, uint* snapshotIndex)
        {
            // Find the acked snapshot to delta against, setup pointer to current and previous entity* and data*
            // Remember to bump writeIndex when done
            currentSnapshot.SnapshotData = chunkState.GetData(snapshotSize, chunk.Capacity, writeIndex);
            currentSnapshot.SnapshotEntity = chunkState.GetEntity(snapshotSize, chunk.Capacity, writeIndex);
            currentSnapshot.SnapshotDynamicData = chunkState.GetDynamicDataPtr(writeIndex, chunk.Capacity, out currentSnapshot.SnapshotDynamicDataCapacity);
            //Resize the snapshot dynamic data storage to fit the chunk buffers contents.
            if (currentSnapshot.SnapshotDynamicData == null || (currentSnapshot.SnapshotDynamicDataSize > currentSnapshot.SnapshotDynamicDataCapacity))
            {
                chunkState.EnsureDynamicDataCapacity(currentSnapshot.SnapshotDynamicDataSize, chunk.Capacity);
                //Update the chunk state
                chunkSerializationData[chunk] = chunkState;
                currentSnapshot.SnapshotDynamicData = chunkState.GetDynamicDataPtr(writeIndex, chunk.Capacity, out currentSnapshot.SnapshotDynamicDataCapacity);
                if(currentSnapshot.SnapshotDynamicData == null)
                    throw new InvalidOperationException("failed to create history snapshot storage for dynamic data buffer");
            }

#if NETCODE_DEBUG
            NetworkTick ackedExceededMbr = default;
            byte numExceededMbr = 0;
            UnityEngine.Debug.Assert(currentSnapshot.AvailableBaselines.IsCreated && currentSnapshot.AvailableBaselines.IsEmpty);
#endif
            // Prevent invalid tick exception on `ackTick`.
            var ackTick = snapshotAck.LastReceivedSnapshotByRemote.IsValid ? snapshotAck.LastReceivedSnapshotByRemote : currentTick;

            currentSnapshot.NumInFlightBaselines = 0;
            int baseline = (GhostSystemConstants.SnapshotHistorySize + writeIndex - 1) %
                            GhostSystemConstants.SnapshotHistorySize;

            while (baseline != writeIndex)
            {
                var baselineTick = new NetworkTick {SerializedData = snapshotIndex[baseline]};
                if (baselineTick.IsValid)
                {
                    if (Hint.Unlikely(currentTick.TicksSince(baselineTick) >= GhostSystemConstants.MaxBaselineAge))
                    {
                        // No need to clear the ack mask here, as it's good enough to simply not
                        // add it to the list of available baselines. I.e. `CanUseStaticOptimization` can still pass.
#if NETCODE_DEBUG
                        if (chunkState.HasAckFlag(baseline))
                            ackedExceededMbr = baselineTick;
                        numExceededMbr++;
#endif
                    }
                    else
                    {
                        TryAck(ref chunkState, baseline, baselineTick);
                        if (chunkState.HasAckFlag(baseline))
                        {
                            currentSnapshot.AvailableBaselines.Add(new SnapshotBaseline
                            {
                                tick = snapshotIndex[baseline],
                                snapshot = chunkState.GetData(snapshotSize, chunk.Capacity, baseline),
                                entity = chunkState.GetEntity(snapshotSize, chunk.Capacity, baseline),
                                dynamicData = chunkState.GetDynamicDataPtr(baseline, chunk.Capacity, out var _),
                            });
                        }
                        else currentSnapshot.NumInFlightBaselines += (byte)math.select(0, 1, baselineTick.IsNewerThan(ackTick));
                    }
                }

                baseline = (GhostSystemConstants.SnapshotHistorySize + baseline - 1) %
                           GhostSystemConstants.SnapshotHistorySize;
            }

#if NETCODE_DEBUG
            if (Hint.Unlikely(numExceededMbr > 0))
                PacketDumpExceededMaxBaselineAge(in chunk, ackedExceededMbr, numExceededMbr, currentSnapshot.AvailableBaselines.Length);
#endif
        }

        /// <summary>Attempts to ack this chunk, assuming it hasn't yet been.</summary>
        /// <remarks>
        ///     Note: We also have to UNDO ACKS for chunks when the client reports an error
        ///     (by clearing their <see cref="NetworkSnapshotAck.IsReceivedByRemote" /> ack history).
        /// </remarks>
        private void TryAck(ref GhostChunkSerializationState chunkState, int baseline, in NetworkTick baselineTick)
        {
            var wasAcked = chunkState.HasAckFlag(baseline);
            var isNowAcked = snapshotAck.IsReceivedByRemote(baselineTick, backupValue: wasAcked);
            if (wasAcked != isNowAcked)
            {
                if (Hint.Likely(isNowAcked))
                {
                    chunkState.SetAckFlag(baseline);
                    PacketDumpAckedChunk(baselineTick);
                }
                else
                {
                    chunkState.ClearAckFlag(baseline);
                    PacketDumpClearedAckChunk(baselineTick);
                }
            }
        }

        private void FindBaselines(int entIdx, Entity ent, in CurrentSnapshotState currentSnapshot, ref int baseline0, ref int baseline1, ref int baseline2, bool useSingleBaseline)
        {
            int numAvailableBaselines = currentSnapshot.AvailableBaselines.Length;
            var availableBaselines = (SnapshotBaseline*)currentSnapshot.AvailableBaselines.GetUnsafeReadOnlyPtr();
            baseline0 = 0;
            while (baseline0 < numAvailableBaselines && availableBaselines[baseline0].entity[entIdx] != ent)
                ++baseline0;
            if (useSingleBaseline)
                return;
            baseline1 = baseline0+1;
            while (baseline1 < numAvailableBaselines && availableBaselines[baseline1].entity[entIdx] != ent)
                ++baseline1;
            baseline2 = baseline1+1;
            while (baseline2 < numAvailableBaselines && availableBaselines[baseline2].entity[entIdx] != ent)
                ++baseline2;
            if (baseline2 >= numAvailableBaselines)
            {
                baseline1 = numAvailableBaselines;
                baseline2 = numAvailableBaselines;
            }
        }

        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpStructuralChange(in PrioChunk ghostType)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString512Bytes)$", structural change detected (new count:{ghostType.chunk.Count})");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpExceededMaxBaselineAge(in ArchetypeChunk chunk, NetworkTick ackedExceededMbr, byte numExceededMbr, int availableBaselinesLength)
        {
#if NETCODE_DEBUG
            // Only WARN if it actually invalidates our last acked baseline.
            if (Hint.Likely(!ackedExceededMbr.IsValid || availableBaselinesLength != 0)) return;
            netDebug.LogWarning((FixedString512Bytes)$"[GCS] Ghost chunk {chunk.SequenceNumber} - sending to NID[{NetworkId}] - lost it's acked baseline:{ackedExceededMbr.ToFixedString()} as was older than MaxBaselineAge ticks from currentTick:{currentTick.ToFixedString()}!");
            if (Hint.Unlikely(netDebugPacket.IsCreated))
                netDebugPacketDebug.Append((FixedString64Bytes) $"\tWARN: B:{numExceededMbr}>MaxBaselineAge:{ackedExceededMbr.ToFixedString()}! ");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpGhostCount(int ghostType, int relevantGhostCount)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append(FixedString.Format(", RelevantGhostCount:{2}", ghostTypeName, ghostType, relevantGhostCount));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpBegin(ref ArchetypeChunk chunk, int start, int end)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                netDebugPacketDebug.Append((FixedString64Bytes)$"\n\t\t[SerializeEntities:{chunk.SequenceNumber}|{start}->{end}]");
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpBaseline(int entOffset, NetworkTick base0, NetworkTick base1, NetworkTick base2, int sameBaselineCount, bool useSingleBaseline, int numAvailableBaselines, int numPrespawnBaselines, byte numInFlightSnapshots)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                if (entOffset != 0) netDebugPacketDebug.Append((FixedString32Bytes)"\n\t\t");
                netDebugPacketDebug.Append((FixedString512Bytes)$" B0:{base0.ToFixedString()} B1:{base1.ToFixedString()} B2:{base2.ToFixedString()} SameBL:{sameBaselineCount}, UseSingleBL:{useSingleBaseline}, AvailBLs:{numAvailableBaselines}, PrespawnBLs:{numPrespawnBaselines}, inFl:{numInFlightSnapshots}/{GhostSystemConstants.SnapshotHistorySize}");
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpForceResendPrespawn(bool forceResendExisting)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated && forceResendExisting)
                netDebugPacketDebug.Append((FixedString32Bytes) " FRP!");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpMovedAckHistory(NetworkTick networkTick, bool didHaveAck, bool hasAck)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                var didHaveAckC = (didHaveAck ? '1' : '0');
                var hasAckIntC = (hasAck ? '1' : '0');
                netDebugPacketDebug.Append((FixedString32Bytes)$", MOVE:{networkTick.ToFixedString()}:{didHaveAckC}-{hasAckIntC}");
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpGhostID(int ghostId)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString64Bytes) $"\n\t\t\tGID:{ghostId}");
#endif
        }

        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpSpawnTick(NetworkTick spawnTick)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append(FixedString.Format(" SpawnTick:{0}", spawnTick.ToFixedString()));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpChangeMasks(uint* changeMaskUints, int numChangeMaskUints)
        {
#if NETCODE_DEBUG
            if (!netDebugPacket.IsCreated)
                return;
            for (int i = 0; i < numChangeMaskUints; ++i)
                netDebugPacketDebug.Append(FixedString.Format(" ChangeMask:{0}", NetDebug.PrintMask(changeMaskUints[i])));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpHasRelevancySpawns()
        {
#if NETCODE_DEBUG
            netDebugPacketDebug.Append((FixedString32Bytes)", HasRelevancySpawns");
#endif
        }
#if NETCODE_DEBUG
        private void PacketDumpComponentSize(in GhostCollectionPrefabSerializer typeData, int* entityStartBit, int bitCountsPerComponent, int entOffset, ref NativeList<UnsafeGhostStatsSnapshot.PerComponentStats> tempComponentStats)
        {
            var packetDumpEnabled = netDebugPacket.IsCreated;

            uint total = 0;

            for (int comp = 0; comp < typeData.NumComponents; ++comp)
            {
                uint numBits = (uint)entityStartBit[(bitCountsPerComponent*comp + entOffset)*2+1];
                tempComponentStats.ElementAt(comp).SizeInSnapshotInBits += numBits;
                if (packetDumpEnabled)
                {
                    if (netDebugPacketDebug.Length > (netDebugPacketDebug.Capacity >> 1))
                    {
                        netDebugPacketDebug.Append((FixedString32Bytes)" CONT");
                        PacketDumpFlush();
                    }

                    total += numBits;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    var ghostComponent = GhostComponentCollection[serializerIdx];
                    var typeName = netDebug.ComponentTypeNameLookup[ghostComponent.ComponentType.TypeIndex];
                    netDebugPacketDebug.Append(FixedString.Format(" {0}:{1} ({2}b)", typeName, ghostComponent.PredictionErrorNames, numBits));
                }
            }
            if (packetDumpEnabled)
                netDebugPacketDebug.Append(FixedString.Format(" Total ({0}b)", total));
            // TODO there's cancellation happening after this where we reset the datastream with certain conditions. We should cancel the stats gathering there too
        }
#endif
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpSkipInvalidGroup(int ghostId)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append(FixedString.Format("Skip invalid GhostGroup GID:{0}\n", ghostId));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpBeginGroup(int grpLen)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append(FixedString.Format("\n\t\t\tGhostGroup.Len:{0}\n\t\t\t[", grpLen));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpEndGroup(bool success)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                if(success)
                    netDebugPacketDebug.Append((FixedString32Bytes) "\t\t\t] Ok!");
                else netDebugPacketDebug.Append((FixedString32Bytes) "\t\t\t] Failed!");
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpGroupItem(int ghostChildIdx, int grpLen, int ghostType)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append(FixedString.Format("\n\t\t\t\t[{0}/{1}] GhostType:{2}", ghostChildIdx, grpLen, ghostType));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        internal void PacketDumpFlush()
        {
#if NETCODE_DEBUG
            if (!netDebugPacket.IsCreated || netDebugPacketDebug.IsEmpty)
                return;
            netDebugPacket.Log(netDebugPacketDebug);
            netDebugPacketDebug.Clear();
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_ZeroChangeOptimizedChunk(ref DataStreamWriter dataStream, ref DataStreamWriter oldStream, ref GhostChunkSerializationState chunkState)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketResult = $"Undid write of static as allowed to, saving {(dataStream.LengthInBits-oldStream.LengthInBits)}b! Set {chunkState.ZeroChangeFixedString()} LU:{chunkState.GetLastUpdate()}";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_WriterFullBeforeSerialize()
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketResult = "DidFillPacket before serialize!";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpSerializeChunk(in ArchetypeChunk chunk, int ghostType)
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString512Bytes)$"\n\t[SerializeChunk:{chunk.SequenceNumber}] {ghostTypeName}({ghostType}), Count:{chunk.Count}");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSONotStatic(in GhostCollectionPrefabSerializer typeData)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                if (typeData.StaticOptimization == 0)
                    netDebugPacketDebug.Append((FixedString32Bytes) ", CUSO=false as dynamic");
                else if (typeData.IsGhostGroup != 0)
                    netDebugPacketDebug.Append((FixedString32Bytes) ", CUSO=false as GhostGroup");
                else
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.Assert(typeData.NumChildComponents > 0);
#endif
                    netDebugPacketDebug.Append((FixedString64Bytes) $", CUSO=false as has {typeData.NumChildComponents} replicated child comps");
                }
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_CUSOSuccess(ref GhostChunkSerializationState chunkState)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                netDebugPacketDebug.Append((FixedString128Bytes)$", CUSO=true as acked {chunkState.ZeroChangeFixedString()}]");
                netDebugPacketResult = "CUSO early out!";
            }
#endif
        }

        private void PacketDumpResult_SnapshotHistorySaturated(int ghostType, in ArchetypeChunk chunk, byte numInFlightBaselines, int ticksSinceLastReceive, bool bypassSnapshotHistoryFull)
        {
            if(Hint.Unlikely(netDebug.LogLevel == NetDebug.LogLevelType.Debug))
                netDebug.LogWarning($"PERFORMANCE: Snapshot history is saturated for ghost chunk:{chunk.SequenceNumber}, ghostType:{ghostType}, {numInFlightBaselines}/{GhostSystemConstants.SnapshotHistorySize} in-flight (TSLR:{ticksSinceLastReceive}<={expectedSnapshotRttInSimTicks}), sent anyway:{bypassSnapshotHistoryFull}!");
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                if (!bypassSnapshotHistoryFull)
                    netDebugPacketResult = $"{numInFlightBaselines}/{GhostSystemConstants.SnapshotHistorySize} in-flight (TSLR:{ticksSinceLastReceive}<={expectedSnapshotRttInSimTicks}), cancelled send!";
                else
                    netDebugPacketDebug.Append((FixedString512Bytes) $", bypassing in-flight cap during lag spike (TSLR:{ticksSinceLastReceive}<={expectedSnapshotRttInSimTicks})");
            }
#endif
        }

        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSOAnyGhostComponentChanged(int compIdx)
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
            {
                netDebugPacketDebug.Append((FixedString512Bytes)$", CUSO=false as chunk.DidChange on ");
                netDebugPacketDebug.Append(ghostChunkComponentTypesPtr[compIdx].ToFixedString());
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSONoZC()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)", CUSO=false as no ZC");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSONotYetAckedZC()
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$", CUSO=false as not yet acked any ZC!");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSOHasAckedZC(ref GhostChunkSerializationState chunkState)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$", acked{chunkState.ZeroChangeFixedString()}! ");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSOImplicitAck(ref GhostChunkSerializationState chunkState)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$", implctAcked{chunkState.ZeroChangeFixedString()}! ");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSOHasRelevancyChanges()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString64Bytes)", CUSO=false as relevancy changes");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSOOrderChanged()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString64Bytes)", CUSO=false as chunk.DidOrderChange");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_DynamicFullSend()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketResult = "Full & dynamic send, so reset ZC!";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_StaticFullSend(ref GhostChunkSerializationState chunkState)
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketResult = $"Full & static send as has changes since last acked baseline, so set {chunkState.ZeroChangeFixedString()}!";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_PartialSend()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketResult = "Partial send, so reset ZC!";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_PacketFullBeforeOneEntity()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketResult = "Packet full before writing any ghosts in this chunk!";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_NoRelevantGhostsInChunk()
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketResult = "Skipped chunk as AllIrrelevant!";
#endif
        }
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void ValidateGhostComponentIndex(int compIdx)
        {
            if (compIdx >= ghostChunkComponentTypesLength)
                throw new InvalidOperationException("Component index out of range");
        }
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void ValidateNoNestedGhostGroups(int isGhostGroup)
        {
            if (isGhostGroup != 0)
                throw new InvalidOperationException("Nested ghost groups are not supported, non-root members of a group cannot be roots for their own groups.");
        }
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void ValidateGhostType(int entityGhostType, int ghostType)
        {
            if (entityGhostType != ghostType && entityGhostType >= 0)
            {
                // FIXME: what needs to happen to support this case? Should it be treated as a respawn?
                throw new InvalidOperationException(
                    "A ghost changed type, ghost must keep the same serializer type throughout their lifetime");
            }
        }
        [Conditional("NETCODE_DEBUG")]
        private void ComponentScopeBegin(int serializerIdx)
        {
            #if NETCODE_DEBUG
            if (systemData.EnablePerComponentProfiling)
                GhostComponentCollection[serializerIdx].ProfilerMarker.Begin();
            #endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void ComponentScopeEnd(int serializerIdx)
        {
            #if NETCODE_DEBUG
            if (systemData.EnablePerComponentProfiling)
                GhostComponentCollection[serializerIdx].ProfilerMarker.End();
            #endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpAckedChunk(NetworkTick baselineTick)
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString32Bytes)$", ACKED:{baselineTick.ToFixedString()}");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpClearedAckChunk(NetworkTick baselineTick)
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString32Bytes)$", CLEAR-ACKED:{baselineTick.ToFixedString()}");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpHasFailedWritesDuringSerializeEntities(int ent)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$"\n\t\t\tFilled packet writer, undoing write of ent:{ent}!\n");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpIrrelevant(int ghostId)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$"\n\t\t\tGID:{ghostId} -- Irrelevant!\n");
#endif
        }
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void ValidatePrespawnBaseline(Entity ghost, int ghostId, int ent, int baselinesCount)
        {
            if(!PrespawnHelper.IsPrespawnGhostId(ghostId))
                throw new InvalidOperationException("Invalid prespawn ghost id. All prespawn ghost ids must be < 0");
            if (baselinesCount <= ent)
                throw new InvalidOperationException($"Could not find prespawn baseline data for entity {ghost.Index}:{ghost.Version}.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void ValidatePrespawnSpaceForDynamicData(int prespawnBaselineLength, int prespawnSnapshotSize)
        {
            if (prespawnBaselineLength == prespawnSnapshotSize)
                throw new InvalidOperationException("Prespawn baseline does not have have space for dynamic buffer data");
        }

        /// <summary>
        ///     - Writes predictive component data into the snapshot.
        ///     - Writes the snapshot into the dataStream writer.
        /// </summary>
        /// <remarks>Recursive when iterating over ghost groups.</remarks>
        /// <param name="dataStream">Transport write stream to write "prediction-compressed" snapshots into.</param>
        /// <param name="skippedEntityCount"></param>
        /// <param name="anyChangeMask"></param>
        /// <param name="ghostType"></param>
        /// <param name="chunk">Chunk containing these ghosts (and thus their components).</param>
        /// <param name="startIndex">Index of the first entity to process.</param>
        /// <param name="endIndex">Index of the NEXT entity (PASSED the LAST entity) to process.</param>
        /// <param name="useSingleBaseline"></param>
        /// <param name="currentSnapshot"></param>
        /// <param name="baselinesPerEntity"></param>
        /// <param name="sameBaselinePerEntity"></param>
        /// <param name="dynamicDataLenPerEntity"></param>
        /// <param name="entityStartBit">Stores 2 ints per component, per entity. [1st] Writer bit offset to the start of this components writes. [2nd] Num bits written for this component.</param>
        /// <returns>The entity we ended on.</returns>
        /// <exception cref="InvalidOperationException"></exception>
        private int SerializeEntities(ref DataStreamWriter dataStream, out int skippedEntityCount, out uint anyChangeMask,
            int ghostType, ArchetypeChunk chunk, int startIndex, int endIndex, bool useSingleBaseline, in CurrentSnapshotState currentSnapshot,
            byte** baselinesPerEntity = null, int* sameBaselinePerEntity = null, int* dynamicDataLenPerEntity = null, int* entityStartBit = null)
        {
            PacketDumpBegin(ref chunk, startIndex, endIndex);
#if NETCODE_DEBUG
            using var tempComponentStats = new NativeList<UnsafeGhostStatsSnapshot.PerComponentStats>(20, Allocator.Temp); // each recursive call to SerializeEntities needs its own temp buffer
#endif
            skippedEntityCount = 0;
            anyChangeMask = 0;

            var realStartIndex = startIndex;

            if (currentSnapshot.relevancyData != null)
            {
                // Skip irrelevant entities at the start of hte chunk without serializing them
                while (currentSnapshot.relevancyData[startIndex] == 0)
                {
                    currentSnapshot.SnapshotEntity[startIndex] = Entity.Null;
                    ++startIndex;
                    ++skippedEntityCount;
                }
                // If everything was irrelevant, do nothing
                if (startIndex >= endIndex)
                    return endIndex;
            }

            var typeData = GhostTypeCollection[ghostType];
            int snapshotSize = typeData.SnapshotSize;
            int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
            int enableableMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);

            var ghostEntities = chunk.GetNativeArray(entityType);
            var ghosts = chunk.GetNativeArray(ref ghostComponentType);
            NativeArray<GhostCleanup> ghostSystemState = default;
            if (currentSnapshot.SnapshotData != null)
                ghostSystemState = chunk.GetNativeArray(ref ghostSystemStateType);

            byte* snapshot;
            if (currentSnapshot.SnapshotData == null)
                snapshot = (byte*)UnsafeUtility.Malloc(snapshotSize * (endIndex-startIndex), 16, Allocator.Temp);
            else
            {
                snapshot = (byte*) currentSnapshot.SnapshotData;
                snapshot += startIndex * snapshotSize;
            }

            // Setup the pointers to the baselines used per entity, also calculate the number of entities after which uses the same set of baselines
            var numAvailableBaselines = currentSnapshot.AvailableBaselines.Length;
            if (baselinesPerEntity == null)
                baselinesPerEntity = tempBaselinesPerEntity;
            if (dynamicDataLenPerEntity == null)
                dynamicDataLenPerEntity = tempDynamicDataLenPerEntity;
            if (sameBaselinePerEntity == null)
                sameBaselinePerEntity = tempSameBaselinePerEntity;
            if (entityStartBit == null)
                entityStartBit = tempEntityStartBit;

            int baseline0 = numAvailableBaselines;
            int baseline1 = numAvailableBaselines;
            int baseline2 = numAvailableBaselines;
            int sameBaseline0 = -1;
            int sameBaseline1 = -1;
            int sameBaseline2 = -1;
            int sameBaselineIndex = 0;
            int lastRelevantEntity = startIndex+1;
            int baseGhostId = chunk.Has(ref PrespawnIndexType) ? unchecked((int)PrespawnHelper.PrespawnGhostIdBase) : 1;
            var baseSpawnTick = currentTick; // Compression optimization: Assume the SpawnTick of a ghost
            baseSpawnTick.Decrement();       // will be nearer to the currentTick-1, than 0.
            var isPrespawn = chunk.Has(ref prespawnBaselineTypeHandle);
            int numPrespawnBaselines = 0;
            for (int ent = startIndex; ent < endIndex; ++ent)
            {
                var baselineIndex = ent - startIndex;
                dynamicDataLenPerEntity[baselineIndex] = 0;
                // Make sure to set the tick for this snapshot so the serialization code can read it both for the snapshot and the baselines
                *(uint*)(snapshot + snapshotSize * (baselineIndex)) = currentTick.SerializedData;

                int offset = baselineIndex*4;
                baselinesPerEntity[offset] = null;
                baselinesPerEntity[offset+1] = null;
                baselinesPerEntity[offset+2] = null;
                baselinesPerEntity[offset+3] = null;

                if (currentSnapshot.relevancyData != null && currentSnapshot.relevancyData[ent] == 0)
                {
                    currentSnapshot.SnapshotEntity[ent] = Entity.Null;
                    // FIXME: should probably also skip running serialization code for irrelevant ghosts in the middle of a chunk if that speeds things up
                    sameBaselinePerEntity[ent-startIndex] = -1;
                    continue;
                }
                lastRelevantEntity = ent+1;

                FindBaselines(ent, ghostEntities[ent], currentSnapshot, ref baseline0, ref baseline1, ref baseline2, useSingleBaseline);

                // Calculate the same baseline count for each entity - same baseline count 0 means it is part of the previous run
                if (baseline0 == sameBaseline0 && baseline1 == sameBaseline1 && baseline2 == sameBaseline2)
                {
                    // This is the same set of baselines as the current run, update the length
                    sameBaselinePerEntity[sameBaselineIndex] = sameBaselinePerEntity[sameBaselineIndex] + 1;
                    sameBaselinePerEntity[baselineIndex] = 0;
                }
                else
                {
                    // This is a different set of baselines - start a new run
                    sameBaselineIndex = baselineIndex;
                    sameBaselinePerEntity[sameBaselineIndex] = 1;

                    sameBaseline0 = baseline0;
                    sameBaseline1 = baseline1;
                    sameBaseline2 = baseline2;
                }
                if (baseline0 < numAvailableBaselines)
                {
                    baselinesPerEntity[offset] = (currentSnapshot.AvailableBaselines[baseline0].snapshot) + ent*snapshotSize;
                    baselinesPerEntity[offset+3] = (currentSnapshot.AvailableBaselines[baseline0].dynamicData);
                }
                if (baseline2 < numAvailableBaselines)
                {
                    baselinesPerEntity[offset+1] = (currentSnapshot.AvailableBaselines[baseline1].snapshot) + ent*snapshotSize;
                    baselinesPerEntity[offset+2] = (currentSnapshot.AvailableBaselines[baseline2].snapshot) + ent*snapshotSize;
                }

                if (baseline0 == numAvailableBaselines && isPrespawn && chunk.Has(ref PrespawnIndexType) &&
                    (ghostStateData.GetGhostState(ghostSystemState[ent]).Flags & ConnectionStateData.GhostStateFlags.HasBeenDespawnedAtLeastOnce) == 0)
                {
                    var prespawnBaselines = chunk.GetBufferAccessor(ref prespawnBaselineTypeHandle);
                    ValidatePrespawnBaseline(ghostEntities[ent], ghosts[ent].ghostId,ent,prespawnBaselines.Length);
                    if (prespawnBaselines[ent].Length > 0)
                    {
                        numPrespawnBaselines++;
                        var baselinePtr = (byte*)prespawnBaselines[ent].GetUnsafeReadOnlyPtr();
                        baselinesPerEntity[offset] = baselinePtr;
                        if (typeData.NumBuffers > 0)
                        {
                            ValidatePrespawnSpaceForDynamicData(prespawnBaselines[ent].Length, snapshotSize);
                            baselinesPerEntity[offset + 3] = baselinePtr + snapshotSize;
                        }
                    }
                }
            }
            // Update the end index to skip irrelevant entities at the end of the chunk
            int realEndIndex = endIndex;
            endIndex = lastRelevantEntity;
            int entityOffset = endIndex-startIndex;
            int snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) +
                                                                           (changeMaskUints * sizeof(uint)) +
                                                                           (enableableMaskUints * sizeof(uint)));
            int snapshotMaskOffsetInBits = 0;

            int dynamicDataHeaderSize = GhostChunkSerializationState.GetDynamicDataHeaderSize(chunk.Capacity);
            int snapshotDynamicDataOffset = dynamicDataHeaderSize;
            int dynamicSnapshotDataCapacity = currentSnapshot.SnapshotDynamicDataCapacity;

            byte* snapshotDynamicDataPtr = currentSnapshot.SnapshotDynamicData;
            //This condition is possible when we spawn new entities and we send the chunk the first time
            if (typeData.NumBuffers > 0 && currentSnapshot.SnapshotDynamicData == null && currentSnapshot.SnapshotDynamicDataSize > 0)
            {
                snapshotDynamicDataPtr = (byte*)UnsafeUtility.Malloc(currentSnapshot.SnapshotDynamicDataSize + dynamicDataHeaderSize, 16, Allocator.Temp);
                dynamicSnapshotDataCapacity = currentSnapshot.SnapshotDynamicDataSize;
            }
            var oldTempWriter = tempWriter;

            SnapshotPreSerializeData preSerializedSnapshot = default;
            var hasPreserializeData = chunk.Has(ref preSerializedGhostType) && SnapshotPreSerializeData.TryGetValue(chunk, out preSerializedSnapshot);
            var hasCustomSerializer = systemData.UseCustomSerializer != 0 && typeData.CustomSerializer.Ptr.IsCreated;
            var lastSerializedEntity = endIndex;

            if (hasPreserializeData)
            {
                UnsafeUtility.MemCpy(snapshot, (byte*)preSerializedSnapshot.Data+snapshotSize*startIndex, snapshotSize*(endIndex-startIndex));
                // If this chunk has been processed for this tick before we cannot copy the dynamic snapshot data since doing so would
                // overwrite already computed change masks and break delta compression.
                // Sending the same chunk multiple times only happens for non-root members of a ghost group
                if (preSerializedSnapshot.DynamicSize > 0 && currentSnapshot.AlreadyUsedChunk == 0)
                    UnsafeUtility.MemCpy(snapshotDynamicDataPtr + dynamicDataHeaderSize, (byte*)preSerializedSnapshot.Data+preSerializedSnapshot.Capacity, preSerializedSnapshot.DynamicSize);
            }

            if (hasCustomSerializer)
            {
                var context = new GhostPrefabCustomSerializer.Context
                {
                    startIndex = startIndex,
                    endIndex = endIndex,
                    ghostType = ghostType,
                    networkId = NetworkId,
                    childEntityLookup = childEntityLookup,
                    serializerState = serializerState,
                    ghostChunkComponentTypes = (IntPtr)ghostChunkComponentTypesPtr,
                    linkedEntityGroupTypeHandle = linkedEntityGroupType,
                    snapshotDataPtr = (IntPtr)snapshot,
                    baselinePerEntityPtr = (IntPtr)baselinesPerEntity,
                    sameBaselinePerEntityPtr = (IntPtr)sameBaselinePerEntity,
                    snapshotDynamicDataPtr = (IntPtr)snapshotDynamicDataPtr,
                    dynamicDataSizePerEntityPtr = (IntPtr)dynamicDataLenPerEntity,
                    zeroBaseline = (IntPtr)tempZeroBaseline,
                    entityStartBit = (IntPtr)entityStartBit,
                    ghostInstances = (IntPtr)ghosts.GetUnsafeReadOnlyPtr(),
                    snapshotOffset = snapshotOffset,
                    snapshotStride = snapshotSize,
                    hasPreserializedData = (byte)(hasPreserializeData
                        ? 1
                        : 0),
                    dynamicDataOffset = dynamicDataHeaderSize,
                    dynamicDataCapacity = dynamicSnapshotDataCapacity + dynamicDataHeaderSize
                };
                typeData.CustomSerializer.Ptr.Invoke(ref chunk,
                    typeData, GhostComponentIndex,
                    ref context,
                    ref tempWriter, compressionModel,
                    ref lastSerializedEntity);
                //Temp writer in this case only fails if there is not enough space for a single entity.
                //There is no need to retry serializing the whole chunk in this case, we know is not going to fit in the
                //current data stream size (because the size of temp writer is going to be same).
                if (tempWriter.HasFailedWrites)
                {
                    return startIndex;
                }
            }
            else
            {
                if (hasPreserializeData)
                {
                    int numComponents = typeData.NumComponents;
                    for (int comp = 0; comp < numComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        ValidateGhostComponentIndex(compIdx);

                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        if (ghostSerializer.ComponentType.IsBuffer)
                        {
                            ComponentScopeBegin(serializerIdx);
                            ghostSerializer.PostSerializeBuffer.Invoke((IntPtr)snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits,
                                ghostSerializer.ChangeMaskBits, endIndex - startIndex, (IntPtr)baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr)(entityStartBit+2*entityOffset*comp), (IntPtr)snapshotDynamicDataPtr, (IntPtr)dynamicDataLenPerEntity, dynamicSnapshotDataCapacity + dynamicDataHeaderSize);
                            ComponentScopeEnd(serializerIdx);
                            if (ghostSerializer.HasGhostFields)
                            {
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                                snapshotMaskOffsetInBits += GhostComponentSerializer.DynamicBufferComponentMaskBits;
                            }
                        }
                        else
                        {
                            // TODO: Ensure these pointer invocations are NOT called in the ZeroSize case (but we must update entityStartBit)!
                            // Which means we can remove the #ifdef in Serializer Template.
                            ComponentScopeBegin(serializerIdx);
                            ghostSerializer.PostSerialize.Invoke((IntPtr)snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits, endIndex - startIndex, (IntPtr)baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr)(entityStartBit+2*entityOffset*comp));
                            ComponentScopeEnd(serializerIdx);
                            if (ghostSerializer.HasGhostFields)
                            {
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                                snapshotMaskOffsetInBits += ghostSerializer.ChangeMaskBits;
                            }
                        }
                    }
                }
                else
                {
                    // Loop through all components and call the serialize method which will write the snapshot data and serialize the entities to the temporary stream
                    int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                    int enableableMaskOffset = 0;
                    for (int comp = 0; comp < numBaseComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        ValidateGhostComponentIndex(compIdx);
                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        var compSize = ghostSerializer.ComponentSize;
                        byte** compData = tempComponentDataPerEntity;
                        int* compDataLen = tempComponentDataLenPerEntity;
                        for (int ent = startIndex; ent < endIndex; ++ent)
                        {
                            compData[ent-startIndex] = null;
                            compDataLen[ent-startIndex] = 0;
                        }
                        //Don't access the data but always increment the offset by the component SnapshotSize.
                        //Otherwise, the next serialized component would technically copy the data in the wrong memory slot
                        //It might still work in some cases but if this snapshot is then part of the history and used for
                        //interpolated data we might get incorrect results

                        if (ghostSerializer.SerializesEnabledBit != 0)
                        {
                            var handle = ghostChunkComponentTypesPtr[compIdx];
                            UpdateEnableableMasks(chunk, startIndex, endIndex, ref handle, snapshot, changeMaskUints, enableableMaskOffset, snapshotSize);
                            ++enableableMaskOffset;
                            ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                        }
                        if (ghostSerializer.ComponentType.IsBuffer)
                        {
                            if (ghostSerializer.HasGhostFields && chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                            {
                                var bufData = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                for (int ent = startIndex; ent < endIndex; ++ent)
                                {
                                    compData[ent-startIndex] = (byte*)bufData.GetUnsafeReadOnlyPtrAndLength(ent, out var len);
                                    compDataLen[ent-startIndex] = len;
                                }
                            }
                            ComponentScopeBegin(serializerIdx);
                            ghostSerializer.SerializeBuffer.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState), (IntPtr)snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits,
                                ghostSerializer.ChangeMaskBits, (IntPtr)compData, (IntPtr)compDataLen, endIndex - startIndex, (IntPtr)baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr)(entityStartBit+2*entityOffset*comp), (IntPtr)snapshotDynamicDataPtr, ref snapshotDynamicDataOffset, (IntPtr)dynamicDataLenPerEntity, dynamicSnapshotDataCapacity + dynamicDataHeaderSize);
                            ComponentScopeEnd(serializerIdx);
                            if (ghostSerializer.HasGhostFields)
                            {
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                                snapshotMaskOffsetInBits += GhostComponentSerializer.DynamicBufferComponentMaskBits;
                            }
                        }
                        else
                        {
                            if (ghostSerializer.HasGhostFields && chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                            {
                                var data = (byte*) chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                for (int ent = startIndex; ent < endIndex; ++ent)
                                    compData[ent-startIndex] = data + ent * compSize;
                            }
                            ComponentScopeBegin(serializerIdx);
                            ghostSerializer.Serialize.Invoke((IntPtr) UnsafeUtility.AddressOf(ref serializerState), (IntPtr) snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits, (IntPtr) compData, endIndex - startIndex, (IntPtr) baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr) (entityStartBit + 2 * entityOffset * comp));
                            ComponentScopeEnd(serializerIdx);
                            if(ghostSerializer.HasGhostFields)
                            {
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                                snapshotMaskOffsetInBits += ghostSerializer.ChangeMaskBits;
                            }
                        }
                    }
                    if (typeData.NumChildComponents > 0)
                    {
                        var linkedEntityGroupAccessor = chunk.GetBufferAccessor(ref linkedEntityGroupType);
                        for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                        {
                            int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                            int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                            ValidateGhostComponentIndex(compIdx);
                            ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                            var compSize = ghostSerializer.ComponentSize;
                            byte** compData = tempComponentDataPerEntity;
                            int* compDataLen = tempComponentDataLenPerEntity;
                            for (int ent = startIndex; ent < endIndex; ++ent)
                            {
                                compData[ent-startIndex] = null;
                                compDataLen[ent - startIndex] = 0;
                            }
                            if(ghostSerializer.ComponentType.IsBuffer)
                            {
                                var snapshotPtr = snapshot;
                                for (int ent = startIndex; ent < endIndex; ++ent)
                                {
                                    var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                    var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                    if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                                    {
                                        if (ghostSerializer.SerializesEnabledBit != 0)
                                        {
                                            var entityIndex = childChunk.IndexInChunk;
                                            var handle = ghostChunkComponentTypesPtr[compIdx];
                                            UpdateEnableableMasks(childChunk.Chunk, entityIndex, entityIndex+1, ref handle, snapshotPtr, changeMaskUints, enableableMaskOffset, snapshotSize);
                                        }
                                        if (ghostSerializer.HasGhostFields)
                                        {
                                            var bufData = childChunk.Chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                            compData[ent-startIndex] = (byte*)bufData.GetUnsafeReadOnlyPtrAndLength(childChunk.IndexInChunk, out var len);
                                            compDataLen[ent-startIndex] = len;
                                        }
                                    }
                                    snapshotPtr += snapshotSize;
                                }
                                ComponentScopeBegin(serializerIdx);
                                ghostSerializer.SerializeBuffer.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState), (IntPtr)snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits,
                                    ghostSerializer.ChangeMaskBits, (IntPtr)compData, (IntPtr)compDataLen, endIndex - startIndex, (IntPtr)baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr)(entityStartBit+2*entityOffset*comp), (IntPtr)snapshotDynamicDataPtr, ref snapshotDynamicDataOffset, (IntPtr)dynamicDataLenPerEntity, dynamicSnapshotDataCapacity + dynamicDataHeaderSize);
                                ComponentScopeEnd(serializerIdx);
                                if (ghostSerializer.HasGhostFields)
                                {
                                    snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                                    snapshotMaskOffsetInBits += GhostComponentSerializer.DynamicBufferComponentMaskBits;
                                }
                                if (ghostSerializer.SerializesEnabledBit != 0)
                                {
                                    ++enableableMaskOffset;
                                    ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                                }
                            }
                            else
                            {
                                var snapshotPtr = snapshot;
                                for (int ent = startIndex; ent < endIndex; ++ent)
                                {
                                    var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                    var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                    compData[ent-startIndex] = null;
                                    //We can skip here, because the memory buffer offset is computed using the start-end entity indices
                                    if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                                    {
                                        if (ghostSerializer.SerializesEnabledBit != 0)
                                        {
                                            var entityIndex = childChunk.IndexInChunk;
                                            var handle = ghostChunkComponentTypesPtr[compIdx];
                                            UpdateEnableableMasks(childChunk.Chunk, entityIndex, entityIndex + 1, ref handle, snapshotPtr, changeMaskUints, enableableMaskOffset, snapshotSize);
                                        }

                                        if (ghostSerializer.HasGhostFields)
                                        {
                                            compData[ent - startIndex] = (byte*) childChunk.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                            compData[ent - startIndex] += childChunk.IndexInChunk * compSize;
                                        }
                                    }

                                    snapshotPtr += snapshotSize;
                                }
                                ComponentScopeBegin(serializerIdx);
                                ghostSerializer.Serialize.Invoke((IntPtr) UnsafeUtility.AddressOf(ref serializerState), (IntPtr) snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits, (IntPtr) compData, endIndex - startIndex, (IntPtr) baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr) (entityStartBit + 2 * entityOffset * comp));
                                ComponentScopeEnd(serializerIdx);
                                if (ghostSerializer.HasGhostFields)
                                {
                                    snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                                    snapshotMaskOffsetInBits += ghostSerializer.ChangeMaskBits;
                                }
                                if (ghostSerializer.SerializesEnabledBit != 0)
                                {
                                    ++enableableMaskOffset;
                                    ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                                }
                            }
                        }
                    }
                    ValidateAllEnableBitsHasBeenWritten(enableableMaskOffset, typeData.EnableableBits);
                }
                if (tempWriter.HasFailedWrites)
                {
                    //We are paying the cost of this string concatenation even though the log level will skip this.
                    //At least try to avoid that with an if
                    if (Hint.Unlikely(netDebug.LogLevel == NetDebug.LogLevelType.Debug))
                    {
                        netDebug.LogWarning($"PERFORMANCE: Could not fit snapshot content into temporary buffer of size {tempWriter.Capacity}, increasing size to {tempWriter.Capacity*2} and trying again! If this happens frequently, increase the size of this buffer via `GhostSendSystemData.TempStreamInitialSize`.");
                    }
                    // The temporary buffer could not fit the content for all entities, make it bigger and retry
                    tempWriter = new DataStreamWriter(tempWriter.Capacity*2, Allocator.Temp);
                    tempWriter.WriteBytes(oldTempWriter.AsNativeArray());
                    return SerializeEntities(ref dataStream, out skippedEntityCount, out anyChangeMask,
                        ghostType, chunk, realStartIndex, realEndIndex, useSingleBaseline, currentSnapshot,
                        baselinesPerEntity, sameBaselinePerEntity, dynamicDataLenPerEntity, entityStartBit);
                }
            }
            tempWriter.Flush();
            // Copy the content per entity from the temporary stream to the output stream in the correct order
            var writerData = (uint*)tempWriter.AsNativeArray().GetUnsafePtr();
            uint zeroChangeMask = 0;
            bool hasPartialSends = false;
            if (typeData.PredictionOwnerOffset !=0)
            {
                hasPartialSends = ((typeData.PartialComponents != 0) && (typeData.OwnerPredicted != 0));
                hasPartialSends |= typeData.PartialSendToOwner != 0;
            }
            for (int ent = startIndex; ent < lastSerializedEntity; ++ent)
            {
                var oldStream = dataStream;
                int entOffset = ent-startIndex;
                var sameBaselineCount = sameBaselinePerEntity[entOffset];

                int offset = entOffset*sizeof(uint);
                var baseline = baselinesPerEntity[offset];
                if (sameBaselineCount != 0)
                {
                    if (sameBaselineCount < 0)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.Assert(currentSnapshot.SnapshotEntity[ent] == Entity.Null);
#endif
                        // This is an irrelevant ghost, do not send anything
                        snapshot += snapshotSize;
                        ++skippedEntityCount;
                        PacketDumpIrrelevant(ghosts[ent].ghostId);
                        continue;
                    }
                    var baselineTick0 = (baseline != null) ? new NetworkTick{SerializedData = *(uint*)baseline} : currentTick;
                    var baselinePtr1 = baselinesPerEntity[offset + 1];
                    var baselinePtr2 = baselinesPerEntity[offset + 2];
                    var baselineTick1 = (baselinePtr1 != null) ? new NetworkTick{SerializedData = *(uint*)baselinePtr1} : currentTick;
                    var baselineTick2 = (baselinePtr2 != null) ? new NetworkTick{SerializedData = *(uint*)baselinePtr2} : currentTick;

                    uint baseDiff0 = baselineTick0.IsValid ? (uint)currentTick.TicksSince(baselineTick0) : GhostSystemConstants.MaxBaselineAge;
                    uint baseDiff1 = baselineTick1.IsValid ? (uint)currentTick.TicksSince(baselineTick1) : GhostSystemConstants.MaxBaselineAge;
                    uint baseDiff2 = baselineTick2.IsValid ? (uint)currentTick.TicksSince(baselineTick2) : GhostSystemConstants.MaxBaselineAge;
                    dataStream.WritePackedUInt(baseDiff0, compressionModel);
                    dataStream.WritePackedUInt(baseDiff1, compressionModel);
                    dataStream.WritePackedUInt(baseDiff2, compressionModel);
                    dataStream.WritePackedUInt((uint) sameBaselineCount, compressionModel);

                    PacketDumpBaseline(entOffset, baselineTick0, baselineTick1, baselineTick2, sameBaselineCount, useSingleBaseline, numAvailableBaselines, numPrespawnBaselines, currentSnapshot.NumInFlightBaselines);
                }

                var ghost = ghosts[ent];
                ValidateGhostType(ghost.ghostType, ghostType);

                // write the ghost + change mask from the snapshot
                dataStream.WritePackedIntDelta(ghost.ghostId, baseGhostId, compressionModel);
                baseGhostId = ghost.ghostId + 1; // Compression optimization: Assume the next GID will be:
                                                 // A) Similar to the last, and...
                                                 // B) Commonly the next GID assigned, as ghosts adjacent to
                                                 // each other in chunks tend to be spawned together.
                PacketDumpFlush();
                PacketDumpGhostID(ghost.ghostId);

                uint* changeMaskBaseline = (uint*)(baseline+sizeof(uint));
                uint* enableableMaskBaseline = (uint*)(baseline+sizeof(uint) + changeMaskUints * sizeof(uint));

                int changeMaskBaselineMask = ~0;
                int enableableMaskBaselineMask = ~0;

                var isNewGhostForClient = baseline == null;
                if (isNewGhostForClient)
                {
                    changeMaskBaseline = &zeroChangeMask;
                    enableableMaskBaseline = &zeroChangeMask;

                    changeMaskBaselineMask = 0;
                    enableableMaskBaselineMask = 0;

                    //Serialize the spawn tick only for runtime spawned ghost
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghost.ghostId))
                    {
                        dataStream.WritePackedUIntDelta(ghost.spawnTick.SerializedData, baseSpawnTick.SerializedData, compressionModel);
                        baseSpawnTick = ghost.spawnTick; // Compression optimization: Assume the next spawnTick will be similar to the last.
                        PacketDumpSpawnTick(ghost.spawnTick);
                    }
                }

                uint prevDynamicSize = 0;
                uint curDynamicSize = 0;
                //write the dynamic data size in the snapshot and sent it delta compressed against the current available baseline
                if (typeData.NumBuffers != 0)
                {
                    if(dynamicDataLenPerEntity[ent-startIndex] > dynamicSnapshotDataCapacity)
                        throw new InvalidOperationException("dynamic data size larger then the buffer capacity");
                    // Can be null if buffer has been removed from the chunk
                    if (snapshotDynamicDataPtr != null)
                    {
                        curDynamicSize = (uint) dynamicDataLenPerEntity[ent-startIndex];
                        //Store the used dynamic size for that entity in the snapshot data. It is used for delta compression
                        ((uint*) snapshotDynamicDataPtr)[ent] = curDynamicSize;
                        var baselineDynamicData = baselinesPerEntity[offset+3];
                        //this need a special case for prespawn since the data are encoded differently
                        if (baselineDynamicData != null)
                        {
                            //for prespawn ghosts only consider the fallback baseline if the tick is 0.
                            if (PrespawnHelper.IsPrespawnGhostId(ghost.ghostId) && (*(uint*)baseline) == 0)
                                prevDynamicSize = ((uint*) baselineDynamicData)[0];
                            else
                                prevDynamicSize = ((uint*) baselineDynamicData)[ent];
                        }
                    }
                    else
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.Assert(dynamicDataLenPerEntity[entOffset]==0);
                        UnityEngine.Debug.Assert(currentSnapshot.SnapshotDynamicDataSize==0);
#endif
                    }
                }

                uint* changeMasks = (uint*)(snapshot+sizeof(uint));
                uint* enableableMasks = (uint*)(snapshot+sizeof(uint) + changeMaskUints * sizeof(uint));

                //This can't work with custom serializer and it is expected they will do that as part of the
                //serialization
                if (hasPartialSends && !hasCustomSerializer)
                {
                    GhostSendType serializeMask = GhostSendType.AllClients;
                    var sendToOwner = SendToOwnerType.All;
                    var isOwner = (NetworkId == *(int*) (snapshot + typeData.PredictionOwnerOffset));
                    sendToOwner = isOwner ? SendToOwnerType.SendToOwner : SendToOwnerType.SendToNonOwner;
                    if (typeData.PartialComponents != 0 && typeData.OwnerPredicted != 0)
                        serializeMask = isOwner ? GhostSendType.OnlyPredictedClients : GhostSendType.OnlyInterpolatedClients;

                    var curMaskOffsetInBits = 0;
                    int curSnapshotDataOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + (changeMaskUints * sizeof(uint)) + (enableableMaskUints * sizeof(uint)));

                    // SIDE NOTE:
                    // IF, we sort the component differently and we allow an order like
                    // GhostOwner (always 0 or even always serialised, it is just 1 bit the by default instead of 2
                    // All non optional components
                    // All optimisable components (predicted-only)
                    // All send-onwer masked components
                    // All optimisable components (interpolated-only)
                    // we may have a better mask order in general for both changemaks and enablemasks that would lead to better compression
                    // (delta).
                    // Also, as added benefith, it may give better opportunity for perf improvement as well, since we can perform certain
                    // logic again on "per-range" of stuff.
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        var changeBits = ghostSerializer.ComponentType.IsBuffer
                            ? GhostComponentSerializer.DynamicBufferComponentMaskBits
                            : ghostSerializer.ChangeMaskBits;
                        var hasGhostFields = ghostSerializer.HasGhostFields;
                        int componentStride = ghostSerializer.ComponentType.IsBuffer
                            ? GhostComponentSerializer.DynamicBufferComponentSnapshotSize
                            : ghostSerializer.SnapshotSize;
                        componentStride = GhostComponentSerializer.SnapshotSizeAligned(componentStride);
                        //For the context: this is a very rarely used feature, only a bunch of component on some entity (usually the player)
                        //may benefit from these. So it is fundamental to avoid slowing down the serialisation fast path with any of
                        //these.
                        //However, (and this is an opt for another PR) we are doing work for nothing in that case (that may be still faster because of SIMD
                        //but I doubt in that case, because of the nature of DataStreamWriter and Huffman compression).
                        if ((serializeMask & GhostComponentIndex[typeData.FirstComponent + comp].SendMask) == 0 ||
                            (sendToOwner & GhostComponentIndex[typeData.FirstComponent + comp].SendToOwner) == 0)
                        {
                            entityStartBit[(entityOffset*comp + entOffset)*2+1] = 0;
                            //Resetting the enablemaks is not necessary here. The mask itself is not a change mask.
                            //Furthermore, the gain (1 bit) in term of compression inside a uint mask is hard to predicy, probably not much if nothing at all.
                            //Also, in term of delta compression (done later), because the default values of enable components is true, a better value would be to use
                            //as default the ~0 mask instead. Or change the way they are encoded by actually xoring against the true value instead.
                            if (hasGhostFields)
                            {
                                // This component should not be sent for this specific entity, clear the change mask and number of bits to prevent it from being sent
                                GhostComponentSerializer.ResetChangeMask((IntPtr)changeMasks, curMaskOffsetInBits, changeBits);
                                //Ideally, here we need to reset the snapshot data to value of the predicted baseline. This will allow to then
                                //resend the data (in case) using less bits. However, the longer the component is not sent, the highest is the
                                //odd that the value is way different from this baseline anyway. That may not make then any sensible different
                                //from using the default baseline instead.
                                //The big advantage of the former is that it keep the value of the snapshot identical, that is "cleaner".
                                //The client is already honour the fact he don't update the component anymore, so in that
                                //sense the value of the snapshot data is irrelvant (even though it is nicer to keep the last value of it)
                                //For sake of simplicity a reset to 0 is done here, and the same does the client when receive the component
                                //data update. That complicate the receiving side a bit but the logic is a mirror of this one (and it is necessary to ensure consistency).
                                var snapshotData = (uint*)(snapshot + curSnapshotDataOffset);
                                for(int i=0;i<componentStride/4;++i)
                                    snapshotData[i] = 0;
                            }
                            // FIXME: We need to modify the test to ensure that the enableableMasks is a MIX of 1s and 0s,
                            // otherwise this code could be broken (by removing the wrong 1) and we wont know.
                            // TODO: buffers could also reduce the required dynamic buffer size to save some memory on clients
                        }
                        if (hasGhostFields)
                        {
                            curSnapshotDataOffset += componentStride;
                            curMaskOffsetInBits += changeBits;
                        }
                    }
                }
                // make sure the last few bits of the changemask is cleared
                if ((typeData.ChangeMaskBits&31) != 0)
                    GhostComponentSerializer.CopyToChangeMask((IntPtr)changeMasks, 0, typeData.ChangeMaskBits, 32 - (typeData.ChangeMaskBits&31));
                PacketDumpChangeMasks(changeMasks, changeMaskUints);

                uint anyChangeMaskThisEntity = 0;
                uint anyEnableableMaskChangedThisEntity = 0;
                if (GhostSystemConstants.SnapshotHasCompressedGhostSize)
                {
                    var headerLen = 0;
                    //Calculate the compressed size of the header part and add that to the final ghost size
                    if (typeData.NumBuffers != 0)
                    {
                        var compressedSize = GhostComponentSerializer.GetDeltaCompressedSizeInBits(curDynamicSize, prevDynamicSize, compressionModel);
                        headerLen += compressedSize;
                    }

                    for (int i = 0; i < changeMaskUints; ++i)
                    {
                        uint changeMaskUint = changeMasks[i];
                        anyChangeMaskThisEntity |= changeMaskUint;
                        headerLen += GhostComponentSerializer.GetDeltaCompressedSizeInBits(changeMaskUint, changeMaskBaseline[i & changeMaskBaselineMask], compressionModel);
                    }

                    for (int i = 0; i < enableableMaskUints; ++i)
                    {
                        uint enableBitUint = enableableMasks[i];
                        headerLen += GhostComponentSerializer.GetDeltaCompressedSizeInBits(enableBitUint, enableableMaskBaseline[i & enableableMaskBaselineMask], compressionModel);
                    }
                    int ghostSizeInBits = 0;
                    if (anyChangeMaskThisEntity != 0)
                    {
                        if (hasCustomSerializer)
                        {
                            ghostSizeInBits = entityStartBit[entOffset * 2 + 1];
                        }
                        else
                        {
                            for (int comp = 0; comp < typeData.NumComponents; ++comp)
                                ghostSizeInBits += entityStartBit[(entityOffset * comp + entOffset) * 2 + 1];
                        }
                    }
                    dataStream.WritePackedUIntDelta((uint)(ghostSizeInBits+headerLen), 0, compressionModel);
                }
                //write the dynamic data size in the snapshot and sent it delta compressed against the current available baseline
                if (typeData.NumBuffers != 0)
                    dataStream.WritePackedUIntDelta(curDynamicSize, prevDynamicSize, compressionModel);

                for (int i = 0; i < changeMaskUints; ++i)
                {
                    uint changeMaskUint = changeMasks[i];
                    anyChangeMaskThisEntity |= changeMaskUint;
                    dataStream.WritePackedUIntDelta(changeMaskUint, changeMaskBaseline[i&changeMaskBaselineMask], compressionModel);
                }
                for (int i = 0; i < enableableMaskUints; ++i)
                {
                    uint enableableMaskUint = enableableMasks[i];
                    anyEnableableMaskChangedThisEntity |= enableableMaskUint ^ enableableMaskBaseline[i & enableableMaskBaselineMask];
                    dataStream.WritePackedUIntDelta(enableableMaskUint, enableableMaskBaseline[i & enableableMaskBaselineMask], compressionModel);
                }
                snapshot += snapshotSize;
                anyChangeMask |= anyChangeMaskThisEntity;
                anyChangeMask |= anyEnableableMaskChangedThisEntity;

#if NETCODE_DEBUG
                // resize only grows the allocation, never shrinks. Our analytics show on average users have a bit more than 20 components per ghost type, I figured 20 was a good first size and worst comes to worst resize can make it grow.
                tempComponentStats.Resize(typeData.NumComponents, options: NativeArrayOptions.ClearMemory);
                tempComponentStats.ResetToDefault();
                var stats = tempComponentStats;
#endif
                if (anyChangeMaskThisEntity != 0)
                {
                    if (hasCustomSerializer)
                    {
#if NETCODE_DEBUG
                        PacketDumpComponentSize(typeData, entityStartBit+entityOffset*2, entityOffset, entOffset, ref stats);
#endif
                        int start = entityStartBit[(entOffset)*2];
                        int len = entityStartBit[(entOffset)*2+1];
                        if (len > 0)
                        {
                            while (len > 32)
                            {
                                dataStream.WriteRawBits(writerData[start++], 32);
                                len -= 32;
                            }
                            dataStream.WriteRawBits(writerData[start], len);
                        }
                    }
                    else
                    {
#if NETCODE_DEBUG
                        PacketDumpComponentSize(typeData, entityStartBit, entityOffset, entOffset, ref stats);
#endif
                        for (int comp = 0; comp < typeData.NumComponents; ++comp)
                        {
                            int start = entityStartBit[(entityOffset*comp + entOffset)*2];
                            int len = entityStartBit[(entityOffset*comp + entOffset)*2+1];
                            if (len > 0)
                            {
                                while (len > 32)
                                {
                                    dataStream.WriteRawBits(writerData[start++], 32);
                                    len -= 32;
                                }
                                dataStream.WriteRawBits(writerData[start], len);
                            }
                        }
                    }
                }

                if (dataStream.HasFailedWrites)
                {
                    // Rollback to the last good state, and apply additional constraints on the quantity of entities we can serialize.
                    PacketDumpHasFailedWritesDuringSerializeEntities(ent);
                    dataStream = oldStream;
                    return ent;
                }

                if (typeData.IsGhostGroup != 0)
                {
                    PacketDumpFlush();
                    GhostSendSystem.s_GhostGroupMarker.Begin();
                    var ghostGroup = chunk.GetBufferAccessor(ref ghostGroupType)[ent];
                    // Serialize all other ghosts in the group, this also needs to be handled correctly in the receive system
                    dataStream.WritePackedUInt((uint)ghostGroup.Length, compressionModel);
                    if (dataStream.HasFailedWrites)
                    {
                        PacketDumpFailedSerializeGhostGroup(ent);
                        dataStream = oldStream;
                        return ent;
                    }
                    PacketDumpBeginGroup(ghostGroup.Length);
                    PacketDumpFlush();

                    bool success = SerializeGroup(ref dataStream, ref compressionModel, ghostGroup, useSingleBaseline, ref anyChangeMaskThisEntity);
                    anyChangeMask |= anyChangeMaskThisEntity;

                    GhostSendSystem.s_GhostGroupMarker.End();

                    PacketDumpEndGroup(success);
                    PacketDumpFlush();
                    if (!success)
                    {
                        // Abort before setting the entity since the snapshot is not going to be sent
                        dataStream = oldStream;
                        return ent;
                    }
                }

#if NETCODE_DEBUG
                // there was no cancellation, so we can increment the stats
                this.componentStats.IncrementWith(tempComponentStats);
#endif
                if (currentSnapshot.SnapshotData != null)
                {
                    currentSnapshot.SnapshotEntity[ent] = ghostEntities[ent];

                    // Mark this entity as spawned.
                    ref var ghostState = ref ghostStateData.GetGhostState(ghostSystemState[ent]);

                    // Our static-optimization system needs to be able to distinguish between all of the following permutations:
                    // A) [A newly spawned ghost | a pre-spawned ghost | an existing ghost].
                    // B) [with SOME existing baselines | with ZERO baselines | with PRESPAWN baselines].
                    // C) [With no changes vs baseline OR `default(T)` | with changes].
                    // D) [Ghost has just MOVED into this chunk (WITHOUT baselines) | Ghost has just MOVED into this chunk (WITH baselines) | ghost has not recently moved to this chunk]
                    //
                    // Thus, we send the ghost when we have the following conditions:
                    // 1. If we have ZERO previously acked baselines (including prespawn baselines), we MUST send,
                    // as this is a new ghost, or a (very unlucky) recently changed or moved or frequently sent ghost.
                    // Note: Prespawns are automatically spawned on the client with their special prespawn baseline.
                    // Note2: When a ghost moves to another chunk, its baselines MAY be moved too (see `UpdateChunkHistory`).
                    // 2. If anyChangeMask != 0, we MUST send, as our baseline is out of date i.e. we can presume some data changed.
                    var hasRuntimeBaseline = baseline0 < numAvailableBaselines;
                    var hasValidAckedBaseline = hasRuntimeBaseline || (isPrespawn && numPrespawnBaselines > 0);
                    var wasSentWithChangesBefore = (ghostState.Flags & ConnectionStateData.GhostStateFlags.SentWithChanges) != 0;
                    var forceResendPrespawn = isPrespawn && anyChangeMaskThisEntity == 0 && wasSentWithChangesBefore && !hasRuntimeBaseline;
                    PacketDumpForceResendPrespawn(forceResendPrespawn);

                    anyChangeMaskThisEntity |= !hasValidAckedBaseline || forceResendPrespawn ? 1u : 0u;
                    anyChangeMask |= anyChangeMaskThisEntity;

                    ghostState.Flags |= ConnectionStateData.GhostStateFlags.IsRelevant;
                    if(anyChangeMaskThisEntity != 0)
                        ghostState.Flags |= ConnectionStateData.GhostStateFlags.SentWithChanges;
                }
                PacketDumpFlush();
            }

            if (hasCustomSerializer && lastSerializedEntity != endIndex)
                return lastSerializedEntity;
            // If all entities were processes, remember to include the ones we skipped in the end of the chunk
            skippedEntityCount += realEndIndex - endIndex;
            return realEndIndex;
        }

        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpFailedSerializeGhostGroup(int ent)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$"\n\t\t\tFailed to serialize entity group. undoing write of ent:{ent}!\n");
#endif
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal static void ValidateAllEnableBitsHasBeenWritten(int enableableMaskOffset, int numEnableBits)
        {
            if (enableableMaskOffset != numEnableBits)
                throw new InvalidOperationException($"Written only {enableableMaskOffset} enable bits data which are less than the expected {numEnableBits} for this ghost type. This is a serialization/replication error.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal static void ValidateWrittenEnableBits(int enableableMaskOffset, int numEnableBits)
        {
            if (enableableMaskOffset > numEnableBits)
                throw new InvalidOperationException($"Written {enableableMaskOffset} enable bits, but expected to write exactly {numEnableBits} for this ghost type.");
        }

        public static int TypeIndexToIndexInTypeArray(ArchetypeChunk chunk, int typeIndex)
        {
            var types = chunk.Archetype.GetComponentTypes();
            for (int i = 0; i < types.Length; ++i)
            {
                if (types[i].TypeIndex == typeIndex)
                    return i;
            }
            return -1;
        }

        public static void UpdateEnableableMasks(ArchetypeChunk chunk, int startIndex, int endIndex, ref DynamicComponentTypeHandle handle, byte* snapshot,
            int changeMaskUints, int enableableMaskOffset, int snapshotSize)
        {
            var array = chunk.GetEnableableBits(ref handle);
            var bitArray = new UnsafeBitArray(&array, 2 * sizeof(ulong));

            var uintOffset = enableableMaskOffset >> 5; // This is a shortcut for `floor(enableableMaskOffset / 32)`.
            var maskOffset = enableableMaskOffset & 0x1f; // This is a shortcut for `enableableMaskOffset % 32`.
            snapshotSize /= 4;

            uint* enableableMasks = (uint*)(snapshot + sizeof(uint) + changeMaskUints * sizeof(uint)) + uintOffset;
            for (int i = startIndex; i < endIndex; ++i)
            {
                if (maskOffset == 0) // First time writing, reset the entire 32 bits.
                    *enableableMasks = 0U;
                if (bitArray.IsSet(i))
                    (*enableableMasks) |= 1U << maskOffset;
                else
                    (*enableableMasks) &= ~(1U << maskOffset);

                enableableMasks += snapshotSize;
            }
        }

        private bool CanSerializeGroup(in DynamicBuffer<GhostGroup> ghostGroup)
        {
            for (int i = 0; i < ghostGroup.Length; ++i)
            {
                if (!childEntityLookup.TryGetValue(ghostGroup[i].Value, out var groupChunk))
                {
                    netDebug.LogError("Ghost group contains an member which is not a valid entity");
                    return false;
                }
                #if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (!groupChunk.Chunk.Has(ref ghostChildEntityComponentType))
                    throw new InvalidOperationException("Ghost group contains an member which does not have a GhostChildEntityComponent.");
                #endif
                // Entity does not have valid state initialized yet, wait some more
                if (!chunkSerializationData.TryGetValue(groupChunk.Chunk, out var chunkState))
                    return false;
                // Prefab for this ghost type has not been acked yet
                if (chunkState.ghostType >= snapshotAck.NumLoadedPrefabs)
                    return false;
            }
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool SerializeGroup(ref DataStreamWriter dataStream, ref StreamCompressionModel compressionModel,
            in DynamicBuffer<GhostGroup> ghostGroup, bool useSingleBaseline, ref uint anyChangeMaskThisGroup)
        {
            var grpAvailableBaselines = new NativeList<SnapshotBaseline>(GhostSystemConstants.SnapshotHistorySize, Allocator.Temp);
            var baselinesPerEntity = stackalloc byte*[4];
            //this will fit any possible number of replicated component.
            var entityStartBit = stackalloc int[ghostChunkComponentTypesLength*2 + 2];
            //we also need to track the current write index for rollback
            var currentWriteIndex = stackalloc int[ghostGroup.Length];
            for (int i = 0; i < ghostGroup.Length; ++i)
            {
                if (!childEntityLookup.TryGetValue(ghostGroup[i].Value, out var childChunk))
                    throw new InvalidOperationException("Ghost group contains an member which is not a valid entity.");
                if (!chunkSerializationData.TryGetValue(childChunk.Chunk, out var childChunkState))
                    throw new InvalidOperationException("Ghost group member does not have state.");
                var childGhostType = childChunkState.ghostType;
                #if ENABLE_UNITY_COLLECTIONS_CHECKS
                var ghostComp = childChunk.Chunk.GetNativeArray(ref ghostComponentType);
                if (ghostComp[childChunk.IndexInChunk].ghostType >= 0 && ghostComp[childChunk.IndexInChunk].ghostType != childGhostType)
                    throw new InvalidOperationException("Ghost group member has invalid ghost type.");
                #endif
                ref readonly var childGhostPrefabSerializer = ref GhostTypeCollection.ElementAtRO(childGhostType);
                ValidateNoNestedGhostGroups(childGhostPrefabSerializer.IsGhostGroup);
                dataStream.WritePackedUInt((uint)childGhostType, compressionModel);
                dataStream.WritePackedUInt(1, compressionModel);
                dataStream.WriteRawBits(childChunk.Chunk.Has(ref prespawnBaselineTypeHandle) ? 1u : 0, 1);
                PacketDumpGroupItem(i, ghostGroup.Length, childGhostType);

                var groupSnapshot = default(CurrentSnapshotState);

                grpAvailableBaselines.Clear();
                groupSnapshot.AvailableBaselines = grpAvailableBaselines;
                if (childGhostPrefabSerializer.NumBuffers > 0)
                {
                    groupSnapshot.SnapshotDynamicDataSize = GatherDynamicBufferSize(childChunk.Chunk, childChunk.IndexInChunk, childChunk.IndexInChunk + 1, childGhostType);
                }

                uint* snapshotIndex = childChunkState.GetSnapshotIndex();

                var writeIndex = childChunkState.GetSnapshotWriteIndex();
                var baselineIndex = (GhostSystemConstants.SnapshotHistorySize + writeIndex - 1) %
                            GhostSystemConstants.SnapshotHistorySize;
                bool clearEntityArray = true;
                if (snapshotIndex[baselineIndex] != currentTick.SerializedData)
                {
                    // The chunk history only needs to be updated once per frame, this is the first time we are using this chunk this frame
                    // TODO: Updating the chunk history is only required if there has been a structural change - should skip it as an optimization
                    UpdateChunkHistory(childGhostType, childChunk.Chunk, childChunkState, childGhostPrefabSerializer.SnapshotSize);
                    snapshotIndex[writeIndex] = currentTick.SerializedData;
                    var nextWriteIndex = (writeIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
                    childChunkState.SetSnapshotWriteIndex(nextWriteIndex);
                }
                else
                {
                    // already bumped, so use previous value
                    writeIndex = baselineIndex;
                    baselineIndex = (GhostSystemConstants.SnapshotHistorySize + writeIndex - 1) %
                            GhostSystemConstants.SnapshotHistorySize;
                    clearEntityArray = false;
                    groupSnapshot.AlreadyUsedChunk = 1;
                }
                currentWriteIndex[i] = writeIndex;

                SetupDataAndAvailableBaselines(ref groupSnapshot, ref childChunkState, childChunk.Chunk, childGhostPrefabSerializer.SnapshotSize, writeIndex, snapshotIndex);

                if (clearEntityArray)
                    UnsafeUtility.MemClear(groupSnapshot.SnapshotEntity, UnsafeUtility.SizeOf<Entity>()*childChunk.Chunk.Capacity);

                // ComponentDataPerEntity, ComponentDataLengthPerEntity, and tempWriter can all be re-used in this recursive call
                // tempBaselinesPerEntity, tempDynamicDataLenPerEntity, tempSameBaselinePerEntity and tempEntityStartBit must be changed

                int sameBaselinePerEntity;
                int dynamicDataLenPerEntity;

                if (SerializeEntities(ref dataStream, out _, out var anyChangeMaskThisEntity, childGhostType, childChunk.Chunk, childChunk.IndexInChunk, childChunk.IndexInChunk+1, useSingleBaseline, groupSnapshot,
                    baselinesPerEntity, &sameBaselinePerEntity, &dynamicDataLenPerEntity, entityStartBit) != childChunk.IndexInChunk+1)
                {
                    // FIXME: this does not work if a group member is itself the root of a group since it can fail to roll back state to compress against in that case. This is the reason nested ghost groups are not supported
                    // Roll back all written entities for group members
                    while(i-- > 0)
                    {
                        if (!childEntityLookup.TryGetValue(ghostGroup[i].Value, out var revertChunk))
                            throw new InvalidOperationException("Ghost group contains an member which is not a valid entity.");
                        if (chunkSerializationData.TryGetValue(revertChunk.Chunk, out var revertChunkState))
                        {
                            var childWriteIndex = currentWriteIndex[i];
                            var childCompDataSize = GhostTypeCollection.ElementAtRO(revertChunkState.ghostType).SnapshotSize;
                            var groupSnapshotEntity = revertChunkState.GetEntity(childCompDataSize, revertChunk.Chunk.Capacity, childWriteIndex);
                            groupSnapshotEntity[revertChunk.IndexInChunk] = Entity.Null;
                        }
                    }
                    return false;
                }
                anyChangeMaskThisGroup |= anyChangeMaskThisEntity;
            }
            return true;
        }

        //Cycle over all the components for the given entity range in the chunk and compute the capacity
        //to store all the dynamic buffer contents (if any)
        private int GatherDynamicBufferSize(in ArchetypeChunk chunk, int startIndex, int endIndex, int ghostType)
        {
            if (chunk.Has(ref preSerializedGhostType) && SnapshotPreSerializeData.TryGetValue(chunk, out var preSerializedSnapshot))
            {
                return preSerializedSnapshot.DynamicSize;
            }

            var helper = new GhostSerializeHelper
            {
                ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                GhostComponentIndex = GhostComponentIndex,
                GhostComponentCollection = GhostComponentCollection,
                childEntityLookup = childEntityLookup,
                linkedEntityGroupType = linkedEntityGroupType,
                ghostChunkComponentTypesPtrLen = ghostChunkComponentTypesLength
            };

            int requiredSize = helper.GatherBufferSize(chunk, startIndex, endIndex, GhostTypeCollection[ghostType]);
            return requiredSize;
        }

        int UpdateGhostRelevancy(ArchetypeChunk chunk, in PrioChunk prioChunk, int startIndex, byte* relevancyData,
            in GhostChunkSerializationState chunkState, int snapshotSize, out bool hasRelevancySpawns)
        {
            hasRelevancySpawns = false;
            var ghost = chunk.GetNativeArray(ref ghostComponentType);
            var ghostSystemState = chunk.GetNativeArray(ref ghostSystemStateType);
            // First figure out the baselines to use per entity so they can be sent as baseline + maxCount instead of one per entity
            int irrelevantCount = 0;
            bool setIsRelevant = relevancyMode == GhostRelevancyMode.SetIsRelevant;
            bool chunkMatchesInternalRelevantRule = internalGlobalRelevantMask.Matches(chunk.Archetype);
            bool chunkMatchesEitherRelevantRule = chunkMatchesInternalRelevantRule || userGlobalRelevantMask.Matches(chunk.Archetype);
            for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
            {
                // Use query and/or importance scaling relevancy flag ONLY IF the ghost is not manually marked with a specific rule.
                // Why? Relevancy set overrides global rules, so keep the rule if there's one.
                bool isRelevant = chunkMatchesEitherRelevantRule | prioChunk.isRelevant;
                if (!setIsRelevant | !isRelevant)
                {
                    var key = new RelevantGhostForConnection(NetworkId, ghost[ent].ghostId);
                    isRelevant = relevantGhostForConnection.ContainsKey(key) == setIsRelevant;
                }

                ref var ghostState = ref ghostStateData.GetGhostState(ghostSystemState[ent]);
                bool wasRelevant = (ghostState.Flags&ConnectionStateData.GhostStateFlags.IsRelevant) != 0;
                var isDespawning = (ghostState.Flags & ConnectionStateData.GhostStateFlags.IsDespawning) != 0;
                relevancyData[ent] = 1;

                // If this ghost was previously irrelevant, we need to wait until that despawn is acked to avoid sending spawn + despawn in the same snapshot.
                if (!isRelevant || isDespawning)
                {
                    relevancyData[ent] = 0;
                    // if the already irrelevant flag is not set the client might have seen this entity
                    if (wasRelevant)
                    {
                        // Clear the snapshot history buffer, so we do not delta compress against this.
                        for (int hp = 0; hp < GhostSystemConstants.SnapshotHistorySize; ++hp)
                        {
                            var clearSnapshotEntity = chunkState.GetEntity(snapshotSize, chunk.Capacity, hp);
                            clearSnapshotEntity[ent] = Entity.Null;
                        }
                        // Add this ghost to the pending despawn queue. We have not actually sent the despawn yet!
                        PendingGhostDespawn.AddNewPendingDespawn(ref *pendingDespawns, ref ghostState.Flags, new GhostCleanup
                        {
                            ghostId = ghost[ent].ghostId,
                            spawnTick = ghost[ent].spawnTick,
                            despawnTick = NetworkTick.Invalid, // Not applicable to irrelevant despawns.
                        }, PendingGhostDespawn.DespawnReason.Irrelevant);
                    }
                    if (ent >= startIndex)
                        irrelevantCount = irrelevantCount + 1;
                }
                else if (!wasRelevant)
                    hasRelevancySpawns = true;
            }
            return irrelevantCount;
        }
        int UpdateValidGhostGroupRelevancy(ArchetypeChunk chunk, int startIndex, byte* relevancyData, bool keepState)
        {
            var ghost = chunk.GetNativeArray(ref ghostComponentType);
            var ghostGroupAccessor = chunk.GetBufferAccessor(ref ghostGroupType);

            int irrelevantCount = 0;
            for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
            {
                relevancyData[ent] = keepState ? relevancyData[ent] : (byte)1;
                if (relevancyData[ent] != 0 && !CanSerializeGroup(ghostGroupAccessor[ent]))
                {
                    PacketDumpSkipInvalidGroup(ghost[ent].ghostId);
                    PacketDumpFlush();
                    relevancyData[ent] = 0;
                    if (ent >= startIndex)
                        ++irrelevantCount;
                }
            }
            return irrelevantCount;
        }
        bool CanUseStaticOptimization(in ArchetypeChunk chunk, int ghostType, int writeIndex, uint* snapshotIndex,
            ref GhostChunkSerializationState chunkState, bool hasRelevancySpawns, bool didOrderChange)
        {
            using var _ = GhostSendSystem.s_CanUseStaticOptimizationMarker.Auto();

            // If this chunk has relevancy changes, cannot static optimize.
            if (hasRelevancySpawns)
            {
                PacketDumpCUSOHasRelevancyChanges();
                return false;
            }

            // New entities added or removed - so cannot rely on component change versioning.
            if (didOrderChange)
            {
                PacketDumpCUSOOrderChanged();
                return false;
            }

            // NOTE: We MUST ALWAYS run the `TryAck` logic below, due to the nuances of how we 'confirm' the client's ack-mask.
            // Ideally, we would early-out significantly earlier.

            // Let's look for ANY zero-change snapshots we may have sent
            // and since acked (i.e. any snapshot >= our zero-change snapshot tick).
            // We only need one, but we may have sent 2+ while we wait for an ack.
            var zeroChangeTick = chunkState.GetFirstZeroChangeTick();
            var zeroChangeVersion = chunkState.GetFirstZeroChangeVersion();
            // Prespawn chunks are special - if their ZC.Version is != 0 AND they're ZC.Tick is Invalid,
            // they haven't changed since the pre-spawn scene was loaded (except for order changes).
            // Thus, we can infer that they have been "implicitly acked" (as the client is known to have the prespawn values
            // by virtue of loading the sub-scene), and thus we CAN still use static optimization.
            var hasImplicitlyAckedZeroChange = zeroChangeVersion != 0 && !zeroChangeTick.IsValid;
            if (hasImplicitlyAckedZeroChange)
                PacketDumpCUSOImplicitAck(ref chunkState);
            bool hasAckedAnyZeroChangeSnapshot = hasImplicitlyAckedZeroChange;
            for (int i = 0; i < GhostSystemConstants.SnapshotHistorySize; ++i)
            {
                var snapshotTick = new NetworkTick {SerializedData = snapshotIndex[i]};
                if (!snapshotTick.IsValid || i == writeIndex) continue;

                // Note: We intentionally ignore `GhostSystemConstants.MaxBaselineAge` here, because we can
                // still mark the ghost as static, even if we no longer know the baseline.
                // We'll just send it as uncompressed if/when we do resend it.
                TryAck(ref chunkState, i, snapshotTick);

                hasAckedAnyZeroChangeSnapshot |= (chunkState.HasAckFlag(i) && zeroChangeTick.IsValid && snapshotTick.TicksSince(zeroChangeTick) >= 0);
                // NOTE: Don't early out here, as we MUST continue to loop through and call `TryAck`!
            }

            if (!hasAckedAnyZeroChangeSnapshot)
            {
                PacketDumpCUSONotYetAckedZC();
                return false;
            }
            PacketDumpCUSOHasAckedZC(ref chunkState);

            // If our ZC version is zero, this denotes that no zero-change snapshot exists (either implicitly or explicitly)
            // for us to try to early out from. I.e. We must have sent some un-acked changes recently.
            if (zeroChangeVersion == 0)
            {
                PacketDumpCUSONoZC();
                return false;
            }

            // So, we have a confirmed zero-change version.
            // Now, let's ensure ALL GhostField components have NOT changed.
            // If ANY have, we CANNOT skip this chunk:
            ref readonly var typeData = ref GhostTypeCollection.ElementAtRO(ghostType);
            int baseOffset = typeData.FirstComponent;
            int numChildComponents = typeData.NumChildComponents;
            int numBaseComponents = typeData.NumComponents - numChildComponents;
            for (int i = 0; i < numBaseComponents; ++i)
            {
                int compIdx = GhostComponentIndex[baseOffset + i].ComponentIndex;
                ValidateGhostComponentIndex(compIdx);
                if (chunk.DidChange(ref ghostChunkComponentTypesPtr[compIdx], zeroChangeVersion))
                {
                    PacketDumpCUSOAnyGhostComponentChanged(compIdx);
                    // TODO - As we know when change versioning led to a packet being serialized,
                    // AND we ALSO know if the ghost ended up ACTUALLY having changes,
                    // can't we just log a warning/error to the user if we encountered a false positive?
                    // Note, though; it's valid for users to write to non-GhostField's on said component,
                    // so any validation would have to be opt-in/out on a per-component (or per-ghost-type) basis?
                    return false;
                }
            }

            // Success!
            PacketDumpResult_CUSOSuccess(ref chunkState);
            return true;
        }
        private void UpdateChunkHistory(int ghostType, ArchetypeChunk currentChunk, GhostChunkSerializationState curChunkState, int snapshotSize)
        {
            var ghostSystemState = currentChunk.GetNativeArray(ref ghostSystemStateType);
            var ghostEntities = currentChunk.GetNativeArray(entityType);
            NativeParallelHashMap<uint, IntPtr> prevSnapshots = default;
            for (int currentIndexInChunk = 0, chunkEntityCount = currentChunk.Count; currentIndexInChunk < chunkEntityCount; ++currentIndexInChunk)
            {
                ref var ghostState = ref ghostStateData.GetGhostState(ghostSystemState[currentIndexInChunk]);
                var entity = ghostEntities[currentIndexInChunk];
                // Loop over all entities and find the ones that are in a different chunk or at a different index than they were last time
                if (ghostState.LastChunk != currentChunk || ghostState.LastIndexInChunk != currentIndexInChunk)
                {
                    // If the feature to keep snapshot history is enabled, the history data exists and does not contain buffers we try to copy hisotry data
                    // IsSameSizeAndCapacity is required in case the chunk was re-used for a different archetype. In that case we will get a valid chunk state,
                    // but we cannot read the entity array because we do not know the capacity
                    // GetLastValidTick is required to know that the memory used by LastChunk is currently used as a chunk storing ghosts, without that check the chunk memory could be reused for
                    // something else before or during this loop causing it to access invalid memory (which could also change during the loop)
                    if (systemData.KeepSnapshotHistoryOnStructuralChange && ghostState.LastChunk != default && GhostTypeCollection[ghostType].NumBuffers == 0 &&
                        chunkSerializationData.TryGetValue(ghostState.LastChunk, out var prevChunkState) && prevChunkState.GetLastValidTick() == currentTick &&
                        prevChunkState.IsSameSizeAndCapacity(snapshotSize, ghostState.LastChunk.Capacity))
                    {
                        uint* snapshotIndex = prevChunkState.GetSnapshotIndex();
                        int writeIndex = prevChunkState.GetSnapshotWriteIndex();

                        // Build a map from tick -> snapshot data pointer for all valid history items we find in the old chunk
                        if (prevSnapshots.IsCreated)
                            prevSnapshots.Clear();
                        else
                            prevSnapshots = new NativeParallelHashMap<uint, IntPtr>(GhostSystemConstants.SnapshotHistorySize, Allocator.Temp);
                        for (int history = 0; history < GhostSystemConstants.SnapshotHistorySize; ++history)
                        {
                            // We do not want to copy snapshot data from the write index since it is ok to keep incomplete data there
                            // The same check is not applied when we clear / write for the same reason - it is ok to keep incomplete data there
                            if (history == writeIndex)
                                continue;
                            var historyEntity = prevChunkState.GetEntity(snapshotSize, ghostState.LastChunk.Capacity, history);
                            if (historyEntity[ghostState.LastIndexInChunk] == entity)
                            {
                                var src = prevChunkState.GetData(snapshotSize, ghostState.LastChunk.Capacity, history);
                                src += snapshotSize*ghostState.LastIndexInChunk;
                                // Add this to prevSnapshots mapping
                                prevSnapshots.TryAdd(snapshotIndex[history], (IntPtr)src);
                                // Clear out the history slot in previous chunk since the new chunk will be the authority
                                historyEntity[ghostState.LastIndexInChunk] = Entity.Null;
                            }
                        }
                        snapshotIndex = curChunkState.GetSnapshotIndex();
                        // Write or clear all history for this
                        for (int history = 0; history < GhostSystemConstants.SnapshotHistorySize; ++history)
                        {
                            // if this exists in prevSnapshots, copy instead of setting entity to null
                            var historyEntity = curChunkState.GetEntity(snapshotSize, currentChunk.Capacity, history);
                            // If the tick for this history item exists in the old snapshot too, we copy the data and flag the
                            // history position as valid, otherwise we mark it as not valid
                            if (prevSnapshots.TryGetValue(snapshotIndex[history], out var src))
                            {
                                var dst = curChunkState.GetData(snapshotSize, currentChunk.Capacity, history);
                                dst += snapshotSize*currentIndexInChunk;
                                UnsafeUtility.MemCpy(dst, (void*)src, snapshotSize);
                                historyEntity[currentIndexInChunk] = entity;
                                PacketDumpMovedAckHistory(new NetworkTick{SerializedData = snapshotIndex[history],}, prevChunkState.HasAckFlag(history), curChunkState.HasAckFlag(history));
                            }
                            else
                                historyEntity[currentIndexInChunk] = Entity.Null;
                        }
                    }
                    else
                    {
                        // Clear all history for this since there is no previous history we can or want to copy from
                        for (int history = 0; history < GhostSystemConstants.SnapshotHistorySize; ++history)
                        {
                            var historyEntity = curChunkState.GetEntity(snapshotSize, currentChunk.Capacity, history);
                            historyEntity[currentIndexInChunk] = Entity.Null;
                        }
                    }
                    ghostState.LastChunk = currentChunk;
                    ghostState.LastIndexInChunk = currentIndexInChunk;
                }
            }
        }
        public SerializeEnitiesResult SerializeChunk(in PrioChunk serialChunk, ref DataStreamWriter dataStream,
            out uint thisChunkSentEntities, ref bool didFillPacket)
        {
            thisChunkSentEntities = 0;
            int entitySize = UnsafeUtility.SizeOf<Entity>();
            bool relevancyEnabled = (relevancyMode != GhostRelevancyMode.Disabled);
            bool hasRelevancySpawns = false;
            didFillPacket = false;

            var currentSnapshot = default(CurrentSnapshotState);
            currentSnapshot.AvailableBaselines = tempAvailableBaselines;
            currentSnapshot.AvailableBaselines.Clear();

            var chunk = serialChunk.chunk;
            var startIndex = serialChunk.startIndex;
            var endIndex = chunk.Count;
            var ghostType = serialChunk.ghostType;

            var typeData = GhostTypeCollection[ghostType];
            var isStatic = typeData.CanBeStaticOptimized();

            int snapshotSize = typeData.SnapshotSize;
            var useSingleBaseline = typeData.UseSingleBaseline != 0;
            useSingleBaseline |= isStatic || systemData.ForceSingleBaseline;

            int relevantGhostCount = chunk.Count - serialChunk.startIndex;
            var chunkState = chunkSerializationData[chunk];

            uint* snapshotIndex = chunkState.GetSnapshotIndex();
            int writeIndex = chunkState.GetSnapshotWriteIndex();
            PacketDumpSerializeChunk(chunk, ghostType);
            var didOrderChange = chunk.DidOrderChange(chunkState.GetOrderChangeVersion());
            if (didOrderChange)
            {
                // There has been a structural change to this chunk, which means:
                // - Possibly some new ghosts.
                // - Possibly some deleted ghosts.
                // - Possibly some moved ghosts (from other ghost chunks).
                chunkState.SetOrderChangeVersion(chunk.GetOrderVersion());
                // For prespawns; the first zero-change tick is 0, and the version is going to be equal to the
                // change version of of the PrespawnBaseline buffer.
                // Note: Structural changes within the chunk do not invalidate baselines,
                // so there is still a chance we can skip sending this chunk.
                if (chunk.Has(ref prespawnBaselineTypeHandle))
                    chunkState.SetFirstZeroChange(NetworkTick.Invalid, chunk.GetChangeVersion(ref prespawnBaselineTypeHandle));
                else
                    chunkState.SetFirstZeroChange(NetworkTick.Invalid, 0);
                PacketDumpStructuralChange(in serialChunk);
                // Validate that no items in the history buffer reference a ghost that was sent as part of a different chunk
                // Not doing this could mean that we delta compress against snapshots which are no longer available on the client
                UpdateChunkHistory(ghostType, chunk, chunkState, snapshotSize);
            }

            // Calculate which entities are relevant and trigger despawn for irrelevant entities
            if (relevancyEnabled)
            {
                using var _ = GhostSendSystem.s_RelevancyMarker.Auto();
                currentSnapshot.relevancyData = (byte*)tempRelevancyPerEntity.GetUnsafePtr();
                int irrelevantCount = UpdateGhostRelevancy(chunk, in serialChunk, startIndex, currentSnapshot.relevancyData, chunkState, snapshotSize, out hasRelevancySpawns);
                relevantGhostCount -= irrelevantCount;
                if (hasRelevancySpawns)
                {
                    // We treat this as a structural change, don't try to skip any zero change packets
                    chunkState.SetFirstZeroChange(NetworkTick.Invalid, 0);
                    PacketDumpHasRelevancySpawns();
                }
            }

            // go through and set ghost groups with missing children as irrelevant
            if (typeData.IsGhostGroup!=0)
            {
                using var _ = GhostSendSystem.s_GhostGroupRelevancyMarker.Auto();
                currentSnapshot.relevancyData = (byte*)tempRelevancyPerEntity.GetUnsafePtr();
                int irrelevantCount = UpdateValidGhostGroupRelevancy(chunk, startIndex, currentSnapshot.relevancyData, relevancyEnabled);
                relevantGhostCount -= irrelevantCount;
            }
            chunkState.SetNumRelevant(relevantGhostCount, in chunk);

            if (relevantGhostCount <= 0)
            {
                // There is nothing to send, so not need to spend time serializing
                // We do want to mark the chunk as sent this frame though - to make sure it is not processed
                // again next frame if there are more important chunks
                // This happens when using relevancy and on structural changes while there is a partially sent chunk
                // We update the timestamp as if the chunk was sent but do not actually send anything
                chunkState.SetLastFullUpdate(currentTick);
                PacketDumpResult_NoRelevantGhostsInChunk();
                return SerializeEnitiesResult.Ok;
            }

            // Only apply the zero change optimization for ghosts tagged as optimize for static
            // Ghosts optimized for dynamic get zero change snapshots when they have constant changes thanks to the delta prediction
            // Ghost groups are special, since they contain other ghosts which we do not know if they have been
            // acked as zero change or not we can never skip zero change packets for ghost groups.
            if (isStatic)
            {
                // If a chunk was modified it will be cleared after we serialize the content
                // If the snapshot is still zero change we only want to update the version, not the tick, since we still did not send anything
                if (CanUseStaticOptimization(chunk, ghostType, writeIndex, snapshotIndex, ref chunkState, hasRelevancySpawns, didOrderChange))
                {
                    // There were not changes we required to send, treat is as if we did send the chunk to make sure we do not collect all static chunks as the top priority ones
                    chunkState.SetLastFullUpdate(currentTick);
                    return SerializeEnitiesResult.Ok;
                }
            }
            else PacketDumpCUSONotStatic(in typeData);

            if (typeData.NumBuffers > 0)
            {
                //Dynamic buffer contents are always stored from the beginning of the dynamic storage buffer (for the specific history slot).
                //That because each snapshot is only relative to the entities ranges startIndex-endIndex, the outer ranges are invalidate (0-StartIndex and count-Capacity).
                //This is why we gather the buffer size starting from startIndex position instead of 0 here.

                //FIXME: this operation is costly (we traverse the whole chunk and child entities too), do that only if something changed. Backup the current size and version in the
                //chunk state. It is a non trivial check in general, due to the entity children they might be in another chunk)
                currentSnapshot.SnapshotDynamicDataSize = GatherDynamicBufferSize(chunk, serialChunk.startIndex, serialChunk.chunk.Count, ghostType);
            }

            SetupDataAndAvailableBaselines(ref currentSnapshot, ref chunkState, chunk, snapshotSize, writeIndex, snapshotIndex);

            // For SnapshotHistorySize correctness: If we've filled up our snapshot history with in-flight snapshots,
            // we CANNOT send this ghost again, as we don't have the available snapshot history space to put it.
            const int neededFreeSlots = 2; // One for this snapshot's writeIndex, another for this snapshots baselines.
            var snapshotHistorySaturated = currentSnapshot.NumInFlightBaselines >= GhostSystemConstants.SnapshotHistorySize - neededFreeSlots;
            // The following bypass safeguards this logic by ensuring that a lag spike (which would appear as a
            // high number of in-flight baselines) does not reduce our send cadence.
            var ticksSinceLastReceive = Hint.Likely(snapshotAck.LastReceivedSnapshotByRemote.IsValid) ? currentTick.TicksSince(snapshotAck.LastReceivedSnapshotByRemote) : 0;
            var bypassSnapshotHistoryFull = ticksSinceLastReceive > expectedSnapshotRttInSimTicks;
            if (snapshotHistorySaturated)
            {
                PacketDumpResult_SnapshotHistorySaturated(ghostType, in chunk, currentSnapshot.NumInFlightBaselines, ticksSinceLastReceive, bypassSnapshotHistoryFull);
                if (!bypassSnapshotHistoryFull)
                {
                    return SerializeEnitiesResult.Ok;
                }
            }

            snapshotIndex[writeIndex] = currentTick.SerializedData;
            var oldStream = dataStream;

            dataStream.WritePackedUInt((uint) ghostType, compressionModel);
            dataStream.WritePackedUInt((uint) relevantGhostCount, compressionModel);
            // Write 1 bits for that run if the entity are pre-spawned objects. This will change how the ghostId
            // is encoded and will not write the spawn tick
            dataStream.WriteRawBits(chunk.Has(ref PrespawnIndexType)?1u:0u, 1);
            PacketDumpGhostCount(ghostType, relevantGhostCount);
            if (dataStream.HasFailedWrites)
            {
                PacketDumpResult_WriterFullBeforeSerialize();
                dataStream = oldStream;
                didFillPacket = true;
                return SerializeEnitiesResult.Failed;
            }

            typeData.profilerMarker.Begin();
            // Write the chunk for current ghostType to the data stream
            tempWriter.Clear(); // Clearing the temp writer here instead of inside the method to make it easier to deal with ghost groups which recursively adds more data to the temp writer
            var ent = SerializeEntities(ref dataStream, out var skippedEntityCount, out var anyChangeMask, ghostType, chunk, startIndex, endIndex, useSingleBaseline, currentSnapshot);
            typeData.profilerMarker.End();

            // Only append chunks which ACTUALLY contain changes since their last acked baseline, and only update
            // the write index if we actually sent it:
            var isPartialChunkSend = startIndex != 0 || ent < endIndex;
            var isZeroChange = anyChangeMask == 0 && !hasRelevancySpawns; // Note: isZeroChange will be FALSE if SerializeEntities
                                                                          // detected an order change resulting in a required send.
            var triggeredZeroChangeOptimization = !isPartialChunkSend && isZeroChange && isStatic;
            if (triggeredZeroChangeOptimization)
            {
                chunkState.SetLastFullUpdate(currentTick);

                var zeroChangeTick = chunkState.GetFirstZeroChangeTick();
                if (!zeroChangeTick.IsValid) zeroChangeTick = currentTick;
                chunkState.SetFirstZeroChange(zeroChangeTick, CurrentSystemVersion);
                PacketDumpResult_ZeroChangeOptimizedChunk(ref dataStream, ref oldStream, ref chunkState);

                dataStream = oldStream;
                return SerializeEnitiesResult.Ok;
            }

            thisChunkSentEntities = (uint) (ent - serialChunk.startIndex - skippedEntityCount);
            var sentAtLeastOneEntity = thisChunkSentEntities > 0;
            if (sentAtLeastOneEntity)
            {
                if (serialChunk.startIndex > 0)
                    UnsafeUtility.MemClear(currentSnapshot.SnapshotEntity, entitySize * serialChunk.startIndex);
                if (ent < chunk.Capacity)
                    UnsafeUtility.MemClear(currentSnapshot.SnapshotEntity + ent,
                        entitySize * (chunk.Capacity - ent));
                var nextWriteIndex = (chunkState.GetSnapshotWriteIndex() + 1) % GhostSystemConstants.SnapshotHistorySize;
                chunkState.SetSnapshotWriteIndex(nextWriteIndex);
            }

            if (isPartialChunkSend)
            {
                // TODO: should this always be run or should partial chunks only be allowed for the highest priority chunk?
                //if (pc == 0)

                // Note: We take advantage of the fact that static ghosts have zero changeBits for MOST ghosts,
                // by constantly resending from the 0th Entity. E.g.
                // Send0: Send 0 - 4.
                // Send1: Send 0 - 8 (where 0 - 4 are tiny as zero change).
                // Send2: Send 0 - 10 (where 0 - 8 are tine as zero change).
                if (isStatic)
                    chunkState.SetStartIndex(0);

                // Partial chunk sends cannot be treated as static!
                // The good news is; as we ack more and more of this chunks entities, each write gets smaller
                // (because all previous ghosts are acked, thus zero change delta-compression is working).
                // This means we're VERY LIKELY to send a non-partial version of this chunk shortly,
                // which then can be 'zero change' optimized.
                // However, this is concerning as it theoretically COULD fail indefinitely.
                // But pragmatically - this works.
                didFillPacket = true; // Could not send all ghosts, so packet MUST BE full.
                chunkState.SetFirstZeroChange(NetworkTick.Invalid, 0);

                if (sentAtLeastOneEntity)
                {
                    PacketDumpResult_PartialSend();
                    return SerializeEnitiesResult.Ok;
                }
                PacketDumpResult_PacketFullBeforeOneEntity();
                dataStream = oldStream;
                return SerializeEnitiesResult.Failed;
            }

            chunkState.SetLastFullUpdate(currentTick);

            // This static ghost chunk was sent fully. Thus, we can say; ONCE the user acks THIS snapshot, they don't need
            // to be sent this chunk again (until it next changes). So we flag it as "zero-change (ZC) from here".
            if (isStatic)
            {
                chunkState.SetFirstZeroChange(currentTick, CurrentSystemVersion);
                PacketDumpResult_StaticFullSend(ref chunkState);
            }
            else
            {
                // Dynamic ghosts can never have a zero-change, as we always send them.
                chunkState.SetFirstZeroChange(NetworkTick.Invalid, 0);
                PacketDumpResult_DynamicFullSend();
            }
            return SerializeEnitiesResult.Ok;
        }
    }
}

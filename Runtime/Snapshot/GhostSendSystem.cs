using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Transforms;
using Unity.Jobs.LowLevel.Unsafe;

using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    internal struct GhostSystemStateComponent : ISystemStateComponentData
    {
        public int ghostId;
        public uint despawnTick;
    }

    internal unsafe struct SnapshotBaseline
    {
        public uint tick;
        public byte* snapshot;
        public Entity* entity;
    }

    internal unsafe struct SerializeData
    {
        public int ghostType;
        internal ArchetypeChunk chunk;
        internal int startIndex;
        internal int endIndex;
        internal uint currentTick;
        internal Entity* currentSnapshotEntity;
        internal void* currentSnapshotData;
        internal byte* currentSnapshotFlags;
        internal GhostComponent* ghosts;
        internal GhostSystemStateComponent* ghostStates;
        internal NativeArray<Entity> ghostEntities;
        internal NativeArray<int> baselinePerEntity;
        internal NativeList<SnapshotBaseline> availableBaselines;
        internal NetworkCompressionModel compressionModel;
        internal GhostSerializerState serializerState;
        internal int NetworkId;
        public byte* tempBaselineData;
    }

    public struct GhostSerializerState
    {
        public ComponentDataFromEntity<GhostComponent> GhostFromEntity;
    }

    internal struct GhostSystemConstants
    {
        public const int SnapshotHistorySize = 32;
    }

    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    [UpdateAfter(typeof(TransformSystemGroup))]
    [UpdateAfter(typeof(GhostCollectionSystem))]
    [UpdateAfter(typeof(PopulatePreSpawnedGhosts))]
    public class GhostSendSystem : SystemBase
    {
        public GhostRelevancyMode GhostRelevancyMode{get; set;}
        public JobHandle GhostRelevancySetWriteHandle{get;set;}
        public NativeHashMap<RelevantGhostForConnection, int> GhostRelevancySet => m_GhostRelevancySet;
        private NativeHashMap<RelevantGhostForConnection, int> m_GhostRelevancySet;

        private EntityQuery ghostGroup;
        private EntityQuery ghostSpawnGroup;
        private EntityQuery ghostDespawnGroup;

        private EntityQuery connectionGroup;

        private NativeQueue<int> m_FreeGhostIds;
        protected NativeArray<int> m_AllocatedGhostIds;
        private NativeList<int> m_DestroyedPrespawns;
        private NativeQueue<int> m_DestroyedPrespawnsQueue;
        private NativeArray<uint> m_AckedByAllTick;

        private NativeList<ConnectionStateData> m_ConnectionStates;
        private NativeHashMap<Entity, int> m_ConnectionStateLookup;
        private NetworkCompressionModel m_CompressionModel;

        private NativeList<PrioChunk> m_SerialSpawnChunks;

        private ServerSimulationSystemGroup m_ServerSimulation;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private NetworkStreamReceiveSystem m_ReceiveSystem;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private NativeArray<uint> m_NetStats;
        private GhostStatsCollectionSystem m_StatsCollection;
#endif

        private PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate> m_NoDistanceScale;

        private GhostCollectionSystem m_GhostCollectionSystem;

        private EntityQuery m_ChildEntityQuery;
        private NativeHashMap<Entity, EntityChunkLookup> m_ChildEntityLookup;

        unsafe struct SerializationState
        {
            public EntityArchetype arch;
            public uint lastUpdate;
            public int startIndex;
            public int ghostType;

            // the entity and data arrays are 2d arrays (chunk capacity * max snapshots)
            // Find baseline by finding the largest tick not at writeIndex which has been acked by the other end
            // Pass in entity, data [writeIndex] as current and entity, data [baseline] as baseline
            // If entity[baseline] is incorrect there is no delta compression
            public int snapshotWriteIndex;
            private byte* snapshotData;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            private int allocatedChunkCapacity;
            private int allocatedDataSize;
#endif

            // The memory layout of the snapshot data is (all items are rounded up to an even 16 bytes)
            // uint orderChangeVersion
            // uint firstZeroChangeTick
            // uint firstZeroChangeVersion
            // uint[GhostSystemConstants.SnapshotHistorySize] snapshot index, the tick for each history position
            // uint[GhostSystemConstants.SnapshotHistorySize+31 / 32] snapshot is acked, one bit per history position
            // byte[(capacity + 7) / 8] flags, one bit per entity specifying if it is despawned because it is irrelevant
            // begin array[GhostSystemConstants.SnapshotHistorySize], the following are interleaved, one of these per history position
            //     Entity[capacity], the entity which this history position is for, if it does not match current entity the snapshot cannot be used
            //     byte[capacity * snapshotSize], the raw snapshot data for each entity in the chunk
            // end array

            // 4 is size of uint in bytes, the chunk size is in bytes
            const int DataPerChunkSize = (4 * (GhostSystemConstants.SnapshotHistorySize + 3 + ((GhostSystemConstants.SnapshotHistorySize+31)>>5)) + 15) & (~15);

            public void AllocateSnapshotData(int serializerDataSize, int chunkCapacity)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                allocatedChunkCapacity = chunkCapacity;
                allocatedDataSize = serializerDataSize;
#endif
                snapshotData = (byte*) UnsafeUtility.Malloc(
                    SerializationState.CalculateSize(serializerDataSize, chunkCapacity), 16, Allocator.Persistent);

                int flagBytes = (chunkCapacity+7)>>3;
                int flagSize = (flagBytes + 15) & (~15);
                // Just clear snapshot index and flags
                UnsafeUtility.MemClear(snapshotData, DataPerChunkSize + flagSize);
            }

            public void FreeSnapshotData()
            {
                UnsafeUtility.Free(snapshotData, Allocator.Persistent);
                snapshotData = null;
            }

            public uint GetOrderChangeVersion()
            {
                return *(uint*)snapshotData;
            }
            public void SetOrderChangeVersionAndClearFlags(uint version, int chunkCapacity)
            {
                *(uint*)snapshotData = version;

                int flagBytes = (chunkCapacity+7)>>3;
                int flagSize = (flagBytes + 15) & (~15);
                // Just clear snapshot index and flags
                UnsafeUtility.MemClear(snapshotData + DataPerChunkSize, flagSize);
            }
            public uint GetFirstZeroChangeTick()
            {
                return ((uint*)snapshotData)[1];
            }
            public uint GetFirstZeroChangeVersion()
            {
                return ((uint*)snapshotData)[2];
            }
            public void SetFirstZeroChange(uint tick, uint version)
            {
                ((uint*)snapshotData)[1] = tick;
                ((uint*)snapshotData)[2] = version;
            }
            public bool HasAckFlag(int pos)
            {
                var idx = GhostSystemConstants.SnapshotHistorySize + (pos>>5);
                uint bit = 1u<<(pos&31);
                return (GetSnapshotIndex()[idx] & bit) != 0;
            }
            public void SetAckFlag(int pos)
            {
                var idx = GhostSystemConstants.SnapshotHistorySize + (pos>>5);
                uint bit = 1u<<(pos&31);
                GetSnapshotIndex()[idx] |= bit;
            }
            public void ClearAckFlag(int pos)
            {
                var idx = GhostSystemConstants.SnapshotHistorySize + (pos>>5);
                uint bit = 1u<<(pos&31);
                GetSnapshotIndex()[idx] &= (~bit);
            }


            public static int CalculateSize(int serializerDataSize, int chunkCapacity)
            {
                int flagBytes = (chunkCapacity+7)>>3;
                int flagSize = (flagBytes + 15) & (~15);
                int entitySize = (UnsafeUtility.SizeOf<Entity>() * chunkCapacity + 15) & (~15);
                int dataSize = (serializerDataSize * chunkCapacity + 15) & (~15);
                return DataPerChunkSize + flagSize + GhostSystemConstants.SnapshotHistorySize * (entitySize + dataSize);
            }

            public uint* GetSnapshotIndex()
            {
                // The +3 is the change versions and tick
                return ((uint*) snapshotData) + 3;
            }
            public byte* GetFlags(int chunkCapacity)
            {
                return (snapshotData + DataPerChunkSize);
            }

            public Entity* GetEntity(int serializerDataSize, int chunkCapacity, int historyPosition)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (historyPosition < 0 || historyPosition >= GhostSystemConstants.SnapshotHistorySize)
                    throw new IndexOutOfRangeException("Reading invalid history position");
                if (serializerDataSize != allocatedDataSize || chunkCapacity != allocatedChunkCapacity)
                    throw new IndexOutOfRangeException("Chunk capacity or data size changed");
#endif
                int flagBytes = (chunkCapacity+7)>>3;
                int flagSize = (flagBytes + 15) & (~15);
                int entitySize = (UnsafeUtility.SizeOf<Entity>() * chunkCapacity + 15) & (~15);
                int dataSize = (serializerDataSize * chunkCapacity + 15) & (~15);
                return (Entity*) (snapshotData + DataPerChunkSize + flagSize + historyPosition * (entitySize + dataSize));
            }

            public byte* GetData(int serializerDataSize, int chunkCapacity, int historyPosition)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (historyPosition < 0 || historyPosition >= GhostSystemConstants.SnapshotHistorySize)
                    throw new IndexOutOfRangeException("Reading invalid history position");
                if (serializerDataSize != allocatedDataSize || chunkCapacity != allocatedChunkCapacity)
                    throw new IndexOutOfRangeException("Chunk capacity or data size changed");
#endif
                int flagBytes = (chunkCapacity+7)>>3;
                int flagSize = (flagBytes + 15) & (~15);
                int entitySize = (UnsafeUtility.SizeOf<Entity>() * chunkCapacity + 15) & (~15);
                int dataSize = (serializerDataSize * chunkCapacity + 15) & (~15);
                return (snapshotData + DataPerChunkSize + flagSize + entitySize + historyPosition * (entitySize + dataSize));
            }
        }

        struct ConnectionStateData : IDisposable
        {
            public unsafe void Dispose()
            {
                var oldChunks = SerializationState.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < oldChunks.Length; ++i)
                {
                    SerializationState state;
                    SerializationState.TryGetValue(oldChunks[i], out state);
                    state.FreeSnapshotData();
                }

                SerializationState.Dispose();
                ClearHistory.Dispose();
            }

            public Entity Entity;
            public UnsafeHashMap<ArchetypeChunk, SerializationState> SerializationState;
            public UnsafeHashMap<int, uint> ClearHistory;
        }

        protected override void OnCreate()
        {
            m_GhostCollectionSystem = World.GetOrCreateSystem<GhostCollectionSystem>();

            m_NoDistanceScale = GhostDistanceImportance.NoScaleFunctionPointer;
            ghostGroup = GetEntityQuery(ComponentType.ReadOnly<GhostComponent>(), ComponentType.ReadOnly<GhostSystemStateComponent>());
            EntityQueryDesc filterSpawn = new EntityQueryDesc
            {
                All = new ComponentType[] {typeof(GhostComponent)},
                None = new ComponentType[] {typeof(GhostSystemStateComponent)}
            };
            EntityQueryDesc filterDespawn = new EntityQueryDesc
            {
                All = new ComponentType[] {typeof(GhostSystemStateComponent)},
                None = new ComponentType[] {typeof(GhostComponent)}
            };
            ghostSpawnGroup = GetEntityQuery(filterSpawn);
            ghostDespawnGroup = GetEntityQuery(filterDespawn);

            m_FreeGhostIds = new NativeQueue<int>(Allocator.Persistent);
            m_AllocatedGhostIds = new NativeArray<int>(1, Allocator.Persistent);
            m_AllocatedGhostIds[0] = 1; // To make sure 0 is invalid
            m_DestroyedPrespawns = new NativeList<int>(Allocator.Persistent);
            m_DestroyedPrespawnsQueue = new NativeQueue<int>(Allocator.Persistent);
            m_AckedByAllTick = new NativeArray<uint>(1, Allocator.Persistent);

            connectionGroup = GetEntityQuery(
                ComponentType.ReadWrite<NetworkStreamConnection>(),
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());

            m_ServerSimulation = World.GetExistingSystem<ServerSimulationSystemGroup>();
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_ReceiveSystem = World.GetOrCreateSystem<NetworkStreamReceiveSystem>();

            m_ConnectionStates = new NativeList<ConnectionStateData>(256, Allocator.Persistent);
            m_ConnectionStateLookup = new NativeHashMap<Entity, int>(256, Allocator.Persistent);
            m_CompressionModel = new NetworkCompressionModel(Allocator.Persistent);

            m_SerialSpawnChunks = new NativeList<PrioChunk>(1024, Allocator.Persistent);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_StatsCollection = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif

            RequireSingletonForUpdate<GhostPrefabCollectionComponent>();
            RequireSingletonForUpdate<NetworkStreamInGame>();

            m_ChildEntityQuery = GetEntityQuery(ComponentType.ReadOnly<GhostChildEntityComponent>());
            m_ChildEntityLookup = new NativeHashMap<Entity, EntityChunkLookup>(1024, Allocator.Persistent);

            m_GhostRelevancySet = new NativeHashMap<RelevantGhostForConnection, int>(1024, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_NetStats.IsCreated)
                m_NetStats.Dispose();
#endif
            m_SerialSpawnChunks.Dispose();
            m_CompressionModel.Dispose();
            m_AllocatedGhostIds.Dispose();
            m_FreeGhostIds.Dispose();
            m_DestroyedPrespawns.Dispose();
            m_DestroyedPrespawnsQueue.Dispose();
            m_AckedByAllTick.Dispose();
            foreach (var connectionState in m_ConnectionStates)
            {
                connectionState.Dispose();
            }
            m_ConnectionStates.Dispose();

            m_ConnectionStateLookup.Dispose();
            m_ChildEntityLookup.Dispose();

            GhostRelevancySetWriteHandle.Complete();
            m_GhostRelevancySet.Dispose();
        }

        [BurstCompile]
        struct SpawnGhostJob : IJob
        {
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostTypeState> GhostTypeCollection;
            [ReadOnly] public NativeArray<ArchetypeChunk> spawnChunks;
            public NativeList<PrioChunk> serialSpawnChunks;
            [ReadOnly] public EntityTypeHandle entityType;
            public ComponentTypeHandle<GhostComponent> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostChildEntityComponent> ghostChildEntityComponentType;
            public NativeQueue<int> freeGhostIds;
            public NativeArray<int> allocatedGhostIds;
            public EntityCommandBuffer commandBuffer;

            [ReadOnly] public ComponentDataFromEntity<GhostTypeComponent> ghostTypeFromEntity;
            [ReadOnly] public BufferFromEntity<GhostPrefabBuffer> ghostPrefabBufferFromEntity;
            public Entity serverPrefabEntity;
            public uint serverTick;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            [ReadOnly] public ComponentTypeHandle<GhostOwnerComponent> ghostOwnerComponentType;
#endif
            public unsafe void Execute()
            {
                var prefabList = ghostPrefabBufferFromEntity[serverPrefabEntity];
                for (int chunk = 0; chunk < spawnChunks.Length; ++chunk)
                {
                    var entities = spawnChunks[chunk].GetNativeArray(entityType);
                    var ghostTypeComponent = ghostTypeFromEntity[entities[0]];
                    int ghostType;
                    for (ghostType = 0; ghostType < prefabList.Length; ++ghostType)
                    {
                        if (ghostTypeFromEntity[prefabList[ghostType].Value] == ghostTypeComponent)
                            break;
                    }
                    if (ghostType >= prefabList.Length)
                        throw new InvalidOperationException("Could not find ghost type in the collection");
                    var ghosts = spawnChunks[chunk].GetNativeArray(ghostComponentType);
                    for (var ent = 0; ent < entities.Length; ++ent)
                    {
                        int newId;
                        if (!freeGhostIds.TryDequeue(out newId))
                        {
                            newId = allocatedGhostIds[0];
                            allocatedGhostIds[0] = newId + 1;
                        }

                        ghosts[ent] = new GhostComponent {ghostId = newId, ghostType = ghostType, spawnTick = serverTick};
                        var ghostState = new GhostSystemStateComponent
                        {
                            ghostId = newId, despawnTick = 0
                        };

                        commandBuffer.AddComponent(entities[ent], ghostState);
                    }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (GhostTypeCollection[ghostType].PredictionOwnerOffset != 0)
                    {
                        if (!spawnChunks[chunk].Has(ghostOwnerComponentType))
                            throw new InvalidOperationException("Ghost type is owner predicted but does not have a GhostOwnerComponent");
                        // Validate that the entity has a GhostOwnerComponent and that the value in the GhosOwnerComponent has been initialized
                        var ghostOwners = spawnChunks[chunk].GetNativeArray(ghostOwnerComponentType);
                        for (int ent = 0; ent < ghostOwners.Length; ++ent)
                        {
                            if (ghostOwners[ent].NetworkId == 0)
                                UnityEngine.Debug.LogError("Trying to spawn an owner predicted ghost which does not have a valid owner set. When using owner prediction you must set GhostOwnerComponent.NetworkId when spawning the ghost. If the ghost is not owned by a player you can set NetworkId to -1.");
                        }
                    }
#endif
                    if (spawnChunks[chunk].Has(ghostChildEntityComponentType))
                        continue;

                    var pc = new PrioChunk
                    {
                        chunk = spawnChunks[chunk],
                        priority = GhostTypeCollection[ghostType].BaseImportance, // Age is always 1 for new chunks
                        startIndex = 0,
                        ghostType = ghostType
                    };
                    serialSpawnChunks.Add(pc);
                }
            }
        }

        [BurstCompile]
        unsafe struct SerializeJob32 : IJobParallelFor
        {
            public DynamicTypeList32 List;
            public SerializeJob Job;
            public void Execute(int idx)
            {
                Job.Execute(idx, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        unsafe struct SerializeJob64 : IJobParallelFor
        {
            public DynamicTypeList64 List;
            public SerializeJob Job;
            public void Execute(int idx)
            {
                Job.Execute(idx, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        unsafe struct SerializeJob128 : IJobParallelFor
        {
            public DynamicTypeList128 List;
            public SerializeJob Job;
            public void Execute(int idx)
            {
                Job.Execute(idx, List.GetData(), List.Length);
            }
        }

        [BurstCompile]
        struct SerializeJob
        {
            [ReadOnly] public NativeArray<GhostComponentSerializer.State> GhostComponentCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostTypeState> GhostTypeCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostComponentIndex> GhostComponentIndex;

            public NetworkDriver.Concurrent driver;
            public NetworkPipeline unreliablePipeline;
            public NetworkPipeline unreliableFragmentedPipeline;

            [ReadOnly] public NativeArray<ArchetypeChunk> despawnChunks;
            [ReadOnly] public NativeArray<ArchetypeChunk> ghostChunks;

            public NativeArray<ConnectionStateData> connectionState;
            [ReadOnly] public ComponentDataFromEntity<NetworkSnapshotAckComponent> ackFromEntity;
            [ReadOnly] public ComponentDataFromEntity<NetworkStreamConnection> connectionFromEntity;
            [ReadOnly] public ComponentDataFromEntity<NetworkIdComponent> networkIdFromEntity;

            [ReadOnly] public NativeList<PrioChunk> serialSpawnChunks;

            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<GhostComponent> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostSystemStateComponent> ghostSystemStateType;
            [ReadOnly] public ComponentTypeHandle<GhostSimpleDeltaCompression> ghostSimpleDeltaCompressionType;
            [ReadOnly] public BufferTypeHandle<GhostGroup> ghostGroupType;
            [ReadOnly] public ComponentTypeHandle<GhostChildEntityComponent> ghostChildEntityComponentType;

            [ReadOnly] public NativeHashMap<RelevantGhostForConnection, int> relevantGhostForConnection;
            public GhostRelevancyMode relevancyMode;
            [ReadOnly] public NativeArray<int> relevantGhostCountForConnection;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            [NativeDisableParallelForRestriction] public NativeArray<uint> netStatsBuffer;
#pragma warning disable 649
            [NativeSetThreadIndex] public int ThreadIndex;
#pragma warning restore 649
            public int netStatStride;
            public int netStatSize;
#endif
            [ReadOnly] public NetworkCompressionModel compressionModel;

            [ReadOnly] public ComponentDataFromEntity<GhostComponent> ghostFromEntity;

            public uint currentTick;
            public uint localTime;

            public PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate> scaleGhostImportanceByDistance;

            public int3 tileSize;
            public int3 tileCenter;
            [ReadOnly] public ComponentTypeHandle<GhostDistancePartition> ghostDistancePartitionType;
            [ReadOnly] public ComponentDataFromEntity<GhostConnectionPosition> ghostConnectionPositionFromEntity;
            [ReadOnly] public ComponentDataFromEntity<NetworkStreamSnapshotTargetSize> snapshotTargetSizeFromEntity;

            [ReadOnly] public NativeList<int> prespawnDespawns;
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            [ReadOnly] public ComponentTypeHandle<GhostOwnerComponent> ghostOwnerType;

            [ReadOnly] public NativeHashMap<Entity, EntityChunkLookup> childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            public uint CurrentSystemVersion;
            public ulong CurrentGhostTypeVersion;


            Entity connectionEntity;
            UnsafeHashMap<ArchetypeChunk, SerializationState> chunkSerializationData;
            UnsafeHashMap<int, uint> clearHistoryData;
            int connectionIdx;

            public unsafe void Execute(int idx, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                connectionIdx = idx;
                connectionEntity = connectionState[connectionIdx].Entity;
                chunkSerializationData = connectionState[connectionIdx].SerializationState;
                clearHistoryData = connectionState[connectionIdx].ClearHistory;

                var connectionId = connectionFromEntity[connectionEntity].Value;
                if (driver.GetConnectionState(connectionId) != NetworkConnection.State.Connected)
                    return;
                int maxSnapshotSizeWithoutFragmentation = NetworkParameterConstants.MTU - driver.MaxHeaderSize(unreliablePipeline);
                int targetSnapshotSize = maxSnapshotSizeWithoutFragmentation;
                if (snapshotTargetSizeFromEntity.HasComponent(connectionEntity))
                {
                    targetSnapshotSize = snapshotTargetSizeFromEntity[connectionEntity].Value;
                }

                var success = false;
                while (!success)
                {
                    // If the requested packet size if larger than one MTU we have to use the fragmentation pipeline
                    var pipelineToUse = (targetSnapshotSize <= maxSnapshotSizeWithoutFragmentation) ? unreliablePipeline : unreliableFragmentedPipeline;
                    DataStreamWriter dataStream = driver.BeginSend(pipelineToUse, connectionId, targetSnapshotSize);
                    if (!dataStream.IsCreated)
                        throw new InvalidOperationException("Failed to send a snapshot to a client");
                    success = sendEntities(ref dataStream, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength);
                    if (success)
                        driver.EndSend(dataStream);
                    else
                        driver.AbortSend(dataStream);
                    targetSnapshotSize += targetSnapshotSize;
                }
            }

            private unsafe bool sendEntities(ref DataStreamWriter dataStream, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                var serializerState = new GhostSerializerState
                {
                    GhostFromEntity = ghostFromEntity
                };
                var NetworkId = networkIdFromEntity[connectionEntity].Value;
                var snapshotAck = ackFromEntity[connectionEntity];
                var ackTick = snapshotAck.LastReceivedSnapshotByRemote;

                dataStream.WriteByte((byte) NetworkStreamProtocol.Snapshot);

                dataStream.WriteUInt(localTime);
                uint returnTime = snapshotAck.LastReceivedRemoteTime;
                if (returnTime != 0)
                    returnTime -= (localTime - snapshotAck.LastReceiveTimestamp);
                dataStream.WriteUInt(returnTime);
                dataStream.WriteInt(snapshotAck.ServerCommandAge);
                dataStream.WriteUInt(currentTick);
                //Write the ghost version on every snapshot until the client acked one snapshot.
                if(ackTick == 0)
                {
                    dataStream.WritePackedUInt(1, compressionModel);
                    dataStream.WritePackedUInt((uint)(CurrentGhostTypeVersion >> 32),compressionModel);
                    dataStream.WritePackedUInt((uint)CurrentGhostTypeVersion, compressionModel);
                }
                else
                {
                    dataStream.WritePackedUInt(0, compressionModel);
                }
                int entitySize = UnsafeUtility.SizeOf<Entity>();

                var serialChunks = GatherGhostChunks(out var maxCount, out var totalCount);
                switch (relevancyMode)
                {
                case GhostRelevancyMode.SetIsRelevant:
                    totalCount = relevantGhostCountForConnection[NetworkId];
                    break;
                case GhostRelevancyMode.SetIsIrrelevant:
                    totalCount -= relevantGhostCountForConnection[NetworkId];
                    break;
                }
                dataStream.WritePackedUInt((uint)totalCount, compressionModel);

                var lenWriter = dataStream;
                dataStream.WriteUInt((uint) 0);
                dataStream.WriteUInt((uint) 0);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                int startPos = dataStream.LengthInBits;
#endif
                uint despawnLen = WriteDespawnGhosts(ref dataStream, ackTick);
                if (dataStream.HasFailedWrites)
                {
                    return false;
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                var netStats = netStatsBuffer.GetSubArray(netStatStride * ThreadIndex, netStatSize);
                netStats[0] = currentTick;
                netStats[1] = netStats[1] + despawnLen;
                netStats[2] = netStats[2] + (uint) (dataStream.LengthInBits - startPos);
                netStats[3] = 0;
                startPos = dataStream.LengthInBits;
#endif
                int maxSnapshotSize = 0;
                for (int i = 0; i < GhostTypeCollection.Length; ++i)
                    maxSnapshotSize = math.max(maxSnapshotSize, GhostTypeCollection[i].SnapshotSize);
                byte* tempBaselineData = (byte*)UnsafeUtility.Malloc(maxSnapshotSize, 16, Allocator.Temp);

                uint updateLen = 0;

                var availableBaselines =
                    new NativeList<SnapshotBaseline>(GhostSystemConstants.SnapshotHistorySize, Allocator.Temp);
                var baselinePerEntity = new NativeArray<int>(maxCount * 3, Allocator.Temp);
                bool didFillPacket = false;
                bool relevancyEnabled = (relevancyMode != GhostRelevancyMode.Disabled);
                for (int pc = 0; pc < serialChunks.Length; ++pc)
                {
                    var chunk = serialChunks[pc].chunk;
                    var ghostType = serialChunks[pc].ghostType;

                    Entity* currentSnapshotEntity = null;
                    byte* currentSnapshotData = null;
                    byte* currentSnapshotFlags = null;
                    SerializationState chunkState;
                    availableBaselines.Clear();

                    // Ghosts tagged with "optimize for static" set this to 1 to disable delta prediction and enable not sending data for unchanged chunks
                    int targetBaselines = serialChunks[pc].chunk.Has(ghostSimpleDeltaCompressionType) ? 1 : 3;

                    int snapshotSize = GhostTypeCollection[ghostType].SnapshotSize;
                    int connectionId = networkIdFromEntity[connectionEntity].Value;
                    bool canSkipZeroChange = false;
                    if (chunkSerializationData.TryGetValue(chunk, out chunkState))
                    {
                        uint* snapshotIndex = chunkState.GetSnapshotIndex();
                        currentSnapshotFlags = chunkState.GetFlags(chunk.Capacity);

                        // Make sure the ack masks are up to date
                        for (int i = 0; i < GhostSystemConstants.SnapshotHistorySize; ++i)
                        {
                            if (i != chunkState.snapshotWriteIndex && snapshotAck.IsReceivedByRemote(snapshotIndex[i]))
                                chunkState.SetAckFlag(i);
                        }

                        if (chunk.DidOrderChange(chunkState.GetOrderChangeVersion()))
                        {
                            // There has been a structural change to this chunk, clear all the already irrelevant flags since we can no longer trust them
                            chunkState.SetOrderChangeVersionAndClearFlags(chunk.GetOrderVersion(), chunk.Capacity);
                            chunkState.SetFirstZeroChange(0, 0);
                        }

                        var zeroChangeTick = chunkState.GetFirstZeroChangeTick();
                        // Only apply the zero change optimization for ghosts tagged as optimize for static
                        // Ghosts optimized for dynamic get zero change snapshots when they have constant changes thanks to the delta prediction
                        // Ghost groups are special, since they contain other ghosts which we do not know if they have been
                        // acked as zero change or not we can never skip zero change packets for ghost groups
                        if (zeroChangeTick != 0 && targetBaselines == 1 && GhostTypeCollection[ghostType].IsGhostGroup==0)
                        {
                            var zeroChangeVersion = chunkState.GetFirstZeroChangeVersion();

                            if (ackTick != 0 && !SequenceHelpers.IsNewer(zeroChangeTick, ackTick))
                            {
                                // check if the remote received one of the zero change versions we sent
                                for (int i = 0; i < GhostSystemConstants.SnapshotHistorySize; ++i)
                                {
                                    if (snapshotIndex[i] != 0 && !SequenceHelpers.IsNewer(zeroChangeTick, snapshotIndex[i]) && chunkState.HasAckFlag(i))
                                        canSkipZeroChange = true;
                                }
                            }

                            // If nothing in the chunk changed we don't even have to try sending it
                            int baseOffset = GhostTypeCollection[ghostType].FirstComponent;
                            int numChildComponents = GhostTypeCollection[ghostType].NumChildComponents;
                            int numBaseComponents = GhostTypeCollection[ghostType].NumComponents - numChildComponents;
                            // Ghost consisting of multiple entities are always treated as modified since they consist of multiple chunks
                            bool isModified = numChildComponents > 0;
                            for (int i = 0; i < numBaseComponents; ++i)
                            {
                                int compIdx = GhostComponentIndex[baseOffset + i].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                if (compIdx >= ghostChunkComponentTypesLength)
                                    throw new InvalidOperationException("Component index out of range");
#endif
                                isModified |= chunk.DidChange(ghostChunkComponentTypesPtr[compIdx], zeroChangeVersion);
                            }
                            bool triggerRelevancyDespawn = false;
                            if (relevancyEnabled)
                            {
                                var chunkGhosts = chunk.GetNativeArray(ghostComponentType);
                                for (int relEnt = 0; relEnt < chunkGhosts.Length; ++relEnt)
                                {
                                    var key = new RelevantGhostForConnection(connectionId, chunkGhosts[relEnt].ghostId);
                                    // If this ghost was previously irrelevant we need to wait until that despawn is acked to avoid sending spawn + despawn in the same snapshot
                                    bool setIsRelevant = (relevancyMode == GhostRelevancyMode.SetIsRelevant);
                                    bool isRelevant = (relevantGhostForConnection.TryGetValue(key, out var temp) == setIsRelevant);

                                    var flags = currentSnapshotFlags[relEnt >> 3];
                                    var flagBit = 1<<(relEnt&7);
                                    bool wasRelevant = ((flags&flagBit) == 0);
                                    if (isRelevant != wasRelevant)
                                    {
                                        if (isRelevant)
                                        {
                                            isModified = true;
                                            // We treat this as a structural change, don't try to skip any zero change packets
                                            canSkipZeroChange = false;
                                            chunkState.SetFirstZeroChange(0, 0);
                                        }
                                        else
                                            // Despawning from relevancy changes is treated differently, we just need to process the chunk once, after that it can go back to zero change. This is because despawns are relaible
                                            triggerRelevancyDespawn = true;
                                    }
                                }
                            }
                            // If a chunk was modified it will be cleared after we serialize the content
                            // If the snapshot is still zero change we only want to update the version, not the tick, since we still did not send anything
                            if (!isModified && canSkipZeroChange && !triggerRelevancyDespawn)
                            {
                                // There were not changes we required to send, treat is as if we did send the chunk to make sure we do not collect all static chunks as the top priority ones
                                chunkState.lastUpdate = currentTick;
                                chunkState.startIndex = 0;
                                chunkSerializationData.Remove(chunk);
                                chunkSerializationData.TryAdd(chunk, chunkState);
                                continue;
                            }
                        }

                        snapshotIndex[chunkState.snapshotWriteIndex] = currentTick;
                        int baseline = (GhostSystemConstants.SnapshotHistorySize + chunkState.snapshotWriteIndex - 1) %
                                       GhostSystemConstants.SnapshotHistorySize;
                        while (baseline != chunkState.snapshotWriteIndex)
                        {
                            if (chunkState.HasAckFlag(baseline))
                            {
                                availableBaselines.Add(new SnapshotBaseline
                                {
                                    tick = snapshotIndex[baseline],
                                    snapshot = chunkState.GetData(snapshotSize, chunk.Capacity, baseline),
                                    entity = chunkState.GetEntity(snapshotSize, chunk.Capacity, baseline)
                                });
                            }

                            baseline = (GhostSystemConstants.SnapshotHistorySize + baseline - 1) %
                                       GhostSystemConstants.SnapshotHistorySize;
                        }

                        // Find the acked snapshot to delta against, setup pointer to current and previous entity* and data*
                        // Remember to bump writeIndex when done
                        currentSnapshotData =
                            chunkState.GetData(snapshotSize, chunk.Capacity, chunkState.snapshotWriteIndex);
                        currentSnapshotEntity =
                            chunkState.GetEntity(snapshotSize, chunk.Capacity, chunkState.snapshotWriteIndex);
                    }
                    else if (relevancyEnabled)
                        continue; // Do not send ghosts which were just created since they have not had a chance to be added to the relevancy set yet

                    GhostSystemStateComponent* ghostState = null;
                    if (chunk.Has(ghostSystemStateType))
                    {
                        ghostState = (GhostSystemStateComponent*) chunk.GetNativeArray(ghostSystemStateType)
                            .GetUnsafeReadOnlyPtr();
                    }

                    var ghost = (GhostComponent*) chunk.GetNativeArray(ghostComponentType)
                        .GetUnsafeReadOnlyPtr();

                    var ghostEntities = chunk.GetNativeArray(entityType);
                    int ent;

                    int irrelevantCount = 0;
                    // First figure out the baselines to use per entity so they can be sent as baseline + maxCount instead of one per entity
                    for (ent = serialChunks[pc].startIndex; ent < chunk.Count; ++ent)
                    {
                        if (relevancyEnabled)
                        {
                            var key = new RelevantGhostForConnection(connectionId, ghost[ent].ghostId);
                            // If this ghost was previously irrelevant we need to wait until that despawn is acked to avoid sending spawn + despawn in the same snapshot
                            bool setIsRelevant = (relevancyMode == GhostRelevancyMode.SetIsRelevant);
                            bool isRelevant = (relevantGhostForConnection.TryGetValue(key, out var temp) == setIsRelevant);
                            if (!isRelevant || clearHistoryData.TryGetValue(ghost[ent].ghostId, out var tempTick))
                            {
                                if (currentSnapshotFlags != null)
                                {
                                    // if the already irrelevant flag is not set the client might have seen this entity
                                    var flags = currentSnapshotFlags[ent >> 3];
                                    var flagBit = 1<<(ent&7);
                                    if ((flags&flagBit) == 0)
                                    {
                                        // Clear the snapshot history buffer so we do not delta compress against this
                                        if (chunkSerializationData.TryGetValue(chunk, out var clearChunkState))
                                        {
                                            for (int hp = 0; hp < GhostSystemConstants.SnapshotHistorySize; ++hp)
                                            {
                                                var clearSnapshotEntity = clearChunkState.GetEntity(snapshotSize, chunk.Capacity, hp);
                                                clearSnapshotEntity[ent] = Entity.Null;
                                            }
                                        }
                                        // Add this ghost to the despawn queue
                                        clearHistoryData.TryAdd(ghost[ent].ghostId, currentTick);
                                        // set the flag indicating this entity is already irrelevant and does not need to be despawned again
                                        currentSnapshotFlags[ent >> 3] = (byte)(flags | flagBit);
                                    }
                                }

                                // -2 means the same baseline count will never match this
                                // We also use this to skip serialization of this entity
                                baselinePerEntity[ent * 3] = -2;
                                baselinePerEntity[ent * 3 + 1] = -2;
                                baselinePerEntity[ent * 3 + 2] = -2;
                                irrelevantCount = irrelevantCount + 1;
                                continue;
                            }
                        }
                        int foundBaselines = 0;
                        for (int baseline = 0; baseline < availableBaselines.Length; ++baseline)
                        {
                            if (availableBaselines[baseline].entity[ent] == ghostEntities[ent])
                            {
                                baselinePerEntity[ent * 3 + foundBaselines] = baseline;
                                ++foundBaselines;
                                if (foundBaselines == targetBaselines)
                                    break;
                            }
                            // Only way an entity can be missing from a snapshot but be available in an older is if last snapshot was partial
                            else if (availableBaselines[baseline].entity[ent] != Entity.Null)
                                break;
                        }

                        if (foundBaselines == 2)
                            foundBaselines = 1;
                        while (foundBaselines < 3)
                        {
                            baselinePerEntity[ent * 3 + foundBaselines] = -1;
                            ++foundBaselines;
                        }
                    }
                    uint anyChangeMask = 0;
                    int skippedEntityCount = 0;
                    int relevantGhostCount = chunk.Count - serialChunks[pc].startIndex - irrelevantCount;
                    uint currentChunkUpdateLen = 0;
                    var oldStream = dataStream;
                    if (relevantGhostCount > 0)
                    {
                        dataStream.WritePackedUInt((uint) ghostType, compressionModel);
                        dataStream.WritePackedUInt((uint) relevantGhostCount, compressionModel);
                        if (dataStream.HasFailedWrites)
                        {
                            dataStream = oldStream;
                            didFillPacket = true;
                            break;
                        }

                        var data = new SerializeData
                        {
                            ghostType = ghostType,
                            chunk = chunk,
                            startIndex = serialChunks[pc].startIndex,
                            endIndex = chunk.Count,
                            currentTick = currentTick,
                            currentSnapshotEntity = currentSnapshotEntity,
                            currentSnapshotData = currentSnapshotData,
                            currentSnapshotFlags = currentSnapshotFlags,
                            ghosts = ghost,
                            ghostStates = ghostState,
                            ghostEntities = ghostEntities,
                            baselinePerEntity = baselinePerEntity,
                            availableBaselines = availableBaselines,
                            compressionModel = compressionModel,
                            serializerState = serializerState,
                            NetworkId = NetworkId,
                            tempBaselineData = tempBaselineData
                        };

                        ent = SerializeChunk(ref dataStream, data, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength, out skippedEntityCount, out anyChangeMask);
                        if (targetBaselines == 1 && anyChangeMask == 0 && data.startIndex == 0 && ent < data.endIndex && updateLen > 0)
                        {
                            // Do not sent partial chunks for zero changes unless we have to since the zero change optimizations only kick in if the full chunk was sent
                            dataStream = oldStream;
                            didFillPacket = true;
                            break;
                        }
                        currentChunkUpdateLen = (uint) (ent - serialChunks[pc].startIndex - skippedEntityCount);
                    }
                    else
                    {
                        // Nothing needed to be sent, treat it as if everything was successful
                        ent = chunk.Count;
                        if (currentSnapshotEntity != null && ent > serialChunks[pc].startIndex)
                        {
                            skippedEntityCount = ent - serialChunks[pc].startIndex;
                            // Nothing was actually written, so just clear the entity references
                            UnsafeUtility.MemClear(currentSnapshotEntity + serialChunks[pc].startIndex, entitySize * skippedEntityCount);
                        }
                    }
                    bool isZeroChange = ent >= chunk.Count && serialChunks[pc].startIndex == 0 && anyChangeMask == 0;
                    if (isZeroChange && canSkipZeroChange)
                    {
                        // We do not actually need to send this chunk, but we treat it as if it was sent so the age etc gets updated
                        dataStream = oldStream;
                    }
                    else
                    {
                        updateLen += currentChunkUpdateLen;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        netStats[ghostType*3 + 4] = netStats[ghostType*3 + 4] + currentChunkUpdateLen;
                        netStats[ghostType*3 + 5] = netStats[ghostType*3 + 5] + (uint) (dataStream.LengthInBits - startPos);
                        // FIXME: support uncompressed count
                        //netStats[ghostType*3 + 6] = netStats[ghostType*3 + 6] + 0;
                        startPos = dataStream.LengthInBits;
#endif
                    }

                    // Spawn chunks are temporary and should not be added to the state data cache
                    if (ghostState != null)
                    {
                        // Only append chunks which contain data, and only update the write index if we actually sent it
                        if (currentChunkUpdateLen > 0 && !(isZeroChange && canSkipZeroChange))
                        {
                            if (serialChunks[pc].startIndex > 0)
                                UnsafeUtility.MemClear(currentSnapshotEntity, entitySize * serialChunks[pc].startIndex);
                            if (ent < chunk.Capacity)
                                UnsafeUtility.MemClear(currentSnapshotEntity + ent,
                                    entitySize * (chunk.Capacity - ent));
                            chunkState.snapshotWriteIndex =
                                (chunkState.snapshotWriteIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
                            // Mark this new thing we are trying to send as not acked
                            chunkState.ClearAckFlag(chunkState.snapshotWriteIndex);
                        }

                        if (ent >= chunk.Count)
                        {
                            chunkState.lastUpdate = currentTick;
                            chunkState.startIndex = 0;
                        }
                        else
                        {
                            // TODO: should this always be run or should partial chunks only be allowed for the highest priority chunk?
                            //if (pc == 0)
                            chunkState.startIndex = ent;
                        }

                        if (isZeroChange)
                        {
                            var zeroChangeTick = chunkState.GetFirstZeroChangeTick();
                            if (zeroChangeTick == 0)
                                zeroChangeTick = currentTick;
                            chunkState.SetFirstZeroChange(zeroChangeTick, CurrentSystemVersion);
                        }
                        else
                        {
                            chunkState.SetFirstZeroChange(0, 0);
                        }

                        chunkSerializationData.Remove(chunk);
                        chunkSerializationData.TryAdd(chunk, chunkState);
                    }
                    // Could not send all ghosts, so packet must be full
                    if (ent < chunk.Count)
                    {
                        didFillPacket = true;
                        break;
                    }
                }
                if (dataStream.HasFailedWrites)
                {
                    driver.AbortSend(dataStream);
                    throw new InvalidOperationException("Size limitation on snapshot did not prevent all errors");
                }

                dataStream.Flush();
                lenWriter.WriteUInt(despawnLen);
                lenWriter.WriteUInt(updateLen);

                return !(didFillPacket && updateLen == 0);    // Returns true if successful
            }
            /// Write a list of all ghosts which have been despawned after the last acked packet. Return the number of ghost ids written
            uint WriteDespawnGhosts(ref DataStreamWriter dataStream, uint ackTick)
            {
                uint despawnLen = 0;
                // TODO: if not all despawns fit, sort them based on age and maybe time since last send
                // TODO: only resend despawn on nack
                for (var chunk = 0; chunk < despawnChunks.Length; ++chunk)
                {
                    var ghostStates = despawnChunks[chunk].GetNativeArray(ghostSystemStateType);
                    for (var ent = 0; ent < ghostStates.Length; ++ent)
                    {
                        if (ackTick == 0 || SequenceHelpers.IsNewer(ghostStates[ent].despawnTick, ackTick))
                        {
                            dataStream.WritePackedUInt((uint) ghostStates[ent].ghostId, compressionModel);
                            ++despawnLen;
                        }
                    }
                }

                // If relevancy is enabled, despawn all ghosts which are irrelevant and has not been acked
                if (relevancyMode != GhostRelevancyMode.Disabled)
                {
                    // Write the despawns
                    var irrelevant = clearHistoryData.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < irrelevant.Length; ++i)
                    {
                        var irrelevantGhost = irrelevant[i];
                        clearHistoryData.TryGetValue(irrelevantGhost, out var despawnTick);
                        // The logic here is different than for regular despawn, we need an acked snapshot which is newer than
                        // when we despawned, having the exact tick it was despaned acked is not enough since the despawn message
                        // was not included in that snapshot
                        if (ackTick == 0 || !SequenceHelpers.IsNewer(ackTick, despawnTick))
                        {
                            dataStream.WritePackedUInt((uint) irrelevantGhost, compressionModel);
                            ++despawnLen;
                        }
                        else
                        {
                            clearHistoryData.Remove(irrelevantGhost);
                        }
                    }
                }

                // On new clients send out the current list of destroyed prespawned entities for despawning
                if (ackTick == 0)
                {
                    for (int i = 0; i < prespawnDespawns.Length; ++i)
                    {
                        dataStream.WritePackedUInt((uint) prespawnDespawns[i], compressionModel);
                        ++despawnLen;
                    }
                }
                return despawnLen;
            }
            /// Collect a list of all chunks which could be serialized and sent. Sort the list so other systems get it in priority order.
            /// Also cleanup any stale ghost state in the map and create new storage buffers for new chunks so all chunks are in a valid state after this has executed
            NativeList<PrioChunk> GatherGhostChunks(out int maxCount, out int totalCount)
            {
                var serialChunks =
                    new NativeList<PrioChunk>(ghostChunks.Length + serialSpawnChunks.Length, Allocator.Temp);
                serialChunks.AddRange(serialSpawnChunks);
                var existingChunks = new NativeHashMap<ArchetypeChunk, int>(ghostChunks.Length, Allocator.Temp);
                maxCount = 0;
                totalCount = 0;
                for (int chunk = 0; chunk < serialSpawnChunks.Length; ++chunk)
                {
                    totalCount += serialSpawnChunks[chunk].chunk.Count;
                    if (serialSpawnChunks[chunk].chunk.Count > maxCount)
                        maxCount = serialSpawnChunks[chunk].chunk.Count;
                }

                for (int chunk = 0; chunk < ghostChunks.Length; ++chunk)
                {
                    SerializationState chunkState;
                    var addNew = !chunkSerializationData.TryGetValue(ghostChunks[chunk], out chunkState);
                    // FIXME: should be using chunk sequence number instead of this hack
                    if (!addNew && chunkState.arch != ghostChunks[chunk].Archetype)
                    {
                        chunkState.FreeSnapshotData();
                        chunkSerializationData.Remove(ghostChunks[chunk]);
                        addNew = true;
                    }

                    if (addNew)
                    {
                        var ghosts = ghostChunks[chunk].GetNativeArray(ghostComponentType);
                        chunkState.lastUpdate = currentTick - 1;
                        chunkState.startIndex = 0;
                        chunkState.ghostType = ghosts[0].ghostType;
                        chunkState.arch = ghostChunks[chunk].Archetype;

                        chunkState.snapshotWriteIndex = 0;
                        int serializerDataSize = GhostTypeCollection[chunkState.ghostType].SnapshotSize;
                        chunkState.AllocateSnapshotData(serializerDataSize, ghostChunks[chunk].Capacity);

                        chunkSerializationData.TryAdd(ghostChunks[chunk], chunkState);
                    }
                    totalCount += ghostChunks[chunk].Count;
                    if (ghostChunks[chunk].Count > maxCount)
                        maxCount = ghostChunks[chunk].Count;

                    existingChunks.TryAdd(ghostChunks[chunk], 1);
                    if (ghostChunks[chunk].Has(ghostChildEntityComponentType))
                        continue;
                    var partitionArray = ghostChunks[chunk].GetNativeArray(ghostDistancePartitionType);
                    var ghostType = chunkState.ghostType;
                    var chunkPriority = (GhostTypeCollection[chunkState.ghostType].BaseImportance *
                                         (int) (currentTick - chunkState.lastUpdate));
                    if (partitionArray.Length > 0 && ghostConnectionPositionFromEntity.HasComponent(connectionEntity))
                    {
                        var connectionPosition = ghostConnectionPositionFromEntity[connectionEntity];
                        int3 chunkTile = partitionArray[0].Index;
                        chunkPriority = scaleGhostImportanceByDistance.Ptr.Invoke(ref connectionPosition, ref tileSize, ref tileCenter, ref chunkTile, chunkPriority);
                    }

                    var pc = new PrioChunk
                    {
                        chunk = ghostChunks[chunk],
                        priority = chunkPriority,
                        startIndex = chunkState.startIndex,
                        ghostType = ghostType
                    };
                    serialChunks.Add(pc);
                }

                var oldChunks = chunkSerializationData.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < oldChunks.Length; ++i)
                {
                    int val;
                    if (!existingChunks.TryGetValue(oldChunks[i], out val))
                    {
                        SerializationState chunkState;
                        chunkSerializationData.TryGetValue(oldChunks[i], out chunkState);
                        chunkState.FreeSnapshotData();
                        chunkSerializationData.Remove(oldChunks[i]);
                    }
                }

                NativeArray<PrioChunk> serialChunkArray = serialChunks;
                serialChunkArray.Sort();
                return serialChunks;
            }
            unsafe int SerializeChunk(ref DataStreamWriter dataStream, in SerializeData data, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength, out int skippedEntityCount, out uint anyChangeMask)
            {
                skippedEntityCount = 0;
                var compressionModel = data.compressionModel;
                var serializerState = data.serializerState;
                var typeData = GhostTypeCollection[data.ghostType];
                int ent;
                int sameBaselineCount = 0;
                int snapshotSize = typeData.SnapshotSize;
                byte* tempBaselineData = data.tempBaselineData;
                int changeMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);

                int snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                byte* snapshot;
                if (data.currentSnapshotData == null)
                    snapshot = (byte*)UnsafeUtility.Malloc(snapshotSize * data.endIndex, 16, Allocator.Temp);
                else
                    snapshot = (byte*) data.currentSnapshotData;
                snapshot += data.startIndex * snapshotSize;

                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new InvalidOperationException("Component index out of range");
#endif
                    //Don't access the data but always increment the offset by the component SnapshotSize.
                    //Otherwise, the next serialized component would technically copy the data in the wrong memory slot
                    //It might still work in some cases but if this snapshot is then part of the history and used for
                    //interpolated data we might get incorrect results
                    if (data.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                    {
                        var compSize = GhostComponentCollection[compIdx].ComponentSize;
                        var compData = (byte*)data.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                        compData += data.startIndex * compSize;
                        GhostComponentCollection[compIdx].CopyToSnapshot.Ptr.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState), (IntPtr)snapshot, snapshotOffset, snapshotSize, (IntPtr)compData, compSize, data.chunk.Count - data.startIndex);
                    }
                    else
                    {
                        var componentSnapshotSize = GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                        var snapshotIndexOffset = 0;
                        for (ent = data.startIndex; ent < data.endIndex; ++ent)
                        {
                            UnsafeUtility.MemClear(snapshot + snapshotIndexOffset + snapshotOffset, componentSnapshotSize);
                            snapshotIndexOffset += snapshotSize;
                        }
                    }
                    snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = data.chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new InvalidOperationException("Component index out of range");
#endif
                        var compSize = GhostComponentCollection[compIdx].ComponentSize;

                        for (ent = data.startIndex; ent < data.endIndex; ++ent)
                        {
                            var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                            var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                            var snapshotIndexOffset = (ent - data.startIndex) * snapshotSize;
                            //We can skip here, becase the memory buffer offset is computed using the start-end entity indices
                            if (!childEntityLookup.TryGetValue(childEnt, out var childChunk) || !childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                            {
                                var componentSnapshotSize = GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                                UnsafeUtility.MemClear(snapshot + snapshotIndexOffset + snapshotOffset, componentSnapshotSize);
                                continue;
                            }
                            var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                            compData += childChunk.index * compSize;
                            GhostComponentCollection[compIdx].CopyToSnapshot.Ptr.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState), (IntPtr)(snapshot + snapshotIndexOffset), snapshotOffset, snapshotSize, (IntPtr)compData, compSize, 1);
                        }
                        snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                    }
                }

                anyChangeMask = 0;
                for (ent = data.startIndex; ent < data.endIndex; ++ent)
                {
                    // if entity is not in relevant set - skip it
                    if (data.baselinePerEntity[ent * 3] == -2)
                    {
                        if (data.currentSnapshotData != null)
                            data.currentSnapshotEntity[ent] = Entity.Null;
                        // Ghost is irrelevant, do not send it
                        snapshot += snapshotSize;
                        ++skippedEntityCount;
                        continue;
                    }
                    var oldStream = dataStream;
    #if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (data.ghostStates != null && data.ghosts[ent].ghostType != data.ghostType)
                    {
                        // FIXME: what needs to happen to support this case? Should it be treated as a respawn?
                        throw new InvalidOperationException(
                            "A ghost changed type, ghost must keep the same serializer type throughout their lifetime");
                    }
    #endif

                    int baseline0 = data.baselinePerEntity[ent * 3];
                    int baseline1 = data.baselinePerEntity[ent * 3 + 1];
                    int baseline2 = data.baselinePerEntity[ent * 3 + 2];
                    if (sameBaselineCount == 0)
                    {
                        // Count how many entities will use the same baselines as this one, send baselines + count
                        uint baselineTick0 = data.currentTick;
                        uint baselineTick1 = data.currentTick;
                        uint baselineTick2 = data.currentTick;
                        if (baseline0 >= 0)
                        {
                            baselineTick0 = data.availableBaselines[baseline0].tick;
                        }

                        if (baseline1 >= 0)
                        {
                            baselineTick1 = data.availableBaselines[baseline1].tick;
                        }

                        if (baseline2 >= 0)
                        {
                            baselineTick2 = data.availableBaselines[baseline2].tick;
                        }

                        for (sameBaselineCount = 1; ent + sameBaselineCount < data.endIndex; ++sameBaselineCount)
                        {
                            if (data.baselinePerEntity[(ent + sameBaselineCount) * 3] != baseline0 ||
                                data.baselinePerEntity[(ent + sameBaselineCount) * 3 + 1] != baseline1 ||
                                data.baselinePerEntity[(ent + sameBaselineCount) * 3 + 2] != baseline2)
                                break;
                        }

                        uint baseDiff0 = data.currentTick - baselineTick0;
                        uint baseDiff1 = data.currentTick - baselineTick1;
                        uint baseDiff2 = data.currentTick - baselineTick2;
                        dataStream.WritePackedUInt(baseDiff0, compressionModel);
                        dataStream.WritePackedUInt(baseDiff1, compressionModel);
                        dataStream.WritePackedUInt(baseDiff2, compressionModel);
                        dataStream.WritePackedUInt((uint) sameBaselineCount, compressionModel);
                    }

                    --sameBaselineCount;
                    byte* baselineSnapshotData0 = null;
                    if (baseline0 >= 0)
                    {
                        baselineSnapshotData0 = ((byte*) data.availableBaselines[baseline0].snapshot) + ent*snapshotSize;
                    }

                    byte* baselineSnapshotData1 = null;
                    byte* baselineSnapshotData2 = null;
                    if (baseline2 >= 0)
                    {
                        baselineSnapshotData1 = ((byte*) data.availableBaselines[baseline1].snapshot) + ent*snapshotSize;
                        baselineSnapshotData2 = ((byte*) data.availableBaselines[baseline2].snapshot) + ent*snapshotSize;
                    }

                    dataStream.WritePackedUInt((uint) data.ghosts[ent].ghostId, data.compressionModel);
                    if (baseline0 < 0)
                    {
                        dataStream.WritePackedUInt(data.ghosts[ent].spawnTick, data.compressionModel);
                    }

                    byte* baseline = tempBaselineData;
                    if (baselineSnapshotData2 != null)
                    {
                        UnsafeUtility.MemCpy(baseline, baselineSnapshotData0, snapshotSize);
                        var predictor = new GhostDeltaPredictor(data.currentTick, data.availableBaselines[baseline0].tick, data.availableBaselines[baseline1].tick, data.availableBaselines[baseline2].tick);
                        snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                        for (int comp = 0; comp < typeData.NumComponents; ++comp)
                        {
                            int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                            GhostComponentCollection[compIdx].PredictDelta.Ptr.Invoke((IntPtr)(baseline+snapshotOffset), (IntPtr)(baselineSnapshotData1+snapshotOffset), (IntPtr)(baselineSnapshotData2+snapshotOffset), ref predictor);
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                        }
                    }
                    else if (baselineSnapshotData0 != null)
                    {
                        baseline = baselineSnapshotData0;
                    }
                    else
                    {
                        UnsafeUtility.MemClear(tempBaselineData, snapshotSize);
                    }

                    int maskOffset = 0;
                    snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);

                    GhostComponentSerializer.SendMask serializeMask = GhostComponentSerializer.SendMask.Interpolated | GhostComponentSerializer.SendMask.Predicted;
                    if (typeData.PredictionOwnerOffset != 0 && typeData.PartialComponents != 0)
                    {
                        if (data.NetworkId == *(int*)(snapshot + typeData.PredictionOwnerOffset))
                            serializeMask = GhostComponentSerializer.SendMask.Predicted;
                        else
                            serializeMask = GhostComponentSerializer.SendMask.Interpolated;
                    }
                    // Clear the change masks since we will not write all of them
                    UnsafeUtility.MemClear(snapshot + 4, changeMaskUints*4);

                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        // By not setting the is changed mask in this case we make sure the affected fields are not sent
                        if ((serializeMask & GhostComponentCollection[compIdx].SendMask) != 0)
                            GhostComponentCollection[compIdx].CalculateChangeMask.Ptr.Invoke((IntPtr)(snapshot+snapshotOffset), (IntPtr)(baseline+snapshotOffset), (IntPtr)(snapshot+4), maskOffset);
                        snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                        maskOffset += GhostComponentCollection[compIdx].ChangeMaskBits;
                    }
                    for (int i = 0; i < changeMaskUints; ++i)
                    {
                        uint changeMaskUint = ((uint*)(snapshot+4))[i];
                        anyChangeMask |= changeMaskUint;
                        dataStream.WritePackedUIntDelta(changeMaskUint, ((uint*)(baseline+4))[i], compressionModel);
                    }
                    maskOffset = 0;
                    snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        GhostComponentCollection[compIdx].Serialize.Ptr.Invoke((IntPtr)(snapshot+snapshotOffset), (IntPtr)(baseline+snapshotOffset), ref dataStream, ref compressionModel, (IntPtr)(snapshot+4), maskOffset);
                        snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                        maskOffset += GhostComponentCollection[compIdx].ChangeMaskBits;
                    }

                    if (typeData.IsGhostGroup != 0)
                    {
                        var snapshotAck = ackFromEntity[connectionEntity];
                        int entitySize = UnsafeUtility.SizeOf<Entity>();
                        var ghostGroup = data.chunk.GetBufferAccessor(ghostGroupType)[ent];
                        // Serialize all other ghosts in the group, this also needs to be handled correctly in the receive system
                        dataStream.WritePackedUInt((uint)ghostGroup.Length, compressionModel);
                        for (int i = 0; i < ghostGroup.Length; ++i)
                        {
                            if (!childEntityLookup.TryGetValue(ghostGroup[i].Value, out var groupChunk))
                            {
                                throw new InvalidOperationException("Could not find data for a ghost in a group. All non-root group members must have the GhostChildEntityComponent.");
                            }
                            // FIXME: use delta compression
                            var ghostComp = groupChunk.chunk.GetNativeArray(ghostComponentType);
                            #if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if (GhostTypeCollection[ghostComp[groupChunk.index].ghostType].IsGhostGroup != 0)
                            {
                                throw new InvalidOperationException("Nested ghost groups are not supported, non-root members of a group cannot be roots for their own groups.");
                            }
                            #endif
                            dataStream.WritePackedUInt((uint)ghostComp[groupChunk.index].ghostType, compressionModel);
                            dataStream.WritePackedUInt(1, compressionModel);
                            // FIXME: allocates much more memory than required - only needs 3 ints
                            var baselinePerEntity = new NativeArray<int>(3*(groupChunk.index+1), Allocator.Temp);
                            baselinePerEntity[groupChunk.index*3] = -1;
                            baselinePerEntity[groupChunk.index*3+1] = -1;
                            baselinePerEntity[groupChunk.index*3+2] = -1;

                            // FIXME: make a utility method since it is called from multiple places with slight variations
                            Entity* currentSnapshotEntity = null;
                            byte* currentSnapshotData = null;
                            byte* currentSnapshotFlags = null;
                            SerializationState chunkState;
                            var availableBaselines = new NativeList<SnapshotBaseline>(3, Allocator.Temp);
                            if (chunkSerializationData.TryGetValue(groupChunk.chunk, out chunkState))
                            {
                                int dataSize = GhostTypeCollection[chunkState.ghostType].SnapshotSize;
                                uint* snapshotIndex = chunkState.GetSnapshotIndex();

                                var writeIndex = chunkState.snapshotWriteIndex;
                                var baselineIndex = (GhostSystemConstants.SnapshotHistorySize + writeIndex - 1) %
                                            GhostSystemConstants.SnapshotHistorySize;
                                bool clearEntityArray = true;
                                if (snapshotIndex[baselineIndex] != currentTick)
                                {
                                    snapshotIndex[writeIndex] = currentTick;
                                    chunkState.snapshotWriteIndex = (writeIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
                                    // Mark this new thing we are trying to send as not acked
                                    chunkState.ClearAckFlag(chunkState.snapshotWriteIndex);
                                    chunkSerializationData.Remove(groupChunk.chunk);
                                    chunkSerializationData.TryAdd(groupChunk.chunk, chunkState);
                                }
                                else
                                {
                                    // already bumped, so use previous value
                                    writeIndex = baselineIndex;
                                    baselineIndex = (GhostSystemConstants.SnapshotHistorySize + writeIndex - 1) %
                                            GhostSystemConstants.SnapshotHistorySize;
                                    clearEntityArray = false;
                                }

                                // Find the acked snapshot to delta against, setup pointer to current and previous entity* and data*
                                currentSnapshotData =
                                    chunkState.GetData(dataSize, groupChunk.chunk.Capacity, writeIndex);
                                currentSnapshotEntity =
                                    chunkState.GetEntity(dataSize, groupChunk.chunk.Capacity, writeIndex);
                                currentSnapshotFlags =
                                    chunkState.GetFlags(groupChunk.chunk.Capacity);
                                if (clearEntityArray)
                                    UnsafeUtility.MemClear(currentSnapshotEntity, entitySize*groupChunk.chunk.Capacity);

                                while (baselineIndex != writeIndex && availableBaselines.Length < 3)
                                {
                                    // Since this chunk is only sent as a child we have to update the ack bits here
                                    if (snapshotAck.IsReceivedByRemote(snapshotIndex[baselineIndex]))
                                        chunkState.SetAckFlag(baselineIndex);
                                    if (chunkState.HasAckFlag(baselineIndex))
                                    {
                                        var entityArray = chunkState.GetEntity(dataSize, groupChunk.chunk.Capacity, baselineIndex);
                                        if (entityArray[groupChunk.index] == ghostGroup[i].Value)
                                        {
                                            baselinePerEntity[groupChunk.index*3 + availableBaselines.Length] = availableBaselines.Length;
                                            availableBaselines.Add(new SnapshotBaseline
                                            {
                                                tick = snapshotIndex[baselineIndex],
                                                snapshot = chunkState.GetData(dataSize, groupChunk.chunk.Capacity, baselineIndex),
                                                entity = entityArray
                                            });
                                        }
                                    }

                                    baselineIndex = (GhostSystemConstants.SnapshotHistorySize + baselineIndex - 1) %
                                            GhostSystemConstants.SnapshotHistorySize;
                                }
                                if (availableBaselines.Length == 2)
                                {
                                    // Roll back one since only 0, 1 or 3 baselines are supported
                                    baselinePerEntity[groupChunk.index*3+1] = -1;
                                }

                            }
                            var groupData = new SerializeData
                            {
                                ghostType = ghostComp[groupChunk.index].ghostType,
                                chunk = groupChunk.chunk,
                                startIndex = groupChunk.index,
                                endIndex = groupChunk.index+1,
                                currentTick = data.currentTick,
                                currentSnapshotEntity = currentSnapshotEntity,
                                currentSnapshotData = currentSnapshotData,
                                currentSnapshotFlags = currentSnapshotFlags,
                                ghosts = (GhostComponent*)ghostComp.GetUnsafeReadOnlyPtr(),
                                ghostStates = null, // FIXME: could extract this array too
                                ghostEntities = groupChunk.chunk.GetNativeArray(entityType),
                                baselinePerEntity = baselinePerEntity,
                                availableBaselines = availableBaselines,
                                compressionModel = data.compressionModel,
                                serializerState = data.serializerState,
                                tempBaselineData = data.tempBaselineData
                            };
                            if (SerializeChunk(ref dataStream, groupData, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength, out var ignoreSkipCount, out var ignoreAnyChangeMask) != groupChunk.index+1)
                            {
                                // FIXME: this does not work if a group member is itself the root of a group since it can fail to roll back state to compress against in that case. This is the reason nested ghost groups are not supported
                                // Roll back all written entities for group members
                                while (i-- > 0)
                                {
                                    if (!childEntityLookup.TryGetValue(ghostGroup[i].Value, out groupChunk))
                                    {
                                        throw new InvalidOperationException("Could not find data for a ghost in a group. All non-root group members must have the GhostChildEntityComponent.");
                                    }
                                    if (chunkSerializationData.TryGetValue(groupChunk.chunk, out chunkState))
                                    {
                                        int dataSize = GhostTypeCollection[chunkState.ghostType].SnapshotSize;
                                        int writeIndex = (GhostSystemConstants.SnapshotHistorySize + chunkState.snapshotWriteIndex - 1) %
                                                    GhostSystemConstants.SnapshotHistorySize;
                                        currentSnapshotEntity =
                                            chunkState.GetEntity(dataSize, groupChunk.chunk.Capacity, writeIndex);
                                        currentSnapshotEntity[groupChunk.index] = Entity.Null;
                                    }

                                }
                                // Abort before setting the entity since the snapshot is not going to be sent
                                dataStream = oldStream;
                                return ent;
                            }
                        }
                    }

                    if (dataStream.HasFailedWrites)
                    {
                        // Abort before setting the entity since the snapshot is not going to be sent
                        dataStream = oldStream;
                        break;
                    }

                    if (data.currentSnapshotData != null)
                    {
                        data.currentSnapshotEntity[ent] = data.ghostEntities[ent];
                        // Clear the flag saying this entity is already irrelevant and has been despawned
                        var flags = data.currentSnapshotFlags[ent>>3];
                        var flagBit = 1<<(ent&7);
                        data.currentSnapshotFlags[ent>>3] = (byte)(flags & (~flagBit));
                    }

                    snapshot += snapshotSize;
                }

                return ent;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void UpdateNetStats(out int netStatSize, out int netStatStride)
        {
            var intsPerCacheLine = JobsUtility.CacheLineSize/4;
            netStatSize = m_GhostCollectionSystem.m_GhostTypeCollection.Length * 3 + 3 + 1;
            // Round up to an even cache line size in order to reduce false sharing
            netStatStride = (netStatSize + intsPerCacheLine-1) & (~(intsPerCacheLine-1));
            if (m_NetStats.IsCreated && m_NetStats.Length != netStatStride * JobsUtility.MaxJobThreadCount)
                m_NetStats.Dispose();
            if (!m_NetStats.IsCreated)
                m_NetStats = new NativeArray<uint>(netStatStride * JobsUtility.MaxJobThreadCount, Allocator.Persistent);
            for (int worker = 1; worker < JobsUtility.MaxJobThreadCount; ++worker)
            {
                int statOffset = worker * netStatStride;
                // First uint is tick
                if (m_NetStats[0] == 0)
                    m_NetStats[0] += m_NetStats[statOffset];
                for (int i = 1; i < netStatSize; ++i)
                {
                    m_NetStats[i] += m_NetStats[statOffset+i];
                    m_NetStats[statOffset+i] = 0;
                }
            }
            m_StatsCollection.AddSnapshotStats(m_NetStats.GetSubArray(0, netStatSize));
            for (int i = 0; i < netStatSize; ++i)
            {
                m_NetStats[i] = 0;
            }
        }
#endif

        protected unsafe override void OnUpdate()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UpdateNetStats(out var netStatSize, out var netStatStride);
#endif
            // Make sure the list of connections and connection state is up to date
            var connections = connectionGroup.ToEntityArray(Allocator.TempJob);
            var existing = new NativeHashMap<Entity, int>(connections.Length, Allocator.Temp);
            int maxConnectionId = 0;
            var networkIdFromEntity = GetComponentDataFromEntity<NetworkIdComponent>(true);
            for (int i = 0; i < connections.Length; ++i)
            {
                existing.TryAdd(connections[i], 1);
                int stateIndex;
                if (!m_ConnectionStateLookup.TryGetValue(connections[i], out stateIndex))
                {
                    m_ConnectionStates.Add(new ConnectionStateData
                    {
                        Entity = connections[i],
                        SerializationState =
                            new UnsafeHashMap<ArchetypeChunk, SerializationState>(1024, Allocator.Persistent),
                        ClearHistory = new UnsafeHashMap<int, uint>(256, Allocator.Persistent)
                    });
                    m_ConnectionStateLookup.TryAdd(connections[i], m_ConnectionStates.Length - 1);
                }
                maxConnectionId = math.max(maxConnectionId, networkIdFromEntity[connections[i]].Value);
            }

            connections.Dispose();

            // go through all keys in the relevancy set, +1 to the connection idx array
            var connectionRelevantCount = new NativeArray<int>(maxConnectionId+1, Allocator.TempJob);
            if (GhostRelevancyMode != GhostRelevancyMode.Disabled)
            {
                var relevancySet = m_GhostRelevancySet;
                var countRelevantHandle = Job.WithCode(() => {
                    var values = relevancySet.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < values.Length; ++i)
                    {
                        var cid = values[i].Connection;
                        connectionRelevantCount[cid] = connectionRelevantCount[cid] + 1;
                    }
                }).Schedule(GhostRelevancySetWriteHandle);
                Dependency = JobHandle.CombineDependencies(Dependency, countRelevantHandle);
            }

            for (int i = 0; i < m_ConnectionStates.Length; ++i)
            {
                int val;
                if (!existing.TryGetValue(m_ConnectionStates[i].Entity, out val))
                {
                    m_ConnectionStateLookup.Remove(m_ConnectionStates[i].Entity);
                    m_ConnectionStates[i].Dispose();
                    if (i != m_ConnectionStates.Length - 1)
                    {
                        m_ConnectionStates[i] = m_ConnectionStates[m_ConnectionStates.Length - 1];
                        m_ConnectionStateLookup.Remove(m_ConnectionStates[i].Entity);
                        m_ConnectionStateLookup.TryAdd(m_ConnectionStates[i].Entity, i);
                    }

                    m_ConnectionStates.RemoveAtSwapBack(m_ConnectionStates.Length - 1);
                    --i;
                }
            }

            // Prepare a command buffer
            EntityCommandBuffer commandBuffer = m_Barrier.CreateCommandBuffer();
            var commandBufferConcurrent = commandBuffer.AsParallelWriter();
            // Find the latest tick which has been acknowledged by all clients and cleanup all ghosts destroyed before that
            uint currentTick = m_ServerSimulation.ServerTick;

            // Find the latest tick received by all connections
            var ackedByAll = m_AckedByAllTick;
            ackedByAll[0] = currentTick;
            Entities.ForEach((in NetworkSnapshotAckComponent ack) => {
                uint ackedByAllTick = ackedByAll[0];
                var snapshot = ack.LastReceivedSnapshotByRemote;
                if (snapshot == 0)
                    ackedByAllTick = 0;
                else if (ackedByAllTick != 0 && SequenceHelpers.IsNewer(ackedByAllTick, snapshot))
                    ackedByAllTick = snapshot;
                ackedByAll[0] = ackedByAllTick;
            }).Schedule();

            // Find the highest presspawn ghost id if any
            int highestPrespawnId = 0;
            if (HasSingleton<HighestPrespawnIDAllocated>())
                highestPrespawnId = GetSingleton<HighestPrespawnIDAllocated>().GhostId;

            // Setup the tick at which ghosts were despawned, cleanup ghosts which have been despawned and acked by al connections
            var freeGhostIds = m_FreeGhostIds.AsParallelWriter();
            var prespawnDespawn = m_DestroyedPrespawnsQueue.AsParallelWriter();
            Entities
                .WithNone<GhostComponent>()
                .ForEach((Entity entity, int entityInQueryIndex, ref GhostSystemStateComponent ghost) => {
                uint ackedByAllTick = ackedByAll[0];
                if (ghost.despawnTick == 0)
                {
                    ghost.despawnTick = currentTick;
                }
                else if (ackedByAllTick != 0 && !SequenceHelpers.IsNewer(ghost.despawnTick, ackedByAllTick))
                {
                    if (ghost.ghostId > highestPrespawnId)
                        freeGhostIds.Enqueue(ghost.ghostId);
                    commandBufferConcurrent.RemoveComponent<GhostSystemStateComponent>(entityInQueryIndex, entity);
                }

                if (ghost.ghostId <= highestPrespawnId)
                    prespawnDespawn.Enqueue(ghost.ghostId);
            }).ScheduleParallel();

            // Copy destroyed entities in the parallel write queue populated by ghost cleanup to a single list
            var despawnQueue = m_DestroyedPrespawnsQueue;
            var despawnList = m_DestroyedPrespawns;
            Job.WithCode(() => {
                if (despawnQueue.TryDequeue(out int destroyed))
                {
                    if (!despawnList.Contains(destroyed))
                        despawnList.Add(destroyed);
                }
            }).Schedule();

            // Calculate how many state updates we should send this frame
            var tickRate = default(ClientServerTickRate);
            if (HasSingleton<ClientServerTickRate>())
            {
                tickRate = GetSingleton<ClientServerTickRate>();
            }

            tickRate.ResolveDefaults();
            var netTickInterval =
                (tickRate.SimulationTickRate + tickRate.NetworkTickRate - 1) / tickRate.NetworkTickRate;
            var sendPerFrame = (m_ConnectionStates.Length + netTickInterval - 1) / netTickInterval;
            var sendStartPos = sendPerFrame * (int) (currentTick % netTickInterval);

            if (sendStartPos + sendPerFrame > m_ConnectionStates.Length)
                sendPerFrame = m_ConnectionStates.Length - sendStartPos;
            if (sendPerFrame > 0)
            {
                // Initialize the distance scale function
                var distanceScaleFunction = m_NoDistanceScale;
                var tileSize = default(int3);
                var tileCenter = default(int3);
                if (HasSingleton<GhostDistanceImportance>())
                {
                    var config = GetSingleton<GhostDistanceImportance>();
                    distanceScaleFunction = config.ScaleImportanceByDistance;
                    tileSize = config.TileSize;
                    tileCenter = config.TileCenter;
                }

                // Generate a lookup from child entity to chunk + index
                m_ChildEntityLookup.Clear();
                var childCount = m_ChildEntityQuery.CalculateEntityCountWithoutFiltering();
                if (childCount > m_ChildEntityLookup.Capacity)
                    m_ChildEntityLookup.Capacity = childCount;
                var buildChildJob = new BuildChildEntityLookupJob
                {
                    entityType = GetEntityTypeHandle(),
                    childEntityLookup = m_ChildEntityLookup.AsParallelWriter()
                };
                Dependency = buildChildJob.ScheduleParallel(m_ChildEntityQuery, Dependency);

                // Get component types for serialization
                var entityType = GetEntityTypeHandle();
                var ghostSystemStateType = GetComponentTypeHandle<GhostSystemStateComponent>(true);
                var ghostSimpleDeltaCompressionType = GetComponentTypeHandle<GhostSimpleDeltaCompression>(true);
                var ghostComponentType = GetComponentTypeHandle<GhostComponent>();
                var ghostChildEntityComponentType = GetComponentTypeHandle<GhostChildEntityComponent>(true);
                var ghostGroupType = GetBufferTypeHandle<GhostGroup>(true);
                var ghostOwnerComponentType = GetComponentTypeHandle<GhostOwnerComponent>(true);

                // Extract all newly spawned ghosts and set their ghost ids
                m_SerialSpawnChunks.Clear();
                JobHandle spawnChunkHandle;
                var spawnChunks = ghostSpawnGroup.CreateArchetypeChunkArrayAsync(Allocator.TempJob, out spawnChunkHandle);
                var spawnJob = new SpawnGhostJob
                {
                    GhostTypeCollection = m_GhostCollectionSystem.m_GhostTypeCollection,
                    spawnChunks = spawnChunks,
                    serialSpawnChunks = m_SerialSpawnChunks,
                    entityType = entityType,
                    ghostComponentType = ghostComponentType,
                    ghostChildEntityComponentType = ghostChildEntityComponentType,
                    freeGhostIds = m_FreeGhostIds,
                    allocatedGhostIds = m_AllocatedGhostIds,
                    commandBuffer = commandBuffer,
                    ghostTypeFromEntity = GetComponentDataFromEntity<GhostTypeComponent>(true),
                    ghostPrefabBufferFromEntity = GetBufferFromEntity<GhostPrefabBuffer>(true),
                    serverPrefabEntity = GetSingleton<GhostPrefabCollectionComponent>().serverPrefabs,
                    serverTick = currentTick,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    ghostOwnerComponentType = ghostOwnerComponentType
#endif
                };
                Dependency = spawnJob.Schedule(JobHandle.CombineDependencies(Dependency, spawnChunkHandle));

                // Create chunk arrays for ghosts and despawned ghosts
                JobHandle despawnChunksHandle, ghostChunksHandle;
                var despawnChunks = ghostDespawnGroup.CreateArchetypeChunkArrayAsync(Allocator.TempJob, out despawnChunksHandle);
                var ghostChunks = ghostGroup.CreateArchetypeChunkArrayAsync(Allocator.TempJob, out ghostChunksHandle);
                Dependency = JobHandle.CombineDependencies(Dependency, despawnChunksHandle, ghostChunksHandle);

                // If there are any connections to send data to, serilize the data fo them in parallel
                var serializeJob = new SerializeJob
                {
                    GhostComponentCollection = m_GhostCollectionSystem.m_GhostComponentCollection,
                    GhostTypeCollection = m_GhostCollectionSystem.m_GhostTypeCollection,
                    GhostComponentIndex = m_GhostCollectionSystem.m_GhostComponentIndex,
                    driver = m_ReceiveSystem.ConcurrentDriver,
                    unreliablePipeline = m_ReceiveSystem.UnreliablePipeline,
                    unreliableFragmentedPipeline = m_ReceiveSystem.UnreliableFragmentedPipeline,
                    despawnChunks = despawnChunks,
                    ghostChunks = ghostChunks,
                    connectionState = ((NativeArray<ConnectionStateData>)m_ConnectionStates).GetSubArray(sendStartPos, sendPerFrame),
                    ackFromEntity = GetComponentDataFromEntity<NetworkSnapshotAckComponent>(true),
                    connectionFromEntity = GetComponentDataFromEntity<NetworkStreamConnection>(true),
                    networkIdFromEntity = networkIdFromEntity,
                    serialSpawnChunks = m_SerialSpawnChunks,
                    entityType = entityType,
                    ghostSystemStateType = ghostSystemStateType,
                    ghostComponentType = ghostComponentType,
                    ghostGroupType = ghostGroupType,
                    ghostChildEntityComponentType = ghostChildEntityComponentType,
                    relevantGhostForConnection = GhostRelevancySet,
                    relevancyMode = GhostRelevancyMode,
                    relevantGhostCountForConnection = connectionRelevantCount,
                    ghostSimpleDeltaCompressionType = ghostSimpleDeltaCompressionType,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    netStatsBuffer = m_NetStats,
                    netStatSize = netStatSize,
                    netStatStride = netStatStride,
#endif
                    compressionModel = m_CompressionModel,
                    ghostFromEntity = GetComponentDataFromEntity<GhostComponent>(true),
                    currentTick = currentTick,
                    localTime = NetworkTimeSystem.TimestampMS,
                    scaleGhostImportanceByDistance = distanceScaleFunction,
                    tileSize = tileSize,
                    tileCenter = tileCenter,
                    ghostDistancePartitionType = GetComponentTypeHandle<GhostDistancePartition>(true),
                    ghostConnectionPositionFromEntity = GetComponentDataFromEntity<GhostConnectionPosition>(true),
                    snapshotTargetSizeFromEntity = GetComponentDataFromEntity<NetworkStreamSnapshotTargetSize>(true),
                    prespawnDespawns = m_DestroyedPrespawns,
                    commandBuffer = commandBufferConcurrent,
                    ghostOwnerType = ghostOwnerComponentType,
                    childEntityLookup = m_ChildEntityLookup,
                    linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(true),
                    CurrentSystemVersion = GlobalSystemVersion,
                    CurrentGhostTypeVersion = m_GhostCollectionSystem.GhostTypeCollectionHash
                };

                Dependency = JobHandle.CombineDependencies(Dependency, m_ReceiveSystem.LastDriverWriter, GhostRelevancySetWriteHandle);
                var listLength = m_GhostCollectionSystem.m_GhostComponentCollection.Length;
                if (listLength <= 32)
                {
                    var dynamicListJob = new SerializeJob32 {Job = serializeJob};
                    DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                    Dependency = dynamicListJob.Schedule(sendPerFrame, 1, Dependency);
                }
                else if (listLength <= 64)
                {
                    var dynamicListJob = new SerializeJob64 {Job = serializeJob};
                    DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                    Dependency = dynamicListJob.Schedule(sendPerFrame, 1, Dependency);
                }
                else if (listLength <= 128)
                {
                    var dynamicListJob = new SerializeJob128 {Job = serializeJob};
                    DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                    Dependency = dynamicListJob.Schedule(sendPerFrame, 1, Dependency);
                }
                else
                    throw new InvalidOperationException(
                        $"Too many ghost component types present in project, limit is {DynamicTypeList.MaxCapacity} types. This is any struct which has a field marked with GhostField attribute.");
                GhostRelevancySetWriteHandle = Dependency;
                Dependency = m_ReceiveSystem.Driver.ScheduleFlushSend(Dependency);
                m_ReceiveSystem.LastDriverWriter = Dependency;

                despawnChunks.Dispose(Dependency);
                spawnChunks.Dispose(Dependency);
                ghostChunks.Dispose(Dependency);
            }
            connectionRelevantCount.Dispose(Dependency);
            // Only the spawn job is using the commandBuffer, but the serialize job is using the same chunks - so we must wait for that too before we can modify them
            m_Barrier.AddJobHandleForProducer(Dependency);
        }

        unsafe struct PrioChunk : IComparable<PrioChunk>
        {
            public ArchetypeChunk chunk;
            public int priority;
            public int startIndex;
            public int ghostType;

            public int CompareTo(PrioChunk other)
            {
                // Reverse priority for sorting
                return other.priority - priority;
            }
        }

        // Set the allocation ghost ID, increments what the next ID should be if appropriate,
        // next ID can never go backwards
        public bool SetAllocatedGhostId(int id)
        {
            if (id >= m_AllocatedGhostIds[0])
            {
                m_AllocatedGhostIds[0] = id + 1;
                return true;
            }
            return false;
        }
    }
}

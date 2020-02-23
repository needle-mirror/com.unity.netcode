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

namespace Unity.NetCode
{
    // FIXME: should be internal
    public struct GhostSystemStateComponent : ISystemStateComponentData
    {
        public int ghostId;
        public int ghostTypeIndex;
        public uint despawnTick;
    }

    public unsafe struct SnapshotBaseline
    {
        public uint tick;
        public byte* snapshot;
        public Entity* entity;
    }

    public unsafe struct SerializeData
    {
        public int ghostType;
        internal ArchetypeChunk chunk;
        internal int startIndex;
        internal uint currentTick;
        internal Entity* currentSnapshotEntity;
        internal void* currentSnapshotData;
        internal GhostComponent* ghosts;
        internal GhostSystemStateComponent* ghostStates;
        internal NativeArray<Entity> ghostEntities;
        internal NativeArray<int> baselinePerEntity;
        internal NativeList<SnapshotBaseline> availableBaselines;
        internal NetworkCompressionModel compressionModel;
        internal GhostSerializerState serializerState;
    }

    public struct GhostSerializerState
    {
        // TODO: figure out a way to use the GhostComponent, but the despawn tick is required
        public ComponentDataFromEntity<GhostSystemStateComponent> GhostStateFromEntity;
        public int NetworkId;
    }

    internal struct GhostSystemConstants
    {
        public const int SnapshotHistorySize = 32;
    }

    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    [UpdateAfter(typeof(TransformSystemGroup))]
    public class GhostSendSystem<TGhostSerializerCollection> : JobComponentSystem
        where TGhostSerializerCollection : struct, IGhostSerializerCollection
    {
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

            public void AllocateSnapshotData(int serializerDataSize, int chunkCapacity)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                allocatedChunkCapacity = chunkCapacity;
                allocatedDataSize = serializerDataSize;
#endif
                snapshotData = (byte*) UnsafeUtility.Malloc(
                    SerializationState.CalculateSize(serializerDataSize, chunkCapacity), 16, Allocator.Persistent);

                // Just clear snapshot index
                UnsafeUtility.MemClear(snapshotData,
                    UnsafeUtility.SizeOf<int>() * GhostSystemConstants.SnapshotHistorySize);
            }

            public void FreeSnapshotData()
            {
                UnsafeUtility.Free(snapshotData, Allocator.Persistent);
                snapshotData = null;
            }

            public static int CalculateSize(int serializerDataSize, int chunkCapacity)
            {
                int snapshotIndexSize = (UnsafeUtility.SizeOf<uint>() * GhostSystemConstants.SnapshotHistorySize + 15) &
                                        (~15);
                int entitySize = (UnsafeUtility.SizeOf<Entity>() * chunkCapacity + 15) & (~15);
                int dataSize = (serializerDataSize * chunkCapacity + 15) & (~15);
                return snapshotIndexSize + GhostSystemConstants.SnapshotHistorySize * (entitySize + dataSize);
            }

            public uint* GetSnapshotIndex()
            {
                return (uint*) snapshotData;
            }

            public Entity* GetEntity(int serializerDataSize, int chunkCapacity, int historyPosition)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (historyPosition < 0 || historyPosition >= GhostSystemConstants.SnapshotHistorySize)
                    throw new IndexOutOfRangeException("Reading invalid history position");
                if (serializerDataSize != allocatedDataSize || chunkCapacity != allocatedChunkCapacity)
                    throw new IndexOutOfRangeException("Chunk capacity or data size changed");
#endif
                int snapshotIndexSize = (UnsafeUtility.SizeOf<uint>() * GhostSystemConstants.SnapshotHistorySize + 15) &
                                        (~15);
                int entitySize = (UnsafeUtility.SizeOf<Entity>() * chunkCapacity + 15) & (~15);
                int dataSize = (serializerDataSize * chunkCapacity + 15) & (~15);
                return (Entity*) (snapshotData + snapshotIndexSize + historyPosition * (entitySize + dataSize));
            }

            public byte* GetData(int serializerDataSize, int chunkCapacity, int historyPosition)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (historyPosition < 0 || historyPosition >= GhostSystemConstants.SnapshotHistorySize)
                    throw new IndexOutOfRangeException("Reading invalid history position");
                if (serializerDataSize != allocatedDataSize || chunkCapacity != allocatedChunkCapacity)
                    throw new IndexOutOfRangeException("Chunk capacity or data size changed");
#endif
                int snapshotIndexSize = (UnsafeUtility.SizeOf<uint>() * GhostSystemConstants.SnapshotHistorySize + 15) &
                                        (~15);
                int entitySize = (UnsafeUtility.SizeOf<Entity>() * chunkCapacity + 15) & (~15);
                int dataSize = (serializerDataSize * chunkCapacity + 15) & (~15);
                return (snapshotData + snapshotIndexSize + entitySize + historyPosition * (entitySize + dataSize));
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
            }

            public Entity Entity;
            public NativeHashMap<ArchetypeChunk, SerializationState> SerializationState;
        }

        private EntityQuery ghostGroup;
        private EntityQuery ghostSpawnGroup;
        private EntityQuery ghostDespawnGroup;

        private EntityQuery connectionGroup;

        private TGhostSerializerCollection serializers;

        private NativeQueue<int> m_FreeGhostIds;
        private NativeArray<int> m_AllocatedGhostIds;

        private List<ConnectionStateData> m_ConnectionStates;
        private NativeHashMap<Entity, int> m_ConnectionStateLookup;
        private NetworkCompressionModel m_CompressionModel;

        private NativeList<PrioChunk> m_SerialSpawnChunks;

        private const int TargetPacketSize = 1200;

        private ServerSimulationSystemGroup m_ServerSimulation;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private NetworkStreamReceiveSystem m_ReceiveSystem;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private NativeArray<uint> m_NetStats;
        private GhostStatsCollectionSystem m_StatsCollection;
#endif

        private PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate> m_NoDistanceScale;

        protected override void OnCreate()
        {
            m_NoDistanceScale = GhostDistanceImportance.NoScaleFunctionPointer;
            serializers = default(TGhostSerializerCollection);
            ghostGroup = GetEntityQuery(typeof(GhostComponent), typeof(GhostSystemStateComponent));
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

            connectionGroup = GetEntityQuery(
                ComponentType.ReadWrite<NetworkStreamConnection>(),
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());

            m_ServerSimulation = World.GetExistingSystem<ServerSimulationSystemGroup>();
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_ReceiveSystem = World.GetOrCreateSystem<NetworkStreamReceiveSystem>();

            m_ConnectionStates = new List<ConnectionStateData>(256);
            m_ConnectionStateLookup = new NativeHashMap<Entity, int>(256, Allocator.Persistent);
            m_CompressionModel = new NetworkCompressionModel(Allocator.Persistent);

            m_SerialSpawnChunks = new NativeList<PrioChunk>(1024, Allocator.Persistent);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats = new NativeArray<uint>(serializers.Length * 3 + 3 + 1, Allocator.Persistent);
            m_StatsCollection = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif

            RequireSingletonForUpdate<GhostPrefabCollectionComponent>();
        }

        protected override void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats.Dispose();
#endif
            m_SerialSpawnChunks.Dispose();
            m_CompressionModel.Dispose();
            m_AllocatedGhostIds.Dispose();
            m_FreeGhostIds.Dispose();
            foreach (var connectionState in m_ConnectionStates)
            {
                connectionState.Dispose();
            }

            m_ConnectionStateLookup.Dispose();
        }

        [BurstCompile]
        struct FindAckedByAllJob : IJobForEach<NetworkSnapshotAckComponent>
        {
            public NativeArray<uint> tick;

            public void Execute([ReadOnly] ref NetworkSnapshotAckComponent ack)
            {
                uint ackedByAllTick = tick[0];
                var snapshot = ack.LastReceivedSnapshotByRemote;
                if (snapshot == 0)
                    ackedByAllTick = 0;
                else if (ackedByAllTick != 0 && SequenceHelpers.IsNewer(ackedByAllTick, snapshot))
                    ackedByAllTick = snapshot;
                tick[0] = ackedByAllTick;
            }
        }

        [BurstCompile]
        [ExcludeComponent(typeof(GhostComponent))]
        struct CleanupGhostJob : IJobForEachWithEntity<GhostSystemStateComponent>
        {
            public uint currentTick;
            [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<uint> tick;
            public EntityCommandBuffer.Concurrent commandBuffer;
            public NativeQueue<int>.ParallelWriter freeGhostIds;
            public ComponentType ghostStateType;

            public void Execute(Entity entity, int index, ref GhostSystemStateComponent ghost)
            {
                uint ackedByAllTick = tick[0];
                if (ghost.despawnTick == 0)
                {
                    ghost.despawnTick = currentTick;
                }
                else if (ackedByAllTick != 0 && !SequenceHelpers.IsNewer(ghost.despawnTick, ackedByAllTick))
                {
                    freeGhostIds.Enqueue(ghost.ghostId);
                    commandBuffer.RemoveComponent(index, entity, ghostStateType);
                }
            }
        }

        [BurstCompile]
        struct SpawnGhostJob : IJob
        {
            [ReadOnly] public NativeArray<ArchetypeChunk> spawnChunks;
            public NativeList<PrioChunk> serialSpawnChunks;
            [ReadOnly] public ArchetypeChunkEntityType entityType;
            public ArchetypeChunkComponentType<GhostComponent> ghostComponentType;
            public TGhostSerializerCollection serializers;
            public NativeQueue<int> freeGhostIds;
            public NativeArray<int> allocatedGhostIds;
            public EntityCommandBuffer commandBuffer;

            [ReadOnly] public ComponentDataFromEntity<GhostTypeComponent> ghostTypeFromEntity;
            [ReadOnly] public BufferFromEntity<GhostPrefabBuffer> ghostPrefabBufferFromEntity;
            public Entity serverPrefabEntity;

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
                    var ghostState = (GhostSystemStateComponent*) UnsafeUtility.Malloc(
                        UnsafeUtility.SizeOf<GhostSystemStateComponent>() * entities.Length,
                        UnsafeUtility.AlignOf<GhostSystemStateComponent>(), Allocator.TempJob);
                    var ghosts = spawnChunks[chunk].GetNativeArray(ghostComponentType);
                    for (var ent = 0; ent < entities.Length; ++ent)
                    {
                        int newId;
                        if (!freeGhostIds.TryDequeue(out newId))
                        {
                            newId = allocatedGhostIds[0];
                            allocatedGhostIds[0] = newId + 1;
                        }

                        ghosts[ent] = new GhostComponent {ghostId = newId};
                        ghostState[ent] = new GhostSystemStateComponent
                        {
                            ghostId = newId, ghostTypeIndex = ghostType, despawnTick = 0
                        };

                        commandBuffer.AddComponent(entities[ent], ghostState[ent]);
                    }

                    var pc = new PrioChunk
                    {
                        chunk = spawnChunks[chunk],
                        ghostState = ghostState,
                        priority = serializers.CalculateImportance(ghostType,
                            spawnChunks[chunk]), // Age is always 1 for new chunks
                        startIndex = 0,
                        ghostType = ghostType
                    };
                    serialSpawnChunks.Add(pc);
                }
            }
        }

        [BurstCompile]
        struct SerializeJob : IJob
        {
            public NetworkDriver.Concurrent driver;
            public NetworkPipeline unreliablePipeline;

            [ReadOnly] public NativeArray<ArchetypeChunk> despawnChunks;
            [ReadOnly] public NativeArray<ArchetypeChunk> ghostChunks;

            public Entity connectionEntity;
            public NativeHashMap<ArchetypeChunk, SerializationState> chunkSerializationData;
            [ReadOnly] public ComponentDataFromEntity<NetworkSnapshotAckComponent> ackFromEntity;
            [ReadOnly] public ComponentDataFromEntity<NetworkStreamConnection> connectionFromEntity;
            [ReadOnly] public ComponentDataFromEntity<NetworkIdComponent> networkIdFromEntity;

            [ReadOnly] public NativeList<PrioChunk> serialSpawnChunks;

            [ReadOnly] public ArchetypeChunkEntityType entityType;
            [ReadOnly] public ArchetypeChunkComponentType<GhostComponent> ghostComponentType;
            [ReadOnly] public ArchetypeChunkComponentType<GhostSystemStateComponent> ghostSystemStateType;
            [ReadOnly] public ArchetypeChunkComponentType<GhostSimpleDeltaCompression> ghostSimpleDeltaCompressionType;


            [ReadOnly] public TGhostSerializerCollection serializers;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif
            [ReadOnly] public NetworkCompressionModel compressionModel;

            [ReadOnly] public ComponentDataFromEntity<GhostSystemStateComponent> ghostFromEntity;

            public uint currentTick;
            public uint localTime;

            public PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate> scaleGhostImportanceByDistance;

            public int3 tileSize;
            public int3 tileCenter;
            [ReadOnly] public ArchetypeChunkComponentType<GhostDistancePartition> ghostDistancePartitionType;
            [ReadOnly] public ComponentDataFromEntity<GhostConnectionPosition> ghostConnectionPositionFromEntity;


            public unsafe void Execute()
            {
                var serializerState = new GhostSerializerState
                {
                    GhostStateFromEntity = ghostFromEntity,
                    NetworkId = networkIdFromEntity[connectionEntity].Value
                };
                var snapshotAck = ackFromEntity[connectionEntity];
                var ackTick = snapshotAck.LastReceivedSnapshotByRemote;

                DataStreamWriter dataStream = driver.BeginSend(unreliablePipeline, connectionFromEntity[connectionEntity].Value);
                if (!dataStream.IsCreated)
                    return;
                dataStream.WriteByte((byte) NetworkStreamProtocol.Snapshot);

                dataStream.WriteUInt(localTime);
                uint returnTime = snapshotAck.LastReceivedRemoteTime;
                if (returnTime != 0)
                    returnTime -= (localTime - snapshotAck.LastReceiveTimestamp);
                dataStream.WriteUInt(returnTime);
                dataStream.WriteInt(snapshotAck.ServerCommandAge);

                dataStream.WriteUInt(currentTick);

                int entitySize = UnsafeUtility.SizeOf<Entity>();

                var lenWriter = dataStream;
                dataStream.WriteUInt((uint) 0);
                dataStream.WriteUInt((uint) 0);
                uint despawnLen = 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                int startPos = dataStream.LengthInBits;
#endif
                // TODO: if not all despawns fit, sort them based on age and maybe time since last send
                // TODO: only resend despawn on nack
                // FIXME: the TargetPacketSize cannot be used since CleanupGhostJob relies on all ghosts being sent every frame
                for (var chunk = 0; chunk < despawnChunks.Length /*&& dataStream.Length < TargetPacketSize*/; ++chunk)
                {
                    var entities = despawnChunks[chunk].GetNativeArray(entityType);
                    var ghostStates = despawnChunks[chunk].GetNativeArray(ghostSystemStateType);
                    for (var ent = 0; ent < entities.Length /*&& dataStream.Length < TargetPacketSize*/; ++ent)
                    {
                        if (ackTick == 0 || SequenceHelpers.IsNewer(ghostStates[ent].despawnTick, ackTick))
                        {
                            dataStream.WritePackedUInt((uint) ghostStates[ent].ghostId, compressionModel);
                            ++despawnLen;
                        }
                    }
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats[0] = currentTick;
                netStats[1] = despawnLen;
                netStats[2] = (uint) (dataStream.LengthInBits - startPos);
                netStats[3] = 0;
                startPos = dataStream.LengthInBits;
#endif

                uint updateLen = 0;
                var serialChunks =
                    new NativeList<PrioChunk>(ghostChunks.Length + serialSpawnChunks.Length, Allocator.Temp);
                serialChunks.AddRange(serialSpawnChunks);
                var existingChunks = new NativeHashMap<ArchetypeChunk, int>(ghostChunks.Length, Allocator.Temp);
                int maxCount = 0;
                for (int chunk = 0; chunk < serialSpawnChunks.Length; ++chunk)
                {
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
                        var ghostStates = ghostChunks[chunk].GetNativeArray(ghostSystemStateType);
                        chunkState.lastUpdate = currentTick - 1;
                        chunkState.startIndex = 0;
                        chunkState.ghostType = ghostStates[0].ghostTypeIndex;
                        chunkState.arch = ghostChunks[chunk].Archetype;

                        chunkState.snapshotWriteIndex = 0;
                        int serializerDataSize = serializers.GetSnapshotSize(chunkState.ghostType);
                        chunkState.AllocateSnapshotData(serializerDataSize, ghostChunks[chunk].Capacity);

                        chunkSerializationData.TryAdd(ghostChunks[chunk], chunkState);
                    }

                    var partitionArray = ghostChunks[chunk].GetNativeArray(ghostDistancePartitionType);
                    existingChunks.TryAdd(ghostChunks[chunk], 1);
                    // FIXME: only if modified or force sync
                    var ghostType = chunkState.ghostType;
                    var chunkPriority = (serializers.CalculateImportance(ghostType, ghostChunks[chunk]) *
                                         (int) (currentTick - chunkState.lastUpdate));
                    if (partitionArray.Length > 0 && ghostConnectionPositionFromEntity.Exists(connectionEntity))
                    {
                        var connectionPosition = ghostConnectionPositionFromEntity[connectionEntity];
                        int3 chunkTile = partitionArray[0].Index;
                        chunkPriority = scaleGhostImportanceByDistance.Ptr.Invoke(ref connectionPosition, ref tileSize, ref tileCenter, ref chunkTile, chunkPriority);
                    }

                    var pc = new PrioChunk
                    {
                        chunk = ghostChunks[chunk],
                        ghostState = null,
                        priority = chunkPriority,
                        startIndex = chunkState.startIndex,
                        ghostType = ghostType
                    };
                    serialChunks.Add(pc);
                    if (ghostChunks[chunk].Count > maxCount)
                        maxCount = ghostChunks[chunk].Count;
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
                var availableBaselines =
                    new NativeList<SnapshotBaseline>(GhostSystemConstants.SnapshotHistorySize, Allocator.Temp);
                var baselinePerEntity = new NativeArray<int>(maxCount * 3, Allocator.Temp);
                for (int pc = 0; pc < serialChunks.Length && dataStream.Length < TargetPacketSize; ++pc)
                {
                    var chunk = serialChunks[pc].chunk;
                    var ghostType = serialChunks[pc].ghostType;

                    Entity* currentSnapshotEntity = null;
                    byte* currentSnapshotData = null;
                    SerializationState chunkState;
                    int dataSize = 0;
                    availableBaselines.Clear();
                    if (chunkSerializationData.TryGetValue(chunk, out chunkState))
                    {
                        dataSize = serializers.GetSnapshotSize(chunkState.ghostType);

                        uint* snapshotIndex = chunkState.GetSnapshotIndex();
                        snapshotIndex[chunkState.snapshotWriteIndex] = currentTick;
                        int baseline = (GhostSystemConstants.SnapshotHistorySize + chunkState.snapshotWriteIndex - 1) %
                                       GhostSystemConstants.SnapshotHistorySize;
                        while (baseline != chunkState.snapshotWriteIndex)
                        {
                            if (snapshotAck.IsReceivedByRemote(snapshotIndex[baseline]))
                            {
                                availableBaselines.Add(new SnapshotBaseline
                                {
                                    tick = snapshotIndex[baseline],
                                    snapshot = chunkState.GetData(dataSize, chunk.Capacity, baseline),
                                    entity = chunkState.GetEntity(dataSize, chunk.Capacity, baseline)
                                });
                            }

                            baseline = (GhostSystemConstants.SnapshotHistorySize + baseline - 1) %
                                       GhostSystemConstants.SnapshotHistorySize;
                        }

                        // Find the acked snapshot to delta against, setup pointer to current and previous entity* and data*
                        // Remember to bump writeIndex when done
                        currentSnapshotData =
                            chunkState.GetData(dataSize, chunk.Capacity, chunkState.snapshotWriteIndex);
                        currentSnapshotEntity =
                            chunkState.GetEntity(dataSize, chunk.Capacity, chunkState.snapshotWriteIndex);
                    }

                    var ghostState = serialChunks[pc].ghostState;
                    if (ghostState == null)
                    {
                        ghostState = (GhostSystemStateComponent*) chunk.GetNativeArray(ghostSystemStateType)
                            .GetUnsafeReadOnlyPtr();
                    }

                    var ghost = (GhostComponent*) chunk.GetNativeArray(ghostComponentType)
                        .GetUnsafeReadOnlyPtr();

                    var ghostEntities = chunk.GetNativeArray(entityType);
                    int ent;
                    if (serialChunks[pc].startIndex < chunk.Count)
                    {
                        dataStream.WritePackedUInt((uint) ghostType, compressionModel);
                        dataStream.WritePackedUInt((uint) (chunk.Count - serialChunks[pc].startIndex),
                            compressionModel);
                    }

                    // First figure out the baselines to use per entity so they can be sent as baseline + maxCount instead of one per entity
                    // Ghosts tagged with "single baseline" should set this to 1 to disable delta prediction
                    int targetBaselines = serialChunks[pc].chunk.Has(ghostSimpleDeltaCompressionType) ? 1 : 3;
                    for (ent = serialChunks[pc].startIndex; ent < chunk.Count; ++ent)
                    {
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

                    var data = new SerializeData
                    {
                        ghostType = ghostType,
                        chunk = chunk,
                        startIndex = serialChunks[pc].startIndex,
                        currentTick = currentTick,
                        currentSnapshotEntity = currentSnapshotEntity,
                        currentSnapshotData = currentSnapshotData,
                        ghosts = ghost,
                        ghostStates = ghostState,
                        ghostEntities = ghostEntities,
                        baselinePerEntity = baselinePerEntity,
                        availableBaselines = availableBaselines,
                        compressionModel = compressionModel,
                        serializerState = serializerState
                    };

                    ent = serializers.Serialize(ref dataStream, data);
                    updateLen += (uint) (ent - serialChunks[pc].startIndex);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    netStats[ghostType*3 + 4] = netStats[ghostType*3 + 4] + (uint)(ent - serialChunks[pc].startIndex);
                    netStats[ghostType*3 + 5] = netStats[ghostType*3 + 5] + (uint) (dataStream.LengthInBits - startPos);
                    // FIXME: support uncompressed count
                    //netStats[ghostType*3 + 6] = netStats[ghostType*3 + 6] + 0;
                    startPos = dataStream.LengthInBits;
#endif

                    // Spawn chunks are temporary and should not be added to the state data cache
                    if (serialChunks[pc].ghostState == null)
                    {
                        // Only append chunks which contain data
                        if (ent > serialChunks[pc].startIndex)
                        {
                            if (serialChunks[pc].startIndex > 0)
                                UnsafeUtility.MemClear(currentSnapshotEntity, entitySize * serialChunks[pc].startIndex);
                            if (ent < chunk.Capacity)
                                UnsafeUtility.MemClear(currentSnapshotEntity + ent,
                                    entitySize * (chunk.Capacity - ent));
                            chunkState.snapshotWriteIndex =
                                (chunkState.snapshotWriteIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
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

                        chunkSerializationData.Remove(chunk);
                        chunkSerializationData.TryAdd(chunk, chunkState);
                    }
                }

                dataStream.Flush();
                lenWriter.WriteUInt(despawnLen);
                lenWriter.WriteUInt(updateLen);

                driver.EndSend(dataStream);
            }
        }

        [BurstCompile]
        struct CleanupJob : IJob
        {
            [DeallocateOnJobCompletion] public NativeArray<ArchetypeChunk> despawnChunks;
            [DeallocateOnJobCompletion] public NativeArray<ArchetypeChunk> spawnChunks;
            [DeallocateOnJobCompletion] public NativeArray<ArchetypeChunk> ghostChunks;
            public NativeList<PrioChunk> serialSpawnChunks;

            public unsafe void Execute()
            {
                for (int i = 0; i < serialSpawnChunks.Length; ++i)
                {
                    UnsafeUtility.Free(serialSpawnChunks[i].ghostState, Allocator.TempJob);
                }
            }
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_StatsCollection.CurrentNameOwner != this)
                m_StatsCollection.SetGhostNames(this, serializers.CreateSerializerNameList());
            m_StatsCollection.AddSnapshotStats(m_NetStats);
            for (int i = 0; i < m_NetStats.Length; ++i)
            {
                m_NetStats[i] = 0;
            }
#endif
            m_SerialSpawnChunks.Clear();
            // Make sure the list of connections and connection state is up to date
            var connections = connectionGroup.ToEntityArray(Allocator.TempJob);
            var existing = new NativeHashMap<Entity, int>(connections.Length, Allocator.Temp);
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
                            new NativeHashMap<ArchetypeChunk, SerializationState>(1024, Allocator.Persistent)
                    });
                    m_ConnectionStateLookup.TryAdd(connections[i], m_ConnectionStates.Count - 1);
                }
            }

            connections.Dispose();

            for (int i = 0; i < m_ConnectionStates.Count; ++i)
            {
                int val;
                if (!existing.TryGetValue(m_ConnectionStates[i].Entity, out val))
                {
                    m_ConnectionStateLookup.Remove(m_ConnectionStates[i].Entity);
                    m_ConnectionStates[i].Dispose();
                    if (i != m_ConnectionStates.Count - 1)
                    {
                        m_ConnectionStates[i] = m_ConnectionStates[m_ConnectionStates.Count - 1];
                        m_ConnectionStateLookup.Remove(m_ConnectionStates[i].Entity);
                        m_ConnectionStateLookup.TryAdd(m_ConnectionStates[i].Entity, i);
                    }

                    m_ConnectionStates.RemoveAt(m_ConnectionStates.Count - 1);
                    --i;
                }
            }

            // Find the latest tick which has been acknowledged by all clients and cleanup all ghosts destroyed ebfore that
            uint currentTick = m_ServerSimulation.ServerTick;

            var ackedByAll = new NativeArray<uint>(1, Allocator.TempJob);
            ackedByAll[0] = currentTick;
            var findAckJob = new FindAckedByAllJob
            {
                tick = ackedByAll
            };
            inputDeps = findAckJob.ScheduleSingle(this, inputDeps);

            EntityCommandBuffer commandBuffer = m_Barrier.CreateCommandBuffer();
            var ghostCleanupJob = new CleanupGhostJob
            {
                currentTick = currentTick,
                tick = ackedByAll,
                commandBuffer = commandBuffer.ToConcurrent(),
                freeGhostIds = m_FreeGhostIds.AsParallelWriter(),
                ghostStateType = ComponentType.ReadWrite<GhostSystemStateComponent>()
            };
            inputDeps = ghostCleanupJob.Schedule(this, inputDeps);


            var entityType = GetArchetypeChunkEntityType();
            var ghostSystemStateType = GetArchetypeChunkComponentType<GhostSystemStateComponent>(true);
            var ghostSimpleDeltaCompressionType = GetArchetypeChunkComponentType<GhostSimpleDeltaCompression>(true);
            var ghostComponentType = GetArchetypeChunkComponentType<GhostComponent>();
            serializers.BeginSerialize(this);

            // Extract all newly spawned ghosts and set their ghost ids
            JobHandle spawnChunkHandle;
            var spawnChunks = ghostSpawnGroup.CreateArchetypeChunkArrayAsync(Allocator.TempJob, out spawnChunkHandle);
            var spawnJob = new SpawnGhostJob
            {
                spawnChunks = spawnChunks,
                serialSpawnChunks = m_SerialSpawnChunks,
                entityType = entityType,
                ghostComponentType = ghostComponentType,
                serializers = serializers,
                freeGhostIds = m_FreeGhostIds,
                allocatedGhostIds = m_AllocatedGhostIds,
                commandBuffer = commandBuffer,
                ghostTypeFromEntity = GetComponentDataFromEntity<GhostTypeComponent>(true),
                ghostPrefabBufferFromEntity = GetBufferFromEntity<GhostPrefabBuffer>(true),
                serverPrefabEntity = GetSingleton<GhostPrefabCollectionComponent>().serverPrefabs
            };
            inputDeps = spawnJob.Schedule(JobHandle.CombineDependencies(inputDeps, spawnChunkHandle));

            JobHandle despawnChunksHandle, ghostChunksHandle;
            var despawnChunks = ghostDespawnGroup.CreateArchetypeChunkArrayAsync(Allocator.TempJob, out despawnChunksHandle);
            var ghostChunks = ghostGroup.CreateArchetypeChunkArrayAsync(Allocator.TempJob, out ghostChunksHandle);
            inputDeps = JobHandle.CombineDependencies(inputDeps, despawnChunksHandle, ghostChunksHandle);

            var tickRate = default(ClientServerTickRate);
            if (HasSingleton<ClientServerTickRate>())
            {
                tickRate = GetSingleton<ClientServerTickRate>();
            }

            tickRate.ResolveDefaults();
            var netTickInterval =
                (tickRate.SimulationTickRate + tickRate.NetworkTickRate - 1) / tickRate.NetworkTickRate;
            var sendPerFrame = (m_ConnectionStates.Count + netTickInterval - 1) / netTickInterval;
            var sendStartPos = sendPerFrame * (int) (currentTick % netTickInterval);

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


            var serialDep = new NativeArray<JobHandle>(sendPerFrame + 1, Allocator.Temp);
            // In case there are 0 connections
            serialDep[0] = inputDeps;
            for (int con = 0; sendStartPos + con < m_ConnectionStates.Count && con < sendPerFrame; ++con)
            {
                var connectionEntity = m_ConnectionStates[sendStartPos + con].Entity;
                var chunkSerializationData = m_ConnectionStates[sendStartPos + con].SerializationState;
                var serializeJob = new SerializeJob
                {
                    driver = m_ReceiveSystem.ConcurrentDriver,
                    unreliablePipeline = m_ReceiveSystem.UnreliablePipeline,
                    despawnChunks = despawnChunks,
                    ghostChunks = ghostChunks,
                    connectionEntity = connectionEntity,
                    chunkSerializationData = chunkSerializationData,
                    ackFromEntity = GetComponentDataFromEntity<NetworkSnapshotAckComponent>(true),
                    connectionFromEntity = GetComponentDataFromEntity<NetworkStreamConnection>(true),
                    networkIdFromEntity = GetComponentDataFromEntity<NetworkIdComponent>(true),
                    serialSpawnChunks = m_SerialSpawnChunks,
                    entityType = entityType,
                    ghostSystemStateType = ghostSystemStateType,
                    ghostComponentType = ghostComponentType,
                    ghostSimpleDeltaCompressionType = ghostSimpleDeltaCompressionType,
                    serializers = serializers,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    netStats = m_NetStats,
#endif
                    compressionModel = m_CompressionModel,
                    ghostFromEntity = GetComponentDataFromEntity<GhostSystemStateComponent>(true),
                    currentTick = currentTick,
                    localTime = NetworkTimeSystem.TimestampMS,
                    scaleGhostImportanceByDistance = distanceScaleFunction,
                    tileSize = tileSize,
                    tileCenter = tileCenter,
                    ghostDistancePartitionType = GetArchetypeChunkComponentType<GhostDistancePartition>(true),
                    ghostConnectionPositionFromEntity = GetComponentDataFromEntity<GhostConnectionPosition>(true)
                };
                // FIXME: disable safety for BufferFromEntity is not working
                serialDep[con + 1] =
                    serializeJob.Schedule(JobHandle.CombineDependencies(serialDep[con],
                        m_ReceiveSystem.LastDriverWriter));
            }

            inputDeps = JobHandle.CombineDependencies(serialDep);
            inputDeps = m_ReceiveSystem.Driver.ScheduleFlushSend(inputDeps);
            m_ReceiveSystem.LastDriverWriter = inputDeps;

            // Only the spawn job is using the commandBuffer, but the serialize job is using the same chunks - so we must wait for that too before we can modify them
            m_Barrier.AddJobHandleForProducer(inputDeps);

            var cleanupJob = new CleanupJob
            {
                despawnChunks = despawnChunks,
                spawnChunks = spawnChunks,
                ghostChunks = ghostChunks,
                serialSpawnChunks = m_SerialSpawnChunks
            };
            inputDeps = cleanupJob.Schedule(inputDeps);

            return inputDeps;
        }

        unsafe struct PrioChunk : IComparable<PrioChunk>
        {
            public ArchetypeChunk chunk;
            public GhostSystemStateComponent* ghostState;
            public int priority;
            public int startIndex;
            public int ghostType;

            public int CompareTo(PrioChunk other)
            {
                // Reverse priority for sorting
                return other.priority - priority;
            }
        }

        public static unsafe int InvokeSerialize<TSerializer, TSnapshotData>(
            TSerializer serializer, ref DataStreamWriter dataStream, SerializeData data)
            where TSnapshotData : unmanaged, ISnapshotData<TSnapshotData>
            where TSerializer : struct, IGhostSerializer<TSnapshotData>
        {
            int ent;
            int sameBaselineCount = 0;
            TSnapshotData* currentSnapshotData = (TSnapshotData*) data.currentSnapshotData;
            for (ent = data.startIndex; ent < data.chunk.Count && dataStream.Length < TargetPacketSize; ++ent)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (data.ghostStates[ent].ghostTypeIndex != data.ghostType)
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

                    for (sameBaselineCount = 1; ent + sameBaselineCount < data.chunk.Count; ++sameBaselineCount)
                    {
                        if (data.baselinePerEntity[(ent + sameBaselineCount) * 3] != baseline0 ||
                            data.baselinePerEntity[(ent + sameBaselineCount) * 3 + 1] != baseline1 ||
                            data.baselinePerEntity[(ent + sameBaselineCount) * 3 + 2] != baseline2)
                            break;
                    }

                    uint baseDiff0 = data.currentTick - baselineTick0;
                    uint baseDiff1 = data.currentTick - baselineTick1;
                    uint baseDiff2 = data.currentTick - baselineTick2;
                    dataStream.WritePackedUInt(baseDiff0, data.compressionModel);
                    dataStream.WritePackedUInt(baseDiff1, data.compressionModel);
                    dataStream.WritePackedUInt(baseDiff2, data.compressionModel);
                    dataStream.WritePackedUInt((uint) sameBaselineCount, data.compressionModel);
                }

                --sameBaselineCount;
                TSnapshotData* baselineSnapshotData0 = null;
                if (baseline0 >= 0)
                {
                    baselineSnapshotData0 = ((TSnapshotData*) data.availableBaselines[baseline0].snapshot) + ent;
                }

                TSnapshotData* baselineSnapshotData1 = null;
                TSnapshotData* baselineSnapshotData2 = null;
                if (baseline2 >= 0)
                {
                    baselineSnapshotData1 = ((TSnapshotData*) data.availableBaselines[baseline1].snapshot) + ent;
                    baselineSnapshotData2 = ((TSnapshotData*) data.availableBaselines[baseline2].snapshot) + ent;
                }

                dataStream.WritePackedUInt((uint) data.ghosts[ent].ghostId, data.compressionModel);

                TSnapshotData* snapshot;
                var snapshotData = default(TSnapshotData);
                if (currentSnapshotData == null)
                    snapshot = &snapshotData;
                else
                    snapshot = currentSnapshotData + ent;
                serializer.CopyToSnapshot(data.chunk, ent, data.currentTick, ref *snapshot, data.serializerState);
                var baselineData = default(TSnapshotData);
                TSnapshotData* baseline = &baselineData;
                if (baselineSnapshotData2 != null)
                {
                    baselineData = *baselineSnapshotData0;
                    baselineData.PredictDelta(data.currentTick, ref *baselineSnapshotData1, ref *baselineSnapshotData2);
                }
                else if (baselineSnapshotData0 != null)
                {
                    baseline = baselineSnapshotData0;
                }

                snapshot->Serialize(data.serializerState.NetworkId, ref *baseline, ref dataStream, data.compressionModel);

                if (currentSnapshotData != null)
                    data.currentSnapshotEntity[ent] = data.ghostEntities[ent];
            }

            return ent;
        }
    }
}

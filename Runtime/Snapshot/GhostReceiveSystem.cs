using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    public struct SpawnedGhost : IEquatable<SpawnedGhost>
    {
        public int ghostId;
        public uint spawnTick;
        public override int GetHashCode()
        {
            return ghostId;
        }
        public bool Equals(SpawnedGhost ghost)
        {
            return ghost.ghostId == ghostId && ghost.spawnTick == spawnTick;
        }
    }
    internal struct SpawnedGhostMapping
    {
        public SpawnedGhost ghost;
        public Entity entity;
        public Entity previousEntity;
    }
    internal struct NonSpawnedGhostMapping
    {
        public int ghostId;
        public Entity entity;
    }

    public struct GhostDeserializerState
    {
        public NativeHashMap<SpawnedGhost, Entity> GhostMap;
        public uint SnapshotTick;
        public int GhostOwner;
        public SendToOwnerType SendToOwner;
    }

    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostUpdateSystem))]
    [UpdateBefore(typeof(GhostSpawnClassificationSystem))]
    [UpdateAfter(typeof(GhostCollectionSystem))]
    [UpdateAfter(typeof(PopulatePreSpawnedGhosts))]
    public unsafe class GhostReceiveSystem : SystemBase, IGhostMappingSystem
    {
        private EntityQuery playerGroup;
        private EntityQuery ghostCleanupGroup;
        private EntityQuery clearJobGroup;

        private NativeHashMap<int, Entity> m_ghostEntityMap;
        private NativeHashMap<SpawnedGhost, Entity> m_spawnedGhostEntityMap;
        private NativeList<byte> m_tempDynamicData;

        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private GhostDespawnSystem m_GhostDespawnSystem;

        private NativeArray<int> m_GhostCompletionCount;
        /// <summary>
        /// The total number of ghosts on the server the last time a snapshot was received. Use this and GhostCountOnClient to figure out how much of the state the client has received.
        /// </summary>
        public int GhostCountOnServer => m_GhostCompletionCount[0];
        /// <summary>
        /// The total number of ghosts received by this client the last time a snapshot was received. The number of received ghosts can be different from the number of currently spawned ghosts. Use this and GhostCountOnServer to figure out how much of the state the client has received.
        /// </summary>
        public int GhostCountOnClient => m_GhostCompletionCount[1];

        public JobHandle LastGhostMapWriter { get; set; }
        public NativeHashMap<SpawnedGhost, Entity> SpawnedGhostEntityMap => m_spawnedGhostEntityMap;

        protected override void OnCreate()
        {
            m_ghostEntityMap = new NativeHashMap<int, Entity>(2048, Allocator.Persistent);
            m_spawnedGhostEntityMap = new NativeHashMap<SpawnedGhost, Entity>(2048, Allocator.Persistent);

            playerGroup = GetEntityQuery(
                ComponentType.ReadWrite<NetworkStreamConnection>(),
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());

            ghostCleanupGroup = GetEntityQuery(ComponentType.ReadOnly<GhostComponent>(),
                ComponentType.Exclude<PreSpawnedGhostId>());

            clearJobGroup = GetEntityQuery(ComponentType.ReadOnly<GhostComponent>(),
                ComponentType.Exclude<PreSpawnedGhostId>());

            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_CompressionModel = new NetworkCompressionModel(Allocator.Persistent);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_StatsCollection = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif
            m_GhostDespawnSystem = World.GetOrCreateSystem<GhostDespawnSystem>();

            RequireSingletonForUpdate<GhostCollection>();
            m_GhostCompletionCount = new NativeArray<int>(2, Allocator.Persistent);
            m_tempDynamicData = new NativeList<byte>(Allocator.Persistent);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private NativeArray<uint> m_NetStats;
        private GhostStatsCollectionSystem m_StatsCollection;
#endif
        private NetworkCompressionModel m_CompressionModel;

        protected override void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_NetStats.IsCreated)
                m_NetStats.Dispose();
#endif
            LastGhostMapWriter.Complete();
            m_ghostEntityMap.Dispose();
            m_spawnedGhostEntityMap.Dispose();

            m_CompressionModel.Dispose();
            m_GhostCompletionCount.Dispose();
            m_tempDynamicData.Dispose();
        }

        [BurstCompile]
        struct ClearGhostsJob : IJobChunk
        {
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            [ReadOnly] public EntityTypeHandle entitiesType;

            public void LambdaMethod(Entity entity, int index)
            {
                commandBuffer.DestroyEntity(index, entity);
            }

            public void Execute(ArchetypeChunk chunk, int orderIndex, int firstEntityIndex)
            {
                var entities = chunk.GetNativeArray(entitiesType);
                for (int i = 0; i < chunk.Count; ++i)
                {
                    LambdaMethod(entities[i], orderIndex);
                }
            }
        }

        [BurstCompile]
        struct ClearMapJob : IJob
        {
            public NativeHashMap<int, Entity> ghostMap;
            public NativeHashMap<SpawnedGhost, Entity> spawnedGhostMap;

            public void Execute()
            {
                ghostMap.Clear();
                spawnedGhostMap.Clear();
            }
        }

        [BurstCompile]
        struct ReadStreamJob : IJob
        {
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferFromEntity<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            public BufferFromEntity<GhostCollectionPrefab> GhostCollectionFromEntity;

            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionPrefabSerializer> GhostTypeCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;

            [DeallocateOnJobCompletion] public NativeArray<Entity> players;
            public BufferFromEntity<IncomingSnapshotDataStreamBufferComponent> snapshotFromEntity;
            public BufferFromEntity<SnapshotDataBuffer> snapshotDataBufferFromEntity;
            public BufferFromEntity<SnapshotDynamicDataBuffer> snapshotDynamicDataFromEntity;
            public BufferFromEntity<GhostSpawnBuffer> ghostSpawnBufferFromEntity;
            public ComponentDataFromEntity<SnapshotData> snapshotDataFromEntity;
            public ComponentDataFromEntity<NetworkSnapshotAckComponent> snapshotAckFromEntity;
            [ReadOnly] public ComponentDataFromEntity<NetworkIdComponent> networkIdFromEntity;
            public NativeHashMap<int, Entity> ghostEntityMap;
            public NetworkCompressionModel compressionModel;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> interpolatedDespawnQueue;
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> predictedDespawnQueue;
            [ReadOnly] public ComponentDataFromEntity<PredictedGhostComponent> predictedFromEntity;
            public ComponentDataFromEntity<GhostComponent> ghostFromEntity;
            public bool isThinClient;

            public EntityCommandBuffer commandBuffer;
            public Entity ghostSpawnEntity;
            private int connectionId;
            public NativeArray<int> GhostCompletionCount;
            public NativeList<byte> tempDynamicData;

            public unsafe void Execute()
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                for (int i = 0; i < netStats.Length; ++i)
                {
                    netStats[i] = 0;
                }
#endif
                GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];

                // FIXME: should handle any number of connections with individual ghost mappings for each
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (players.Length > 1)
                    throw new InvalidOperationException("Ghost receive system only supports a single connection");
#endif

                var snapshot = snapshotFromEntity[players[0]];
                if (snapshot.Length == 0)
                    return;

                //compute the size for the temporary buffer used to extract delta compressed buffer elements
                int maxDynamicSnapshotSize = 0;
                for (int i = 0; i < GhostTypeCollection.Length; ++i)
                    maxDynamicSnapshotSize = math.max(maxDynamicSnapshotSize, GhostTypeCollection[i].MaxBufferSnapshotSize);
                tempDynamicData.Resize(maxDynamicSnapshotSize,NativeArrayOptions.ClearMemory);

                var dataStream = snapshot.AsDataStreamReader();
                // Read the ghost stream
                // find entities to spawn or destroy
                var serverTick = dataStream.ReadUInt();

                var ack = snapshotAckFromEntity[players[0]];
                if (ack.LastReceivedSnapshotByLocal != 0 &&
                    !SequenceHelpers.IsNewer(serverTick, ack.LastReceivedSnapshotByLocal))
                    return;
                if (ack.LastReceivedSnapshotByLocal != 0)
                {
                    var shamt = (int) (serverTick - ack.LastReceivedSnapshotByLocal);
                    if (shamt < 32)
                        ack.ReceivedSnapshotByLocalMask <<= shamt;
                    else
                        ack.ReceivedSnapshotByLocalMask = 0;
                }

                ack.ReceivedSnapshotByLocalMask |= 1;
                ack.LastReceivedSnapshotByLocal = serverTick;

                // Load all new prefabs
                uint numPrefabs = dataStream.ReadPackedUInt(compressionModel);
                if (numPrefabs > 0)
                {
                    var ghostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                    // The server only sends ghost types which have not been acked yet, acking takes one RTT so we need to check
                    // which prefab was the first included in the list sent by the server
                    int firstPrefab = (int)dataStream.ReadUInt();
                    for (int i = 0; i < numPrefabs; ++i)
                    {
                        GhostTypeComponent type;
                        ulong hash;
                        type.guid0 = dataStream.ReadUInt();
                        type.guid1 = dataStream.ReadUInt();
                        type.guid2 = dataStream.ReadUInt();
                        type.guid3 = dataStream.ReadUInt();
                        hash = dataStream.ReadULong();
                        if (firstPrefab+i == ghostCollection.Length)
                        {
                            // This just adds the type, the prefab entity will be populated by the GhostCollectionSystem
                            ghostCollection.Add(new GhostCollectionPrefab{GhostType = type, GhostPrefab = Entity.Null, Hash = hash});
                        }
                        else if (type != ghostCollection[firstPrefab+i].GhostType || hash != ghostCollection[firstPrefab+i].Hash)
                        {
        #if ENABLE_UNITY_COLLECTIONS_CHECKS
                            UnityEngine.Debug.LogError($"GhostReceiveSystem ghost list item {firstPrefab+i} was modified (Hash {ghostCollection[firstPrefab+i].Hash} -> {hash})");
        #endif
                            commandBuffer.AddComponent(players[0], new NetworkStreamRequestDisconnect{Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
                            return;
                        }
                    }
                }
                snapshotAckFromEntity[players[0]] = ack;

                if (isThinClient)
                    return;

                uint totalGhostCount = dataStream.ReadPackedUInt(compressionModel);
                if(networkIdFromEntity.HasComponent(players[0]))
                    connectionId = networkIdFromEntity[players[0]].Value;
                GhostCompletionCount[0] = (int)totalGhostCount;

                uint despawnLen = dataStream.ReadUInt();
                uint updateLen = dataStream.ReadUInt();

                var data = default(DeserializeData);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                data.startPos = dataStream.GetBitsRead();
#endif
                for (var i = 0; i < despawnLen; ++i)
                {
                    int ghostId = (int) dataStream.ReadPackedUInt(compressionModel);
                    Entity ent;
                    if (!ghostEntityMap.TryGetValue(ghostId, out ent))
                        continue;

                    ghostEntityMap.Remove(ghostId);

                    if (predictedFromEntity.HasComponent(ent))
                        predictedDespawnQueue.Enqueue(new GhostDespawnSystem.DelayedDespawnGhost
                            {ghost = new SpawnedGhost{ghostId = ghostId, spawnTick = ghostFromEntity[ent].spawnTick}, tick = serverTick});
                    else
                        interpolatedDespawnQueue.Enqueue(new GhostDespawnSystem.DelayedDespawnGhost
                            {ghost = new SpawnedGhost{ghostId = ghostId, spawnTick = ghostFromEntity[ent].spawnTick}, tick = serverTick});
                }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                data.curPos = dataStream.GetBitsRead();
                netStats[0] = serverTick;
                netStats[1] = despawnLen;
                netStats[2] = (uint) (dataStream.GetBitsRead() - data.startPos);
                netStats[3] = 0;
                data.startPos = data.curPos;
#endif

                bool dataValid = true;
                for (var i = 0; i < updateLen && dataValid; ++i)
                {
                    dataValid = DeserializeEntity(serverTick, ref dataStream, ref data);
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (data.statCount > 0)
                {
                    data.curPos = dataStream.GetBitsRead();
                    int statType = (int) data.targetArch;
                    netStats[statType * 3 + 4] = netStats[statType * 3 + 4] + data.statCount;
                    netStats[statType * 3 + 5] = netStats[statType * 3 + 5] + (uint) (data.curPos - data.startPos);
                    netStats[statType * 3 + 6] = netStats[statType * 3 + 6] + data.uncompressedCount;
                }
#endif
                while (ghostEntityMap.Capacity < ghostEntityMap.Count() + data.newGhosts)
                    ghostEntityMap.Capacity += 1024;

                snapshot.Clear();

                GhostCompletionCount[1] = ghostEntityMap.Count();

                if (!dataValid)
                {
                    // Desync - reset received snapshots
                    ack.ReceivedSnapshotByLocalMask = 0;
                    ack.LastReceivedSnapshotByLocal = 0;
                    snapshotAckFromEntity[players[0]] = ack;
                }
            }
            struct DeserializeData
            {
                public uint targetArch;
                public uint targetArchLen;
                public uint baselineTick;
                public uint baselineTick2;
                public uint baselineTick3;
                public uint baselineLen;
                public int newGhosts;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                public int startPos;
                public int curPos;
                public uint statCount;
                public uint uncompressedCount;
#endif
            }

            private bool DeserializeEntity(uint serverTick, ref DataStreamReader dataStream, ref DeserializeData data)
            {
                if (data.targetArchLen == 0)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    data.curPos = dataStream.GetBitsRead();
                    if (data.statCount > 0)
                    {
                        int statType = (int) data.targetArch;
                        netStats[statType * 3 + 4] = netStats[statType * 3 + 4] + data.statCount;
                        netStats[statType * 3 + 5] = netStats[statType * 3 + 5] + (uint) (data.curPos - data.startPos);
                        netStats[statType * 3 + 6] = netStats[statType * 3 + 6] + data.uncompressedCount;
                    }

                    data.startPos = data.curPos;
                    data.statCount = 0;
                    data.uncompressedCount = 0;
#endif
                    data.targetArch = dataStream.ReadPackedUInt(compressionModel);
                    data.targetArchLen = dataStream.ReadPackedUInt(compressionModel);
                    if (data.targetArch >= GhostTypeCollection.Length)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.LogError("Received invalid ghost type from server");
#endif
                        return false;
                    }
                }

                --data.targetArchLen;

                if (data.baselineLen == 0)
                {
                    data.baselineTick = serverTick - dataStream.ReadPackedUInt(compressionModel);
                    data.baselineTick2 = serverTick - dataStream.ReadPackedUInt(compressionModel);
                    data.baselineTick3 = serverTick - dataStream.ReadPackedUInt(compressionModel);
                    data.baselineLen = dataStream.ReadPackedUInt(compressionModel);
                    if (data.baselineTick3 != serverTick &&
                        (data.baselineTick3 == data.baselineTick2 || data.baselineTick2 == data.baselineTick))
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.LogError("Received invalid snapshot baseline from server");
#endif
                        return false;
                    }
                }

                --data.baselineLen;

                int ghostId = (int) dataStream.ReadPackedUInt(compressionModel);
                uint serverSpawnTick = 0;
                if (data.baselineTick == serverTick)
                    serverSpawnTick = dataStream.ReadPackedUInt(compressionModel);
                var typeData = GhostTypeCollection[(int)data.targetArch];
                int changeMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                int snapshotOffset;
                int snapshotSize = typeData.SnapshotSize;
                byte* baselineData = (byte*)UnsafeUtility.Malloc(snapshotSize, 16, Allocator.Temp);
                UnsafeUtility.MemClear(baselineData, snapshotSize);
                Entity gent;
                DynamicBuffer<SnapshotDataBuffer> snapshotDataBuffer;
                SnapshotData snapshotDataComponent;
                byte* snapshotData;
                //
                byte* snapshotDynamicDataPtr = null;
                uint snapshotDynamicDataCapacity = 0; // available space in the dynamic snapshot data history slot
                byte* baselineDynamicDataPtr = null;
                uint baselineDynamicDataSize = 0; //used for delta compression, the last stored dynamic size for the entity in snapshot history buffer

                bool existingGhost = ghostEntityMap.TryGetValue(ghostId, out gent);
                var sendToOwnerMask = SendToOwnerType.All;
                if (snapshotDataBufferFromEntity.HasComponent(gent) && ghostFromEntity[gent].ghostType < 0)
                {
                    // Pre-spawned ghosts can have ghost type -1 until they receive the proper type from the server
                    var existingGhostEnt = ghostFromEntity[gent];
                    existingGhostEnt.ghostType = (int)data.targetArch;
                    ghostFromEntity[gent] = existingGhostEnt;

                    snapshotDataBuffer = snapshotDataBufferFromEntity[gent];
                    snapshotDataBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    UnsafeUtility.MemClear(snapshotDataBuffer.GetUnsafePtr(), snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    snapshotDataFromEntity[gent] = new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0};
                }
                if (existingGhost && snapshotDataBufferFromEntity.HasComponent(gent) && ghostFromEntity[gent].ghostType == data.targetArch)
                {
                    snapshotDataBuffer = snapshotDataBufferFromEntity[gent];
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (snapshotDataBuffer.Length != snapshotSize * GhostSystemConstants.SnapshotHistorySize)
                        throw new InvalidOperationException($"Invalid snapshot buffer size");
#endif
                    snapshotData = (byte*)snapshotDataBuffer.GetUnsafePtr();
                    snapshotDataComponent = snapshotDataFromEntity[gent];
                    snapshotDataComponent.LatestIndex = (snapshotDataComponent.LatestIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
                    snapshotDataFromEntity[gent] = snapshotDataComponent;
                    if (data.baselineTick != serverTick)
                    {
                        for (int bi = 0; bi < snapshotDataBuffer.Length; bi += snapshotSize)
                        {
                            if (*(uint*)(snapshotData+bi) == data.baselineTick)
                            {
                                UnsafeUtility.MemCpy(baselineData, snapshotData+bi, snapshotSize);
                                //retrive also the baseline dynamic snapshot buffer if the ghost has some buffers
                                if(typeData.NumBuffers > 0)
                                {
                                    if (!snapshotDynamicDataFromEntity.HasComponent(gent))
                                        throw new InvalidOperationException($"SnapshotDynamictDataBuffer buffer not found for ghost with id {ghostId}");
                                    var snapshotDynamicDataBuffer = snapshotDynamicDataFromEntity[gent];
                                    int bindex = bi / snapshotSize;
                                    var bufferPtr = (byte*)snapshotDynamicDataBuffer.GetUnsafeReadOnlyPtr();
                                    baselineDynamicDataPtr = SnapshotDynamicBuffersHelper.GetDynamicDataPtr(bufferPtr, bindex, snapshotDynamicDataBuffer.Length);
                                    baselineDynamicDataSize = ((uint*)bufferPtr)[bindex];
                                }
                                break;
                            }
                        }

                        if (*(uint*)baselineData == 0)
                            return false; // Ack desync detected
                    }

                    if (data.baselineTick3 != serverTick)
                    {
                        byte* baselineData2 = null;
                        byte* baselineData3 = null;
                        for (int bi = 0; bi < snapshotDataBuffer.Length; bi += snapshotSize)
                        {
                            if (*(uint*)(snapshotData+bi) == data.baselineTick2)
                            {
                                baselineData2 = snapshotData+bi;
                            }

                            if (*(uint*)(snapshotData+bi) == data.baselineTick3)
                            {
                                baselineData3 = snapshotData+bi;
                            }
                        }

                        if (baselineData2 == null || baselineData3 == null)
                            return false; // Ack desync detected
                        snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                        var predictor = new GhostDeltaPredictor(serverTick, data.baselineTick, data.baselineTick2, data.baselineTick3);

                        if (typeData.PredictionOwnerOffset != 0)
                        {
                            var networkId = predictor.PredictInt(
                                *(int*) (baselineData + typeData.PredictionOwnerOffset),
                                *(int*) (baselineData2 + typeData.PredictionOwnerOffset),
                                *(int*) (baselineData3 + typeData.PredictionOwnerOffset));
                            sendToOwnerMask = networkId == connectionId
                                ? SendToOwnerType.SendToOwner
                                : SendToOwnerType.SendToNonOwner;
                        }

                        for (int comp = 0; comp < typeData.NumComponents; ++comp)
                        {
                            int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                            //Buffers does not use delta prediction for the size and the contents
                            if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                if (snapshotOffset+GhostComponentCollection[serializerIdx].SnapshotSize > snapshotSize)
                                    throw new InvalidOperationException("Snapshot buffer overflow during predict");
#endif
                                if ((sendToOwnerMask & GhostComponentCollection[serializerIdx].SendToOwner) != 0)
                                {
                                    GhostComponentCollection[serializerIdx].PredictDelta.Ptr.Invoke(
                                        (IntPtr) (baselineData + snapshotOffset),
                                        (IntPtr) (baselineData2 + snapshotOffset),
                                        (IntPtr) (baselineData3 + snapshotOffset), ref predictor);
                                }
                                snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                            }
                            else
                            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                if (snapshotOffset+GhostSystemConstants.DynamicBufferComponentSnapshotSize > snapshotSize)
                                    throw new InvalidOperationException("Snapshot buffer overflow during predict");
#endif
                                snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                            }
                        }
                    }
                    //buffers: retrieve the dynamic contents size and re-fit the snapshot dynamic history
                    if (typeData.NumBuffers > 0)
                    {
                        //Delta-decompress the dynamic data size
                        //FIXME
                        //@timj This could also use PackedUIntDelta with baselineSize = 0 if the send system is changed to match.
                        //      That's how most other readers / writers work.
                        uint dynamicDataSize;
                        if (baselineDynamicDataPtr == null)
                            dynamicDataSize = dataStream.ReadPackedUInt(compressionModel);
                        else
                            dynamicDataSize = dataStream.ReadPackedUIntDelta(baselineDynamicDataSize, compressionModel);

                        if (!snapshotDynamicDataFromEntity.HasComponent(gent))
                            throw new InvalidOperationException($"SnapshotDynamictDataBuffer buffer not found for ghost with id {ghostId}");

                        //Fit the snapshot buffer to accomodate the new size. Add some room for growth (20%)
                        var buf = snapshotDynamicDataFromEntity[gent];
                        var slotCapacity = SnapshotDynamicBuffersHelper.GetDynamicDataCapacity(SnapshotDynamicBuffersHelper.GetHeaderSize(), buf.Length);
                        var newCapacity = SnapshotDynamicBuffersHelper.CalculateBufferCapacity(dynamicDataSize, out var newSlotCapacity);
                        if (buf.Length < newCapacity)
                        {
                            //Perf: Is already copying over the contents to the new re-allocated buffer. It would be nice to avoid that
                            buf.ResizeUninitialized((int)newCapacity);
                            //Move buffer content around (because the slot size is changed)
                            var sourcePtr = (byte*)buf.GetUnsafePtr() + GhostSystemConstants.SnapshotHistorySize*slotCapacity;
                            var destPtr = (byte*)buf.GetUnsafePtr() + GhostSystemConstants.SnapshotHistorySize*newSlotCapacity;
                            for (int i=0;i<GhostSystemConstants.SnapshotHistorySize;++i)
                            {
                                destPtr -= newSlotCapacity;
                                sourcePtr -= slotCapacity;
                                UnsafeUtility.MemMove(destPtr, sourcePtr, slotCapacity);
                            }
                            slotCapacity = newSlotCapacity;
                        }
                        //write down the received data size inside the snapshot (used for delta compression) and setup dynamic data ptr
                        var bufPtr = (byte*)buf.GetUnsafePtr();
                        ((uint*)bufPtr)[snapshotDataComponent.LatestIndex] = dynamicDataSize;
                        snapshotDynamicDataPtr = SnapshotDynamicBuffersHelper.GetDynamicDataPtr(bufPtr,snapshotDataComponent.LatestIndex, buf.Length);
                        snapshotDynamicDataCapacity = slotCapacity;
                    }
                }
                else
                {
                    if (existingGhost)
                    {
                        // The ghost entity map is out of date, clean it up
                        ghostEntityMap.Remove(ghostId);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (ghostFromEntity.HasComponent(gent) && ghostFromEntity[gent].ghostType != data.targetArch)
                            UnityEngine.Debug.LogError("Received a ghost with an invalid ghost type");
                        UnityEngine.Debug.LogError("Found a ghost in the ghost map which does not have an entity connected to it. This can happen if you delete ghost entities on the client.");
#endif
                    }
                    if (data.baselineTick != serverTick)
                    {
                        // If the server specifies a baseline for a ghost we do not have that is an error
                        return false;
                    }
                    ++data.newGhosts;
                    var ghostSpawnBuffer = ghostSpawnBufferFromEntity[ghostSpawnEntity];
                    snapshotDataBuffer = snapshotDataBufferFromEntity[ghostSpawnEntity];
                    var snapshotDataBufferOffset = snapshotDataBuffer.Length;
                    //Grow the ghostSpawnBuffer to include also the dynamic data size.
                    uint dynamicDataSize = 0;
                    if (typeData.NumBuffers > 0)
                        dynamicDataSize = dataStream.ReadPackedUInt(compressionModel);
                    ghostSpawnBuffer.Add(new GhostSpawnBuffer {
                        GhostType = (int)data.targetArch,
                        GhostID = ghostId,
                        DataOffset = snapshotDataBufferOffset,
                        DynamicDataSize = dynamicDataSize,
                        ClientSpawnTick = serverTick,
                        ServerSpawnTick = serverSpawnTick});
                    snapshotDataBuffer.ResizeUninitialized(snapshotDataBufferOffset + snapshotSize + (int)dynamicDataSize);
                    snapshotData = (byte*)snapshotDataBuffer.GetUnsafePtr() + snapshotDataBufferOffset;
                    UnsafeUtility.MemClear(snapshotData, snapshotSize + dynamicDataSize);
                    snapshotDataComponent = new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0};
                    //dynamic content temporary data start after the snapshot for new ghosts
                    if (typeData.NumBuffers > 0)
                    {
                        snapshotDynamicDataPtr = snapshotData + snapshotSize;
                        snapshotDynamicDataCapacity = dynamicDataSize;
                    }
                }

                int maskOffset = 0;
                //the dynamicBufferOffset is used to track the dynamic content offset from the begining of the dynamic history slot
                //for each entity
                uint dynamicBufferOffset = 0;

                snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                snapshotData += snapshotSize * snapshotDataComponent.LatestIndex;
                *(uint*)(snapshotData) = serverTick;
                uint* changeMask = (uint*)(snapshotData+4);
                for (int cm = 0; cm < changeMaskUints; ++cm)
                    changeMask[cm] = dataStream.ReadPackedUIntDelta(((uint*)(baselineData+4))[cm], compressionModel);

                for (int comp = 0; comp < typeData.NumComponents; ++comp)
                {
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (maskOffset+GhostComponentCollection[serializerIdx].ChangeMaskBits > typeData.ChangeMaskBits || snapshotOffset+GhostComponentCollection[serializerIdx].SnapshotSize > snapshotSize)
                            throw new InvalidOperationException("Snapshot buffer overflow during deserialize");
#endif
                        if ((sendToOwnerMask & GhostComponentCollection[serializerIdx].SendToOwner) != 0)
                        {
                            GhostComponentCollection[serializerIdx].Deserialize.Ptr.Invoke((IntPtr) (snapshotData + snapshotOffset), (IntPtr) (baselineData + snapshotOffset), ref dataStream, ref compressionModel, (IntPtr) changeMask, maskOffset);
                        }
                        snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                        maskOffset += GhostComponentCollection[serializerIdx].ChangeMaskBits;
                    }
                    else
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (maskOffset + GhostSystemConstants.DynamicBufferComponentMaskBits > typeData.ChangeMaskBits || (snapshotOffset + GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize)) > snapshotSize)
                            throw new InvalidOperationException("Snapshot buffer overflow during deserialize");
#endif
                        if ((sendToOwnerMask & GhostComponentCollection[serializerIdx].SendToOwner) != 0)
                        {
                            //Delta decompress the buffer len
                            uint mask = GhostComponentSerializer.CopyFromChangeMask((IntPtr) changeMask, maskOffset, GhostSystemConstants.DynamicBufferComponentMaskBits);
                            var baseLen = *(uint*) (baselineData + snapshotOffset);
                            var baseOffset = *(uint*) (baselineData + snapshotOffset + sizeof(uint));
                            var bufLen = (mask & 0x2) == 0 ? baseLen : dataStream.ReadPackedUIntDelta(baseLen, compressionModel);
                            //Assign the buffer info to the snapshot and register the current offset from the beginning of the dynamic history slot
                            *(uint*) (snapshotData + snapshotOffset) = bufLen;
                            *(uint*) (snapshotData + snapshotOffset + sizeof(uint)) = dynamicBufferOffset;
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                            maskOffset += GhostSystemConstants.DynamicBufferComponentMaskBits;
                            //Copy the buffer contents. Use delta compression based on mask bits configuration
                            //00 : nothing changed
                            //01 : same len, only content changed. Add additional mask bits for each elements
                            //11 : len changed, everthing need to be sent again . No mask bits for the elements
                            var dynamicDataSnapshotStride = (uint)GhostComponentCollection[serializerIdx].SnapshotSize;
                            var contentMaskUInts = (uint)GhostCollectionSystem.ChangeMaskArraySizeInUInts((int)(GhostComponentCollection[serializerIdx].ChangeMaskBits * bufLen));
                            var maskSize = GhostCollectionSystem.SnapshotSizeAligned(contentMaskUInts*4);
    #if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if ((dynamicBufferOffset + maskSize + bufLen * dynamicDataSnapshotStride) > snapshotDynamicDataCapacity)
                                throw new InvalidOperationException("DynamicData Snapshot buffer overflow during deserialize");
    #endif
                            uint* contentMask = (uint*) (snapshotDynamicDataPtr + dynamicBufferOffset);
                            dynamicBufferOffset += maskSize;
                            if ((mask & 0x3) == 0) //Nothing changed, just copy the baseline content
                            {
                                UnsafeUtility.MemSet(contentMask, 0x0, maskSize);
                                UnsafeUtility.MemCpy(snapshotDynamicDataPtr + dynamicBufferOffset,
                                    baselineDynamicDataPtr + baseOffset + maskSize, bufLen * dynamicDataSnapshotStride);
                                dynamicBufferOffset += bufLen * dynamicDataSnapshotStride;
                            }
                            else if ((mask & 0x2) != 0) // len changed, element masks are not present.
                            {
                                UnsafeUtility.MemSet(contentMask, 0xFF, maskSize);
                                var contentMaskOffset = 0;
                                //Performace here are not great. It would be better to call a method that serialize the content inside so only one call
                                for (int i = 0; i < bufLen; ++i)
                                {
                                    GhostComponentCollection[serializerIdx].Deserialize.Ptr.Invoke(
                                        (IntPtr) (snapshotDynamicDataPtr + dynamicBufferOffset),
                                        (IntPtr) tempDynamicData.GetUnsafePtr(),
                                        ref dataStream, ref compressionModel, (IntPtr) contentMask, contentMaskOffset);
                                    dynamicBufferOffset += dynamicDataSnapshotStride;
                                    contentMaskOffset += GhostComponentCollection[serializerIdx].ChangeMaskBits;
                                }
                            }
                            else //same len but content changed, decode the masks and copy the content
                            {
                                var baselineMaskPtr = (uint*) (baselineDynamicDataPtr + baseOffset);
                                for (int cm = 0; cm < contentMaskUInts; ++cm)
                                    contentMask[cm] = dataStream.ReadPackedUIntDelta(baselineMaskPtr[cm], compressionModel);
                                baseOffset += maskSize;
                                var contentMaskOffset = 0;
                                for (int i = 0; i < bufLen; ++i)
                                {
                                    GhostComponentCollection[serializerIdx].Deserialize.Ptr.Invoke(
                                        (IntPtr) (snapshotDynamicDataPtr + dynamicBufferOffset),
                                        (IntPtr) (baselineDynamicDataPtr + baseOffset),
                                        ref dataStream, ref compressionModel, (IntPtr) contentMask, contentMaskOffset);
                                    dynamicBufferOffset += dynamicDataSnapshotStride;
                                    baseOffset += dynamicDataSnapshotStride;
                                    contentMaskOffset += GhostComponentCollection[serializerIdx].ChangeMaskBits;
                                }
                            }
                            dynamicBufferOffset = GhostCollectionSystem.SnapshotSizeAligned(dynamicBufferOffset);
                        }
                        else
                        {
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                            maskOffset += GhostSystemConstants.DynamicBufferComponentMaskBits;
                        }
                    }
                }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                ++data.statCount;
                if (data.baselineTick == serverTick)
                    ++data.uncompressedCount;
#endif

                if (typeData.IsGhostGroup != 0)
                {
                    var groupLen = dataStream.ReadPackedUInt(compressionModel);
                    for (var i = 0; i < groupLen; ++i)
                    {
                        if (!DeserializeEntity(serverTick, ref dataStream, ref data))
                            return false;
                    }
                }
                return true;
            }
        }

        protected override void OnUpdate()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var numLoadedPrefabs = GetSingleton<GhostCollection>().NumLoadedPrefabs;
            if (m_NetStats.IsCreated && m_NetStats.Length != numLoadedPrefabs * 3 + 3 + 1)
                m_NetStats.Dispose();
            if (!m_NetStats.IsCreated)
                m_NetStats = new NativeArray<uint>(numLoadedPrefabs * 3 + 3 + 1, Allocator.Persistent);
            m_StatsCollection.AddSnapshotStats(m_NetStats);
#endif
            var commandBuffer = m_Barrier.CreateCommandBuffer();
            if (playerGroup.IsEmptyIgnoreFilter)
            {
                m_GhostCompletionCount[0] = m_GhostCompletionCount[1] = 0;
                // If there were no ghosts spawned at runtime we don't need to cleanup
                if (ghostCleanupGroup.IsEmptyIgnoreFilter)
                    return;
                m_GhostDespawnSystem.LastQueueWriter.Complete();
                m_GhostDespawnSystem.InterpolatedDespawnQueue.Clear();
                m_GhostDespawnSystem.PredictedDespawnQueue.Clear();
                var clearMapJob = new ClearMapJob
                {
                    ghostMap = m_ghostEntityMap,
                    spawnedGhostMap = m_spawnedGhostEntityMap
                };
                var clearHandle = clearMapJob.Schedule(Dependency);
                LastGhostMapWriter = clearHandle;
                var clearJob = new ClearGhostsJob
                {
                    entitiesType = GetEntityTypeHandle(),
                    commandBuffer = commandBuffer.AsParallelWriter()
                };
                Dependency = clearJob.Schedule(clearJobGroup, Dependency);
                m_Barrier.AddJobHandleForProducer(Dependency);
                Dependency = JobHandle.CombineDependencies(Dependency, clearHandle);
                return;
            }

            // Don't start ghost snapshot processing until we're in game, but allow the cleanup code above to run
            if (!HasSingleton<NetworkStreamInGame>())
            {
                return;
            }

            JobHandle playerHandle;
            var readJob = new ReadStreamJob
            {
                GhostCollectionSingleton = GetSingletonEntity<GhostCollection>(),
                GhostComponentCollectionFromEntity = GetBufferFromEntity<GhostComponentSerializer.State>(true),
                GhostTypeCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefabSerializer>(true),
                GhostComponentIndexFromEntity = GetBufferFromEntity<GhostCollectionComponentIndex>(true),
                GhostCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefab>(),
                players = playerGroup.ToEntityArrayAsync(Allocator.TempJob,
                    out playerHandle),
                snapshotFromEntity = GetBufferFromEntity<IncomingSnapshotDataStreamBufferComponent>(),
                snapshotDataBufferFromEntity = GetBufferFromEntity<SnapshotDataBuffer>(),
                snapshotDynamicDataFromEntity = GetBufferFromEntity<SnapshotDynamicDataBuffer>(),
                ghostSpawnBufferFromEntity = GetBufferFromEntity<GhostSpawnBuffer>(),
                snapshotDataFromEntity = GetComponentDataFromEntity<SnapshotData>(),
                snapshotAckFromEntity = GetComponentDataFromEntity<NetworkSnapshotAckComponent>(),
                networkIdFromEntity = GetComponentDataFromEntity<NetworkIdComponent>(true),
                ghostEntityMap = m_ghostEntityMap,
                compressionModel = m_CompressionModel,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats = m_NetStats,
#endif
                interpolatedDespawnQueue = m_GhostDespawnSystem.InterpolatedDespawnQueue,
                predictedDespawnQueue = m_GhostDespawnSystem.PredictedDespawnQueue,
                predictedFromEntity = GetComponentDataFromEntity<PredictedGhostComponent>(true),
                ghostFromEntity = GetComponentDataFromEntity<GhostComponent>(),
                isThinClient = HasSingleton<ThinClientComponent>(),
                commandBuffer = commandBuffer,
                ghostSpawnEntity = GetSingletonEntity<GhostSpawnQueueComponent>(),
                GhostCompletionCount = m_GhostCompletionCount,
                tempDynamicData = m_tempDynamicData
            };
            Dependency = readJob.Schedule(JobHandle.CombineDependencies(Dependency, playerHandle,
                m_GhostDespawnSystem.LastQueueWriter));
            m_GhostDespawnSystem.LastQueueWriter = Dependency;

            m_Barrier.AddJobHandleForProducer(Dependency);
        }
        internal void AddNonSpawnedGhosts(NativeArray<NonSpawnedGhostMapping> ghosts)
        {
            LastGhostMapWriter.Complete();
            for (int i = 0; i < ghosts.Length; ++i)
            {
                var ghostId = ghosts[i].ghostId;
                var ent = ghosts[i].entity;
                if (!m_ghostEntityMap.TryAdd(ghostId, ent))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.LogError("Ghost ID " + ghostId + " has already been added");
#endif
                    m_ghostEntityMap[ghostId] = ent;
                }
            }
        }
        internal void AddSpawnedGhosts(NativeArray<SpawnedGhostMapping> ghosts)
        {
            LastGhostMapWriter.Complete();
            for (int i = 0; i < ghosts.Length; ++i)
            {
                var ghost = ghosts[i].ghost;
                var ent = ghosts[i].entity;
                if (!m_ghostEntityMap.TryAdd(ghost.ghostId, ent))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.LogError("Ghost ID " + ghost.ghostId + " has already been added");
#endif
                    m_ghostEntityMap[ghost.ghostId] = ent;
                }

                if (!m_spawnedGhostEntityMap.TryAdd(ghost, ent))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.LogError("Ghost ID " + ghost.ghostId + " has already been added to the spawned ghost map");
#endif
                    m_spawnedGhostEntityMap[ghost] = ent;
                }
            }
        }
        internal void UpdateSpawnedGhosts(NativeArray<SpawnedGhostMapping> ghosts)
        {
            LastGhostMapWriter.Complete();
            for (int i = 0; i < ghosts.Length; ++i)
            {
                var ghost = ghosts[i].ghost;
                var ent = ghosts[i].entity;
                var prevEnt = ghosts[i].previousEntity;
                // If the ghost is also in the desapwn queue it will not be in the ghost map
                // If a ghost id previously used for an interpolated ghost is not used for a predicted ghost
                // a different ghost might be in the ghost map
                if (m_ghostEntityMap.TryGetValue(ghost.ghostId, out var existing) && existing == prevEnt)
                {
                    m_ghostEntityMap[ghost.ghostId] =  ent;
                }
                if (!m_spawnedGhostEntityMap.TryAdd(ghost, ent))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.LogError("Ghost ID " + ghost.ghostId + " has already been added to the spawned ghost map");
#endif
                    m_spawnedGhostEntityMap[ghost] = ent;
                }
            }
        }
    }
}

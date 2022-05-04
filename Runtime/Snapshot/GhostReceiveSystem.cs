#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using System.Globalization;
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
        public NativeParallelHashMap<SpawnedGhost, Entity> GhostMap;
        public uint SnapshotTick;
        public int GhostOwner;
        public SendToOwnerType SendToOwner;
    }

    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostUpdateSystem))]
    [UpdateBefore(typeof(GhostSpawnClassificationSystem))]
    [UpdateAfter(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(GhostCollectionSystem))]
    public unsafe partial class GhostReceiveSystem : SystemBase, IGhostMappingSystem
    {
        private EntityQuery playerGroup;
        private EntityQuery ghostCleanupGroup;
        private EntityQuery clearJobGroup;
        private EntityQuery subSceneGroup;

        private NativeParallelHashMap<int, Entity> m_ghostEntityMap;
        private NativeParallelHashMap<SpawnedGhost, Entity> m_spawnedGhostEntityMap;
        private NativeList<byte> m_tempDynamicData;

        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private GhostDespawnSystem m_GhostDespawnSystem;

        private NativeArray<int> m_GhostCompletionCount;
        private NetDebugSystem m_NetDebugSystem;
#if NETCODE_DEBUG
        private NetDebugPacket m_NetDebugPacket;
        private NetDebugPacketLoggers m_PacketLoggers;
        private EntityQuery m_EnablePacketLoggingQuery;
#endif
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;

        /// <summary>
        /// The total number of ghosts on the server the last time a snapshot was received. Use this and GhostCountOnClient to figure out how much of the state the client has received.
        /// </summary>
        public int GhostCountOnServer => m_GhostCompletionCount[0];
        /// <summary>
        /// The total number of ghosts received by this client the last time a snapshot was received. The number of received ghosts can be different from the number of currently spawned ghosts. Use this and GhostCountOnServer to figure out how much of the state the client has received.
        /// </summary>
        public int GhostCountOnClient => m_GhostCompletionCount[1];

        public JobHandle LastGhostMapWriter { get; set; }
        public NativeParallelHashMap<SpawnedGhost, Entity> SpawnedGhostEntityMap => m_spawnedGhostEntityMap;
        internal NativeParallelHashMap<int, Entity> GhostEntityMap => m_ghostEntityMap;

        protected override void OnCreate()
        {
            m_ghostEntityMap = new NativeParallelHashMap<int, Entity>(2048, Allocator.Persistent);
            m_spawnedGhostEntityMap = new NativeParallelHashMap<SpawnedGhost, Entity>(2048, Allocator.Persistent);

            playerGroup = GetEntityQuery(
                ComponentType.ReadWrite<NetworkStreamConnection>(),
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());

            ghostCleanupGroup = GetEntityQuery(ComponentType.ReadOnly<GhostComponent>(),
                ComponentType.Exclude<PreSpawnedGhostIndex>());

            clearJobGroup = GetEntityQuery(ComponentType.ReadOnly<GhostComponent>(),
                ComponentType.Exclude<PreSpawnedGhostIndex>());

            subSceneGroup = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubSceneWithGhostStateComponent>());

            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_CompressionModel = new NetworkCompressionModel(Allocator.Persistent);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_StatsCollection = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif
            m_GhostDespawnSystem = World.GetOrCreateSystem<GhostDespawnSystem>();

            RequireSingletonForUpdate<GhostCollection>();
            m_GhostCompletionCount = new NativeArray<int>(2, Allocator.Persistent);
            m_tempDynamicData = new NativeList<byte>(Allocator.Persistent);
            m_NetDebugSystem = World.GetOrCreateSystem<NetDebugSystem>();
#if NETCODE_DEBUG
            m_PacketLoggers = new NetDebugPacketLoggers();
#endif
            m_ClientSimulationSystemGroup = World.GetOrCreateSystem<ClientSimulationSystemGroup>();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private NativeArray<uint> m_NetStats;
        private GhostStatsCollectionSystem m_StatsCollection;
        internal NativeArray<uint> NetStats => m_NetStats;
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
#if NETCODE_DEBUG
            m_NetDebugPacket.Dispose();
            m_PacketLoggers.Dispose();
#endif
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
            public NativeParallelHashMap<int, Entity> ghostMap;
            public NativeParallelHashMap<SpawnedGhost, Entity> spawnedGhostMap;

            public void Execute()
            {
                //The ghost map should not clear pre-spawn ghost since they aren't destroyed when the
                //client connection is not in-game. It is more correct to rely on the fact the
                //pre-spawn system reset that since it was the one populating it.
                var keys = spawnedGhostMap.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; ++i)
                {
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(keys[i].ghostId))
                    {
                        ghostMap.Remove(keys[i].ghostId);
                        spawnedGhostMap.Remove(keys[i]);
                    }
                }
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
            [ReadOnly]public BufferFromEntity<PrespawnGhostBaseline> prespawnBaselineBufferFromEntity;
            public ComponentDataFromEntity<SnapshotData> snapshotDataFromEntity;
            public ComponentDataFromEntity<NetworkSnapshotAckComponent> snapshotAckFromEntity;
            [ReadOnly] public ComponentDataFromEntity<NetworkIdComponent> networkIdFromEntity;
            public NativeParallelHashMap<int, Entity> ghostEntityMap;
            public NetworkCompressionModel compressionModel;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> interpolatedDespawnQueue;
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> predictedDespawnQueue;
            [ReadOnly] public ComponentDataFromEntity<PredictedGhostComponent> predictedFromEntity;
            public ComponentDataFromEntity<GhostComponent> ghostFromEntity;
            public bool isThinClient;
            public bool snaphostHasCompressedGhostSize;

            public EntityCommandBuffer commandBuffer;
            public Entity ghostSpawnEntity;
            private int connectionId;
            public NativeArray<int> GhostCompletionCount;
            public NativeList<byte> tempDynamicData;
            [DeallocateOnJobCompletion]
            public NativeArray<SubSceneWithGhostStateComponent> prespawnSceneStateArray;

            public NetDebug netDebug;
#if NETCODE_DEBUG
            public NetDebugPacket netDebugPacket;
            [ReadOnly] public ComponentDataFromEntity<PrefabDebugName> prefabNames;
            [ReadOnly] public NativeParallelHashMap<int, FixedString128Bytes> componentTypeNameLookup;
            [ReadOnly] public ComponentDataFromEntity<EnablePacketLogging> enableLoggingFromEntity;
            public FixedString128Bytes timestampAndTick;
            private bool enablePacketLogging;
#endif

            public void Execute()
            {
#if NETCODE_DEBUG
                FixedString512Bytes debugLog = timestampAndTick;
                enablePacketLogging = enableLoggingFromEntity.HasComponent(players[0]);
                if (enablePacketLogging && !netDebugPacket.IsCreated)
                {
                    netDebug.LogError("Packet logger has not been set. Aborting.");
                    return;
                }
#endif
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
                CheckPlayerIsValid();
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
#if NETCODE_DEBUG
                if (enablePacketLogging)
                    debugLog.Append(FixedString.Format(" ServerTick:{0}\n", serverTick));
#endif

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
#if NETCODE_DEBUG
                if (enablePacketLogging)
                    debugLog.Append(FixedString.Format("NewPrefabs: {0}", numPrefabs));
#endif
                if (numPrefabs > 0)
                {
                    var ghostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                    // The server only sends ghost types which have not been acked yet, acking takes one RTT so we need to check
                    // which prefab was the first included in the list sent by the server
                    int firstPrefab = (int)dataStream.ReadUInt();
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                    {
                        debugLog.Append(FixedString.Format(" FirstPrefab: {0}\n", firstPrefab));
                    }
#endif
                    for (int i = 0; i < numPrefabs; ++i)
                    {
                        GhostTypeComponent type;
                        ulong hash;
                        type.guid0 = dataStream.ReadUInt();
                        type.guid1 = dataStream.ReadUInt();
                        type.guid2 = dataStream.ReadUInt();
                        type.guid3 = dataStream.ReadUInt();
                        hash = dataStream.ReadULong();
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                        {
                            debugLog.Append(FixedString.Format("\t {0}-{1}-{2}-{3}", type.guid0, type.guid1, type.guid2, type.guid3));
                            debugLog.Append(FixedString.Format(" Hash:{0}\n", hash));
                        }
#endif
                        if (firstPrefab+i == ghostCollection.Length)
                        {
                            // This just adds the type, the prefab entity will be populated by the GhostCollectionSystem
                            ghostCollection.Add(new GhostCollectionPrefab{GhostType = type, GhostPrefab = Entity.Null, Hash = hash, Loading = GhostCollectionPrefab.LoadingState.NotLoading});
                        }
                        else if (type != ghostCollection[firstPrefab+i].GhostType || hash != ghostCollection[firstPrefab+i].Hash)
                        {
#if NETCODE_DEBUG
                            if (enablePacketLogging)
                            {
                                netDebugPacket.Log(debugLog);
                                netDebugPacket.Log(FixedString.Format("ERROR: ghost list item {0} was modified (Hash {1} -> {2})", firstPrefab + i, ghostCollection[firstPrefab + i].Hash, hash));
                            }
#endif
                            netDebug.LogError(FixedString.Format("GhostReceiveSystem ghost list item {0} was modified (Hash {1} -> {2})", firstPrefab + i, ghostCollection[firstPrefab + i].Hash, hash));
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
#if NETCODE_DEBUG
                if (enablePacketLogging)
                    debugLog.Append(FixedString.Format(" Total: {0} Despawn: {1} Update:{2}\n", totalGhostCount, despawnLen, updateLen));
#endif

                var data = default(DeserializeData);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                data.startPos = dataStream.GetBitsRead();
#endif
#if NETCODE_DEBUG
                if (enablePacketLogging && despawnLen > 0)
                {
                    FixedString32Bytes msg = "\t[Despawn IDs]";
                    debugLog.Append(msg);
                }
#endif
                for (var i = 0; i < despawnLen; ++i)
                {
                    uint encodedGhostId = dataStream.ReadPackedUInt(compressionModel);
                    var ghostId = (int)((encodedGhostId >> 1) | (encodedGhostId << 31));
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                        debugLog.Append(FixedString.Format(" {0}", ghostId));
#endif
                    Entity ent;
                    if (!ghostEntityMap.TryGetValue(ghostId, out ent))
                        continue;

                    ghostEntityMap.Remove(ghostId);

                    if (!ghostFromEntity.HasComponent(ent))
                    {
                        netDebug.LogError($"Trying to despawn a ghost ({ent}) which is in the ghost map but does not have a ghost component. This can happen if you manually delete a ghost on the client.");
                        continue;
                    }

                    if (predictedFromEntity.HasComponent(ent))
                        predictedDespawnQueue.Enqueue(new GhostDespawnSystem.DelayedDespawnGhost
                            {ghost = new SpawnedGhost{ghostId = ghostId, spawnTick = ghostFromEntity[ent].spawnTick}, tick = serverTick});
                    else
                        interpolatedDespawnQueue.Enqueue(new GhostDespawnSystem.DelayedDespawnGhost
                            {ghost = new SpawnedGhost{ghostId = ghostId, spawnTick = ghostFromEntity[ent].spawnTick}, tick = serverTick});
                }
#if NETCODE_DEBUG
                if (enablePacketLogging)
                    netDebugPacket.Log(debugLog);
#endif

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
                public uint baseGhostId;
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
#if NETCODE_DEBUG
                FixedString512Bytes debugLog = default;
#endif
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
                    data.baseGhostId = dataStream.ReadRawBits(1) == 0 ? 0 : PrespawnHelper.PrespawnGhostIdBase;

                    if (data.targetArch >= GhostTypeCollection.Length)
                    {
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                        {
                            netDebugPacket.Log(debugLog);
                            netDebugPacket.Log(FixedString.Format("ERROR: InvalidGhostType:{0}/{1} RelevantGhostCount:{2}\n", data.targetArch, GhostTypeCollection.Length, data.targetArchLen));
                        }
#endif
                        netDebug.LogError("Received invalid ghost type from server");
                        return false;
                    }
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                    {
                        var ghostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                        var prefabName = prefabNames[ghostCollection[(int)data.targetArch].GhostPrefab].Name;
                        debugLog.Append(FixedString.Format("\t GhostType:{0}({1}) RelevantGhostCount:{2}\n", prefabName, data.targetArch, data.targetArchLen));
                    }
#endif
                }

                --data.targetArchLen;

                if (data.baselineLen == 0)
                {
                    data.baselineTick = serverTick - dataStream.ReadPackedUInt(compressionModel);
                    data.baselineTick2 = serverTick - dataStream.ReadPackedUInt(compressionModel);
                    data.baselineTick3 = serverTick - dataStream.ReadPackedUInt(compressionModel);
                    data.baselineLen = dataStream.ReadPackedUInt(compressionModel);
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                        debugLog.Append(FixedString.Format("\t\tB0:{0} B1:{1} B2:{2} Count:{3}\n", data.baselineTick, data.baselineTick2, data.baselineTick3, data.baselineLen));
#endif
                    //baselineTick == 0 is only valid and possible for prespawn since tick=0 is special
                    if(data.baselineTick == 0 && (data.baseGhostId & PrespawnHelper.PrespawnGhostIdBase) == 0)
                    {
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                        {
                            netDebugPacket.Log(debugLog);
                            netDebugPacket.Log("ERROR: Invalid baseline");
                        }
#endif
                        netDebug.LogError("Received snapshot baseline for prespawn ghosts from server but the entity is not a prespawn");
                        return false;
                    }
                    if (data.baselineTick3 != serverTick &&
                        (data.baselineTick3 == data.baselineTick2 || data.baselineTick2 == data.baselineTick))
                    {
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                        {
                            netDebugPacket.Log(debugLog);
                            netDebugPacket.Log("ERROR: Invalid baseline");
                        }
#endif
                        netDebug.LogError("Received invalid snapshot baseline from server");
                        return false;
                    }
                }

                --data.baselineLen;
                int ghostId = (int)(dataStream.ReadPackedUInt(compressionModel) + data.baseGhostId);
#if NETCODE_DEBUG
                if (enablePacketLogging)
                    debugLog.Append(FixedString.Format("\t\t\tGID:{0}", ghostId));
#endif
                uint serverSpawnTick = 0;
                if (data.baselineTick == serverTick)
                {
                    //restrieve spawn tick only for non-prespawn ghosts
                    if (!PrespawnHelper.IsPrespawGhostId(ghostId))
                    {
                        serverSpawnTick = dataStream.ReadPackedUInt(compressionModel);
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                            debugLog.Append(FixedString.Format(" SpawnTick:{0}", serverSpawnTick));
#endif
                    }
                }

                //Get the data size
                uint ghostDataSizeInBits = 0;
                if(snaphostHasCompressedGhostSize)
                    ghostDataSizeInBits = dataStream.ReadPackedUIntDelta(0, compressionModel);

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
                int baselineDynamicDataIndex = -1;
                byte* snapshotDynamicDataPtr = null;
                uint snapshotDynamicDataCapacity = 0; // available space in the dynamic snapshot data history slot
                byte* baselineDynamicDataPtr = null;

                bool existingGhost = ghostEntityMap.TryGetValue(ghostId, out gent);
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
                    CheckSnapshotBufferSizeIsCorrect(snapshotDataBuffer, snapshotSize);
                    snapshotData = (byte*)snapshotDataBuffer.GetUnsafePtr();
                    snapshotDataComponent = snapshotDataFromEntity[gent];
                    snapshotDataComponent.LatestIndex = (snapshotDataComponent.LatestIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
                    snapshotDataFromEntity[gent] = snapshotDataComponent;
                    // If this is a prespawned ghost with no baseline tick set use the prespawn baseline
                    if (data.baselineTick == 0 && PrespawnHelper.IsPrespawGhostId(ghostFromEntity[gent].ghostId))
                    {
                        CheckPrespawnBaselineIsPresent(gent, ghostId);
                        var prespawnBaselineBuffer = prespawnBaselineBufferFromEntity[gent];
                        if (prespawnBaselineBuffer.Length > 0)
                        {
                            //Memcpy in this case is not necessary so we can safely re-assign the pointer
                            baselineData = (byte*)prespawnBaselineBuffer.GetUnsafeReadOnlyPtr();
                            netDebug.DebugLog(FixedString.Format("Client prespawn baseline ghost id={0} serverTick={1}", ghostFromEntity[gent].ghostId, serverTick));
                            //Prespawn baseline is a little different and store the base offset starting from the beginning of the buffer
                            //TODO: change the receive system so everything use this kind of logic (so offset start from DynamicHeader size in general)
                            if (typeData.NumBuffers > 0)
                            {
                                baselineDynamicDataPtr = baselineData + snapshotSize;
                            }
                        }
                        else
                        {
#if NETCODE_DEBUG
                            if (enablePacketLogging)
                            {
                                netDebugPacket.Log(debugLog);
                                netDebugPacket.Log("ERROR: Missing prespawn baseline");
                            }
#endif
                            //This is a non recoverable error. The client MUST have the prespawn baseline
                            netDebug.LogError(FixedString.Format("No prespawn baseline found for entity {0}:{1} ghostId={2}", gent.Index, gent.Version, ghostFromEntity[gent].ghostId));
                            return false;
                        }
                    }
                    else if (data.baselineTick != serverTick)
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
                                        throw new InvalidOperationException($"SnapshotDynamicDataBuffer buffer not found for ghost with id {ghostId}");
                                    baselineDynamicDataIndex = bi / snapshotSize;
                                }
                                break;
                            }
                        }

                        if (*(uint*)baselineData == 0)
                        {
#if NETCODE_DEBUG
                            if (enablePacketLogging)
                            {
                                netDebugPacket.Log(debugLog);
                                netDebugPacket.Log("ERROR: ack desync");
                            }
#endif
                            return false; // Ack desync detected
                        }
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
                        {
#if NETCODE_DEBUG
                            if (enablePacketLogging)
                            {
                                netDebugPacket.Log(debugLog);
                                netDebugPacket.Log("ERROR: ack desync");
                            }
#endif
                            return false; // Ack desync detected
                        }
                        snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                        var predictor = new GhostDeltaPredictor(serverTick, data.baselineTick, data.baselineTick2, data.baselineTick3);

                        for (int comp = 0; comp < typeData.NumComponents; ++comp)
                        {
                            int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                            //Buffers does not use delta prediction for the size and the contents
                            if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            {
                                CheckOffsetLessThanSnapshotBufferSize(snapshotOffset, GhostComponentCollection[serializerIdx].SnapshotSize, snapshotSize);
                                GhostComponentCollection[serializerIdx].PredictDelta.Ptr.Invoke(
                                    (IntPtr) (baselineData + snapshotOffset),
                                    (IntPtr) (baselineData2 + snapshotOffset),
                                    (IntPtr) (baselineData3 + snapshotOffset), ref predictor);
                                snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                            }
                            else
                            {
                                CheckOffsetLessThanSnapshotBufferSize(snapshotOffset, GhostSystemConstants.DynamicBufferComponentSnapshotSize, snapshotSize);
                                snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                            }
                        }
                    }
                    //buffers: retrieve the dynamic contents size and re-fit the snapshot dynamic history
                    if (typeData.NumBuffers > 0)
                    {
                        //Delta-decompress the dynamic data size
                        var buf = snapshotDynamicDataFromEntity[gent];
                        uint baselineDynamicDataSize = 0;
                        if (baselineDynamicDataIndex != -1)
                        {
                            var bufferPtr = (byte*)buf.GetUnsafeReadOnlyPtr();
                            baselineDynamicDataSize = ((uint*) bufferPtr)[baselineDynamicDataIndex];
                        }
                        else if (PrespawnHelper.IsPrespawGhostId(ghostId) && prespawnBaselineBufferFromEntity.HasComponent(gent))
                        {
                            CheckPrespawnBaselinePtrsAreValid(data, baselineData, ghostId, baselineDynamicDataPtr);
                            baselineDynamicDataSize = ((uint*)(baselineDynamicDataPtr))[0];
                        }
                        uint dynamicDataSize = dataStream.ReadPackedUIntDelta(baselineDynamicDataSize, compressionModel);

                        if (!snapshotDynamicDataFromEntity.HasComponent(gent))
                            throw new InvalidOperationException($"SnapshotDynamictDataBuffer buffer not found for ghost with id {ghostId}");

                        //Fit the snapshot buffer to accomodate the new size. Add some room for growth (20%)
                        var slotCapacity = SnapshotDynamicBuffersHelper.GetDynamicDataCapacity(SnapshotDynamicBuffersHelper.GetHeaderSize(), buf.Length);
                        var newCapacity = SnapshotDynamicBuffersHelper.CalculateBufferCapacity(dynamicDataSize, out var newSlotCapacity);
                        if (buf.Length < newCapacity)
                        {
                            //Perf: Is already copying over the contents to the new re-allocated buffer. It would be nice to avoid that
                            buf.ResizeUninitialized((int)newCapacity);
                            //Move buffer content around (because the slot size is changed)
                            if (slotCapacity > 0)
                            {
                                var bufferPtr = (byte*)buf.GetUnsafePtr() + SnapshotDynamicBuffersHelper.GetHeaderSize();
                                var sourcePtr = bufferPtr + GhostSystemConstants.SnapshotHistorySize*slotCapacity;
                                var destPtr = bufferPtr + GhostSystemConstants.SnapshotHistorySize*newSlotCapacity;
                                for (int i=0;i<GhostSystemConstants.SnapshotHistorySize;++i)
                                {
                                    destPtr -= newSlotCapacity;
                                    sourcePtr -= slotCapacity;
                                    UnsafeUtility.MemMove(destPtr, sourcePtr, slotCapacity);
                                }
                            }
                            slotCapacity = newSlotCapacity;
                        }
                        //write down the received data size inside the snapshot (used for delta compression) and setup dynamic data ptr
                        var bufPtr = (byte*)buf.GetUnsafePtr();
                        ((uint*)bufPtr)[snapshotDataComponent.LatestIndex] = dynamicDataSize;
                        //Retrive dynamic data ptrs
                        snapshotDynamicDataPtr = SnapshotDynamicBuffersHelper.GetDynamicDataPtr(bufPtr,snapshotDataComponent.LatestIndex, buf.Length);
                        snapshotDynamicDataCapacity = slotCapacity;
                        if (baselineDynamicDataIndex != -1)
                            baselineDynamicDataPtr = SnapshotDynamicBuffersHelper.GetDynamicDataPtr(bufPtr, baselineDynamicDataIndex, buf.Length);
                    }
                }
                else
                {
                    bool isPrespawn = PrespawnHelper.IsPrespawGhostId(ghostId);
                    if (existingGhost)
                    {
                        // The ghost entity map is out of date, clean it up
                        ghostEntityMap.Remove(ghostId);
                        if (ghostFromEntity.HasComponent(gent) && ghostFromEntity[gent].ghostType != data.targetArch)
                            netDebug.LogError(FixedString.Format("Received a ghost ({0}) with an invalid ghost type {1} (expected {2})", ghostId, data.targetArch, ghostFromEntity[gent].ghostType));
                        else if (isPrespawn)
                            netDebug.LogError("Found a prespawn ghost that has no entity connected to it. This can happend if you unload a scene or destroy the ghost entity on the client");
                        else
                            netDebug.LogError("Found a ghost in the ghost map which does not have an entity connected to it. This can happen if you delete ghost entities on the client.");
                    }
                    int prespawnSceneIndex = -1;
                    if (isPrespawn)
                    {
                        //Received a pre-spawned object that does not exist in the map.  Possible reasons:
                        // - Scene has been unloaded (but server didn't or the client unload before having some sort of ack)
                        // - Ghost has been destroyed by the client
                        // - Relevancy changes.

                        //Lookup the scene that prespawn belong to
                        var prespawnId = (int)(ghostId & ~PrespawnHelper.PrespawnGhostIdBase);
                        for (int i = 0; i < prespawnSceneStateArray.Length; ++i)
                        {
                            if (prespawnId >= prespawnSceneStateArray[i].FirstGhostId &&
                                prespawnId < prespawnSceneStateArray[i].FirstGhostId + prespawnSceneStateArray[i].PrespawnCount)
                            {
                                prespawnSceneIndex = i;
                                break;
                            }
                        }
                    }
                    if (data.baselineTick != serverTick)
                    {
                        //If the client unloaded a subscene before the server or the server will not do that at all, we threat that as a
                        //spurious/temporary inconsistency. The server will be notified soon that the client does not have that scenes anymore
                        //and will stop streaming the subscene ghosts to him.
                        //Try to recover by skipping the data. If the stream does not have a the ghost-size bits, fallback to the standard error.
                        if(isPrespawn && prespawnSceneIndex == -1 && snaphostHasCompressedGhostSize)
                        {
#if NETCODE_DEBUG
                            if (enablePacketLogging)
                            {
                                debugLog.Append(FixedString.Format("SKIP ({0}B)", ghostDataSizeInBits));
                                netDebugPacket.Log(debugLog);
                            }
#endif
                            while (ghostDataSizeInBits > 32)
                            {
                                dataStream.ReadRawBits(32);
                                ghostDataSizeInBits -= 32;
                            }
                            dataStream.ReadRawBits((int)ghostDataSizeInBits);
                            //Still consider the data as good and don't force resync on the server
                            return true;
                        }
                        // If the server specifies a baseline for a ghost we do not have that is an error
                        netDebug.LogError(FixedString.Format($"Received baseline for a ghost we do not have ghostId={0} baselineTick={1} serverTick={2}",ghostId, data.baselineTick, serverTick));
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                        {
                            netDebugPacket.Log(debugLog);
                            netDebugPacket.Log("ERROR: Received baseline for spawn");
                        }
#endif
                        return false;
                    }
                    ++data.newGhosts;
                    var ghostSpawnBuffer = ghostSpawnBufferFromEntity[ghostSpawnEntity];
                    snapshotDataBuffer = snapshotDataBufferFromEntity[ghostSpawnEntity];
                    var snapshotDataBufferOffset = snapshotDataBuffer.Length;
                    //Grow the ghostSpawnBuffer to include also the dynamic data size.
                    uint dynamicDataSize = 0;
                    if (typeData.NumBuffers > 0)
                        dynamicDataSize = dataStream.ReadPackedUIntDelta(0, compressionModel);
                    var spawnedGhost = new GhostSpawnBuffer
                    {
                        GhostType = (int) data.targetArch,
                        GhostID = ghostId,
                        DataOffset = snapshotDataBufferOffset,
                        DynamicDataSize = dynamicDataSize,
                        ClientSpawnTick = serverTick,
                        ServerSpawnTick = serverSpawnTick,
                        PrespawnIndex = -1
                    };
                    if (isPrespawn)
                    {
                        //When a prespawn ghost is re-spawned because of relevancy changes some components are missing
                        //(because they are added by the conversion system to the instance):
                        //SceneSection and PreSpawnedGhostIndex
                        //Without the PreSpawnedGhostIndex some queries does not report the ghost correctly and
                        //the without the SceneSection the ghost will not be destroyed in case the scene is belonging to
                        //is unloaded.
                        if (prespawnSceneIndex != -1)
                        {
                            spawnedGhost.PrespawnIndex = (int)(ghostId & ~PrespawnHelper.PrespawnGhostIdBase) - prespawnSceneStateArray[prespawnSceneIndex].FirstGhostId;
                            spawnedGhost.SceneGUID = prespawnSceneStateArray[prespawnSceneIndex].SceneGUID;
                            spawnedGhost.SectionIndex = prespawnSceneStateArray[prespawnSceneIndex].SectionIndex;
                        }
                        else
                        {
                            netDebug.LogError("Received a new instance of a pre-spawned ghost (relevancy changes) but no section with a enclosing id-range has been found");
                        }
                    }
                    ghostSpawnBuffer.Add(spawnedGhost);
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
                //the dynamicBufferOffset is used to track the dynamic content offset from the beginning of the dynamic history slot
                //for each entity
                uint dynamicBufferOffset = 0;

                snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                snapshotData += snapshotSize * snapshotDataComponent.LatestIndex;
                *(uint*)(snapshotData) = serverTick;
                uint* changeMask = (uint*)(snapshotData+4);
                uint anyChangeMaskThisEntity = 0;
                for (int cm = 0; cm < changeMaskUints; ++cm)
                {
                    var changeMaskUint = dataStream.ReadPackedUIntDelta(((uint*)(baselineData+4))[cm], compressionModel);
                    changeMask[cm] = changeMaskUint;
                    anyChangeMaskThisEntity |= changeMaskUint;
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                        debugLog.Append(FixedString.Format(" Changemask:{0}", NetDebug.PrintMask(changeMask[cm])));
#endif
                }

#if NETCODE_DEBUG
                int entityStartBit = dataStream.GetBitsRead();
#endif
                for (int comp = 0; comp < typeData.NumComponents; ++comp)
                {
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
#if NETCODE_DEBUG
                    FixedString128Bytes componentName = default;
                    int numBits = 0;
                    if (enablePacketLogging)
                    {
                        var componentTypeIndex = GhostComponentCollection[serializerIdx].ComponentType.TypeIndex;
                        componentName = componentTypeNameLookup[componentTypeIndex];
                        numBits = dataStream.GetBitsRead();
                    }
#endif
                    if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                    {
                        CheckSnaphostBufferOverflow(maskOffset, GhostComponentCollection[serializerIdx].ChangeMaskBits,
                            typeData.ChangeMaskBits, snapshotOffset, GhostComponentCollection[serializerIdx].SnapshotSize, snapshotSize);
                        GhostComponentCollection[serializerIdx].Deserialize.Ptr.Invoke((IntPtr) (snapshotData + snapshotOffset), (IntPtr) (baselineData + snapshotOffset), ref dataStream, ref compressionModel, (IntPtr) changeMask, maskOffset);
                        snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                        maskOffset += GhostComponentCollection[serializerIdx].ChangeMaskBits;
                    }
                    else
                    {
                        CheckSnaphostBufferOverflow(maskOffset, GhostSystemConstants.DynamicBufferComponentMaskBits,
                            typeData.ChangeMaskBits, snapshotOffset, GhostSystemConstants.DynamicBufferComponentSnapshotSize, snapshotSize);
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
                        CheckDynamicSnapshotBufferOverflow(dynamicBufferOffset, maskSize, bufLen*dynamicDataSnapshotStride, snapshotDynamicDataCapacity);
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
#if NETCODE_DEBUG
                    if (enablePacketLogging && anyChangeMaskThisEntity != 0)
                    {
                        if (debugLog.Length > (debugLog.Capacity >> 1))
                        {
                            FixedString32Bytes cont = " CONT";
                            debugLog.Append(cont);
                            netDebugPacket.Log(debugLog);
                            debugLog = "";
                        }
                        numBits = dataStream.GetBitsRead() - numBits;
                        #if UNITY_EDITOR || DEVELOPMENT_BUILD
                        debugLog.Append(FixedString.Format(" {0}:{1} ({2}B)", componentName, GhostComponentCollection[serializerIdx].PredictionErrorNames, numBits));
                        #else
                        debugLog.Append(FixedString.Format(" {0}:{1} ({2}B)", componentName, serializerIdx, numBits));
                        #endif
                    }
#endif
                }
#if NETCODE_DEBUG
                if (enablePacketLogging)
                {
                    if (anyChangeMaskThisEntity != 0)
                        debugLog.Append(FixedString.Format(" Total ({0}B)", dataStream.GetBitsRead()-entityStartBit));
                    FixedString32Bytes endLine = "\n";
                    debugLog.Append(endLine);
                    netDebugPacket.Log(debugLog);
                }
#endif

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                ++data.statCount;
                if (data.baselineTick == serverTick)
                    ++data.uncompressedCount;
#endif

                if (typeData.IsGhostGroup != 0)
                {
                    var groupLen = dataStream.ReadPackedUInt(compressionModel);
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                        netDebugPacket.Log(FixedString.Format("\t\t\tGrpLen:{0} [\n", groupLen));
#endif
                    for (var i = 0; i < groupLen; ++i)
                    {
                        var childData = default(DeserializeData);
                        if (!DeserializeEntity(serverTick, ref dataStream, ref childData))
                            return false;
                    }
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                        netDebugPacket.Log("\t\t\t]\n");
#endif
                }
                return true;
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void CheckPrespawnBaselineIsPresent(Entity gent, int ghostId)
            {
                if (!prespawnBaselineBufferFromEntity.HasComponent(gent))
                    throw new InvalidOperationException($"Prespawn baseline for ghost with id {ghostId} not present");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private static void CheckPrespawnBaselinePtrsAreValid(DeserializeData data, byte* baselineData, int ghostId,
                byte* baselineDynamicDataPtr)
            {
                if (baselineData == null)
                    throw new InvalidOperationException(
                        $"Prespawn ghost with id {ghostId} and archetype {data.targetArch} does not have a baseline");
                if (baselineDynamicDataPtr == null)
                    throw new InvalidOperationException(
                        $"Prespawn ghost with id {ghostId} and archetype {data.targetArch} does not have a baseline for the dynamic buffer");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private static void CheckDynamicSnapshotBufferOverflow(uint dynamicBufferOffset, uint maskSize, uint dynamicDataSize,
                uint snapshotDynamicDataCapacity)
            {
                if ((dynamicBufferOffset + maskSize + dynamicDataSize) > snapshotDynamicDataCapacity)
                    throw new InvalidOperationException("DynamicData Snapshot buffer overflow during deserialize");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void CheckSnaphostBufferOverflow(int maskOffset, int maskBits, int totalMaskBits,
                int snapshotOffset, int snapshotSize, int bufferSize)
            {
                if (maskOffset + maskBits > totalMaskBits || snapshotOffset + GhostCollectionSystem.SnapshotSizeAligned(snapshotSize) > bufferSize)
                    throw new InvalidOperationException("Snapshot buffer overflow during deserialize");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private static void CheckOffsetLessThanSnapshotBufferSize(int snapshotOffset, int snapshotSize, int bufferSize)
            {
                if (snapshotOffset + GhostCollectionSystem.SnapshotSizeAligned(snapshotSize) > bufferSize)
                    throw new InvalidOperationException("Snapshot buffer overflow during predict");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private static void CheckSnapshotBufferSizeIsCorrect(DynamicBuffer<SnapshotDataBuffer> snapshotDataBuffer, int snapshotSize)
            {
                if (snapshotDataBuffer.Length != snapshotSize * GhostSystemConstants.SnapshotHistorySize)
                    throw new InvalidOperationException($"Invalid snapshot buffer size");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void CheckPlayerIsValid()
            {
                if (players.Length > 1)
                    throw new InvalidOperationException("Ghost receive system only supports a single connection");
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
                LastGhostMapWriter.Complete();
                // If there were no ghosts spawned at runtime we don't need to cleanup
                if (ghostCleanupGroup.IsEmptyIgnoreFilter &&
                    m_spawnedGhostEntityMap.Count() == 0 && m_ghostEntityMap.Count() == 0)
                    return;
                var clearMapJob = new ClearMapJob
                {
                    ghostMap = m_ghostEntityMap,
                    spawnedGhostMap = m_spawnedGhostEntityMap
                };
                var clearHandle = clearMapJob.Schedule(Dependency);
                LastGhostMapWriter = clearHandle;
                if (!ghostCleanupGroup.IsEmptyIgnoreFilter)
                {
                    var clearJob = new ClearGhostsJob
                    {
                        entitiesType = GetEntityTypeHandle(),
                        commandBuffer = commandBuffer.AsParallelWriter()
                    };
                    Dependency = clearJob.Schedule(clearJobGroup, Dependency);
                    m_Barrier.AddJobHandleForProducer(Dependency);
                }
                Dependency = JobHandle.CombineDependencies(Dependency, clearHandle);
                return;
            }

            // Don't start ghost snapshot processing until we're in game, but allow the cleanup code above to run
            if (!HasSingleton<NetworkStreamInGame>())
            {
                return;
            }

#if NETCODE_DEBUG
            FixedString128Bytes timestampAndTick = default;
            if (!m_EnablePacketLoggingQuery.IsEmptyIgnoreFilter)
            {
                var packetLoggers = m_PacketLoggers;
                var worldName = World.Name;
                Entities.WithStoreEntityQueryInField(ref m_EnablePacketLoggingQuery).WithoutBurst().WithAll<EnablePacketLogging>().ForEach((Entity Entity) =>
                {
                    if (!m_NetDebugPacket.IsCreated)
                    {
                        m_NetDebugPacket.Init(worldName, 0);
                        packetLoggers.Init(worldName, 0);
                    }
                    packetLoggers.Process(ref m_NetDebugPacket, 0);
                }).Run();
                timestampAndTick = FixedString.Format("[{0}][PredictedTick:{1}]", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), m_ClientSimulationSystemGroup.ServerTick);
            }
#endif

            JobHandle playerHandle;
            JobHandle prespawnHandle;
            var readJob = new ReadStreamJob
            {
                GhostCollectionSingleton = GetSingletonEntity<GhostCollection>(),
                GhostComponentCollectionFromEntity = GetBufferFromEntity<GhostComponentSerializer.State>(true),
                GhostTypeCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefabSerializer>(true),
                GhostComponentIndexFromEntity = GetBufferFromEntity<GhostCollectionComponentIndex>(true),
                GhostCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefab>(),
                players = playerGroup.ToEntityArrayAsync(Allocator.TempJob, out playerHandle),
                snapshotFromEntity = GetBufferFromEntity<IncomingSnapshotDataStreamBufferComponent>(),
                snapshotDataBufferFromEntity = GetBufferFromEntity<SnapshotDataBuffer>(),
                snapshotDynamicDataFromEntity = GetBufferFromEntity<SnapshotDynamicDataBuffer>(),
                ghostSpawnBufferFromEntity = GetBufferFromEntity<GhostSpawnBuffer>(),
                prespawnBaselineBufferFromEntity = GetBufferFromEntity<PrespawnGhostBaseline>(true),
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
                tempDynamicData = m_tempDynamicData,
                prespawnSceneStateArray = subSceneGroup.ToComponentDataArrayAsync<SubSceneWithGhostStateComponent>(Allocator.TempJob, out prespawnHandle),
#if NETCODE_DEBUG
                netDebugPacket = m_NetDebugPacket,
                prefabNames = GetComponentDataFromEntity<PrefabDebugName>(true),
                componentTypeNameLookup = m_NetDebugSystem.ComponentTypeNameLookup,
                enableLoggingFromEntity = GetComponentDataFromEntity<EnablePacketLogging>(),
                timestampAndTick = timestampAndTick,
#endif
                netDebug = m_NetDebugSystem.NetDebug,
                snaphostHasCompressedGhostSize = GhostSystemConstants.SnaphostHasCompressedGhostSize
            };
            var tempDeps = new NativeArray<JobHandle>(5, Allocator.Temp);
            tempDeps[0] = Dependency;
            tempDeps[1] = LastGhostMapWriter;
            tempDeps[2] = m_GhostDespawnSystem.LastQueueWriter;
            tempDeps[3] = playerHandle;
            tempDeps[4] = prespawnHandle;
            Dependency = readJob.Schedule(JobHandle.CombineDependencies(tempDeps));
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
                    m_NetDebugSystem.NetDebug.LogError($"Ghost ID {ghostId} has already been added");
                    m_ghostEntityMap[ghostId] = ent;
                }
            }
        }

        //Remove from the ghost map all the prespawns
        internal void ClearPrespawnGhostMap()
        {
            var keys = m_spawnedGhostEntityMap.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; ++i)
            {
                if (PrespawnHelper.IsPrespawGhostId(keys[i].ghostId))
                {
                    m_ghostEntityMap.Remove(keys[i].ghostId);
                    m_spawnedGhostEntityMap.Remove(keys[i]);
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
                    m_NetDebugSystem.NetDebug.LogError($"Ghost ID {ghost.ghostId} has already been added");
                    m_ghostEntityMap[ghost.ghostId] = ent;
                }

                if (!m_spawnedGhostEntityMap.TryAdd(ghost, ent))
                {
                    m_NetDebugSystem.NetDebug.LogError($"Ghost ID {ghost.ghostId} has already been added to the spawned ghost map");
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
                    m_NetDebugSystem.NetDebug.LogError($"Ghost ID {ghost.ghostId} has already been added to the spawned ghost map");
                    m_spawnedGhostEntityMap[ghost] = ent;
                }
            }
        }
    }
}

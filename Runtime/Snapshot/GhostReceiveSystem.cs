using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
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

        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private GhostDespawnSystem m_GhostDespawnSystem;

        private GhostCollectionSystem m_GhostCollectionSystem;
        private NativeArray<ulong> m_ReceivedGhostVersion;
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
            m_GhostCollectionSystem = World.GetOrCreateSystem<GhostCollectionSystem>();

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

            RequireSingletonForUpdate<GhostPrefabCollectionComponent>();
            m_ReceivedGhostVersion = new NativeArray<ulong>(1, Allocator.Persistent);
            m_GhostCompletionCount = new NativeArray<int>(2, Allocator.Persistent);
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
            m_ReceivedGhostVersion.Dispose();
            m_GhostCompletionCount.Dispose();
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
            [ReadOnly] public NativeArray<GhostComponentSerializer.State> GhostComponentCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostTypeState> GhostTypeCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostComponentIndex> GhostComponentIndex;

            [DeallocateOnJobCompletion] public NativeArray<Entity> players;
            public BufferFromEntity<IncomingSnapshotDataStreamBufferComponent> snapshotFromEntity;
            public BufferFromEntity<SnapshotDataBuffer> snapshotDataBufferFromEntity;
            public BufferFromEntity<GhostSpawnBuffer> ghostSpawnBufferFromEntity;
            public ComponentDataFromEntity<SnapshotData> snapshotDataFromEntity;
            public ComponentDataFromEntity<NetworkSnapshotAckComponent> snapshotAckFromEntity;
            public NativeHashMap<int, Entity> ghostEntityMap;
            public NativeArray<ulong> receivedGhostVersion;
            public NetworkCompressionModel compressionModel;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> interpolatedDespawnQueue;
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> predictedDespawnQueue;
            [ReadOnly] public ComponentDataFromEntity<PredictedGhostComponent> predictedFromEntity;
            [ReadOnly] public ComponentDataFromEntity<GhostComponent> ghostFromEntity;
            public bool isThinClient;

            public EntityCommandBuffer commandBuffer;
            public Entity ghostSpawnEntity;
            public ulong ghostVersion;
            public NativeArray<int> GhostCompletionCount;

            public unsafe void Execute()
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                for (int i = 0; i < netStats.Length; ++i)
                {
                    netStats[i] = 0;
                }
#endif

                // FIXME: should handle any number of connections with individual ghost mappings for each
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (players.Length > 1)
                    throw new InvalidOperationException("Ghost receive system only supports a single connection");
#endif

                var snapshot = snapshotFromEntity[players[0]];
                if (snapshot.Length == 0)
                    return;

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

                // Find out if the stream is compatible
                if(dataStream.ReadPackedUInt(compressionModel) == 1)
                {
                    ulong hi = dataStream.ReadPackedUInt(compressionModel);
                    ulong lo = dataStream.ReadPackedUInt(compressionModel);
                    receivedGhostVersion[0] = (hi << 32) | lo;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (receivedGhostVersion[0] == 0)
                        throw new InvalidOperationException($"ghost version should always be != 0");
#endif
                }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                else if (receivedGhostVersion[0] == 0)
                    throw new InvalidOperationException($"received snapshot does not contains the ghost version (server received an ack) but the previous received value is invalid or not set");
#endif
                if (receivedGhostVersion[0] != ghostVersion)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.LogError($"GhostReceiveSystem incompatible ghost version {ghostVersion}. Server: {receivedGhostVersion[0]}");
#endif
                    commandBuffer.AddComponent(players[0], new NetworkStreamRequestDisconnect{Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
                    return;
                }
                snapshotAckFromEntity[players[0]] = ack;

                if (isThinClient)
                    return;

                uint totalGhostCount = dataStream.ReadPackedUInt(compressionModel);
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
                bool existingGhost = ghostEntityMap.TryGetValue(ghostId, out gent);
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
                        for (int comp = 0; comp < typeData.NumComponents; ++comp)
                        {
                            int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if (snapshotOffset+GhostComponentCollection[compIdx].SnapshotSize > snapshotSize)
                                throw new InvalidOperationException("Snapshot buffer overflow during predict");
#endif
                            GhostComponentCollection[compIdx].PredictDelta.Ptr.Invoke((IntPtr)(baselineData + snapshotOffset), (IntPtr)(baselineData2 + snapshotOffset), (IntPtr)(baselineData3 + snapshotOffset), ref predictor);
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                        }
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
                    ghostSpawnBuffer.Add(new GhostSpawnBuffer {GhostType = (int)data.targetArch, GhostID = ghostId, DataOffset = snapshotDataBufferOffset, ClientSpawnTick = serverTick, ServerSpawnTick = serverSpawnTick});
                    snapshotDataBuffer.ResizeUninitialized(snapshotDataBufferOffset + snapshotSize);
                    snapshotData = (byte*)snapshotDataBuffer.GetUnsafePtr() + snapshotDataBufferOffset;
                    UnsafeUtility.MemClear(snapshotData, snapshotSize);
                    snapshotDataComponent = new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0};
                }
                int maskOffset = 0;
                snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                snapshotData += snapshotSize * snapshotDataComponent.LatestIndex;
                *(uint*)(snapshotData) = serverTick;
                uint* changeMask = (uint*)(snapshotData+4);
                for (int cm = 0; cm < changeMaskUints; ++cm)
                    changeMask[cm] = dataStream.ReadPackedUIntDelta(((uint*)(baselineData+4))[cm], compressionModel);
                for (int comp = 0; comp < typeData.NumComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (maskOffset+GhostComponentCollection[compIdx].ChangeMaskBits > typeData.ChangeMaskBits || snapshotOffset+GhostComponentCollection[compIdx].SnapshotSize > snapshotSize)
                        throw new InvalidOperationException("Snapshot buffer overflow during deserialize");
#endif
                    GhostComponentCollection[compIdx].Deserialize.Ptr.Invoke((IntPtr)(snapshotData + snapshotOffset), (IntPtr)(baselineData + snapshotOffset), ref dataStream, ref compressionModel, (IntPtr)changeMask, maskOffset);
                    snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                    maskOffset += GhostComponentCollection[compIdx].ChangeMaskBits;
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
            if (m_NetStats.IsCreated && m_NetStats.Length != m_GhostCollectionSystem.m_GhostTypeCollection.Length * 3 + 3 + 1)
                m_NetStats.Dispose();
            if (!m_NetStats.IsCreated)
                m_NetStats = new NativeArray<uint>(m_GhostCollectionSystem.m_GhostTypeCollection.Length * 3 + 3 + 1, Allocator.Persistent);
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
                //I need to reset the received version if the player is not in game.
                m_ReceivedGhostVersion[0] = 0;
                return;
            }

            JobHandle playerHandle;
            var readJob = new ReadStreamJob
            {
                GhostComponentCollection = m_GhostCollectionSystem.m_GhostComponentCollection,
                GhostTypeCollection = m_GhostCollectionSystem.m_GhostTypeCollection,
                GhostComponentIndex = m_GhostCollectionSystem.m_GhostComponentIndex,
                players = playerGroup.ToEntityArrayAsync(Allocator.TempJob,
                    out playerHandle),
                snapshotFromEntity = GetBufferFromEntity<IncomingSnapshotDataStreamBufferComponent>(),
                snapshotDataBufferFromEntity = GetBufferFromEntity<SnapshotDataBuffer>(),
                ghostSpawnBufferFromEntity = GetBufferFromEntity<GhostSpawnBuffer>(),
                snapshotDataFromEntity = GetComponentDataFromEntity<SnapshotData>(),
                snapshotAckFromEntity = GetComponentDataFromEntity<NetworkSnapshotAckComponent>(),
                ghostEntityMap = m_ghostEntityMap,
                receivedGhostVersion = m_ReceivedGhostVersion,
                compressionModel = m_CompressionModel,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats = m_NetStats,
#endif
                interpolatedDespawnQueue = m_GhostDespawnSystem.InterpolatedDespawnQueue,
                predictedDespawnQueue = m_GhostDespawnSystem.PredictedDespawnQueue,
                predictedFromEntity = GetComponentDataFromEntity<PredictedGhostComponent>(true),
                ghostFromEntity = GetComponentDataFromEntity<GhostComponent>(true),
                isThinClient = HasSingleton<ThinClientComponent>(),
                commandBuffer = commandBuffer,
                ghostSpawnEntity = GetSingletonEntity<GhostSpawnQueueComponent>(),
                ghostVersion = m_GhostCollectionSystem.GhostTypeCollectionHash,
                GhostCompletionCount = m_GhostCompletionCount
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

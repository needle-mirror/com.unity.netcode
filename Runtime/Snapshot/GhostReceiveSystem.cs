using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    public struct GhostEntity
    {
        public Entity entity;
        public uint spawnTick;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public int ghostType;
#endif
    }

    public struct GhostDeserializerState
    {
        public NativeHashMap<int, GhostEntity> GhostMap;
    }

    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(PopulatePreSpawnedGhosts))]
    public class GhostReceiveSystem<TGhostDeserializerCollection> : JobComponentSystem
        where TGhostDeserializerCollection : struct, IGhostDeserializerCollection
    {
        private EntityQuery playerGroup;
        private EntityQuery ghostCleanupGroup;
        private EntityQuery clearJobGroup;

        private TGhostDeserializerCollection serializers;

        private GhostUpdateSystemGroup m_GhostUpdateSystemGroup;
        private NativeHashMap<int, GhostEntity> m_ghostEntityMap;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private GhostDespawnSystem m_GhostDespawnSystem;

        protected override void OnCreate()
        {
            serializers = default(TGhostDeserializerCollection);
            m_GhostUpdateSystemGroup = World.GetOrCreateSystem<GhostUpdateSystemGroup>();
            m_ghostEntityMap = m_GhostUpdateSystemGroup.GhostEntityMap;

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
            m_NetStats = new NativeArray<uint>(serializers.Length * 3 + 3 + 1, Allocator.Persistent);
            m_StatsCollection = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif
            m_GhostDespawnSystem = World.GetOrCreateSystem<GhostDespawnSystem>();

            RequireSingletonForUpdate<GhostPrefabCollectionComponent>();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private NativeArray<uint> m_NetStats;
        private GhostStatsCollectionSystem m_StatsCollection;
#endif
        private NetworkCompressionModel m_CompressionModel;
        private bool m_serializersAreInitialized = false;

        protected override void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats.Dispose();
#endif
            m_CompressionModel.Dispose();
        }

        [BurstCompile]
        struct ClearGhostsJob : IJobChunk
        {
            public EntityCommandBuffer.Concurrent commandBuffer;
            [ReadOnly] public ArchetypeChunkEntityType entitiesType;

            public void LambdaMethod(Entity entity, int index)
            {
                commandBuffer.RemoveComponent<GhostComponent>(index, entity);
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
            public NativeHashMap<int, GhostEntity> ghostMap;

            public void Execute()
            {
                ghostMap.Clear();
            }
        }

        [BurstCompile]
        struct ReadStreamJob : IJob
        {
            [DeallocateOnJobCompletion] public NativeArray<Entity> players;
            public BufferFromEntity<IncomingSnapshotDataStreamBufferComponent> snapshotFromEntity;
            public ComponentDataFromEntity<NetworkSnapshotAckComponent> snapshotAckFromEntity;
            public NativeHashMap<int, GhostEntity> ghostEntityMap;
            public NetworkCompressionModel compressionModel;
            public TGhostDeserializerCollection serializers;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> interpolatedDespawnQueue;
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> predictedDespawnQueue;
            [ReadOnly] public ComponentDataFromEntity<PredictedGhostComponent> predictedFromEntity;
            public bool isThinClient;

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
                snapshotAckFromEntity[players[0]] = ack;

                if (isThinClient)
                    return;

                uint despawnLen = dataStream.ReadUInt();
                uint updateLen = dataStream.ReadUInt();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                int startPos = dataStream.GetBitsRead();
#endif
                for (var i = 0; i < despawnLen; ++i)
                {
                    int ghostId = (int) dataStream.ReadPackedUInt(compressionModel);
                    GhostEntity ent;
                    if (!ghostEntityMap.TryGetValue(ghostId, out ent))
                        continue;

                    ghostEntityMap.Remove(ghostId);

                    if (predictedFromEntity.Exists(ent.entity))
                        predictedDespawnQueue.Enqueue(new GhostDespawnSystem.DelayedDespawnGhost
                            {ghost = ent.entity, tick = serverTick});
                    else
                        interpolatedDespawnQueue.Enqueue(new GhostDespawnSystem.DelayedDespawnGhost
                            {ghost = ent.entity, tick = serverTick});
                }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                int curPos = dataStream.GetBitsRead();
                netStats[0] = serverTick;
                netStats[1] = despawnLen;
                netStats[2] = (uint) (dataStream.GetBitsRead() - startPos);
                netStats[3] = 0;
                startPos = curPos;
                uint statCount = 0;
                uint uncompressedCount = 0;
#endif

                uint targetArch = 0;
                uint targetArchLen = 0;
                uint baselineTick = 0;
                uint baselineTick2 = 0;
                uint baselineTick3 = 0;
                uint baselineLen = 0;
                int newGhosts = 0;
                for (var i = 0; i < updateLen; ++i)
                {
                    if (targetArchLen == 0)
                    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        curPos = dataStream.GetBitsRead();
                        if (statCount > 0)
                        {
                            int statType = (int) targetArch;
                            netStats[statType * 3 + 4] = netStats[statType * 3 + 4] + statCount;
                            netStats[statType * 3 + 5] = netStats[statType * 3 + 5] + (uint) (curPos - startPos);
                            netStats[statType * 3 + 6] = netStats[statType * 3 + 6] + uncompressedCount;
                        }

                        startPos = curPos;
                        statCount = 0;
                        uncompressedCount = 0;
#endif
                        targetArch = dataStream.ReadPackedUInt(compressionModel);
                        targetArchLen = dataStream.ReadPackedUInt(compressionModel);
                    }

                    --targetArchLen;

                    if (baselineLen == 0)
                    {
                        baselineTick = serverTick - dataStream.ReadPackedUInt(compressionModel);
                        baselineTick2 = serverTick - dataStream.ReadPackedUInt(compressionModel);
                        baselineTick3 = serverTick - dataStream.ReadPackedUInt(compressionModel);
                        baselineLen = dataStream.ReadPackedUInt(compressionModel);
                        if (baselineTick3 != serverTick &&
                            (baselineTick3 == baselineTick2 || baselineTick2 == baselineTick))
                        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            throw new InvalidOperationException("Received invalid snapshot baseline from server");
#else
                            baselineTick = baselineTick2 = baselineTick3 = serverTick;
#endif
                        }
                    }

                    --baselineLen;

                    int ghostId = (int) dataStream.ReadPackedUInt(compressionModel);
                    GhostEntity gent;
                    if (ghostEntityMap.TryGetValue(ghostId, out gent))
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (gent.ghostType != targetArch)
                            throw new InvalidOperationException("Received a ghost with an invalid ghost type");
                        //throw new InvalidOperationException("Received a ghost with an invalid ghost type " + targetArch + ", expected " + gent.ghostType);
#endif
                        if (!serializers.Deserialize((int) targetArch, gent.entity, serverTick, baselineTick,
                            baselineTick2, baselineTick3, ref dataStream, compressionModel))
                        {
                            // Desync - reset received snapshots
                            ack.ReceivedSnapshotByLocalMask = 0;
                            ack.LastReceivedSnapshotByLocal = 0;
                            snapshotAckFromEntity[players[0]] = ack;
                        }
                    }
                    else
                    {
                        ++newGhosts;
                        serializers.Spawn((int) targetArch, ghostId, serverTick, ref dataStream, compressionModel);
                    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    ++statCount;
                    if (baselineTick == serverTick)
                        ++uncompressedCount;
#endif
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (statCount > 0)
                {
                    curPos = dataStream.GetBitsRead();
                    int statType = (int) targetArch;
                    netStats[statType * 3 + 4] = netStats[statType * 3 + 4] + statCount;
                    netStats[statType * 3 + 5] = netStats[statType * 3 + 5] + (uint) (curPos - startPos);
                    netStats[statType * 3 + 6] = netStats[statType * 3 + 6] + uncompressedCount;
                }
#endif
                while (ghostEntityMap.Capacity < ghostEntityMap.Count() + newGhosts)
                    ghostEntityMap.Capacity += 1024;

                snapshot.Clear();
            }
        }


        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_StatsCollection.CurrentNameOwner != this)
                m_StatsCollection.SetGhostNames(this, serializers.CreateSerializerNameList());
            m_StatsCollection.AddSnapshotStats(m_NetStats);
#endif
            //Lazy initialization is necessary due to fact we can't guarantee that all the dependent systems
            //has been initialized when the OnCreate is called.
            if (!m_serializersAreInitialized)
            {
                serializers.Initialize(World);
                m_serializersAreInitialized = true;
            }

            var commandBuffer = m_Barrier.CreateCommandBuffer();
            if (playerGroup.IsEmptyIgnoreFilter)
            {
                // If there were no ghosts spawned at runtime we don't need to cleanup
                if (ghostCleanupGroup.IsEmptyIgnoreFilter)
                    return inputDeps;
                m_GhostDespawnSystem.LastQueueWriter.Complete();
                m_GhostDespawnSystem.InterpolatedDespawnQueue.Clear();
                m_GhostDespawnSystem.PredictedDespawnQueue.Clear();
                var clearMapJob = new ClearMapJob
                {
                    ghostMap = m_ghostEntityMap
                };
                var clearHandle = clearMapJob.Schedule(inputDeps);
                m_GhostUpdateSystemGroup.LastGhostMapWriter = clearHandle;
                var clearJob = new ClearGhostsJob
                {
                    entitiesType = GetArchetypeChunkEntityType(),
                    commandBuffer = commandBuffer.ToConcurrent()
                };
                inputDeps = clearJob.Schedule(clearJobGroup, inputDeps);
                m_Barrier.AddJobHandleForProducer(inputDeps);
                return JobHandle.CombineDependencies(inputDeps, clearHandle);
            }

            // Don't start ghost snapshot processing until we're in game, but allow the cleanup code above to run
            if (!HasSingleton<NetworkStreamInGame>())
                return inputDeps;

            serializers.BeginDeserialize(this);
            JobHandle playerHandle;
            var readJob = new ReadStreamJob
            {
                players = playerGroup.ToEntityArrayAsync(Allocator.TempJob, out playerHandle),
                snapshotFromEntity = GetBufferFromEntity<IncomingSnapshotDataStreamBufferComponent>(),
                snapshotAckFromEntity = GetComponentDataFromEntity<NetworkSnapshotAckComponent>(),
                ghostEntityMap = m_ghostEntityMap,
                compressionModel = m_CompressionModel,
                serializers = serializers,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats = m_NetStats,
#endif
                interpolatedDespawnQueue = m_GhostDespawnSystem.InterpolatedDespawnQueue,
                predictedDespawnQueue = m_GhostDespawnSystem.PredictedDespawnQueue,
                predictedFromEntity = GetComponentDataFromEntity<PredictedGhostComponent>(true),
                isThinClient = HasSingleton<ThinClientComponent>(),
            };
            inputDeps = readJob.Schedule(JobHandle.CombineDependencies(inputDeps, playerHandle,
                m_GhostDespawnSystem.LastQueueWriter));
            m_GhostDespawnSystem.LastQueueWriter = inputDeps;

            m_Barrier.AddJobHandleForProducer(inputDeps);
            return inputDeps;
        }

        public static T InvokeSpawn<T>(uint snapshot,
            ref DataStreamReader reader, NetworkCompressionModel compressionModel)
            where T : struct, ISnapshotData<T>
        {
            var snapshotData = default(T);
            var baselineData = default(T);
            snapshotData.Deserialize(snapshot, ref baselineData, ref reader, compressionModel);
            return snapshotData;
        }

        public static bool InvokeDeserialize<T>(BufferFromEntity<T> snapshotFromEntity,
            Entity entity, uint snapshot, uint baseline, uint baseline2, uint baseline3,
            ref DataStreamReader reader, NetworkCompressionModel compressionModel)
            where T : struct, ISnapshotData<T>
        {
            DynamicBuffer<T> snapshotArray = snapshotFromEntity[entity];
            var data = default(T);
            var baselineData = default(T);
            if (baseline != snapshot)
            {
                for (int i = 0; i < snapshotArray.Length; ++i)
                {
                    if (snapshotArray[i].Tick == baseline)
                    {
                        baselineData = snapshotArray[i];
                        break;
                    }
                }

                if (baselineData.Tick == 0)
                {
                    data.Deserialize(snapshot, ref baselineData, ref reader, compressionModel);
                    return false; // Ack desync detected
                }
            }

            if (baseline3 != snapshot)
            {
                var baselineData2 = default(T);
                var baselineData3 = default(T);
                for (int i = 0; i < snapshotArray.Length; ++i)
                {
                    if (snapshotArray[i].Tick == baseline2)
                    {
                        baselineData2 = snapshotArray[i];
                    }

                    if (snapshotArray[i].Tick == baseline3)
                    {
                        baselineData3 = snapshotArray[i];
                    }
                }

                if (baselineData2.Tick == 0 || baselineData3.Tick == 0)
                {
                    data.Deserialize(snapshot, ref baselineData, ref reader, compressionModel);
                    return false; // Ack desync detected
                }

                baselineData.PredictDelta(snapshot, ref baselineData2, ref baselineData3);
            }

            data.Deserialize(snapshot, ref baselineData, ref reader, compressionModel);
            // Replace the oldest snapshot and add a new one
            if (snapshotArray.Length == GhostSystemConstants.SnapshotHistorySize)
                snapshotArray.RemoveAt(0);
            snapshotArray.Add(data);
            return true;
        }

    }
}

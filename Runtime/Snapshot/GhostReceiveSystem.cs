#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// Struct used to uniquely identify a ghost given its id and spawning time.
    /// </summary>
    public struct SpawnedGhost : IEquatable<SpawnedGhost>
    {
        /// <summary>
        /// The id assigned to the ghost by the server
        /// </summary>
        public int ghostId;
        /// <summary>
        /// The tick at which the ghost has been spawned by the server
        /// </summary>
        public NetworkTick spawnTick;
        /// <summary>
        /// Produce the hash code for the SpawnedGhost.
        /// </summary>
        /// <returns>Ghost id</returns>
        public override int GetHashCode()
        {
            return ghostId;
        }
        /// <summary>
        /// Construct a SpawnedEntity from a <see cref="GhostInstance"/>
        /// </summary>
        /// <param name="ghostInstance">The ghost from which to construct a SpawnedEntity</param>
        public SpawnedGhost(in GhostInstance ghostInstance)
        {
            ghostId = ghostInstance.ghostId;
            spawnTick = ghostInstance.spawnTick;
        }
        /// <summary>
        /// Construct a SpawnedEntity using the ghost identifier and the spawn tick
        /// </summary>
        /// <param name="ghostId">Ghost id</param>
        /// <param name="spawnTick">Spawn tick</param>
        public SpawnedGhost(int ghostId, NetworkTick spawnTick)
        {
            this.ghostId = ghostId;
            this.spawnTick = spawnTick;
        }
        /// <summary>
        /// The SpawnedGhost are identical if both id and tick match.
        /// </summary>
        /// <param name="ghost">Ghost to compare with</param>
        /// <returns>Whether ghost id and spawn tick are identical</returns>
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

    /// <summary>
    /// Inter-op struct used to pass arguments to the ghost component serializers (see <see cref="GhostComponentSerializer"/>).
    /// </summary>
    public struct GhostDeserializerState
    {
        /// <summary>
        /// A map that store an entity reference for each spawned ghost.
        /// </summary>
        public NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly GhostMap;
        /// <summary>
        /// The server tick we are deserializing.
        /// </summary>
        public NetworkTick SnapshotTick;
        /// <summary>
        /// The NetworkId of the client owning the ghost (if the ghost has an <see cref="NetCode.GhostOwner"/>)
        /// </summary>
        public int GhostOwner;
        /// <summary>
        /// <para>- If set to <see cref="SendToOwnerType.SendToOwner"/>, the component is deserialized
        /// only if the <see cref="GhostOwner"/> equals the current client NetworkId..</para>
        /// <para>- If set to <see cref="SendToOwnerType.SendToNonOwner"/>, the component is deserialized
        /// only if the <see cref="GhostOwner"/> is not equals to the current client NetworkId.</para>
        /// </summary>
        public SendToOwnerType SendToOwner;
    }

    /// <summary>
    /// <para>
    /// System present only in clients worlds, receive and decode the ghost snapshosts sent by the server.
    /// </para>
    /// <para>
    /// When a new snapshost is received, the system will start decoding the packet protocol by extracting:
    /// </para>
    /// <para>-the list of ghost that need to despawned</para>
    /// <para>-for each serialized ghost, it delta-compressed or uncompressed state</para>
    /// <para>
    /// The system will schedule spawning and despawning ghosts requests, by using
    /// the <see cref="GhostSpawnBuffer"/>, and <see cref="GhostDespawnQueues"/> respectively.
    /// </para>
    /// <para>
    /// When a new state snapshot is received for a ghost that has been already spawned (see <see cref="SpawnedGhostEntityMap"/>),
    /// the state is deserialized and added to the entity <see cref="SnapshotDataBuffer"/> history buffer.
    /// </para>
    /// <para>
    /// The received snapshot are recorded into the <see cref="NetworkSnapshotAck"/>, that will then used to
    /// send back to the server, as part of the command stream, the latest received snapshot by the client.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(GhostCollectionSystem))]
    [UpdateAfter(typeof(NetDebugSystem))]
    [BurstCompile]
    public unsafe partial struct GhostReceiveSystem : ISystem
    {
        EntityQuery m_ConnectionsQuery;
        EntityQuery m_GhostCleanupQuery;
        EntityQuery m_SubSceneQuery;

        NativeParallelHashMap<int, Entity> m_GhostEntityMap;
        NativeParallelHashMap<SpawnedGhost, Entity> m_SpawnedGhostEntityMap;
        NativeList<byte> m_TempDynamicData;

        NativeArray<int> m_GhostCompletionCount;
        StreamCompressionModel m_CompressionModel;
        static readonly Unity.Profiling.ProfilerMarker k_Scheduling = new Unity.Profiling.ProfilerMarker("GhostUpdateSystem_Scheduling");

        EntityTypeHandle m_EntityTypeHandle;
        ComponentLookup<SnapshotData> m_SnapshotDataFromEntity;
        ComponentLookup<NetworkSnapshotAck> m_SnapshotAckFromEntity;
        ComponentLookup<PredictedGhost> m_PredictedFromEntity;
        ComponentLookup<GhostInstance> m_GhostFromEntity;
        ComponentLookup<GhostOwner> m_GhostOwnerFromEntity;
        ComponentLookup<NetworkId> m_NetworkIdFromEntity;
#if NETCODE_DEBUG
        ComponentLookup<PrefabDebugName> m_PrefabNamesFromEntity;
        FixedString512Bytes m_LogFolder;
#endif
        ComponentLookup<EnablePacketLogging> m_EnableLoggingFromEntity;
        BufferLookup<GhostComponentSerializer.State> m_GhostComponentCollectionFromEntity;
        BufferLookup<GhostCollectionPrefabSerializer> m_GhostTypeCollectionFromEntity;
        BufferLookup<GhostCollectionComponentIndex> m_GhostComponentIndexFromEntity;
        BufferLookup<GhostCollectionPrefab> m_GhostCollectionFromEntity;
        BufferLookup<IncomingSnapshotDataStreamBuffer> m_SnapshotFromEntity;
        BufferLookup<SnapshotDataBuffer> m_SnapshotDataBufferFromEntity;
        BufferLookup<SnapshotDynamicDataBuffer> m_SnapshotDynamicDataFromEntity;
        BufferLookup<GhostSpawnBuffer> m_GhostSpawnBufferFromEntity;
        BufferLookup<PrespawnGhostBaseline> m_PrespawnBaselineBufferFromEntity;

#if NETCODE_DEBUG
        PacketDumpLogger m_NetDebugPacket;
#endif

        // This cannot be burst compiled due to NetDebugInterop.Initialize
        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
#if NETCODE_DEBUG
            m_LogFolder = NetDebug.LogFolderForPlatform();
            NetDebugInterop.Initialize();
#endif
            m_GhostEntityMap = new NativeParallelHashMap<int, Entity>(2048, Allocator.Persistent);
            m_SpawnedGhostEntityMap = new NativeParallelHashMap<SpawnedGhost, Entity>(2048, Allocator.Persistent);
            m_GhostCompletionCount = new NativeArray<int>(3, Allocator.Persistent);

            var componentTypes = new NativeArray<ComponentType>(1, Allocator.Temp);
            componentTypes[0] = ComponentType.ReadWrite<SpawnedGhostEntityMap>();
            var spawnedGhostMap = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(componentTypes));
            componentTypes[0] = ComponentType.ReadWrite<GhostCount>();
            var ghostCompletionCount = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(componentTypes));

            FixedString64Bytes spawnedGhostMapName = "SpawnedGhostEntityMapSingleton";
            state.EntityManager.SetName(spawnedGhostMap, spawnedGhostMapName);
            SystemAPI.SetSingleton(new SpawnedGhostEntityMap{Value = m_SpawnedGhostEntityMap.AsReadOnly(), SpawnedGhostMapRW = m_SpawnedGhostEntityMap, ClientGhostEntityMap = m_GhostEntityMap});

            FixedString64Bytes ghostCompletionCountName = "GhostCountSingleton";
            state.EntityManager.SetName(ghostCompletionCount, ghostCompletionCountName);
            SystemAPI.SetSingleton(new GhostCount(m_GhostCompletionCount));

            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkStreamConnection, NetworkStreamInGame>();
            m_ConnectionsQuery = state.GetEntityQuery(builder);

            builder.Reset();
            builder.WithAll<GhostInstance>()
                .WithNone<PreSpawnedGhostIndex>();
            m_GhostCleanupQuery = state.GetEntityQuery(builder);

            builder.Reset();
            builder.WithAll<SubSceneWithGhostCleanup>();
            m_SubSceneQuery = state.GetEntityQuery(builder);

            m_CompressionModel = StreamCompressionModel.Default;

            state.RequireForUpdate<GhostCollection>();

            m_TempDynamicData = new NativeList<byte>(Allocator.Persistent);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_SnapshotDataFromEntity = state.GetComponentLookup<SnapshotData>();
            m_SnapshotAckFromEntity = state.GetComponentLookup<NetworkSnapshotAck>();
            m_NetworkIdFromEntity = state.GetComponentLookup<NetworkId>(true);
            m_PredictedFromEntity = state.GetComponentLookup<PredictedGhost>(true);
            m_GhostFromEntity = state.GetComponentLookup<GhostInstance>();
            m_GhostOwnerFromEntity = state.GetComponentLookup<GhostOwner>(true);
#if NETCODE_DEBUG
            m_PrefabNamesFromEntity = state.GetComponentLookup<PrefabDebugName>(true);
#endif
            m_EnableLoggingFromEntity = state.GetComponentLookup<EnablePacketLogging>(false);
            m_GhostComponentCollectionFromEntity = state.GetBufferLookup<GhostComponentSerializer.State>(true);
            m_GhostTypeCollectionFromEntity = state.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostComponentIndexFromEntity = state.GetBufferLookup<GhostCollectionComponentIndex>(true);
            m_GhostCollectionFromEntity = state.GetBufferLookup<GhostCollectionPrefab>();
            m_SnapshotFromEntity = state.GetBufferLookup<IncomingSnapshotDataStreamBuffer>();
            m_SnapshotDataBufferFromEntity = state.GetBufferLookup<SnapshotDataBuffer>();
            m_SnapshotDynamicDataFromEntity = state.GetBufferLookup<SnapshotDynamicDataBuffer>();
            m_GhostSpawnBufferFromEntity = state.GetBufferLookup<GhostSpawnBuffer>();
            m_PrespawnBaselineBufferFromEntity = state.GetBufferLookup<PrespawnGhostBaseline>(true);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
                return;
            state.CompleteDependency(); // Make sure we can access the ghost maps
            m_GhostEntityMap.Dispose();
            m_SpawnedGhostEntityMap.Dispose();

            m_GhostCompletionCount.Dispose();
            m_TempDynamicData.Dispose();
#if NETCODE_DEBUG
            m_NetDebugPacket.Dispose();
#endif

        }

        [BurstCompile]
        struct ClearGhostsJob : IJobChunk
        {
            public EntityCommandBuffer.ParallelWriter CommandBuffer;
            [ReadOnly] public EntityTypeHandle EntitiesType;

            public void LambdaMethod(Entity entity, int index)
            {
                CommandBuffer.DestroyEntity(index, entity);
            }

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // This job is not written to support queries with enableable component types.
                Assert.IsFalse(useEnabledMask);

                var entities = chunk.GetNativeArray(EntitiesType);
                for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                {
                    LambdaMethod(entities[i], unfilteredChunkIndex);
                }
            }
        }

        [BurstCompile]
        struct ClearMapJob : IJob
        {
            public NativeParallelHashMap<int, Entity> GhostMap;
            public NativeParallelHashMap<SpawnedGhost, Entity> SpawnedGhostMap;

            public void Execute()
            {
                //The ghost map should not clear pre-spawn ghost since they aren't destroyed when the
                //client connection is not in-game. It is more correct to rely on the fact the
                //pre-spawn system reset that since it was the one populating it.
                var keys = SpawnedGhostMap.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; ++i)
                {
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(keys[i].ghostId))
                    {
                        GhostMap.Remove(keys[i].ghostId);
                        SpawnedGhostMap.Remove(keys[i]);
                    }
                }

                // Bug fix: Some ghosts have not spawned yet, but they still need to be removed from this map, to prevent the error:
                // "Found a ghost in the ghost map which does not have an entity connected to it. This can happen if you delete ghost entities on the client.".
                var ghostMapKeys = GhostMap.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < ghostMapKeys.Length; ++i)
                {
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghostMapKeys[i]))
                    {
                        GhostMap.Remove(ghostMapKeys[i]);
                    }
                }
            }
        }

        [BurstCompile]
        struct ReadStreamJob : IJob
        {
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            public BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity;

            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostComponentSerializer.State> m_GhostComponentCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionPrefabSerializer> m_GhostTypeCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionComponentIndex> m_GhostComponentIndex;

            public NativeList<Entity> Connections;
            public BufferLookup<IncomingSnapshotDataStreamBuffer> SnapshotFromEntity;
            public BufferLookup<SnapshotDataBuffer> SnapshotDataBufferFromEntity;
            public BufferLookup<SnapshotDynamicDataBuffer> SnapshotDynamicDataFromEntity;
            public BufferLookup<GhostSpawnBuffer> GhostSpawnBufferFromEntity;
            [ReadOnly]public BufferLookup<PrespawnGhostBaseline> PrespawnBaselineBufferFromEntity;
            public ComponentLookup<SnapshotData> SnapshotDataFromEntity;
            public ComponentLookup<NetworkSnapshotAck> SnapshotAckFromEntity;
            [ReadOnly]public ComponentLookup<NetworkId> NetworkIdFromEntity;
            [ReadOnly]public ComponentLookup<GhostOwner> GhostOwnerFromEntity;
            public NativeParallelHashMap<int, Entity> GhostEntityMap;
            public NativeHashMap<GhostType, int> PendingGhostPrefabAssignment;
            public StreamCompressionModel CompressionModel;
#if UNITY_EDITOR || NETCODE_DEBUG
            public NativeArray<UnsafeGhostStatsSnapshot> SnapshotStatsWriters;
#endif
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> InterpolatedDespawnQueue;
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> PredictedDespawnQueue;
            public NativeQueue<OwnerSwithchingEntry> OwnerPredictedSwitchQueue;
            [ReadOnly] public ComponentLookup<PredictedGhost> PredictedFromEntity;
            public ComponentLookup<GhostInstance> GhostFromEntity;
            public byte IsThinClient;

            public EntityCommandBuffer CommandBuffer;
            public Entity GhostSpawnEntity;
            public NativeArray<int> GhostCompletionCount;
            public NativeList<byte> TempDynamicData;
            public NativeList<SubSceneWithGhostCleanup> PrespawnSceneStateArray;

            public NetDebug NetDebug;
            public FixedString128Bytes WorldName;
#if NETCODE_DEBUG
            public PacketDumpLogger NetDebugPacket;
            [ReadOnly] public ComponentLookup<PrefabDebugName> PrefabNamesFromEntity;
            [NativeDisableContainerSafetyRestriction] public ComponentLookup<EnablePacketLogging> EnableLoggingFromEntity;
            public FixedString512Bytes DebugLog;
#endif

            public void Execute()
            {
#if UNITY_EDITOR || NETCODE_DEBUG
                ref var netStatsSnapshot = ref SnapshotStatsWriters.AsSpan()[0]; // this is an IJob, so just one thread
#endif
#if NETCODE_DEBUG
                EnablePacketLogging.InitAndFetch(Connections[0], EnableLoggingFromEntity, NetDebugPacket);
#endif

                m_GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                m_GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                m_GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];

                // FIXME: should handle any number of connections with individual ghost mappings for each
                CheckConnectionCountIsValid();
                var snapshot = SnapshotFromEntity[Connections[0]];
                if (snapshot.Length == 0)
                    return;

                //compute the size for the temporary buffer used to extract delta compressed buffer elements
                int maxDynamicSnapshotSize = 0;
                for (int i = 0; i < m_GhostTypeCollection.Length; ++i)
                    maxDynamicSnapshotSize = math.max(maxDynamicSnapshotSize, m_GhostTypeCollection[i].MaxBufferSnapshotSize);
                TempDynamicData.Resize(maxDynamicSnapshotSize,NativeArrayOptions.ClearMemory);

                var dataStream = snapshot.AsDataStreamReader();
                // Read the ghost stream
                // find entities to spawn or destroy
                var serverTick = new NetworkTick{SerializedData = dataStream.ReadUInt()};
                ref var ack = ref SnapshotAckFromEntity.GetRefRW(Connections[0]).ValueRW;

                // Load all new prefabs
                uint numPrefabs = dataStream.ReadPackedUInt(CompressionModel);
#if NETCODE_DEBUG
                // TODO - Map CurrentSnapshotSequenceId exactly to the snapshot data itself, rather than fetching it "second hand" here.
                if(NetDebugPacket.IsCreated)
                    DebugLog.Append((FixedString128Bytes)$"SnapshotTick:{serverTick.ToFixedString()} SSId:{ack.CurrentSnapshotSequenceId} NewPrefabs: {numPrefabs}\n");
#endif
                if (numPrefabs > 0)
                {
                    var ghostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                    // The server only sends ghost types which have not been acked yet, acking takes one RTT so we need to check
                    // which prefab was the first included in the list sent by the server
                    int firstPrefab = (int)dataStream.ReadPackedUInt(CompressionModel);
#if NETCODE_DEBUG
                    if(NetDebugPacket.IsCreated)
                        DebugLog.Append(FixedString.Format(" FirstPrefab: {0}\n", firstPrefab));
#endif
                    for (int i = 0; i < numPrefabs; ++i)
                    {
                        GhostType type;
                        ulong hash;
                        type.guid0 = dataStream.ReadUInt();
                        type.guid1 = dataStream.ReadUInt();
                        type.guid2 = dataStream.ReadUInt();
                        type.guid3 = dataStream.ReadUInt();
                        hash = dataStream.ReadULong();
#if NETCODE_DEBUG
                        if (NetDebugPacket.IsCreated)
                            DebugLog.Append((FixedString512Bytes)$"\t {type.guid0}-{type.guid1}-{type.guid2}-{type.guid3} Hash:{hash}");
#endif
                        if (firstPrefab+i == ghostCollection.Length)
                        {
                            //track pending entries to be assigned.
                            PendingGhostPrefabAssignment.Add(type, ghostCollection.Length);
                            //Notify that the list PendingGhostPrefabAssignment (and subsequently the ghost collection list)
                            //has been modified.
                            // TODO: I may achieve this also by tracking the lenght of the ghostCollection itself
                            PendingGhostPrefabAssignment[default] = 1;
                            // This just adds the type, the prefab entity will be populated by the GhostCollectionSystem
                            ghostCollection.Add(new GhostCollectionPrefab{GhostType = type, GhostPrefab = Entity.Null, Hash = hash, Loading = GhostCollectionPrefab.LoadingState.NotLoading});
                        }
                        else if (type != ghostCollection[firstPrefab+i].GhostType || hash != ghostCollection[firstPrefab+i].Hash)
                        {
                            LogDeserializeFailure(LogType.Error, $"Ghost list item {firstPrefab + i} was modified (Hash {ghostCollection[firstPrefab + i].Hash} -> {hash})!");
                            CommandBuffer.AddComponent(Connections[0], new NetworkStreamRequestDisconnect{Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
                            return;
                        }
                    }
                }

                if (IsThinClient == 1)
                {
                    snapshot.Clear();
                    return;
                }

                uint relevantGhostCount = dataStream.ReadPackedUInt(CompressionModel);
                uint despawnLen = dataStream.ReadUShort();
                uint numEntitiesUpdated = dataStream.ReadUShort();

                // Note: The server counts new chunks as containing 0 relevant ghosts UNTIL it knows how many it actually has.
                // Thus, ensure our relevantGhostCount is at LEAST the number of ghosts contained within this snapshot.
                // It's not perfect - but it's the simplest way to approximate.
                GhostCompletionCount[0] = (int)math.max(relevantGhostCount, numEntitiesUpdated);

#if NETCODE_DEBUG
                if(NetDebugPacket.IsCreated)
                    DebugLog.Append(FixedString.Format("\t TotalGhostCount:{0} Despawns:{1} Updates:{2}\n", relevantGhostCount, despawnLen, numEntitiesUpdated));
#endif

                var data = default(DeserializeData);
#if UNITY_EDITOR || NETCODE_DEBUG
                data.StartPos = dataStream.GetBitsRead();
#endif
#if NETCODE_DEBUG
                if (NetDebugPacket.IsCreated && despawnLen > 0)
                    DebugLog.Append((FixedString32Bytes)"\t[Despawn GIDs]");
#endif
                int nextExpectedGhostId = PendingGhostDespawn.k_ExpectedGhostIdDelta;
                for (var i = 0; i < despawnLen; ++i)
                {
                    var ghostId = dataStream.ReadPackedIntDelta(nextExpectedGhostId, CompressionModel);
                    nextExpectedGhostId = ghostId + PendingGhostDespawn.k_ExpectedGhostIdDelta;
#if NETCODE_DEBUG
                    if(NetDebugPacket.IsCreated)
                        DebugLog.Append(FixedString.Format(" {0}", ghostId));
#endif
                    if (!GhostEntityMap.TryGetValue(ghostId, out var ent))
                        continue;

                    GhostEntityMap.Remove(ghostId);

                    if (!GhostFromEntity.TryGetComponent(ent, out var ghostInstance))
                    {
                        NetDebug.LogError($"Trying to despawn a ghost (GID:{ghostId}, {ent.ToFixedString()}) which is in the ghost map but does not have a ghost component. This can happen if you manually delete a ghost on the client.");
                        continue;
                    }

                    if (PredictedFromEntity.HasComponent(ent))
                        PredictedDespawnQueue.Enqueue(new GhostDespawnSystem.DelayedDespawnGhost
                            {ghost = new SpawnedGhost{ghostId = ghostId, spawnTick = ghostInstance.spawnTick}, tick = serverTick});
                    else
                        InterpolatedDespawnQueue.Enqueue(new GhostDespawnSystem.DelayedDespawnGhost
                            {ghost = new SpawnedGhost{ghostId = ghostId, spawnTick = ghostInstance.spawnTick}, tick = serverTick});
                }

#if UNITY_EDITOR || NETCODE_DEBUG
                PacketDumpFlush();
                data.CurPos = dataStream.GetBitsRead();
                netStatsSnapshot.Tick = serverTick;
                netStatsSnapshot.DespawnCount = despawnLen;
                netStatsSnapshot.DestroySizeInBits = (uint) (dataStream.GetBitsRead() - data.StartPos);
                data.StartPos = data.CurPos;
                data.NetStatsSnapshot = (UnsafeGhostStatsSnapshot*)UnsafeUtility.AddressOf(ref netStatsSnapshot);
#endif

                bool dataValid = true;
                for (var i = 0; i < numEntitiesUpdated && dataValid; ++i)
                {
                    dataValid &= DeserializeEntity(serverTick, ref dataStream, ref data, i);
                }
#if UNITY_EDITOR || NETCODE_DEBUG
                if (data.StatCount > 0)
                {
                    data.CurPos = dataStream.GetBitsRead();
                    int statType = (int) data.TargetArch; // index in the ghost collection. identifies the current ghost type (aka the current prefab)
                    ref var perGhostTypeStats = ref netStatsSnapshot.PerGhostTypeStatsListRefRW.ElementAt(statType);
                    perGhostTypeStats.EntityCount += data.StatCount;
                    perGhostTypeStats.SizeInBits += (uint) (data.CurPos - data.StartPos);
                    perGhostTypeStats.UncompressedCount += data.UncompressedCount;
                }
#endif

                // Check we read EXACTLY what we expected to:
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if(dataValid)
                {
                    dataStream.Flush(); // DataStream can only send full bytes, so align the bit reader with the next full byte.
                    var bitsRead = dataStream.GetBitsRead();
                    var lengthInBits = dataStream.Length * 8;
                    var delta = bitsRead - lengthInBits;
                    if (delta != 0 || dataStream.HasFailedReads)
                    {
                        dataValid = false;
                        LogDeserializeFailure(LogType.Error, $"Read {bitsRead} bits, expected {lengthInBits} bits (delta:{delta}) in received snapshot:{serverTick.ToFixedString()}!");
                    }
                }
#endif

                while (GhostEntityMap.Capacity < GhostEntityMap.Count() + data.NewGhosts)
                    GhostEntityMap.Capacity += 1024;

                snapshot.Clear();

                GhostCompletionCount[1] = GhostEntityMap.Count();

                if (!dataValid)
                {
                    // Denote that ALL previous acks are invalid, as we failed to deserialize this snapshot, so something must be wrong.
                    ack.ReceivedSnapshotByLocalMask = 0;
                    ack.LastReceivedSnapshotByLocal = NetworkTick.Invalid;

                    // TODO - Investigation: Rather than unacking all previously acked snapshots (which causes all relevant
                    // ghostChunks to have to be resent to this client), we can ATTEMPT to smartly unack ONLY the invalid
                    // snapshots (e.g. the invalid baseline ticks we read - when we get baseline errors), and only resort
                    // to unacking everything in the worst-case scenarios.
                    // Note: One advantage to this brute-force approach is that the server can send good data immediately afterwards.
                    // Without the brute-force approach, we need to squash each invalid baseline ack separately, which can take a while
                    // (and cause error spam).
                }
            }
            struct DeserializeData
            {
                public uint TargetArch; // index in the ghost type collection. TODO wrap this int in a specific type?
                public uint TargetArchLen;
                public int BaseGhostId;
                public NetworkTick BaseSpawnTick;
                public NetworkTick BaselineTick;
                public NetworkTick BaselineTick2;
                public NetworkTick BaselineTick3;
                public uint BaselineLen;
                public int NewGhosts;
#if UNITY_EDITOR || NETCODE_DEBUG
                public int StartPos;
                public int CurPos;
                public uint StatCount;
                public uint UncompressedCount;
                public UnsafeGhostStatsSnapshot* NetStatsSnapshot;
#endif
            }

            bool DeserializeEntity(NetworkTick serverTick, ref DataStreamReader dataStream, ref DeserializeData data, int ent)
            {
                if (data.TargetArchLen == 0)
                {
#if UNITY_EDITOR || NETCODE_DEBUG
                    data.CurPos = dataStream.GetBitsRead();
                    if (data.StatCount > 0)
                    {
                        int statType = (int)data.TargetArch;
                        ref var netStatsSnapshot = ref SnapshotStatsWriters.AsSpan()[0];
                        ref var perGhostTypeStats = ref netStatsSnapshot.PerGhostTypeStatsListRefRW.ElementAt(statType);
                        perGhostTypeStats.EntityCount += data.StatCount;
                        perGhostTypeStats.SizeInBits += (uint)(data.CurPos - data.StartPos);
                        perGhostTypeStats.UncompressedCount += data.UncompressedCount;
                    }

                    data.StartPos = data.CurPos;
                    data.StatCount = 0;
                    data.UncompressedCount = 0;
#endif
                    data.TargetArch = dataStream.ReadPackedUInt(CompressionModel);
                    data.TargetArchLen = dataStream.ReadPackedUInt(CompressionModel);
                    data.BaseGhostId = dataStream.ReadRawBits(1) != 0 ? unchecked((int)PrespawnHelper.PrespawnGhostIdBase) : 1;
                    data.BaseSpawnTick = serverTick;
                    data.BaseSpawnTick.Decrement();

                    if (data.TargetArch >= m_GhostTypeCollection.Length)
                    {
                        LogDeserializeFailure(LogType.Error, FixedString.Format("Received invalid GhostType from the server:{0}/{1} RelevantGhostCount:{2}!", data.TargetArch, m_GhostTypeCollection.Length, data.TargetArchLen));
                        return false;
                    }
#if NETCODE_DEBUG
                    if(NetDebugPacket.IsCreated)
                        DebugLog.Append(FixedString.Format("\t GhostType:{0} RelevantGhostCount:{1}\n", GetGhostTypePrefabName(data), data.TargetArchLen));
#endif
                }

                --data.TargetArchLen;

                if (data.BaselineLen == 0)
                {
                    var baselineDelta = dataStream.ReadPackedUInt(CompressionModel);
                    if (baselineDelta >= GhostSystemConstants.MaxBaselineAge)
                        data.BaselineTick = NetworkTick.Invalid;
                    else
                    {
                        data.BaselineTick = serverTick;
                        data.BaselineTick.Subtract(baselineDelta);
                    }
                    baselineDelta = dataStream.ReadPackedUInt(CompressionModel);
                    if (baselineDelta >= GhostSystemConstants.MaxBaselineAge)
                        data.BaselineTick2 = NetworkTick.Invalid;
                    else
                    {
                        data.BaselineTick2 = serverTick;
                        data.BaselineTick2.Subtract(baselineDelta);
                    }
                    baselineDelta = dataStream.ReadPackedUInt(CompressionModel);
                    if (baselineDelta >= GhostSystemConstants.MaxBaselineAge)
                        data.BaselineTick3 = NetworkTick.Invalid;
                    else
                    {
                        data.BaselineTick3 = serverTick;
                        data.BaselineTick3.Subtract(baselineDelta);
                    }
                    data.BaselineLen = dataStream.ReadPackedUInt(CompressionModel);
#if NETCODE_DEBUG
                    if (NetDebugPacket.IsCreated)
                        DebugLog.Append(FixedString.Format("\t\tB0:{0} B1:{1} B2:{2} Count:{3}\n", data.BaselineTick.ToFixedString(), data.BaselineTick2.ToFixedString(), data.BaselineTick3.ToFixedString(), data.BaselineLen));
#endif
                    //baselineTick NetworkTick.Invalid is only valid and possible for prespawn since tick=0 is special
                    if(Hint.Unlikely(!data.BaselineTick.IsValid && (data.BaseGhostId & PrespawnHelper.PrespawnGhostIdBase) == 0))
                    {
                        LogDeserializeFailure(LogType.Error, (FixedString512Bytes) $"Received snapshot baseline for prespawn ghost from the server, but the ghost entity GID:{data.BaseGhostId} (GhostType:{GetGhostTypePrefabName(data)}) is not a prespawn!");
                        return false;
                    }
                    if (Hint.Unlikely(data.BaselineTick3 != serverTick && (data.BaselineTick3 == data.BaselineTick2 || data.BaselineTick2 == data.BaselineTick)))
                    {
                        LogDeserializeFailure(LogType.Error, (FixedString512Bytes) $"Received invalid snapshot baseline (B1:{data.BaselineTick.ToFixedString()}, B2:{data.BaselineTick2.ToFixedString()}, B3:{data.BaselineTick3.ToFixedString()} from server for GID:{data.BaseGhostId} (GhostType:{GetGhostTypePrefabName(data)})!");
                        return false;
                    }
                }

                --data.BaselineLen;
                int ghostId = dataStream.ReadPackedIntDelta(data.BaseGhostId, CompressionModel);
                data.BaseGhostId = ghostId + 1;
#if NETCODE_DEBUG
                if (NetDebugPacket.IsCreated)
                    DebugLog.Append((FixedString128Bytes)$"\t\t\tGID:{ghostId}");
#endif

                // Retrieve the spawn tick only for new, non-prespawn ghosts:
                var isNewGhostForClient = data.BaselineTick == serverTick;
                NetworkTick serverSpawnTick = NetworkTick.Invalid;
                if (isNewGhostForClient && !PrespawnHelper.IsPrespawnGhostId(ghostId))
                {
                    // Retrieve spawn tick only for non-prespawn ghosts:
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghostId))
                    {
                        serverSpawnTick = new NetworkTick{SerializedData = dataStream.ReadPackedUIntDelta(data.BaseSpawnTick.SerializedData, CompressionModel)};
                        data.BaseSpawnTick = serverSpawnTick;
#if NETCODE_DEBUG
                        if (NetDebugPacket.IsCreated)
                            DebugLog.Append(FixedString.Format(" SpawnTick:{0}", serverSpawnTick.ToFixedString()));
#endif
                    }
                }

                //Get the data size
                uint ghostDataSizeInBits = 0;
                int ghostDataStreamStartBitsRead = 0;
                if(GhostSystemConstants.SnapshotHasCompressedGhostSize)
                {
                    ghostDataSizeInBits = dataStream.ReadPackedUIntDelta(0, CompressionModel);
                    ghostDataStreamStartBitsRead = dataStream.GetBitsRead();
                }

                var typeData = m_GhostTypeCollection[(int)data.TargetArch];
                int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                int enableableMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);

                int snapshotOffset;
                int snapshotSize = typeData.SnapshotSize;
                byte* baselineData = (byte*)UnsafeUtility.Malloc(snapshotSize, 16, Allocator.Temp);
                UnsafeUtility.MemClear(baselineData, snapshotSize);
                Entity gent;
                DynamicBuffer<SnapshotDataBuffer> snapshotDataBuffer;
                SnapshotData snapshotDataComponent;
                GhostOwner ghostOwner;
                byte* snapshotData;
                //
                int baselineDynamicDataIndex = -1;
                byte* snapshotDynamicDataPtr = null;
                uint snapshotDynamicDataCapacity = 0; // available space in the dynamic snapshot data history slot
                byte* baselineDynamicDataPtr = null;

                bool existingGhost = GhostEntityMap.TryGetValue(ghostId, out gent);
                if (SnapshotDataBufferFromEntity.HasBuffer(gent) && GhostFromEntity[gent].ghostType < 0)
                {
                    // Pre-spawned ghosts can have ghost type -1 until they receive the proper type from the server
                    var existingGhostEnt = GhostFromEntity[gent];
                    existingGhostEnt.ghostType = (int)data.TargetArch;
                    GhostFromEntity[gent] = existingGhostEnt;

                    snapshotDataBuffer = SnapshotDataBufferFromEntity[gent];
                    snapshotDataBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    UnsafeUtility.MemClear(snapshotDataBuffer.GetUnsafePtr(), snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    SnapshotDataFromEntity[gent] = new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0};
                }
                if (existingGhost && SnapshotDataBufferFromEntity.HasBuffer(gent) && GhostFromEntity[gent].ghostType == data.TargetArch)
                {
                    snapshotDataBuffer = SnapshotDataBufferFromEntity[gent];
                    CheckSnapshotBufferSizeIsCorrect(snapshotDataBuffer, snapshotSize);
                    snapshotData = (byte*)snapshotDataBuffer.GetUnsafePtr();
                    snapshotDataComponent = SnapshotDataFromEntity[gent];
                    snapshotDataComponent.LatestIndex = (snapshotDataComponent.LatestIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
                    SnapshotDataFromEntity[gent] = snapshotDataComponent;
                    // If this is a prespawned ghost with no baseline tick set use the prespawn baseline
                    if (!data.BaselineTick.IsValid && PrespawnHelper.IsPrespawnGhostId(GhostFromEntity[gent].ghostId))
                    {
                        CheckPrespawnBaselineIsPresent(gent, ghostId);
                        var prespawnBaselineBuffer = PrespawnBaselineBufferFromEntity[gent];
                        if (prespawnBaselineBuffer.Length > 0)
                        {
                            //Memcpy in this case is not necessary so we can safely re-assign the pointer
                            baselineData = (byte*)prespawnBaselineBuffer.GetUnsafeReadOnlyPtr();
                            NetDebug.DebugLog(FixedString.Format("Client prespawn baseline ghost id={0} serverTick={1}", GhostFromEntity[gent].ghostId, serverTick.ToFixedString()));
                            //Prespawn baseline is a little different and store the base offset starting from the beginning of the buffer
                            //TODO: change the receive system so everything use this kind of logic (so offset start from DynamicHeader size in general)
                            if (typeData.NumBuffers > 0)
                            {
                                baselineDynamicDataPtr = baselineData + snapshotSize;
                            }
                        }
                        else
                        {
                            //This is an unskippable error. The client MUST have the prespawn baseline.
                            LogDeserializeFailure(LogType.Error, FixedString.Format("No prespawn baseline found for {0} GID:{1} (GhostType:{2}) -- cannot deserialize snapshot.", gent.ToFixedString(), GhostFromEntity[gent].ghostId, GetGhostTypePrefabName(data)));
                            return false;
                        }
                    }
                    else if (data.BaselineTick != serverTick)
                    {
                        // we need to search the buffer backwards from the insert index to always look at latest snapshots first
                        int numSnapshotsInBuffer = snapshotDataBuffer.Length / snapshotSize;

                        for (int snapshotCount = 0; snapshotCount < numSnapshotsInBuffer; ++snapshotCount)
                        {
                            int bi = snapshotDataComponent.GetPreviousSnapshotIndexAtOffset(snapshotCount) * snapshotSize;

                            if (*(uint*)(snapshotData+ bi) == data.BaselineTick.SerializedData)
                            {
                                UnsafeUtility.MemCpy(baselineData, snapshotData+bi, snapshotSize);
                                //retrive also the baseline dynamic snapshot buffer if the ghost has some buffers
                                if(typeData.NumBuffers > 0)
                                {
                                    if (!SnapshotDynamicDataFromEntity.HasBuffer(gent))
                                        throw new InvalidOperationException($"SnapshotDynamicDataBuffer buffer not found for ghost with id {ghostId}");
                                    baselineDynamicDataIndex = bi / snapshotSize;
                                }
                                break;
                            }
                        }

                        if (Hint.Unlikely(*(uint*)baselineData == 0))
                        {
                            LogDeserializeFailure(LogType.Warning, (FixedString512Bytes) $"Ack desync at data.BaselineTick:{data.BaselineTick.ToFixedString()} and ServerTick:{serverTick.ToFixedString()} for GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}) - server sent baseline(s) we do not have!");
                            return false; // Ack desync detected
                        }
                    }

                    if (data.BaselineTick3 != serverTick)
                    {
                        byte* baselineData2 = null;
                        byte* baselineData3 = null;

                        // we need to search the buffer backwards from the insert index to always look at latest snapshots first
                        int numSnapshotsInBuffer = snapshotDataBuffer.Length / snapshotSize;
                        for (int snapshotCount = 0; snapshotCount < numSnapshotsInBuffer; ++snapshotCount)
                        {
                            int bi = snapshotDataComponent.GetPreviousSnapshotIndexAtOffset(snapshotCount) * snapshotSize;

                            if (*(uint*)(snapshotData+bi) == data.BaselineTick2.SerializedData)
                            {
                                baselineData2 = snapshotData+bi;
                            }

                            if (*(uint*)(snapshotData+bi) == data.BaselineTick3.SerializedData)
                            {
                                baselineData3 = snapshotData+bi;
                            }
                        }

                        if (Hint.Unlikely(baselineData2 == null || baselineData3 == null))
                        {
                            LogDeserializeFailure(LogType.Warning, (FixedString512Bytes) $"Ack desync for baseline B2:{baselineData2 != null} and/or B3:{baselineData3 != null} at data.BaselineTick3:{data.BaselineTick3.ToFixedString()} and serverTick:{serverTick.ToFixedString()} for GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}) - server sent baseline(s) we do not have!");
                            return false; // Ack desync detected
                        }
                        snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + changeMaskUints*sizeof(uint) + enableableMaskUints*sizeof(uint));
                        var predictor = new GhostDeltaPredictor(serverTick, data.BaselineTick, data.BaselineTick2, data.BaselineTick3);

                        for (int comp = 0; comp < typeData.NumComponents; ++comp)
                        {
                            int serializerIdx = m_GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                            //Buffers does not use delta prediction for the size and the contents
                            ref readonly var ghostSerializer = ref m_GhostComponentCollection.ElementAtRO(serializerIdx);
                            if (!ghostSerializer.HasGhostFields)
                                continue;
                            if (!ghostSerializer.ComponentType.IsBuffer)
                            {
                                CheckOffsetLessThanSnapshotBufferSize(snapshotOffset, ghostSerializer.SnapshotSize, snapshotSize);
                                ghostSerializer.PredictDelta.Invoke(
                                    (IntPtr) (baselineData + snapshotOffset),
                                    (IntPtr) (baselineData2 + snapshotOffset),
                                    (IntPtr) (baselineData3 + snapshotOffset), ref predictor);
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                            }
                            else
                            {
                                CheckOffsetLessThanSnapshotBufferSize(snapshotOffset, GhostComponentSerializer.DynamicBufferComponentSnapshotSize, snapshotSize);
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                            }
                        }
                    }
                    //buffers: retrieve the dynamic contents size and re-fit the snapshot dynamic history
                    if (typeData.NumBuffers > 0)
                    {
                        //Delta-decompress the dynamic data size
                        var buf = SnapshotDynamicDataFromEntity[gent];
                        uint baselineDynamicDataSize = 0;
                        if (baselineDynamicDataIndex != -1)
                        {
                            var bufferPtr = (byte*)buf.GetUnsafeReadOnlyPtr();
                            baselineDynamicDataSize = ((uint*) bufferPtr)[baselineDynamicDataIndex];
                        }
                        else if (PrespawnHelper.IsPrespawnGhostId(ghostId) && PrespawnBaselineBufferFromEntity.HasBuffer(gent))
                        {
                            CheckPrespawnBaselinePtrsAreValid(data, baselineData, ghostId, baselineDynamicDataPtr);
                            baselineDynamicDataSize = ((uint*)(baselineDynamicDataPtr))[0];
                        }
                        uint dynamicDataSize = dataStream.ReadPackedUIntDelta(baselineDynamicDataSize, CompressionModel);

                        if (Hint.Unlikely(!SnapshotDynamicDataFromEntity.HasBuffer(gent)))
                            throw new InvalidOperationException($"SnapshotDynamicDataBuffer buffer not found for GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)})!");

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
                    bool isPrespawn = PrespawnHelper.IsPrespawnGhostId(ghostId);
                    if (existingGhost)
                    {
                        // The ghost entity map is out of date, clean it up
                        GhostEntityMap.Remove(ghostId);
                        if (GhostFromEntity.HasComponent(gent) && GhostFromEntity[gent].ghostType != data.TargetArch)
                             LogDeserializeFailure(LogType.Error, ($"Received a ghost (GID:{ghostId}, {gent.ToFixedString()}) with an invalid ghost type ({GetGhostTypePrefabName(data)}, expected {GhostFromEntity[gent].ghostType})."));
                        else if (isPrespawn)
                            LogDeserializeFailure(LogType.Error, ($"Found a prespawn ghost (GID:{ghostId}, {gent.ToFixedString()}, GhostType:{GetGhostTypePrefabName(data)}) that has no entity connected to it. This can happen if you unload a scene or destroy the ghost entity on the client."));
                        else
                            LogDeserializeFailure(LogType.Error, $"Found a ghost (GID:{ghostId}, {gent.ToFixedString()}, GhostType:{GetGhostTypePrefabName(data)}) in the ghost map which does not have an entity connected to it (or an invalid entity). This can happen if you delete ghost entities on the client.");
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
                        for (int i = 0; i < PrespawnSceneStateArray.Length; ++i)
                        {
                            if (prespawnId >= PrespawnSceneStateArray[i].FirstGhostId &&
                                prespawnId < PrespawnSceneStateArray[i].FirstGhostId + PrespawnSceneStateArray[i].PrespawnCount)
                            {
                                prespawnSceneIndex = i;
                                break;
                            }
                        }
                    }
                    if (data.BaselineTick != serverTick)
                    {
                        //If the client unloaded a subscene before the server or the server will not do that at all, we threat that as a
                        //spurious/temporary inconsistency. The server will be notified soon that the client does not have that scenes anymore
                        //and will stop streaming the subscene ghosts to him.
                        //Try to recover by skipping the data. If the stream does not have a the ghost-size bits, fallback to the standard error.
                        if(isPrespawn && prespawnSceneIndex == -1 && (GhostSystemConstants.SnapshotHasCompressedGhostSize))
                        {
#if NETCODE_DEBUG
                            if (NetDebugPacket.IsCreated)
                            {
                                DebugLog.Append(FixedString.Format("SKIP ({0}B)", ghostDataSizeInBits));
                                PacketDumpFlush();
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
                        if(!isPrespawn || data.BaselineTick.IsValid)
                        {
                            LogDeserializeFailure(LogType.Error, (FixedString512Bytes) $"Received baseline for a ghost we do not have; GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}), BaselineTick:{data.BaselineTick.ToFixedString()}, existingGhost:{existingGhost}, serverTick:{serverTick.ToFixedString()}!");
                            return false;
                        }
                        LogDeserializeFailure(LogType.Warning, (FixedString512Bytes) $"Unknown baseline mismatch error for GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}), BaselineTick:{data.BaselineTick.ToFixedString()}, existingGhost:{existingGhost}, serverTick:{serverTick.ToFixedString()}!");
                        return false;
                    }

                    ++data.NewGhosts;
                    var ghostSpawnBuffer = GhostSpawnBufferFromEntity[GhostSpawnEntity];
                    snapshotDataBuffer = SnapshotDataBufferFromEntity[GhostSpawnEntity];
                    var snapshotDataBufferOffset = snapshotDataBuffer.Length;
                    //Grow the ghostSpawnBuffer to include also the dynamic data size.
                    uint dynamicDataSize = 0;
                    if (typeData.NumBuffers > 0)
                        dynamicDataSize = dataStream.ReadPackedUIntDelta(0, CompressionModel);
                    var spawnedGhost = new GhostSpawnBuffer
                    {
                        GhostType = (int) data.TargetArch,
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
                        if (Hint.Likely(prespawnSceneIndex != -1))
                        {
                            spawnedGhost.PrespawnIndex = (int) (ghostId & ~PrespawnHelper.PrespawnGhostIdBase) - PrespawnSceneStateArray[prespawnSceneIndex].FirstGhostId;
                            spawnedGhost.SceneGUID = PrespawnSceneStateArray[prespawnSceneIndex].SceneGUID;
                            spawnedGhost.SectionIndex = PrespawnSceneStateArray[prespawnSceneIndex].SectionIndex;
                        }
                        else LogDeserializeFailure(LogType.Error, $"Received a new instance of a pre-spawned ghost GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}) on ServerTick:{serverTick.ToFixedString()} due to relevancy changes, but no section with a enclosing id-range has been found!");
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

                snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + (changeMaskUints*sizeof(uint)) + (enableableMaskUints*sizeof(uint)));
                snapshotData += snapshotSize * snapshotDataComponent.LatestIndex;
                *(uint*)(snapshotData) = serverTick.SerializedData;
                uint* changeMask = (uint*)(snapshotData+sizeof(uint));
                uint anyChangeMaskThisEntity = 0;
                for (int cm = 0; cm < changeMaskUints; ++cm)
                {
                    var changeMaskUint = dataStream.ReadPackedUIntDelta(((uint*)(baselineData+sizeof(uint)))[cm], CompressionModel);
                    changeMask[cm] = changeMaskUint;
                    anyChangeMaskThisEntity |= changeMaskUint;
#if NETCODE_DEBUG
                    if(NetDebugPacket.IsCreated)
                        DebugLog.Append(FixedString.Format(" ChangeMask:{0}", NetDebug.PrintMask(changeMask[cm])));
#endif
                }

                if (typeData.EnableableBits > 0)
                {
                    uint* enableBits = (uint*)(snapshotData+sizeof(uint) + changeMaskUints * sizeof(uint));
                    for (int em = 0; em < enableableMaskUints; ++em)
                    {
                        enableBits[em] = dataStream.ReadPackedUIntDelta(((uint*)(baselineData+sizeof(uint) + changeMaskUints * sizeof(uint)))[em], CompressionModel);
                    }
                }

#if NETCODE_DEBUG
                int entityStartBit = dataStream.GetBitsRead();
#endif
#if UNITY_EDITOR || NETCODE_DEBUG
                var perComponentStats = data.NetStatsSnapshot->PerGhostTypeStatsListRefRW.ElementAt((int)data.TargetArch).PerComponentStatsList;
                if (perComponentStats.Length < typeData.NumComponents) // since comp count doesn't change, this should happen only once
                    perComponentStats.Resize(typeData.NumComponents, NativeArrayOptions.ClearMemory);
#endif
                for (int comp = 0; comp < typeData.NumComponents; ++comp)
                {
                    int serializerIdx = m_GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    ref readonly var ghostSerializer = ref m_GhostComponentCollection.ElementAtRO(serializerIdx);
#if NETCODE_DEBUG
                    FixedString128Bytes componentName = default;
                    var numBits = dataStream.GetBitsRead();
                    if (NetDebugPacket.IsCreated)
                    {
                        var componentTypeIndex = ghostSerializer.ComponentType.TypeIndex;
                        componentName = NetDebug.ComponentTypeNameLookup[componentTypeIndex];
                    }
#endif
                    if (ghostSerializer.HasGhostFields)
                    {
                        if (!ghostSerializer.ComponentType.IsBuffer)
                        {
                            CheckSnaphostBufferOverflow(maskOffset, ghostSerializer.ChangeMaskBits,
                                typeData.ChangeMaskBits, snapshotOffset, ghostSerializer.SnapshotSize, snapshotSize);
                            ghostSerializer.Deserialize.Invoke((IntPtr) (snapshotData + snapshotOffset), (IntPtr) (baselineData + snapshotOffset), ref dataStream, ref CompressionModel, (IntPtr) changeMask, maskOffset);
                            snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                            maskOffset += ghostSerializer.ChangeMaskBits;
                        }
                        else
                        {
                            CheckSnaphostBufferOverflow(maskOffset, GhostComponentSerializer.DynamicBufferComponentMaskBits,
                                typeData.ChangeMaskBits, snapshotOffset, GhostComponentSerializer.DynamicBufferComponentSnapshotSize, snapshotSize);
                            //Delta decompress the buffer len
                            uint mask = GhostComponentSerializer.CopyFromChangeMask((IntPtr) changeMask, maskOffset, GhostComponentSerializer.DynamicBufferComponentMaskBits);
                            var baseLen = *(uint*) (baselineData + snapshotOffset);
                            var baseOffset = *(uint*) (baselineData + snapshotOffset + sizeof(uint));
                            var bufLen = (mask & 0x2) == 0 ? baseLen : dataStream.ReadPackedUIntDelta(baseLen, CompressionModel);
                            //Assign the buffer info to the snapshot and register the current offset from the beginning of the dynamic history slot
                            *(uint*) (snapshotData + snapshotOffset) = bufLen;
                            *(uint*) (snapshotData + snapshotOffset + sizeof(uint)) = dynamicBufferOffset;
                            snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                            maskOffset += GhostComponentSerializer.DynamicBufferComponentMaskBits;
                            //Copy the buffer contents. Use delta compression based on mask bits configuration
                            //00 : nothing changed
                            //01 : same len, only content changed. Add additional mask bits for each elements
                            //11 : len changed, everthing need to be sent again . No mask bits for the elements
                            var dynamicDataSnapshotStride = (uint)ghostSerializer.SnapshotSize;
                            var contentMaskUInts = (uint)GhostComponentSerializer.ChangeMaskArraySizeInUInts((int)(ghostSerializer.ChangeMaskBits * bufLen));
                            var maskSize = GhostComponentSerializer.SnapshotSizeAligned(contentMaskUInts*4);
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
                                    ghostSerializer.Deserialize.Invoke(
                                        (IntPtr) (snapshotDynamicDataPtr + dynamicBufferOffset),
                                        (IntPtr) TempDynamicData.GetUnsafePtr(),
                                        ref dataStream, ref CompressionModel, (IntPtr) contentMask, contentMaskOffset);
                                    dynamicBufferOffset += dynamicDataSnapshotStride;
                                    contentMaskOffset += ghostSerializer.ChangeMaskBits;
                                }
                            }
                            else //same len but content changed, decode the masks and copy the content
                            {
                                var baselineMaskPtr = (uint*) (baselineDynamicDataPtr + baseOffset);
                                for (int cm = 0; cm < contentMaskUInts; ++cm)
                                    contentMask[cm] = dataStream.ReadPackedUIntDelta(baselineMaskPtr[cm], CompressionModel);
                                baseOffset += maskSize;
                                var contentMaskOffset = 0;
                                for (int i = 0; i < bufLen; ++i)
                                {
                                    ghostSerializer.Deserialize.Invoke(
                                        (IntPtr) (snapshotDynamicDataPtr + dynamicBufferOffset),
                                        (IntPtr) (baselineDynamicDataPtr + baseOffset),
                                        ref dataStream, ref CompressionModel, (IntPtr) contentMask, contentMaskOffset);
                                    dynamicBufferOffset += dynamicDataSnapshotStride;
                                    baseOffset += dynamicDataSnapshotStride;
                                    contentMaskOffset += ghostSerializer.ChangeMaskBits;
                                }
                            }
                            dynamicBufferOffset = GhostComponentSerializer.SnapshotSizeAligned(dynamicBufferOffset);
                        }
                    }
#if NETCODE_DEBUG
                    numBits = dataStream.GetBitsRead() - numBits;

                    if (anyChangeMaskThisEntity != 0)
                    {
                        perComponentStats.ElementAt(comp).SizeInSnapshotInBits += (uint)numBits;

                        if (NetDebugPacket.IsCreated)
                        {
                            if (DebugLog.Length > (DebugLog.Capacity >> 1))
                            {
                                DebugLog.Append((FixedString32Bytes)" CONT");
                                PacketDumpFlush();
                            }

                            DebugLog.Append(FixedString.Format(" {0}:{1} ({2}B)", componentName, ghostSerializer.PredictionErrorNames, numBits));
                        }
                    }
#endif
                }
                //This is the dual of the code in the GhostChunkSerialiser. It is responsible to reset the received
                //snapshot data to 0 (as result of deconding from the acked baseline).
                //TODO: Optimisation for later: avoid all these by actually making the server preserve this baseline information
                var networkId = NetworkIdFromEntity[Connections[0]];
                if (typeData.PartialComponents != 0 || typeData.PartialSendToOwner != 0)
                {
                    GhostSendType serializeMask = GhostSendType.AllClients;
                    var sendToOwner = SendToOwnerType.All;
                    var isOwner = networkId.Value == *(int*)(snapshotData + typeData.PredictionOwnerOffset);
                    if(typeData.PartialSendToOwner != 0)
                        sendToOwner = isOwner ? SendToOwnerType.SendToOwner : SendToOwnerType.SendToNonOwner;
                    if (typeData.PartialComponents != 0 && typeData.OwnerPredicted != 0)
                        serializeMask = isOwner ? GhostSendType.OnlyPredictedClients : GhostSendType.OnlyInterpolatedClients;
                    int snapshotDataOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) +
                        (changeMaskUints * sizeof(uint)) +
                        (enableableMaskUints * sizeof(uint)));
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int serializerIdx = m_GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        ref readonly var ghostSerializer = ref m_GhostComponentCollection.ElementAtRO(serializerIdx);
                        var componentStride = m_GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                            ? GhostComponentSerializer.DynamicBufferComponentSnapshotSize
                            : ghostSerializer.SnapshotSize;
                        componentStride = GhostComponentSerializer.SnapshotSizeAligned(componentStride);
                        if (ghostSerializer.HasGhostFields)
                        {
                            if ((serializeMask & m_GhostComponentIndex[typeData.FirstComponent + comp].SendMask) == 0 ||
                                (sendToOwner & m_GhostComponentIndex[typeData.FirstComponent + comp].SendToOwner) == 0)
                            {
                                uint* componentSnapshotData = (uint*)(snapshotData + snapshotDataOffset);
                                for(int i=0;i<componentStride/4;++i)
                                    componentSnapshotData[i] = 0;
                            }
                            snapshotDataOffset += componentStride;
                        }
                    }
                }
                //Check if the owner has changed in respect to the last value stored in the GhostOwnerComponent.
                //If that is the case, then we need to enqueue an owner switch change before the next GhostUpdateSystem
                //update (so all component are actually now ready to be updated) to avoid adding another 1 frame latency
                //to the client before it perceive this change.
                //Another possibilty is to store in another component (internal) the old value and check always against
                //that. That would give more control and safety (because the GhostOwner is public, users can do whatever they want
                //with it).
                if (typeData.OwnerPredicted != 0 && existingGhost && GhostOwnerFromEntity.HasComponent(gent))
                {
                    ghostOwner = GhostOwnerFromEntity[gent];
                    var ownerId = *(int*)(snapshotData + typeData.PredictionOwnerOffset);
                    if(ghostOwner.NetworkId > 0 && ownerId <= 0 || ghostOwner.NetworkId <= 0 && ownerId > 0)
                    {
                        //Owner changed, mark the ghost for further processing by the owner switching system
                        OwnerPredictedSwitchQueue.Enqueue(new OwnerSwithchingEntry
                        {
                            CurrentOwner = ghostOwner.NetworkId,
                            NewOwner = ownerId,
                            TargetEntity = gent,
                        });
                    }
                }
#if NETCODE_DEBUG
                if (NetDebugPacket.IsCreated)
                {
                    if (anyChangeMaskThisEntity != 0)
                        DebugLog.Append(FixedString.Format(" Total ({0}B)", dataStream.GetBitsRead()-entityStartBit));
                    DebugLog.Append('\n');
                    PacketDumpFlush();
                }
#endif

#if UNITY_EDITOR || NETCODE_DEBUG
                ++data.StatCount;
                if (data.BaselineTick == serverTick)
                    ++data.UncompressedCount;
#endif

                var bitsReadForGhost = dataStream.GetBitsRead()-ghostDataStreamStartBitsRead;
                if (GhostSystemConstants.SnapshotHasCompressedGhostSize && Hint.Unlikely(bitsReadForGhost != ghostDataSizeInBits))
                {
                    LogDeserializeFailure(LogType.Error, (FixedString512Bytes) $"Failed to decode ghost GID {ghostId} (GhostType:{GetGhostTypePrefabName(data)}), got {bitsReadForGhost} bits for ghost, expected {ghostDataSizeInBits} bits!");
                    return false;
                }

                if (typeData.IsGhostGroup != 0)
                {
                    var groupLen = dataStream.ReadPackedUInt(CompressionModel);
#if NETCODE_DEBUG
                    if(NetDebugPacket.IsCreated)
                        NetDebugPacket.Log(FixedString.Format("\t\t\tGhostGroup.Len:{0}\n\t\t\t[", groupLen));
#endif
                    for (var i = 0; i < groupLen; ++i)
                    {
                        var childData = default(DeserializeData);
#if NETCODE_DEBUG
                        if(NetDebugPacket.IsCreated)
                            NetDebugPacket.Log(FixedString.Format("\t\t\t\t[{0}/{1}] ", i, groupLen));
#endif
#if UNITY_EDITOR || NETCODE_DEBUG
                        childData.NetStatsSnapshot = data.NetStatsSnapshot;
#endif
                        if (!DeserializeEntity(serverTick, ref dataStream, ref childData, i))
                        {
                            LogDeserializeFailure(LogType.Warning, (FixedString512Bytes)$"Error occurred during the GhostGroup deserialization of GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}) at child index {i} of {groupLen}!");
                            return false;
                        }
                    }
#if NETCODE_DEBUG
                    if (NetDebugPacket.IsCreated)
                        NetDebugPacket.Log("\t\t\t]\n");
#endif
                }
                return true;
            }

            private FixedString128Bytes GetGhostTypePrefabName(in DeserializeData data)
            {
                var ghostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                if (data.TargetArch < ghostCollection.Length)
                {
                    var prefab = ghostCollection[(int) data.TargetArch];
#if NETCODE_DEBUG
                    if(PrefabNamesFromEntity.TryGetComponent(prefab.GhostPrefab, out var pdn))
                        // Burst bug: Use FixedString.Format otherwise it prints BlobStringText.
                        return FixedString.Format("{0}({1}/{2})", pdn.PrefabName, data.TargetArch, ghostCollection.Length);
#endif
                    return (FixedString128Bytes)$"{prefab.GhostType.guid0}-{prefab.GhostType.guid1}-{prefab.GhostType.guid2}-{prefab.GhostType.guid3} Hash:{prefab.Hash}({data.TargetArch}/{ghostCollection.Length})";
                }
                return (FixedString128Bytes)$"???({data.TargetArch}/{ghostCollection.Length})";
            }

            /// <summary>Packet dump AND log the Warning/Error log.</summary>
            private void LogDeserializeFailure(UnityEngine.LogType type, FixedString512Bytes msg)
            {
#if NETCODE_DEBUG
                if (NetDebugPacket.IsCreated)
                {
                    PacketDumpFlush();
                    NetDebugPacket.Log(msg);
                }
#endif
                switch (type)
                {
                    case LogType.Error: NetDebug.LogError($"[{WorldName}][GhostReceiveSystem] {msg}"); break;
                    case LogType.Warning: NetDebug.LogWarning($"[{WorldName}][GhostReceiveSystem] {msg}"); break;
                    default: throw new ArgumentOutOfRangeException(nameof(type), type, null);
                }
            }
            [Conditional("NETCODE_DEBUG")]
            private void PacketDumpFlush()
            {
#if NETCODE_DEBUG
                if (NetDebugPacket.IsCreated && !DebugLog.IsEmpty)
                {
                    NetDebugPacket.Log(DebugLog);
                    DebugLog.Clear();
                }
#endif
            }
            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            void CheckPrespawnBaselineIsPresent(Entity gent, int ghostId)
            {
                if (!PrespawnBaselineBufferFromEntity.HasBuffer(gent))
                    throw new InvalidOperationException($"Prespawn baseline for ghost with id {ghostId} not present");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            static void CheckPrespawnBaselinePtrsAreValid(DeserializeData data, byte* baselineData, int ghostId,
                byte* baselineDynamicDataPtr)
            {
                if (baselineData == null)
                    throw new InvalidOperationException(
                        $"Prespawn ghost with id {ghostId} and archetype {data.TargetArch} does not have a baseline");
                if (baselineDynamicDataPtr == null)
                    throw new InvalidOperationException(
                        $"Prespawn ghost with id {ghostId} and archetype {data.TargetArch} does not have a baseline for the dynamic buffer");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            static void CheckDynamicSnapshotBufferOverflow(uint dynamicBufferOffset, uint maskSize, uint dynamicDataSize,
                uint snapshotDynamicDataCapacity)
            {
                if ((dynamicBufferOffset + maskSize + dynamicDataSize) > snapshotDynamicDataCapacity)
                    throw new InvalidOperationException($"DynamicData Snapshot buffer overflow during deserialize! dynamicBufferOffset({dynamicBufferOffset}) + maskSize({maskSize}) + dynamicDataSize({dynamicDataSize}) must be <= snapshotDynamicDataCapacity({snapshotDynamicDataCapacity})!");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            void CheckSnaphostBufferOverflow(int maskOffset, int maskBits, int totalMaskBits,
                int snapshotOffset, int snapshotSize, int bufferSize)
            {
                if (maskOffset + maskBits > totalMaskBits)
                    throw new InvalidOperationException($"Snapshot buffer overflow during deserialize: maskOffset({maskOffset}) + maskBits({maskBits}) must be <= totalMaskBits({totalMaskBits})!");
                var snapshotSizeAligned = GhostComponentSerializer.SnapshotSizeAligned(snapshotSize);
                if (snapshotOffset + snapshotSizeAligned > bufferSize)
                    throw new InvalidOperationException($"Snapshot buffer overflow during deserialize: snapshotOffset({snapshotOffset}) + snapshotSizeAligned({snapshotSizeAligned}) must be <= bufferSize({bufferSize})!");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            static void CheckOffsetLessThanSnapshotBufferSize(int snapshotOffset, int snapshotSize, int bufferSize)
            {
                var snapshotSizeAligned = GhostComponentSerializer.SnapshotSizeAligned(snapshotSize);
                if (snapshotOffset + snapshotSizeAligned > bufferSize)
                    throw new InvalidOperationException($"Snapshot buffer overflow during predict: snapshotOffset({snapshotOffset}) + snapshotSizeAligned({snapshotSizeAligned}) must be <= bufferSize({bufferSize})!");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            static void CheckSnapshotBufferSizeIsCorrect(DynamicBuffer<SnapshotDataBuffer> snapshotDataBuffer, int snapshotSize)
            {
                if (snapshotDataBuffer.Length != snapshotSize * GhostSystemConstants.SnapshotHistorySize)
                    throw new InvalidOperationException($"Invalid snapshot buffer size: snapshotDataBuffer.Length({snapshotDataBuffer.Length}) must == snapshotSize({snapshotSize}) * GhostSystemConstants.SnapshotHistorySize({GhostSystemConstants.SnapshotHistorySize})!");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            void CheckConnectionCountIsValid()
            {
                if (Connections.Length > 1)
                    throw new InvalidOperationException($"Ghost receive system only supports a single connection: Connections.Length({Connections.Length})!");
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var serverTick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;

#if UNITY_EDITOR || NETCODE_DEBUG
            ref var netStatsSnapshotSingleton = ref SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW;
#endif

            var commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            if (m_ConnectionsQuery.IsEmptyIgnoreFilter)
            {
                m_GhostCompletionCount[0] = m_GhostCompletionCount[1] = m_GhostCompletionCount[2] = 0;
                state.CompleteDependency(); // Make sure we can access the spawned ghost map
                // If there were no ghosts spawned at runtime we don't need to cleanup
                if (m_GhostCleanupQuery.IsEmptyIgnoreFilter &&
                    m_SpawnedGhostEntityMap.Count() == 0 && m_GhostEntityMap.Count() == 0)
                    return;
                var clearMapJob = new ClearMapJob
                {
                    GhostMap = m_GhostEntityMap,
                    SpawnedGhostMap = m_SpawnedGhostEntityMap
                };
                k_Scheduling.Begin();
                var clearHandle = clearMapJob.Schedule(state.Dependency);
                k_Scheduling.End();
                if (!m_GhostCleanupQuery.IsEmptyIgnoreFilter)
                {
                    m_EntityTypeHandle.Update(ref state);
                    var clearJob = new ClearGhostsJob
                    {
                        EntitiesType = m_EntityTypeHandle,
                        CommandBuffer = commandBuffer.AsParallelWriter()
                    };
                    k_Scheduling.Begin();
                    state.Dependency = clearJob.ScheduleParallel(m_GhostCleanupQuery, state.Dependency);
                    k_Scheduling.End();
                }
                state.Dependency = JobHandle.CombineDependencies(state.Dependency, clearHandle);
                return;
            }

            // Don't start ghost snapshot processing until we're in game, but allow the cleanup code above to run
            if (!SystemAPI.HasSingleton<NetworkStreamInGame>())
            {
                return;
            }

#if NETCODE_DEBUG
            FixedString128Bytes timestampAndTick = default;
            if (SystemAPI.HasSingleton<EnablePacketLogging>())
            {
                NetDebugInterop.InitDebugPacketIfNotCreated(ref m_NetDebugPacket, m_LogFolder, state.WorldUnmanaged.Name, 0);
                NetDebugInterop.GetTimestampWithTick(serverTick, out timestampAndTick);
                timestampAndTick = $"█ {timestampAndTick}[GRS-RSJ] ";
            }
#endif

            var connections = m_ConnectionsQuery.ToEntityListAsync(state.WorldUpdateAllocator, out var connectionHandle);
            var prespawnSceneStateArray =
                m_SubSceneQuery.ToComponentDataListAsync<SubSceneWithGhostCleanup>(state.WorldUpdateAllocator,
                    out var prespawnHandle);
            ref readonly var ghostDespawnQueues = ref SystemAPI.GetSingletonRW<GhostDespawnQueues>().ValueRO;
            NativeQueue<OwnerSwithchingEntry> ownerPredictedQueues;
            if (SystemAPI.TryGetSingleton<GhostOwnerPredictedSwitchingQueue>(out var ownerPredictedSwitchingQueue))
                ownerPredictedQueues = ownerPredictedSwitchingQueue.SwitchOwnerQueue;
            else
                ownerPredictedQueues = new NativeQueue<OwnerSwithchingEntry>(state.WorldUpdateAllocator);
            UpdateLookupsForReadStreamJob(ref state);
            var ghostCollectionSingleton = SystemAPI.GetSingletonEntity<GhostCollection>();
            var pendingAssignment = SystemAPI.GetSingletonRW<GhostCollection>().ValueRW.PendingGhostPrefabAssignment;
            var readJob = new ReadStreamJob
            {
                GhostCollectionSingleton = ghostCollectionSingleton,
                GhostComponentCollectionFromEntity = m_GhostComponentCollectionFromEntity,
                GhostTypeCollectionFromEntity = m_GhostTypeCollectionFromEntity,
                GhostComponentIndexFromEntity = m_GhostComponentIndexFromEntity,
                GhostCollectionFromEntity = m_GhostCollectionFromEntity,
                Connections = connections,
                SnapshotFromEntity = m_SnapshotFromEntity,
                SnapshotDataBufferFromEntity = m_SnapshotDataBufferFromEntity,
                SnapshotDynamicDataFromEntity = m_SnapshotDynamicDataFromEntity,
                GhostSpawnBufferFromEntity = m_GhostSpawnBufferFromEntity,
                PrespawnBaselineBufferFromEntity = m_PrespawnBaselineBufferFromEntity,
                SnapshotDataFromEntity = m_SnapshotDataFromEntity,
                SnapshotAckFromEntity = m_SnapshotAckFromEntity,
                GhostOwnerFromEntity = m_GhostOwnerFromEntity,
                NetworkIdFromEntity = m_NetworkIdFromEntity,
                GhostEntityMap = m_GhostEntityMap,
                PendingGhostPrefabAssignment = pendingAssignment,
                CompressionModel = m_CompressionModel,
#if UNITY_EDITOR || NETCODE_DEBUG
                SnapshotStatsWriters = netStatsSnapshotSingleton.allGhostStatsParallelWrites.AsArray(),
#endif
                InterpolatedDespawnQueue = ghostDespawnQueues.InterpolatedDespawnQueue,
                PredictedDespawnQueue = ghostDespawnQueues.PredictedDespawnQueue,
                OwnerPredictedSwitchQueue = ownerPredictedQueues,
                PredictedFromEntity = m_PredictedFromEntity,
                GhostFromEntity = m_GhostFromEntity,
                IsThinClient = state.WorldUnmanaged.IsThinClient() ? (byte)1u : (byte)0u,
                CommandBuffer = commandBuffer,
                GhostSpawnEntity = SystemAPI.GetSingletonEntity<GhostSpawnQueue>(),
                GhostCompletionCount = m_GhostCompletionCount,
                TempDynamicData = m_TempDynamicData,
                PrespawnSceneStateArray = prespawnSceneStateArray,
                NetDebug = SystemAPI.GetSingleton<NetDebug>(),
                WorldName = state.WorldUnmanaged.Name,
#if NETCODE_DEBUG
                NetDebugPacket = m_NetDebugPacket,
                PrefabNamesFromEntity = m_PrefabNamesFromEntity,
                EnableLoggingFromEntity = m_EnableLoggingFromEntity,
                DebugLog = timestampAndTick,
#endif
            };
            var tempDeps = new NativeArray<JobHandle>(3, Allocator.Temp);
            tempDeps[0] = state.Dependency;
            tempDeps[1] = connectionHandle;
            tempDeps[2] = prespawnHandle;
            k_Scheduling.Begin();
            state.Dependency = readJob.Schedule(JobHandle.CombineDependencies(tempDeps));
            k_Scheduling.End();
#if NETCODE_DEBUG && !USING_UNITY_LOGGING
            state.Dependency = m_NetDebugPacket.Flush(state.Dependency);
#endif
        }

        void UpdateLookupsForReadStreamJob(ref SystemState state)
        {
            m_SnapshotDataFromEntity.Update(ref state);
            m_SnapshotAckFromEntity.Update(ref state);
            m_PredictedFromEntity.Update(ref state);
            m_GhostFromEntity.Update(ref state);
            m_GhostOwnerFromEntity.Update(ref state);
            m_NetworkIdFromEntity.Update(ref state);
#if NETCODE_DEBUG
            m_PrefabNamesFromEntity.Update(ref state);
#endif
            m_EnableLoggingFromEntity.Update(ref state);

            m_GhostComponentCollectionFromEntity.Update(ref state);
            m_GhostTypeCollectionFromEntity.Update(ref state);
            m_GhostComponentIndexFromEntity.Update(ref state);
            m_GhostCollectionFromEntity.Update(ref state);
            m_SnapshotFromEntity.Update(ref state);
            m_SnapshotDataBufferFromEntity.Update(ref state);
            m_SnapshotDynamicDataFromEntity.Update(ref state);
            m_GhostSpawnBufferFromEntity.Update(ref state);
            m_PrespawnBaselineBufferFromEntity.Update(ref state);
        }
    }
}

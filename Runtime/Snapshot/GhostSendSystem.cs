#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    internal struct GhostSystemStateComponent : ISystemStateComponentData
    {
        public int ghostId;
        public uint spawnTick;
        public uint despawnTick;
    }

    public struct GhostSerializerState
    {
        public ComponentDataFromEntity<GhostComponent> GhostFromEntity;
    }

    internal struct GhostSystemConstants
    {
        public const int SnapshotHistorySize = 32;
        // Dynamic Buffer have a special entry in the snapshot data:
        // uint Offset: the position in bytes from the beginning of the dynamic data store history slot
        // uint Capacity: the slot capacity
        public const int DynamicBufferComponentSnapshotSize = sizeof(uint) + sizeof(uint);
        public const int DynamicBufferComponentMaskBits = 2;
        public const uint MaxNewPrefabsPerSnapshot = 32u; // At most around half the snapshot can consist of new prefabs to use
        public const int MaxDespawnsPerSnapshot = 100; // At most around one quarter the snapshot can consist of despawns
        /// <summary>
        /// Prepend to all serialized ghosts in the snapshot their compressed size. This can be used by the client
        /// to recover from error condition and to skip ghost data in some situation, for example transitory condition
        /// while streaming in/out scenes.
        /// </summary>
        public const bool SnaphostHasCompressedGhostSize = true;
    }

    [UpdateInGroup(typeof(ServerSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    public partial class GhostSendSystem : SystemBase, IGhostMappingSystem
    {
        public GhostRelevancyMode GhostRelevancyMode{get; set;}
        public JobHandle GhostRelevancySetWriteHandle{get;set;}
        public NativeHashMap<RelevantGhostForConnection, int> GhostRelevancySet => m_GhostRelevancySet;
        private NativeHashMap<RelevantGhostForConnection, int> m_GhostRelevancySet;

        private EntityQuery ghostGroup;
        private EntityQuery ghostSpawnGroup;
        private EntityQuery ghostDespawnGroup;
        private EntityQuery prespawnSharedComponents;

        private EntityQuery connectionGroup;

        private NativeQueue<int> m_FreeGhostIds;
        protected NativeArray<int> m_AllocatedGhostIds;
        private NativeList<int> m_DestroyedPrespawns;
        private NativeQueue<int> m_DestroyedPrespawnsQueue;
        private NativeArray<uint> m_DespawnAckedByAllTick;

        private NativeList<ConnectionStateData> m_ConnectionStates;
        private NativeHashMap<Entity, int> m_ConnectionStateLookup;
        private NetworkCompressionModel m_CompressionModel;
        private NativeHashMap<int, ulong> m_SceneSectionHashLookup;

        private ServerSimulationSystemGroup m_ServerSimulation;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private NetworkStreamReceiveSystem m_ReceiveSystem;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal NativeArray<uint> m_NetStats;
        private GhostStatsCollectionSystem m_StatsCollection;
#endif

        private PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate> m_NoDistanceScale;

        private NativeList<int> m_ConnectionRelevantCount;
        private NativeList<ConnectionStateData> m_ConnectionsToProcess;
        private NetDebugSystem m_NetDebugSystem;
#if NETCODE_DEBUG
        private NetDebugPacketLoggers m_PacketLoggers;
        private EntityQuery m_PacketLogEnableQuery;
#endif

        private NativeHashMap<SpawnedGhost, Entity> m_GhostMap;
        private NativeQueue<SpawnedGhost> m_FreeSpawnedGhostQueue;

        public NativeHashMap<SpawnedGhost, Entity> SpawnedGhostEntityMap => m_GhostMap;
        internal NativeList<int> DestroyedPrespawns => m_DestroyedPrespawns;
        public JobHandle LastGhostMapWriter { get; set; }

        private Unity.Profiling.ProfilerMarker m_PrioritizeChunksMarker;
        private Unity.Profiling.ProfilerMarker m_GhostGroupMarker;

        private GhostPreSerializer m_GhostPreSerializer;

        /// <summary>
        /// Non-zero values for <see cref="MinSendImportance"/> can cause both:
        /// a) 'unchanged chunks that are "new" to a new-joiner' and b) 'newly spawned chunks'
        /// to be ignored by the replication priority system for multiple seconds.
        /// If this behaviour is undesirable, set this to be above <see cref="MinSendImportance"/>.
        /// This multiplies the importance value used on those "new" (to the player or to the world) ghost chunks.
        /// Note: This does not guarantee delivery of all "new" chunks,
        /// it only guarantees that every ghost chunk will get serialized and sent at least once per connection,
        /// as quickly as possible (e.g. assuming you have the bandwidth for it).
        /// </summary>
        public uint FirstSendImportanceMultiplier
        {
            get => m_FirstSendImportanceMultiplier;
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(FirstSendImportanceMultiplier));
#endif
                m_FirstSendImportanceMultiplier = value;
            }
        }
        /// <summary>
        /// The minimum importance considered for inclusion in a snapshot. Any ghost importance lower
        /// than this value will not be send every frame even if there is enough space in the packet.
        /// E.g. Value=60, tick-rate=60, ghost.importance=1 implies a ghost will be replicated roughly once per second.
        /// </summary>
        public int MinSendImportance = 0;
        /// <summary>
        /// The minimum importance considered for inclusion in a snapshot after applying distance based
        /// priority scaling. Any ghost importance lower than this value will not be send every frame
        /// even if there is enough space in the packet.
        /// </summary>
        public int MinDistanceScaledSendImportance = 0;
        /// <summary>
        /// The maximum number of chunks the system will try to send to a single connection in a single frame.
        /// A chunk will count as sent even if it does not contain any ghosts which needed to be sent (because
        /// of relevancy or static optimization).
        /// If there are more chunks than this the least important chunks will not be sent even if there is space
        /// in the packet. This can be used to control CPU time on the server.
        /// </summary>
        public int MaxSendChunks = 0;
        /// <summary>
        /// The maximum number of entities the system will try to send to a single connection in a single frame.
        /// An entity will count even if it is not actually sent (because of relevancy or static optimization).
        /// If there are more chunks than this the least important chunks will not be sent even if there is space
        /// in the packet. This can be used to control CPU time on the server.
        /// </summary>
        public int MaxSendEntities = 0;
        /// <summary>
        /// Value used to scale down the importance of chunks where all entities were irrelevant last time it was sent.
        /// The importance is divided by this value. It can be used together with MinSendImportance to make sure
        /// relevancy is not updated every frame for things with low importance.
        /// </summary>
        public int IrrelevantImportanceDownScale
        {
            get => m_IrrelevantImportanceDownScale;
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(IrrelevantImportanceDownScale));
#endif
                m_IrrelevantImportanceDownScale = value;
            }
        }
        /// <summary>
        /// Force all ghosts to use a single baseline. This will reduce CPU usage at the expense of increased
        /// bandwidth usage. This is mostly meant as a way of measuring which ghosts should use static optimization
        /// instead of dynamic. If the bits / ghost does not significantly increase when enabling this the ghost
        /// can use static optimization to save CPU.
        /// </summary>
        public bool ForceSingleBaseline = false;
        /// <summary>
        /// Force all ghosts to use pre serialization. This means part of the serialization will be done once for
        /// all connection instead of once per connection. This can increase CPU time for simple ghosts and ghosts
        /// which are rarely sent. This switch is meant as a way of measuring which ghosts would benefit from using
        /// pre-serialization.
        /// </summary>
        public bool ForcePreSerialize = false;
        /// <summary>
        /// Try to keep the snapshot history buffer for an entity when there is a structucal change.
        /// Doing this will require a lookup and copy of data whenever a ghost has a structucal change
        /// which will add additional CPU cost on the server.
        /// Keeping the snapshot history will not always be possible so this flag does no give a 100% guarantee,
        /// you are expected to measure CPU and bandwidth when changing this.
        /// </summary>
        public bool KeepSnapshotHistoryOnStructuralChange = true;
        /// <summary>
        /// Enable profiling scopes for each component in a ghost. They can help tracking down why a ghost
        /// is expensive to serialize - but they come with a performance cost so they are not enabled by default.
        /// </summary>
        public bool EnablePerComponentProfiling = false;
        /// <summary>
        /// The number of connections to cleanup unused serialization data for in a single tick. Setting this
        /// higher can recover memory faster, but uses more CPU time.
        /// </summary>
        public int CleanupConnectionStatePerTick = 1;

        int m_CurrentCleanupConnectionState;
        uint m_FirstSendImportanceMultiplier = 1;
        int m_IrrelevantImportanceDownScale = 1;

        uint m_SentSnapshots;

        protected override void OnCreate()
        {
            m_NoDistanceScale = GhostDistanceImportance.NoScaleFunctionPointer;
            ghostGroup = GetEntityQuery(ComponentType.ReadOnly<GhostComponent>(), ComponentType.ReadOnly<GhostSystemStateComponent>());
            EntityQueryDesc filterSpawn = new EntityQueryDesc
            {
                All = new ComponentType[] {typeof(GhostComponent)},
                None = new ComponentType[] {typeof(GhostSystemStateComponent), typeof(PreSpawnedGhostIndex)}
            };
            EntityQueryDesc filterDespawn = new EntityQueryDesc
            {
                All = new ComponentType[] {typeof(GhostSystemStateComponent)},
                None = new ComponentType[] {typeof(GhostComponent)}
            };
            ghostSpawnGroup = GetEntityQuery(filterSpawn);
            ghostDespawnGroup = GetEntityQuery(filterDespawn);
            prespawnSharedComponents = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubSceneGhostComponentHash>());

            m_FreeGhostIds = new NativeQueue<int>(Allocator.Persistent);
            m_AllocatedGhostIds = new NativeArray<int>(2, Allocator.Persistent);
            m_AllocatedGhostIds[0] = 1; // To make sure 0 is invalid
            m_AllocatedGhostIds[1] = 1; // To make sure 0 is invalid
            m_DestroyedPrespawns = new NativeList<int>(Allocator.Persistent);
            m_DestroyedPrespawnsQueue = new NativeQueue<int>(Allocator.Persistent);
            m_DespawnAckedByAllTick = new NativeArray<uint>(1, Allocator.Persistent);

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
            m_SceneSectionHashLookup = new NativeHashMap<int, ulong>(256, Allocator.Persistent);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_StatsCollection = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif

            RequireSingletonForUpdate<GhostCollection>();

            m_GhostRelevancySet = new NativeHashMap<RelevantGhostForConnection, int>(1024, Allocator.Persistent);
            m_ConnectionRelevantCount = new NativeList<int>(16, Allocator.Persistent);
            m_ConnectionsToProcess = new NativeList<ConnectionStateData>(16, Allocator.Persistent);

            m_GhostMap = new NativeHashMap<SpawnedGhost, Entity>(1024, Allocator.Persistent);
            m_FreeSpawnedGhostQueue = new NativeQueue<SpawnedGhost>(Allocator.Persistent);

            m_PrioritizeChunksMarker = new Unity.Profiling.ProfilerMarker("PrioritizeChunks");
            m_GhostGroupMarker = new Unity.Profiling.ProfilerMarker("GhostGroup");

            m_NetDebugSystem = World.GetOrCreateSystem<NetDebugSystem>();
#if NETCODE_DEBUG
            m_PacketLoggers = new NetDebugPacketLoggers();
            m_PacketLogEnableQuery = GetEntityQuery(ComponentType.ReadOnly<EnablePacketLogging>());
#endif

            m_GhostPreSerializer = new GhostPreSerializer(GetEntityQuery(ComponentType.ReadOnly<GhostComponent>(), ComponentType.ReadOnly<GhostTypeComponent>(), ComponentType.ReadOnly<PreSerializedGhost>()));
        }

        protected override void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_NetStats.IsCreated)
                m_NetStats.Dispose();
#endif
            m_GhostPreSerializer.Dispose();
            m_CompressionModel.Dispose();
            m_AllocatedGhostIds.Dispose();
            m_FreeGhostIds.Dispose();
            m_DestroyedPrespawns.Dispose();
            m_DestroyedPrespawnsQueue.Dispose();
            m_DespawnAckedByAllTick.Dispose();
            foreach (var connectionState in m_ConnectionStates)
            {
                connectionState.Dispose();
            }
            m_ConnectionStates.Dispose();

            m_ConnectionStateLookup.Dispose();

            GhostRelevancySetWriteHandle.Complete();
            m_GhostRelevancySet.Dispose();
            m_ConnectionRelevantCount.Dispose();
            m_ConnectionsToProcess.Dispose();

            LastGhostMapWriter.Complete();
            m_GhostMap.Dispose();
            m_FreeSpawnedGhostQueue.Dispose();
            m_SceneSectionHashLookup.Dispose();
#if NETCODE_DEBUG
            m_PacketLoggers.Dispose();
#endif
        }

        [BurstCompile]
        struct SpawnGhostJob : IJob
        {
            [ReadOnly] public NativeArray<ConnectionStateData> connectionState;
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefab> GhostCollectionFromEntity;
            [ReadOnly] public NativeArray<ArchetypeChunk> spawnChunks;
            [ReadOnly] public EntityTypeHandle entityType;
            public ComponentTypeHandle<GhostComponent> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostChildEntityComponent> ghostChildEntityComponentType;
            public NativeQueue<int> freeGhostIds;
            public NativeArray<int> allocatedGhostIds;
            public EntityCommandBuffer commandBuffer;
            public NativeHashMap<SpawnedGhost, Entity> ghostMap;

            [ReadOnly] public ComponentDataFromEntity<GhostTypeComponent> ghostTypeFromEntity;
            public uint serverTick;
            public bool forcePreSerialize;
            public NetDebug netDebug;
#if NETCODE_DEBUG
            [ReadOnly] public ComponentDataFromEntity<PrefabDebugName> prefabNames;
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            [ReadOnly] public ComponentTypeHandle<GhostOwnerComponent> ghostOwnerComponentType;
#endif
            public void Execute()
            {
                if (connectionState.Length == 0)
                    return;
                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                for (int chunk = 0; chunk < spawnChunks.Length; ++chunk)
                {
                    var entities = spawnChunks[chunk].GetNativeArray(entityType);
                    var ghostTypeComponent = ghostTypeFromEntity[entities[0]];
                    int ghostType;
                    for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
                    {
                        if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                            break;
                    }
                    if (ghostType >= GhostCollection.Length)
                        throw new InvalidOperationException("Could not find ghost type in the collection");
                    if (ghostType >= GhostTypeCollection.Length)
                        continue; // serialization data has not been loaded yet
                    var ghosts = spawnChunks[chunk].GetNativeArray(ghostComponentType);
                    for (var ent = 0; ent < entities.Length; ++ent)
                    {
                        if (!freeGhostIds.TryDequeue(out var newId))
                        {
                            newId = allocatedGhostIds[0];
                            allocatedGhostIds[0] = newId + 1;
                        }

                        ghosts[ent] = new GhostComponent {ghostId = newId, ghostType = ghostType, spawnTick = serverTick};

                        var spawnedGhost = new SpawnedGhost
                        {
                            ghostId = newId,
                            spawnTick =  serverTick
                        };
                        if (!ghostMap.TryAdd(spawnedGhost, entities[ent]))
                        {
                            netDebug.LogError(FixedString.Format("GhostID {0} already present in the ghost entity map", newId));
                            ghostMap[spawnedGhost] = entities[ent];
                        }

                        var ghostState = new GhostSystemStateComponent
                        {
                            ghostId = newId, despawnTick = 0, spawnTick = serverTick
                        };
                        commandBuffer.AddComponent(entities[ent], ghostState);
                        if (forcePreSerialize)
                            commandBuffer.AddComponent<PreSerializedGhost>(entities[ent]);
#if NETCODE_DEBUG
                        FixedString64Bytes prefabNameString = default;
                        if (prefabNames.HasComponent(GhostCollection[ghostType].GhostPrefab))
                            prefabNameString.Append(prefabNames[GhostCollection[ghostType].GhostPrefab].Name);
                        netDebug.DebugLog(FixedString.Format("[Spawn] GID:{0} Prefab:{1} TypeID:{2} spawnTick:{3}", newId, prefabNameString, ghostType, serverTick));
#endif
                    }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (GhostTypeCollection[ghostType].PredictionOwnerOffset != 0)
                    {
                        if (!spawnChunks[chunk].Has(ghostOwnerComponentType))
                        {
                            netDebug.LogError(FixedString.Format("Ghost type is owner predicted but does not have a GhostOwnerComponent {0}, {1}", ghostType, ghostTypeComponent.guid0));
                            continue;
                        }
                        if (GhostTypeCollection[ghostType].OwnerPredicted != 0)
                        {
                            // Validate that the entity has a GhostOwnerComponent and that the value in the GhosOwnerComponent has been initialized
                            var ghostOwners = spawnChunks[chunk].GetNativeArray(ghostOwnerComponentType);
                            for (int ent = 0; ent < ghostOwners.Length; ++ent)
                            {
                               if (ghostOwners[ent].NetworkId == 0)
                               {
                                   netDebug.LogError("Trying to spawn an owner predicted ghost which does not have a valid owner set. When using owner prediction you must set GhostOwnerComponent.NetworkId when spawning the ghost. If the ghost is not owned by a player you can set NetworkId to -1.");
                               }
                            }
                        }
                    }
#endif
                }
            }
        }

        [BurstCompile]
        struct SerializeJob : IJobParallelForDefer
        {
            public DynamicTypeList DynamicTypeList;
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferFromEntity<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefab> GhostCollectionFromEntity;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionPrefabSerializer> GhostTypeCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;

            public NetworkDriver.Concurrent driver;
            public NetworkPipeline unreliablePipeline;
            public NetworkPipeline unreliableFragmentedPipeline;

            [ReadOnly] public NativeArray<ArchetypeChunk> despawnChunks;
            [ReadOnly] public NativeArray<ArchetypeChunk> ghostChunks;

            public NativeArray<ConnectionStateData> connectionState;
            [ReadOnly] public ComponentDataFromEntity<NetworkSnapshotAckComponent> ackFromEntity;
            [ReadOnly] public ComponentDataFromEntity<NetworkStreamConnection> connectionFromEntity;
            [ReadOnly] public ComponentDataFromEntity<NetworkIdComponent> networkIdFromEntity;

            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<GhostComponent> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostSystemStateComponent> ghostSystemStateType;
            [ReadOnly] public ComponentTypeHandle<PreSerializedGhost> preSerializedGhostType;
            [ReadOnly] public BufferTypeHandle<GhostGroup> ghostGroupType;
            [ReadOnly] public ComponentTypeHandle<GhostChildEntityComponent> ghostChildEntityComponentType;
            [ReadOnly] public ComponentTypeHandle<PreSpawnedGhostIndex> prespawnGhostIdType;
            [ReadOnly] public SharedComponentTypeHandle<SubSceneGhostComponentHash> subsceneHashSharedTypeHandle;

            public GhostRelevancyMode relevancyMode;
            [ReadOnly] public NativeHashMap<RelevantGhostForConnection, int> relevantGhostForConnection;
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
            [ReadOnly] public ComponentDataFromEntity<GhostTypeComponent> ghostTypeFromEntity;
            [ReadOnly] public NativeArray<int> allocatedGhostIds;
            [ReadOnly] public NativeList<int> prespawnDespawns;

            [ReadOnly] public StorageInfoFromEntity childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            [ReadOnly] public BufferTypeHandle<PrespawnGhostBaseline> prespawnBaselineTypeHandle;
            [ReadOnly] public NativeHashMap<int, ulong> SubSceneHashSharedIndexMap;
            public uint CurrentSystemVersion;
            public NetDebug netDebug;
#if NETCODE_DEBUG
            public NetDebugPacket netDebugPacket;
            [ReadOnly] public ComponentDataFromEntity<PrefabDebugName> prefabNames;
            [ReadOnly] public NativeHashMap<int, FixedString128Bytes> componentTypeNameLookup;
            [ReadOnly] public ComponentDataFromEntity<EnablePacketLogging> enableLoggingFromEntity;
            public FixedString32Bytes timestamp;
            public bool enablePerComponentProfiling;
            bool enablePacketLogging;
#endif

            public Entity prespawnSceneLoadedEntity;
            [ReadOnly] public BufferFromEntity<PrespawnSectionAck> prespawnAckFromEntity;
            [ReadOnly] public BufferFromEntity<PrespawnSceneLoaded> prespawnSceneLoadedFromEntity;

            Entity connectionEntity;
            UnsafeHashMap<ArchetypeChunk, GhostChunkSerializationState> chunkSerializationData;
            UnsafeHashMap<int, uint> clearHistoryData;
            ConnectionStateData.GhostStateList ghostStateData;
            int connectionIdx;

            public Unity.Profiling.ProfilerMarker prioritizeChunksMarker;
            public Unity.Profiling.ProfilerMarker ghostGroupMarker;

            public uint FirstSendImportanceMultiplier;
            public int MinSendImportance;
            public int MinDistanceScaledSendImportance;
            public int MaxSendChunks;
            public int MaxSendEntities;
            public int IrrelevantImportanceDownScale;
            public bool forceSingleBaseline;
            public bool keepSnapshotHistoryOnStructuralChange;
            public bool snaphostHasCompressedGhostSize;

            [ReadOnly] public NativeHashMap<ArchetypeChunk, SnapshotPreSerializeData> SnapshotPreSerializeData;
            public unsafe void Execute(int idx)
            {
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = DynamicTypeList.GetData();
                int ghostChunkComponentTypesLength = DynamicTypeList.Length;
                GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];

                connectionIdx = idx;
                var curConnectionState = connectionState[connectionIdx];
                connectionEntity = curConnectionState.Entity;
                chunkSerializationData = curConnectionState.SerializationState;
                clearHistoryData = curConnectionState.ClearHistory;

                curConnectionState.EnsureGhostStateCapacity(allocatedGhostIds[0], allocatedGhostIds[1]);
                ghostStateData = curConnectionState.GhostStateData;
#if NETCODE_DEBUG
                netDebugPacket = curConnectionState.NetDebugPacket;
                enablePacketLogging = enableLoggingFromEntity.HasComponent(connectionEntity);
                if (enablePacketLogging && !netDebugPacket.IsCreated)
                {
                    netDebug.LogError($"Packet logger has not been set. Aborting.");
                    return;
                }
#endif
                var connectionId = connectionFromEntity[connectionEntity].Value;
                if (driver.GetConnectionState(connectionId) != NetworkConnection.State.Connected)
                    return;
                int maxSnapshotSizeWithoutFragmentation = NetworkParameterConstants.MTU - driver.MaxHeaderSize(unreliablePipeline);
                int targetSnapshotSize = maxSnapshotSizeWithoutFragmentation;
                if (snapshotTargetSizeFromEntity.HasComponent(connectionEntity))
                {
                    targetSnapshotSize = snapshotTargetSizeFromEntity[connectionEntity].Value;
                }

                if (prespawnSceneLoadedEntity != Entity.Null)
                {
                    PrespawnHelper.UpdatePrespawnAckSceneMap(ref curConnectionState,
                        prespawnSceneLoadedEntity, prespawnAckFromEntity, prespawnSceneLoadedFromEntity);
                }

                var success = false;
                var result = 0;
                while (!success)
                {
                    // If the requested packet size if larger than one MTU we have to use the fragmentation pipeline
                    var pipelineToUse = (targetSnapshotSize <= maxSnapshotSizeWithoutFragmentation) ? unreliablePipeline : unreliableFragmentedPipeline;

                    if (driver.BeginSend(pipelineToUse, connectionId, out var dataStream, targetSnapshotSize) == 0)
                    {
                        success = sendEntities(ref dataStream, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength);
                        if (success)
                        {
                            if ((result = driver.EndSend(dataStream)) < 0)
                            {
                                netDebug.LogWarning(FixedString.Format("An error occurred during EndSend. ErrorCode: {0}", result));
                            }
                        }
                        else
                            driver.AbortSend(dataStream);
                    }
                    else
                        throw new InvalidOperationException("Failed to send a snapshot to a client");

                    targetSnapshotSize += targetSnapshotSize;
                }
            }

            private unsafe bool sendEntities(ref DataStreamWriter dataStream, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
#if NETCODE_DEBUG
                FixedString512Bytes debugLog = default;
                if (enablePacketLogging)
                    debugLog = FixedString.Format("\n\n[{0}]", timestamp);
#endif
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
                    returnTime += (localTime - snapshotAck.LastReceiveTimestamp);
                dataStream.WriteUInt(returnTime);
                dataStream.WriteInt(snapshotAck.ServerCommandAge);
                dataStream.WriteUInt(currentTick);
#if NETCODE_DEBUG
                if (enablePacketLogging)
                {
                    debugLog.Append(FixedString.Format(" Protocol:{0} LocalTime:{1} ReturnTime:{2} CommandAge:{3}",
                        (byte) NetworkStreamProtocol.Snapshot, localTime, returnTime, snapshotAck.ServerCommandAge));
                    debugLog.Append(FixedString.Format(" Tick: {0}\n", currentTick));
                }
#endif

                // Write the list of ghost snapshots the client has not acked yet
                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                uint numLoadedPrefabs = snapshotAck.NumLoadedPrefabs;
                if (numLoadedPrefabs > (uint)GhostCollection.Length)
                {
                    // The received ghosts by remote might not have been updated yet
                    numLoadedPrefabs = 0;
                    // Override the copy of the snapshot ack so the GhostChunkSerializer can skip this check
                    snapshotAck.NumLoadedPrefabs = 0;
                }
                uint numNewPrefabs = math.min((uint)GhostCollection.Length - numLoadedPrefabs, GhostSystemConstants.MaxNewPrefabsPerSnapshot);
                dataStream.WritePackedUInt(numNewPrefabs, compressionModel);
#if NETCODE_DEBUG
                if (enablePacketLogging)
                    debugLog.Append(FixedString.Format("NewPrefabs: {0}", numNewPrefabs));
#endif
                if (numNewPrefabs > 0)
                {
                    dataStream.WriteUInt(numLoadedPrefabs);
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                    {
                        debugLog.Append(FixedString.Format(" LoadedPrefabs: {0}\n", numNewPrefabs));
                    }
#endif
                    int prefabNum = (int)numLoadedPrefabs;
                    for (var i = 0; i < numNewPrefabs; ++i)
                    {
                        dataStream.WriteUInt(GhostCollection[prefabNum].GhostType.guid0);
                        dataStream.WriteUInt(GhostCollection[prefabNum].GhostType.guid1);
                        dataStream.WriteUInt(GhostCollection[prefabNum].GhostType.guid2);
                        dataStream.WriteUInt(GhostCollection[prefabNum].GhostType.guid3);
                        dataStream.WriteULong(GhostCollection[prefabNum].Hash);
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                        {
                            debugLog.Append(FixedString.Format("\t {0}-{1}-{2}-{3}",
                                GhostCollection[prefabNum].GhostType.guid0, GhostCollection[prefabNum].GhostType.guid1,
                                GhostCollection[prefabNum].GhostType.guid2,
                                GhostCollection[prefabNum].GhostType.guid3));
                            debugLog.Append(FixedString.Format(" Hash:{0}\n", GhostCollection[prefabNum].Hash));
                        }
#endif
                        ++prefabNum;
                    }
                }

                prioritizeChunksMarker.Begin();
                var serialChunks = GatherGhostChunks(out var maxCount, out var totalCount);
                prioritizeChunksMarker.End();
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
#if NETCODE_DEBUG
                if (enablePacketLogging)
                {
                    debugLog.Append(FixedString.Format(" Total: {0}\n", totalCount));
                    // Snapshot header, snapshot data follows
                    netDebugPacket.Log(debugLog);
                    netDebugPacket.Log(FixedString.Format("\t(RelevancyMode: {0})\n", (int) relevancyMode));
                }
#endif
                var lenWriter = dataStream;
                dataStream.WriteUInt(0);
                dataStream.WriteUInt(0);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                int startPos = dataStream.LengthInBits;
#endif
                uint despawnLen = WriteDespawnGhosts(ref dataStream, ackTick);
                if (dataStream.HasFailedWrites)
                {
                    RevertDespawnGhostState(ackTick);
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                        netDebugPacket.Log("Failed to finish writing snapshot.\n");
#endif
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

                uint updateLen = 0;
                bool didFillPacket = false;
                var serializerData = new GhostChunkSerializer
                {
                    GhostComponentCollection = GhostComponentCollection,
                    GhostTypeCollection = GhostTypeCollection,
                    GhostComponentIndex = GhostComponentIndex,
                    PrespawnIndexType = prespawnGhostIdType,
                    ghostGroupMarker = ghostGroupMarker,
                    childEntityLookup = childEntityLookup,
                    linkedEntityGroupType = linkedEntityGroupType,
                    prespawnBaselineTypeHandle = prespawnBaselineTypeHandle,
                    entityType = entityType,
                    ghostComponentType = ghostComponentType,
                    ghostSystemStateType = ghostSystemStateType,
                    preSerializedGhostType = preSerializedGhostType,
                    ghostChildEntityComponentType = ghostChildEntityComponentType,
                    ghostGroupType = ghostGroupType,
                    snapshotAck = snapshotAck,
                    chunkSerializationData = chunkSerializationData,
                    ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                    ghostChunkComponentTypesLength = ghostChunkComponentTypesLength,
                    currentTick = currentTick,
                    compressionModel = compressionModel,
                    serializerState = serializerState,
                    NetworkId = NetworkId,
                    relevantGhostForConnection = relevantGhostForConnection,
                    relevancyMode = relevancyMode,
                    clearHistoryData = clearHistoryData,
                    ghostStateData = ghostStateData,
                    CurrentSystemVersion = CurrentSystemVersion,

                    netDebug = netDebug,
#if NETCODE_DEBUG
                    netDebugPacket = netDebugPacket,
                    componentTypeNameLookup = componentTypeNameLookup,
                    enablePacketLogging = enablePacketLogging,
                    enablePerComponentProfiling = enablePerComponentProfiling,
#endif
                    SnapshotPreSerializeData = SnapshotPreSerializeData,
                    forceSingleBaseline = forceSingleBaseline,
                    keepSnapshotHistoryOnStructuralChange = keepSnapshotHistoryOnStructuralChange,
                    snaphostHasCompressedGhostSize = snaphostHasCompressedGhostSize
                };
                serializerData.AllocateTempData(maxCount, dataStream.Capacity);
                var numChunks = serialChunks.Length;
                if (MaxSendChunks > 0 && numChunks > MaxSendChunks)
                    numChunks = MaxSendChunks;
                for (int pc = 0; pc < numChunks; ++pc)
                {
                    var chunk = serialChunks[pc].chunk;
                    var ghostType = serialChunks[pc].ghostType;

#if NETCODE_DEBUG
                    serializerData.ghostTypeName = default;
                    if (enablePacketLogging)
                    {
                        if (prefabNames.HasComponent(GhostCollection[ghostType].GhostPrefab))
                            serializerData.ghostTypeName.Append(prefabNames[GhostCollection[ghostType].GhostPrefab].Name);
                    }
#endif

                    // Do not send entities with a ghost type which the client has not acked yet
                    if (ghostType >= numLoadedPrefabs)
                    {
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                            netDebugPacket.Log(FixedString.Format("Skipping {0} in snapshot as client has not acked the spawn for it.\n", serializerData.ghostTypeName));
#endif
                        continue;
                    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    var prevUpdateLen = updateLen;
#endif
                    bool writeOK = serializerData.SerializeChunk(serialChunks[pc], ref dataStream,
                        ref updateLen, ref didFillPacket);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (updateLen > prevUpdateLen)
                    {
                        netStats[ghostType*3 + 4] = netStats[ghostType*3 + 4] + updateLen - prevUpdateLen;
                        netStats[ghostType*3 + 5] = netStats[ghostType*3 + 5] + (uint) (dataStream.LengthInBits - startPos);
                        // FIXME: support uncompressed count
                        //netStats[ghostType*3 + 6] = netStats[ghostType*3 + 6] + 0;
                        startPos = dataStream.LengthInBits;
                    }
#endif
                    if (!writeOK)
                        break;
                    if (MaxSendEntities > 0)
                    {
                        MaxSendEntities -= chunk.Count;
                        if (MaxSendEntities <= 0)
                            break;
                    }
                }
                if (dataStream.HasFailedWrites)
                {
                    RevertDespawnGhostState(ackTick);
                    driver.AbortSend(dataStream);
                    throw new InvalidOperationException("Size limitation on snapshot did not prevent all errors");
                }

                dataStream.Flush();
                lenWriter.WriteUInt(despawnLen);
                lenWriter.WriteUInt(updateLen);
#if NETCODE_DEBUG
                if (enablePacketLogging)
                    netDebugPacket.Log(FixedString.Format("Despawn: {0} Update:{1} {2}B\n\n", despawnLen, updateLen, dataStream.Length));
#endif

                var didSend = !(didFillPacket && updateLen == 0);
                if (!didSend)
                    RevertDespawnGhostState(ackTick);
                return didSend;
            }

            // Revert all state updates that happened from failing to write despawn packets
            void RevertDespawnGhostState(uint ackTick)
            {
                ghostStateData.AckedDespawnTick = ackTick;
                ghostStateData.DespawnRepeatCount = 0;
                for (var chunk = 0; chunk < despawnChunks.Length; ++chunk)
                {
                    var ghostStates = despawnChunks[chunk].GetNativeArray(ghostSystemStateType);
                    for (var ent = 0; ent < ghostStates.Length; ++ent)
                    {
                        ref var state = ref ghostStateData.GetGhostState(ghostStates[ent]);
                        state.LastDespawnSendTick = 0;
                        if (ghostStateData.AckedDespawnTick != 0 &&
                            !SequenceHelpers.IsNewer(ghostStates[ent].despawnTick, ghostStateData.AckedDespawnTick))
                        {
                            var despawnAckTick = ghostStates[ent].despawnTick-1;
                            if (despawnAckTick == 0)
                                --despawnAckTick;
                            ghostStateData.AckedDespawnTick = despawnAckTick;
                        }
                    }
                }
                var irrelevant = clearHistoryData.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < irrelevant.Length; ++i)
                {
                    var irrelevantGhost = irrelevant[i];
                    clearHistoryData[irrelevantGhost] = 0;
                }

            }
            /// Write a list of all ghosts which have been despawned after the last acked packet. Return the number of ghost ids written
            uint WriteDespawnGhosts(ref DataStreamWriter dataStream, uint ackTick)
            {
                //For despawns we use a custom ghost id encoding.
                //We left shift the ghost id by one bit and exchange the LSB <> MSB.
                //This way we can encode the prespawn and runtime ghosts with just 1 more bit per entity (on average)
                uint EncodeGhostId(int ghostId)
                {
                    uint encodedGhostId = (uint)ghostId;
                    encodedGhostId = (encodedGhostId << 1) | (encodedGhostId >> 31);
                    return encodedGhostId;
                }
#if NETCODE_DEBUG
                FixedString512Bytes debugLog = default;
                FixedString32Bytes msg = "\t[Despawn IDs]\n";
#endif
                uint despawnLen = 0;
                ghostStateData.AckedDespawnTick = ackTick;
                var snapshotAck = ackFromEntity[connectionEntity];
                uint despawnRepeatTicks = 5u;
                uint repeatNextFrame = 0;
                uint repeatThisFrame = ghostStateData.DespawnRepeatCount;
                for (var chunk = 0; chunk < despawnChunks.Length; ++chunk)
                {
                    var ghostStates = despawnChunks[chunk].GetNativeArray(ghostSystemStateType);
                    for (var ent = 0; ent < ghostStates.Length; ++ent)
                    {
                        ref var state = ref ghostStateData.GetGhostState(ghostStates[ent]);
                        // If the despawn has already been acked we can just mark it as not relevant to make sure it is not sent again
                        // All desapwn messages are sent for despawnRepeatTicks consecutive frames, if any of those is received the despawn is acked
                        if (state.LastDespawnSendTick != 0)
                        {
                            bool isReceived = snapshotAck.IsReceivedByRemote(state.LastDespawnSendTick);
                            for (uint i = 1; i < despawnRepeatTicks; ++i)
                                isReceived |= snapshotAck.IsReceivedByRemote(state.LastDespawnSendTick+i);
                            if (isReceived)
                            {
                                // Already despawned - mark it as not relevant to make sure it does not go out of sync if the ack mask is full
                                state.Flags &= (~ConnectionStateData.GhostStateFlags.IsRelevant);
                            }
                        }
                        // not relevant, will be despawned by the relevancy system or is alrady despawned
                        if ((state.Flags & ConnectionStateData.GhostStateFlags.IsRelevant) == 0)
                        {
                            if (!clearHistoryData.ContainsKey(ghostStates[ent].ghostId))
                            {
                                // The ghost is irrelevant and not waiting for relevancy despawn, it is already deleted and can be ignored
                                continue;
                            }
                            else
                            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                // This path is only expected to be taken when a despawn happened while waiting for relevancy despawn
                                // In that case the LastDespawnSentTick should always be zero
                                UnityEngine.Debug.Assert(state.LastDespawnSendTick==0);
#endif
                                // Treat this as a regular despawn instead, since regular depsawns have higher priority
                                state.LastDespawnSendTick = clearHistoryData[ghostStates[ent].ghostId];
                                clearHistoryData.Remove(ghostStates[ent].ghostId);
                                state.Flags |= ConnectionStateData.GhostStateFlags.IsRelevant;
                            }
                        }

                        // The despawn is pending or will be sent - update the pending despawn tick
                        if (ghostStateData.AckedDespawnTick != 0 &&
                            !SequenceHelpers.IsNewer(ghostStates[ent].despawnTick, ghostStateData.AckedDespawnTick))
                        {
                            // We are going to send (or wait for) a ghost despawned at tick despawnTick, that means
                            // despawnTick cannot be treated as a tick where all desapwns are acked.
                            // We set the despawnAckTick to despawnTick-1 since that is the newest tick that could possibly have all despawns acked
                            var despawnAckTick = ghostStates[ent].despawnTick-1;
                            if (despawnAckTick == 0)
                                --despawnAckTick;
                            ghostStateData.AckedDespawnTick = despawnAckTick;
                        }
                        // If the despawn was sent less than despawnRepeatTicks ticks ago we must send it again
                        if (state.LastDespawnSendTick == 0 || !SequenceHelpers.IsNewer(state.LastDespawnSendTick+despawnRepeatTicks, currentTick))
                        {
                            // Depsawn has been sent, waiting for an ack to see if it needs to be resent
                            if (state.LastDespawnSendTick != 0 && SequenceHelpers.IsNewer(state.LastDespawnSendTick+despawnRepeatTicks, ackTick))
                                continue;

                            // We cannot break since all ghosts must be checked for despawn ack tick
                            if (despawnLen+repeatThisFrame >= GhostSystemConstants.MaxDespawnsPerSnapshot)
                                continue;

                            // Update when we last sent this and send it
                            state.LastDespawnSendTick = currentTick;
                        }
                        else
                        {
                            // This is a repeat, it will be counted in despawn length and this reserved length can be reduced
                            --repeatThisFrame;
                        }
                        // Check if this despawn is expected to be resent next tick
                        if (SequenceHelpers.IsNewer(state.LastDespawnSendTick+despawnRepeatTicks, currentTick+1))
                            ++repeatNextFrame;
                        dataStream.WritePackedUInt(EncodeGhostId(ghostStates[ent].ghostId), compressionModel);
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                        {
                            if (despawnLen == 0)
                                debugLog.Append(msg);

                            debugLog.Append(FixedString.Format(" {0}", ghostStates[ent].ghostId));
                        }
#endif
                        ++despawnLen;
                    }
                }
                // Send out the current list of destroyed prespawned entities for despawning for all new client's loaded scenes
                // We do this by adding all despawned prespawn to the list of irrelevant ghosts and rely on relevancy depsawns
                var newPrespawnLoadedRanges = connectionState[connectionIdx].NewLoadedPrespawnRanges;
                if (prespawnDespawns.Length > 0 && newPrespawnLoadedRanges.Length > 0)
                {
                    for (int i = 0; i < prespawnDespawns.Length; ++i)
                    {
                        if(clearHistoryData.ContainsKey(prespawnDespawns[i]))
                            continue;

                        //If not in range, skip
                        var ghostId = prespawnDespawns[i];
                        if(ghostId < newPrespawnLoadedRanges[0].Begin ||
                           ghostId > newPrespawnLoadedRanges[newPrespawnLoadedRanges.Length-1].End)
                            continue;

                        //Todo: can use a binary search, like lower-bound in c++
                        int idx = 0;
                        while (idx < newPrespawnLoadedRanges.Length && ghostId > newPrespawnLoadedRanges[idx].End) ++idx;
                        if(idx < newPrespawnLoadedRanges.Length)
                            clearHistoryData.TryAdd(ghostId, 0);
                    }
                }

                // If relevancy is enabled, despawn all ghosts which are irrelevant and has not been acked
                if (!clearHistoryData.IsEmpty)
                {
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                        msg = "\t[IrrelevantDespawn or PrespawnDespawn IDs]\n";
                    var currentLength = despawnLen;
#endif
                    // Write the despawns
                    var irrelevant = clearHistoryData.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < irrelevant.Length; ++i)
                    {
                        var irrelevantGhost = irrelevant[i];
                        clearHistoryData.TryGetValue(irrelevantGhost, out var despawnTick);
                        // Check if despawn has been acked, if it has update all state and do not try to send a despawn again
                        if (despawnTick != 0)
                        {
                            bool isReceived = snapshotAck.IsReceivedByRemote(despawnTick);
                            for (uint dst = 1; dst < despawnRepeatTicks; ++dst)
                                isReceived |= snapshotAck.IsReceivedByRemote(despawnTick+dst);
                            if (isReceived)
                            {
                                clearHistoryData.Remove(irrelevantGhost);
                                continue;
                            }
                        }
                        // If the despawn was sent less than despawnRepeatTicks ticks ago we must send it again
                        if (despawnTick == 0 || !SequenceHelpers.IsNewer(despawnTick+despawnRepeatTicks, currentTick))
                        {
                            // The despawn has been send and we do not yet know if it needs to be resent, so don't send anything
                            if (despawnTick != 0 && SequenceHelpers.IsNewer(despawnTick+despawnRepeatTicks, ackTick))
                                continue;

                            if (despawnLen+repeatThisFrame >= GhostSystemConstants.MaxDespawnsPerSnapshot)
                                continue;

                            // Send the despawn and update last tick we did send it
                            clearHistoryData[irrelevantGhost] = currentTick;
                            despawnTick = currentTick;
                        }
                        else
                        {
                            // This is a repeat, it will be counted in despawn length and this reserved length can be reduced
                            --repeatThisFrame;
                        }
                        // Check if this despawn is expected to be resetn next tick
                        if (SequenceHelpers.IsNewer(despawnTick+despawnRepeatTicks, currentTick+1))
                            ++repeatNextFrame;

                        dataStream.WritePackedUInt(EncodeGhostId(irrelevantGhost), compressionModel);
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                        {
                            if (currentLength == despawnLen)
                                debugLog.Append(msg);
                            debugLog.Append(FixedString.Format(" {0}", irrelevantGhost));
                        }
#endif
                        ++despawnLen;
                    }
                }

                ghostStateData.DespawnRepeatCount = repeatNextFrame;
#if NETCODE_DEBUG
                if (enablePacketLogging && debugLog.Length > 0)
                    netDebugPacket.Log(debugLog);
#endif
                return despawnLen;
            }
            private int FindGhostTypeIndex(Entity ent)
            {
                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                int ghostType;
                var ghostTypeComponent = ghostTypeFromEntity[ent];
                for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
                {
                    if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                        break;
                }
                if (ghostType >= GhostCollection.Length)
                {
                    netDebug.LogError("Could not find ghost type in the collection");
                    return -1;
                }
                return ghostType;
            }
            /// Collect a list of all chunks which could be serialized and sent. Sort the list so other systems get it in priority order.
            /// Also cleanup any stale ghost state in the map and create new storage buffers for new chunks so all chunks are in a valid state after this has executed
            NativeList<PrioChunk> GatherGhostChunks(out int maxCount, out int totalCount)
            {
                var serialChunks =
                    new NativeList<PrioChunk>(ghostChunks.Length, Allocator.Temp);
                maxCount = 0;
                totalCount = 0;

                bool connectionHasPosition = ghostConnectionPositionFromEntity.HasComponent(connectionEntity);
                var connectionPosition = default(GhostConnectionPosition);
                if (connectionHasPosition)
                    connectionPosition = ghostConnectionPositionFromEntity[connectionEntity];
                for (int chunk = 0; chunk < ghostChunks.Length; ++chunk)
                {
                    var ghostChunk = ghostChunks[chunk];
                    GhostChunkSerializationState chunkState;
                    var addNew = !chunkSerializationData.TryGetValue(ghostChunk, out chunkState);
                    // FIXME: should be using chunk sequence number instead of this hack
                    if (!addNew && chunkState.arch != ghostChunk.Archetype)
                    {
                        chunkState.FreeSnapshotData();
                        chunkSerializationData.Remove(ghostChunk);
                        addNew = true;
                    }

                    if (addNew)
                    {
                        var ghosts = ghostChunk.GetNativeArray(ghostComponentType);
                        var chunkGhostType = ghosts[0].ghostType;
                        // Pre spawned ghosts might not have a proper ghost type index yet, we calculate it here for pre spawns
                        if (chunkGhostType < 0)
                        {
                            var ghostEntities = ghostChunk.GetNativeArray(entityType);
                            chunkGhostType = FindGhostTypeIndex(ghostEntities[0]);
                            if (chunkGhostType < 0)
                                continue;
                        }
                        if (chunkGhostType >= GhostTypeCollection.Length)
                            continue;
                        chunkState.ghostType = chunkGhostType;
                        chunkState.arch = ghostChunk.Archetype;

                        int serializerDataSize = GhostTypeCollection[chunkState.ghostType].SnapshotSize;
                        chunkState.AllocateSnapshotData(serializerDataSize, ghostChunk.Capacity);
                        chunkState.SetLastUpdate(currentTick - FirstSendImportanceMultiplier);

                        chunkSerializationData.TryAdd(ghostChunk, chunkState);
#if NETCODE_DEBUG
                        if (enablePacketLogging)
                            netDebugPacket.Log(FixedString.Format("Chunk archetype changed, allocating new one TypeID:{0} LastUpdate:{1}\n", chunkState.ghostType, chunkState.GetLastUpdate()));
#endif
                    }
                    chunkState.SetLastValidTick(currentTick);
                    totalCount += ghostChunk.Count;
                    maxCount = math.max(maxCount, ghostChunk.Count);

                    //Prespawn ghost chunk should be considered only if the subscene wich they belong to as been loaded (acked) by the client.
                    if (ghostChunk.Has(prespawnGhostIdType))
                    {
                        var ackedPrespawnSceneMap = connectionState[connectionIdx].AckedPrespawnSceneMap;
                        //Retrieve the subscene hash from the shared component index.
                        var sharedComponentIndex = ghostChunk.GetSharedComponentIndex(subsceneHashSharedTypeHandle);
                        var hash = SubSceneHashSharedIndexMap[sharedComponentIndex];
                        //Skip the chunk if the client hasn't acked/requested streaming that subscene
                        if (!ackedPrespawnSceneMap.ContainsKey(hash))
                        {
#if NETCODE_DEBUG
                            if (enablePacketLogging)
                                netDebugPacket.Log(FixedString.Format("Skipping prespawn chunk with TypeID:{0} for scene {1} not acked by the client\n", chunkState.ghostType, NetDebug.PrintHex(hash)));
#endif
                            continue;
                        }
                    }

                    if (ghostChunk.Has(ghostChildEntityComponentType))
                        continue;

                    var ghostType = chunkState.ghostType;
                    var chunkPriority = (GhostTypeCollection[chunkState.ghostType].BaseImportance *
                                        (int) (currentTick - chunkState.GetLastUpdate()));
                    if (chunkState.GetAllIrrelevant())
                        chunkPriority /= IrrelevantImportanceDownScale;
                    if (chunkPriority < MinSendImportance)
                        continue;
                    if (connectionHasPosition && ghostChunk.Has(ghostDistancePartitionType))
                    {
                        var partitionArray = ghostChunk.GetNativeArray(ghostDistancePartitionType);
                        int3 chunkTile = partitionArray[0].Index;
                        chunkPriority = scaleGhostImportanceByDistance.Ptr.Invoke(ref connectionPosition, ref tileSize, ref tileCenter, ref chunkTile, chunkPriority);
                        if (chunkPriority < MinDistanceScaledSendImportance)
                            continue;
                    }

                    var pc = new PrioChunk
                    {
                        chunk = ghostChunk,
                        priority = chunkPriority,
                        startIndex = chunkState.GetStartIndex(),
                        ghostType = ghostType
                    };
                    serialChunks.Add(pc);
#if NETCODE_DEBUG
                    if (enablePacketLogging)
                        netDebugPacket.Log(FixedString.Format("Adding chunk ID:{0} TypeID:{1} Priority:{2}\n", chunk, ghostType, chunkPriority));
#endif
                }

                NativeArray<PrioChunk> serialChunkArray = serialChunks;
                serialChunkArray.Sort();
                return serialChunks;
            }

        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void UpdateNetStats(out int netStatSize, out int netStatStride, uint serverTick)
        {
            var numLoadedPrefabs = GetSingleton<GhostCollection>().NumLoadedPrefabs;
            var intsPerCacheLine = JobsUtility.CacheLineSize/4;
            netStatSize = numLoadedPrefabs * 3 + 3 + 1;
            // Round up to an even cache line size in order to reduce false sharing
            netStatStride = (netStatSize + intsPerCacheLine-1) & (~(intsPerCacheLine-1));
            if (m_NetStats.IsCreated && m_NetStats.Length != netStatStride * JobsUtility.MaxJobThreadCount)
                m_NetStats.Dispose();
            if (!m_NetStats.IsCreated)
                m_NetStats = new NativeArray<uint>(netStatStride * JobsUtility.MaxJobThreadCount, Allocator.Persistent);
            if (!m_StatsCollection.IsConnected && m_NetStats[0] == 0)
                return;
            for (int worker = 1; worker < JobsUtility.MaxJobThreadCount; ++worker)
            {
                int statOffset = worker * netStatStride;
                for (int i = 1; i < netStatSize; ++i)
                {
                    m_NetStats[i] += m_NetStats[statOffset+i];
                    m_NetStats[statOffset+i] = 0;
                }
            }
            // First uint is tick
            if (m_NetStats[0] != 0)
                m_StatsCollection.AddSnapshotStats(m_NetStats.GetSubArray(0, netStatSize));
            for (int i = 0; i < netStatSize; ++i)
            {
                m_NetStats[i] = 0;
            }
            if (m_StatsCollection.IsConnected)
                m_NetStats[0] = serverTick;
        }
#endif

        protected override void OnUpdate()
        {
            uint currentTick = m_ServerSimulation.ServerTick;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UpdateNetStats(out var netStatSize, out var netStatStride, currentTick);
#endif
            // Calculate how many state updates we should send this frame
            var tickRate = default(ClientServerTickRate);
            if (HasSingleton<ClientServerTickRate>())
            {
                tickRate = GetSingleton<ClientServerTickRate>();
            }

            tickRate.ResolveDefaults();
            var netTickInterval =
                (tickRate.SimulationTickRate + tickRate.NetworkTickRate - 1) / tickRate.NetworkTickRate;
            var sentSnapshots = m_SentSnapshots;
            var sendThisTick = tickRate.SendSnapshotsForCatchUpTicks || !m_ServerSimulation.IsCatchUpTick;
            if (sendThisTick)
                ++m_SentSnapshots;

            // Make sure the list of connections and connection state is up to date
            var connections = connectionGroup.ToEntityArrayAsync(Allocator.TempJob, out var connectionHandle);
            var connectionStates = m_ConnectionStates;
            var connectionStateLookup = m_ConnectionStateLookup;
            var networkIdFromEntity = GetComponentDataFromEntity<NetworkIdComponent>(true);
            var connectionRelevantCount = m_ConnectionRelevantCount;
            var relevancySet = m_GhostRelevancySet;
            bool relevancyEnabled = (GhostRelevancyMode != GhostRelevancyMode.Disabled);
            // Find the latest tick which has been acknowledged by all clients and cleanup all ghosts destroyed before that

            // Setup the connections which require cleanup this frame
            var cleanupConnectionStatePerTick = CleanupConnectionStatePerTick;
            var currentCleanupConnectionState = m_CurrentCleanupConnectionState;
            // This logic is using length from previous frame, that means we can skip updating connections in some cases
            if (connectionStates.Length > 0)
                m_CurrentCleanupConnectionState = (m_CurrentCleanupConnectionState + CleanupConnectionStatePerTick) % connectionStates.Length;
            else
                m_CurrentCleanupConnectionState = 0;

            // Find the latest tick received by all connections
            var despawnAckedByAll = m_DespawnAckedByAllTick;
            despawnAckedByAll[0] = currentTick;
            var connectionsToProcess = m_ConnectionsToProcess;
            connectionsToProcess.Clear();
            var snapshotAckFromEntity = GetComponentDataFromEntity<NetworkSnapshotAckComponent>(true);
            Dependency = Job
                .WithDisposeOnCompletion(connections)
                .WithReadOnly(networkIdFromEntity)
                .WithName("UpdateConnections")
                .WithCode(() => {
                var existing = new NativeHashMap<Entity, int>(connections.Length, Allocator.Temp);
                int maxConnectionId = 0;
                for (int i = 0; i < connections.Length; ++i)
                {
                    existing.TryAdd(connections[i], 1);
                    int stateIndex;
                    if (!connectionStateLookup.TryGetValue(connections[i], out stateIndex))
                    {
                        stateIndex = connectionStates.Length;
                        unsafe
                        {
                            connectionStates.Add(ConnectionStateData.Create(connections[i]));
                        }
                        connectionStateLookup.TryAdd(connections[i], stateIndex);
                    }
                    maxConnectionId = math.max(maxConnectionId, networkIdFromEntity[connections[i]].Value);

                    uint ackedByAllTick = despawnAckedByAll[0];
                    var snapshot = connectionStates[stateIndex].GhostStateData.AckedDespawnTick;
                    if (snapshot == 0)
                        ackedByAllTick = 0;
                    else if (ackedByAllTick != 0 && SequenceHelpers.IsNewer(ackedByAllTick, snapshot))
                        ackedByAllTick = snapshot;
                    despawnAckedByAll[0] = ackedByAllTick;
                }

                for (int i = 0; i < connectionStates.Length; ++i)
                {
                    int val;
                    if (!existing.TryGetValue(connectionStates[i].Entity, out val))
                    {
                        connectionStateLookup.Remove(connectionStates[i].Entity);
                        connectionStates[i].Dispose();
                        if (i != connectionStates.Length - 1)
                        {
                            connectionStates[i] = connectionStates[connectionStates.Length - 1];
                            connectionStateLookup.Remove(connectionStates[i].Entity);
                            connectionStateLookup.TryAdd(connectionStates[i].Entity, i);
                        }

                        connectionStates.RemoveAtSwapBack(connectionStates.Length - 1);
                        --i;
                    }
                }

                connectionRelevantCount.ResizeUninitialized(maxConnectionId+2);
                for (int i = 0; i < connectionRelevantCount.Length; ++i)
                    connectionRelevantCount[i] = 0;

                // go through all keys in the relevancy set, +1 to the connection idx array
                if (relevancyEnabled)
                {
                    var values = relevancySet.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < values.Length; ++i)
                    {
                        var cid = math.min(values[i].Connection, maxConnectionId+1);
                        connectionRelevantCount[cid] = connectionRelevantCount[cid] + 1;
                    }
                }
                if (!sendThisTick)
                    return;
                var sendPerFrame = (connectionStates.Length + netTickInterval - 1) / netTickInterval;
                var sendStartPos = sendPerFrame * (int) (sentSnapshots % netTickInterval);

                if (sendStartPos + sendPerFrame > connectionStates.Length)
                    sendPerFrame = connectionStates.Length - sendStartPos;
                for (int i = 0; i < sendPerFrame; ++i)
                    connectionsToProcess.Add(connectionStates[sendStartPos + i]);
            }).Schedule(JobHandle.CombineDependencies(Dependency, connectionHandle));

#if NETCODE_DEBUG
            FixedString32Bytes packetDumpTimestamp = default;
            if (!m_PacketLogEnableQuery.IsEmptyIgnoreFilter)
            {
                packetDumpTimestamp = new FixedString32Bytes(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                var packetLoggers = m_PacketLoggers;
                var worldName = World.Name;
                Entities.WithoutBurst().WithNone<NetworkStreamDisconnected>().WithAll<EnablePacketLogging, NetworkStreamConnection, NetworkStreamInGame>().ForEach((Entity entity, in NetworkIdComponent id) =>
                {
                    if (!connectionStateLookup.ContainsKey(entity))
                        return;
                    var connectionId = id.Value;
                    var conState = connectionStates[connectionStateLookup[entity]];

                    if (!conState.NetDebugPacket.IsCreated)
                    {
                        conState.NetDebugPacket.Init(worldName, connectionId);
                        packetLoggers.Init(worldName, connectionId);

                        connectionStates[connectionStateLookup[entity]] = conState;
                        // Find connection state in the list sent to the serialize job and replace with this updated version
                        for (int i = 0; i < connectionsToProcess.Length; ++i)
                        {
                            if (connectionsToProcess[i].Entity == entity)
                            {
                                connectionsToProcess[i] = conState;
                                break;
                            }
                        }
                    }
                    packetLoggers.Process(ref conState.NetDebugPacket, connectionId);
                }).Run();
            }
#endif

            // Prepare a command buffer
            EntityCommandBuffer commandBuffer = m_Barrier.CreateCommandBuffer();
            var commandBufferConcurrent = commandBuffer.AsParallelWriter();

            // Setup the tick at which ghosts were despawned, cleanup ghosts which have been despawned and acked by al connections
            var freeGhostIds = m_FreeGhostIds.AsParallelWriter();
            var prespawnDespawn = m_DestroyedPrespawnsQueue.AsParallelWriter();
            var freeSpawendGhosts = m_FreeSpawnedGhostQueue.AsParallelWriter();
            var ghostMap = m_GhostMap;
            var prespawnIdRanges = EntityManager.GetBuffer<PrespawnGhostIdRange>(GetSingletonEntity<PrespawnGhostIdRange>());
            Dependency = Entities
                .WithReadOnly(despawnAckedByAll)
                .WithReadOnly(ghostMap)
                .WithReadOnly(prespawnIdRanges)
                .WithNone<GhostComponent>()
                .WithName("GhostDespawnParallel")
                .ForEach((Entity entity, int entityInQueryIndex, ref GhostSystemStateComponent ghost) => {
                uint ackedByAllTick = despawnAckedByAll[0];
                if (ghost.despawnTick == 0)
                {
                    ghost.despawnTick = currentTick;
                }
                else if (ackedByAllTick != 0 && !SequenceHelpers.IsNewer(ghost.despawnTick, ackedByAllTick))
                {
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghost.ghostId))
                        freeGhostIds.Enqueue(ghost.ghostId);
                    commandBufferConcurrent.RemoveComponent<GhostSystemStateComponent>(entityInQueryIndex, entity);
                }
                //Remove the ghost from the mapping as soon as possible, regardless of clients acknowledge
                var spawnedGhost = new SpawnedGhost {ghostId = ghost.ghostId, spawnTick = ghost.spawnTick};
                if (ghostMap.ContainsKey(spawnedGhost))
                {
                    freeSpawendGhosts.Enqueue(spawnedGhost);
                    //If there is no allocated range, do not add to the queue. That means the subscene the
                    //prespawn belongs to has been unloaded
                    if (PrespawnHelper.IsPrespawGhostId(ghost.ghostId) && prespawnIdRanges.GhostIdRangeIndex(ghost.ghostId) >= 0)
                        prespawnDespawn.Enqueue(ghost.ghostId);
                }
            }).ScheduleParallel(JobHandle.CombineDependencies(Dependency, LastGhostMapWriter));

            // Copy destroyed entities in the parallel write queue populated by ghost cleanup to a single list
            // and free despawned ghosts from map
            var despawnQueue = m_DestroyedPrespawnsQueue;
            var despawnList = m_DestroyedPrespawns;
            var freeSpawnQueue = m_FreeSpawnedGhostQueue;
            Job.WithName("GhostDespawnSingle").WithCode(() =>
            {
                while (despawnQueue.TryDequeue(out int destroyed))
                {
                    if (!despawnList.Contains(destroyed))
                        despawnList.Add(destroyed);
                }
                while (freeSpawnQueue.TryDequeue(out var spawnedGhost))
                    ghostMap.Remove(spawnedGhost);
            }).Schedule();
            LastGhostMapWriter = Dependency;

            // If the ghost collection has not been initialized yet the send ystem can not process any ghosts
            if (!GetSingleton<GhostCollection>().IsInGame)
            {
                m_Barrier.AddJobHandleForProducer(Dependency);
                return;
            }

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

            // Get component types for serialization
            var entityType = GetEntityTypeHandle();
            var ghostSystemStateType = GetComponentTypeHandle<GhostSystemStateComponent>(true);
            var preSerializedGhostType = GetComponentTypeHandle<PreSerializedGhost>(true);
            var ghostComponentType = GetComponentTypeHandle<GhostComponent>();
            var ghostChildEntityComponentType = GetComponentTypeHandle<GhostChildEntityComponent>(true);
            var ghostGroupType = GetBufferTypeHandle<GhostGroup>(true);
            var ghostOwnerComponentType = GetComponentTypeHandle<GhostOwnerComponent>(true);

            // Extract all newly spawned ghosts and set their ghost ids
            JobHandle spawnChunkHandle;
            var ghostCollectionSingleton = GetSingletonEntity<GhostCollection>();
            var ghostTypeCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefabSerializer>(true);
            var ghostTypeFromEntity = GetComponentDataFromEntity<GhostTypeComponent>(true);
            var spawnChunks = ghostSpawnGroup.CreateArchetypeChunkArrayAsync(Allocator.TempJob, out spawnChunkHandle);
            var spawnJob = new SpawnGhostJob
            {
                connectionState = m_ConnectionsToProcess.AsDeferredJobArray(),
                GhostCollectionSingleton = ghostCollectionSingleton,
                GhostTypeCollectionFromEntity = ghostTypeCollectionFromEntity,
                GhostCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefab>(true),
                spawnChunks = spawnChunks,
                entityType = entityType,
                ghostComponentType = ghostComponentType,
                ghostChildEntityComponentType = ghostChildEntityComponentType,
                freeGhostIds = m_FreeGhostIds,
                allocatedGhostIds = m_AllocatedGhostIds,
                commandBuffer = commandBuffer,
                ghostMap = ghostMap,
                ghostTypeFromEntity = ghostTypeFromEntity,
                serverTick = currentTick,
                forcePreSerialize = ForcePreSerialize,
                netDebug = m_NetDebugSystem.NetDebug,
#if NETCODE_DEBUG
                prefabNames = GetComponentDataFromEntity<PrefabDebugName>(true),
#endif
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                ghostOwnerComponentType = ghostOwnerComponentType
#endif
            };
            Dependency = spawnJob.Schedule(JobHandle.CombineDependencies(Dependency, spawnChunkHandle));
            LastGhostMapWriter = Dependency;

            // Create chunk arrays for ghosts and despawned ghosts
            JobHandle despawnChunksHandle, ghostChunksHandle;
            var despawnChunks = ghostDespawnGroup.CreateArchetypeChunkArrayAsync(Allocator.TempJob, out despawnChunksHandle);
            var ghostChunks = ghostGroup.CreateArchetypeChunkArrayAsync(Allocator.TempJob, out ghostChunksHandle);
            Dependency = JobHandle.CombineDependencies(Dependency, despawnChunksHandle, ghostChunksHandle);

            TryGetSingletonEntity<PrespawnSceneLoaded>(out var prespawnSceneLoadedEntity);
            PrespawnHelper.PopulateSceneHashLookupTable(prespawnSharedComponents, EntityManager, m_SceneSectionHashLookup);

            // If there are any connections to send data to, serialize the data for them in parallel
            var serializeJob = new SerializeJob
            {
                GhostCollectionSingleton = ghostCollectionSingleton,
                GhostComponentCollectionFromEntity = GetBufferFromEntity<GhostComponentSerializer.State>(true),
                GhostTypeCollectionFromEntity = ghostTypeCollectionFromEntity,
                GhostComponentIndexFromEntity = GetBufferFromEntity<GhostCollectionComponentIndex>(true),
                GhostCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefab>(true),
                SubSceneHashSharedIndexMap = m_SceneSectionHashLookup,
                driver = m_ReceiveSystem.ConcurrentDriver,
                unreliablePipeline = m_ReceiveSystem.UnreliablePipeline,
                unreliableFragmentedPipeline = m_ReceiveSystem.UnreliableFragmentedPipeline,
                despawnChunks = despawnChunks,
                ghostChunks = ghostChunks,
                connectionState = m_ConnectionsToProcess.AsDeferredJobArray(),
                ackFromEntity = snapshotAckFromEntity,
                connectionFromEntity = GetComponentDataFromEntity<NetworkStreamConnection>(true),
                networkIdFromEntity = networkIdFromEntity,
                entityType = entityType,
                ghostSystemStateType = ghostSystemStateType,
                preSerializedGhostType = preSerializedGhostType,
                prespawnGhostIdType = GetComponentTypeHandle<PreSpawnedGhostIndex>(true),
                ghostComponentType = ghostComponentType,
                ghostGroupType = ghostGroupType,
                ghostChildEntityComponentType = ghostChildEntityComponentType,
                relevantGhostForConnection = GhostRelevancySet,
                relevancyMode = GhostRelevancyMode,
                relevantGhostCountForConnection = m_ConnectionRelevantCount.AsDeferredJobArray(),
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

                ghostTypeFromEntity = ghostTypeFromEntity,

                allocatedGhostIds = m_AllocatedGhostIds,
                prespawnDespawns = m_DestroyedPrespawns,
                childEntityLookup = GetStorageInfoFromEntity(),
                linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(true),
                prespawnBaselineTypeHandle = GetBufferTypeHandle<PrespawnGhostBaseline>(true),
                subsceneHashSharedTypeHandle = GetSharedComponentTypeHandle<SubSceneGhostComponentHash>(),
                prespawnSceneLoadedEntity = prespawnSceneLoadedEntity,
                prespawnAckFromEntity = GetBufferFromEntity<PrespawnSectionAck>(true),
                prespawnSceneLoadedFromEntity = GetBufferFromEntity<PrespawnSceneLoaded>(true),

                CurrentSystemVersion = GlobalSystemVersion,
                prioritizeChunksMarker = m_PrioritizeChunksMarker,
                ghostGroupMarker = m_GhostGroupMarker,
#if NETCODE_DEBUG
                prefabNames = GetComponentDataFromEntity<PrefabDebugName>(true),
                componentTypeNameLookup = m_NetDebugSystem.ComponentTypeNameLookup,
                enableLoggingFromEntity = GetComponentDataFromEntity<EnablePacketLogging>(),
                timestamp = packetDumpTimestamp,
                enablePerComponentProfiling = EnablePerComponentProfiling,
#endif
                netDebug = m_NetDebugSystem.NetDebug,
                FirstSendImportanceMultiplier = FirstSendImportanceMultiplier,
                MinSendImportance = MinSendImportance,
                MinDistanceScaledSendImportance = MinDistanceScaledSendImportance,
                MaxSendChunks = MaxSendChunks,
                MaxSendEntities = MaxSendEntities,
                IrrelevantImportanceDownScale = IrrelevantImportanceDownScale,
                forceSingleBaseline = ForceSingleBaseline,
                keepSnapshotHistoryOnStructuralChange = KeepSnapshotHistoryOnStructuralChange,
                snaphostHasCompressedGhostSize = GhostSystemConstants.SnaphostHasCompressedGhostSize
            };

            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(ghostCollectionSingleton);
            Dependency = m_GhostPreSerializer.Schedule(Dependency,
                serializeJob.GhostComponentCollectionFromEntity,
                serializeJob.GhostTypeCollectionFromEntity,
                serializeJob.GhostComponentIndexFromEntity,
                serializeJob.GhostCollectionSingleton,
                serializeJob.GhostCollectionFromEntity,
                serializeJob.linkedEntityGroupType,
                serializeJob.childEntityLookup,
                serializeJob.ghostComponentType,
                GetComponentTypeHandle<GhostTypeComponent>(true),
                serializeJob.entityType,
                serializeJob.ghostFromEntity,
                serializeJob.netDebug,
                currentTick,
                this,
                ghostComponentCollection);
            serializeJob.SnapshotPreSerializeData = m_GhostPreSerializer.SnapshotData;


            Dependency = JobHandle.CombineDependencies(Dependency, m_ReceiveSystem.LastDriverWriter, GhostRelevancySetWriteHandle);
            DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref serializeJob.DynamicTypeList);
            Dependency = serializeJob.ScheduleByRef(m_ConnectionsToProcess, 1, Dependency);
            GhostRelevancySetWriteHandle = Dependency;

            var serializeHandle = Dependency;
            // Schedule a job to clean up connections
            var cleanupHandle = Job
                .WithName("CleanupGhostSerializationState")
                .WithCode(() => {
                var conCount = math.min(cleanupConnectionStatePerTick, connectionStates.Length);
                var existingChunks = new NativeHashMap<ArchetypeChunk, int>(ghostChunks.Length, Allocator.Temp);
                for (int chunk = 0; chunk < ghostChunks.Length; ++chunk)
                {
                    existingChunks.TryAdd(ghostChunks[chunk], 1);
                }
                for (int con = 0; con < conCount; ++con)
                {
                    var conIdx = (con + currentCleanupConnectionState) % connectionStates.Length;
                    var chunkSerializationData = connectionStates[conIdx].SerializationState;
                    var oldChunks = chunkSerializationData.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < oldChunks.Length; ++i)
                    {
                        if (!existingChunks.ContainsKey(oldChunks[i]))
                        {
                            GhostChunkSerializationState chunkState;
                            chunkSerializationData.TryGetValue(oldChunks[i], out chunkState);
                            chunkState.FreeSnapshotData();
                            chunkSerializationData.Remove(oldChunks[i]);
                        }
                    }
                }
            }).Schedule(serializeHandle);

            var flushHandle = m_ReceiveSystem.Driver.ScheduleFlushSend(serializeHandle);
            m_ReceiveSystem.LastDriverWriter = flushHandle;
            Dependency = JobHandle.CombineDependencies(flushHandle, cleanupHandle);

            despawnChunks.Dispose(Dependency);
            spawnChunks.Dispose(Dependency);
            ghostChunks.Dispose(Dependency);
            // Only the spawn job is using the commandBuffer, but the serialize job is using the same chunks - so we must wait for that too before we can modify them
            m_Barrier.AddJobHandleForProducer(Dependency);
        }

        internal struct PrioChunk : IComparable<PrioChunk>
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

        // Set the allocation ghost ID,
        internal void SetAllocatedPrespawnGhostId(int prespawnCount)
        {
            m_AllocatedGhostIds[1] = prespawnCount;
        }
    }
}

using System;
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
    // FIXME: temporary workaround for IJobParallelForDefer not passing this by ref
    [JobProducerType(typeof(IJobParallelForDeferRefExtensions.JobParallelForDeferProducer<>))]
    public interface IJobParallelForDeferRef
    {
        void Execute(int index);
    }

    public static class IJobParallelForDeferRefExtensions
    {
        internal struct JobParallelForDeferProducer<T> where T : struct, IJobParallelForDeferRef
        {
            static IntPtr s_JobReflectionData;

            public static unsafe IntPtr Initialize()
            {
                if (s_JobReflectionData == IntPtr.Zero)
                {
#if UNITY_2020_2_OR_NEWER
                    s_JobReflectionData = JobsUtility.CreateJobReflectionData(typeof(T), typeof(T), (ExecuteJobFunction)Execute);
#else
                    s_JobReflectionData = JobsUtility.CreateJobReflectionData(typeof(T), typeof(T),
                        JobType.ParallelFor, (ExecuteJobFunction)Execute);
#endif
                }

                return s_JobReflectionData;
            }

            public delegate void ExecuteJobFunction(ref T jobData, IntPtr additionalPtr, IntPtr bufferRangePatchData, ref JobRanges ranges, int jobIndex);

            public unsafe static void Execute(ref T jobData, IntPtr additionalPtr, IntPtr bufferRangePatchData, ref JobRanges ranges, int jobIndex)
            {
                while (true)
                {
                    if (!JobsUtility.GetWorkStealingRange(ref ranges, jobIndex, out int begin, out int end))
                        break;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    JobsUtility.PatchBufferMinMaxRanges(bufferRangePatchData, UnsafeUtility.AddressOf(ref jobData), begin, end - begin);
#endif
                    for (var i = begin; i < end; ++i)
                        jobData.Execute(i);
                }
            }
        }

        public static unsafe JobHandle Schedule<T, U>(ref this T jobData, NativeList<U> list, int innerloopBatchCount,
            JobHandle dependsOn = new JobHandle())
            where T : struct, IJobParallelForDeferRef
            where U : struct
        {
            void* atomicSafetyHandlePtr = null;

            // Calculate the deferred atomic safety handle before constructing JobScheduleParameters so
            // DOTS Runtime can validate the deferred list statically similar to the reflection based
            // validation in Big Unity.
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = NativeListUnsafeUtility.GetAtomicSafetyHandle(ref list);
            atomicSafetyHandlePtr = UnsafeUtility.AddressOf(ref safety);
#endif

            var scheduleParams = new JobsUtility.JobScheduleParameters(
                UnsafeUtility.AddressOf(ref jobData),
                JobParallelForDeferProducer<T>.Initialize(), dependsOn,
#if UNITY_2020_2_OR_NEWER
                ScheduleMode.Parallel
#else
                ScheduleMode.Batched
#endif
                );

            return JobsUtility.ScheduleParallelForDeferArraySize(ref scheduleParams, innerloopBatchCount,
                NativeListUnsafeUtility.GetInternalListDataPtrUnchecked(ref list), atomicSafetyHandlePtr);
        }
    }
    // </FIXME>

    internal struct GhostSystemStateComponent : ISystemStateComponentData
    {
        public int ghostId;
        public uint spawnTick;
        public uint despawnTick;
    }

    internal unsafe struct SnapshotBaseline
    {
        public uint tick;
        public byte* snapshot;
        public Entity* entity;
        //dynamic buffer data storage associated with the snapshot
        public byte *dynamicData;
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

        //can be null in certain conditions:
        // GhostGroup (temporary)
        // Spawn chunks
        internal byte* currentSnapshotDynamicData;
        internal int currentDynamicDataCapacity;
        // Total chunck dynamic buffers data to serialize.
        // currentDynamicDataCapacity and snapshotDynamicDataSize can be different (currentDynamicDataCapacity is usually larger).
        // Spawn chunks does not allocate a full history buffer and so currentDynamicDataCapacity equals 0 and a temporary
        // data buffer is created instead
        internal int snapshotDynamicDataSize;
        //temporary baseline buffer used to serialize dynamic data
        public byte* tempBaselineDynamicData;
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
    }

    [UpdateInGroup(typeof(ServerSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    public class GhostSendSystem : SystemBase, IGhostMappingSystem
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

        private EntityQuery m_ChildEntityQuery;
        private NativeHashMap<Entity, EntityChunkLookup> m_ChildEntityLookup;
        private NativeList<int> m_ConnectionRelevantCount;
        private NativeList<ConnectionStateData> m_ConnectionsToProcess;

        private NativeHashMap<SpawnedGhost, Entity> m_GhostMap;
        private NativeQueue<SpawnedGhost> m_FreeSpawnedGhostQueue;

        public NativeHashMap<SpawnedGhost, Entity> SpawnedGhostEntityMap => m_GhostMap;
        public JobHandle LastGhostMapWriter { get; set; }

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
            // When a ghost archetype contains buffers, the snapshotData associated with the buffer will contains the following pair:
            //   uint bufferLen the length of the buffer
            //   uint bufferContentOffset offset from the beginning of the dynamic history slot wher buffers elements and the masks are stored (see info below)

            // 4 is size of uint in bytes, the chunk size is in bytes
            const int DataPerChunkSize = (4 * (GhostSystemConstants.SnapshotHistorySize + 3 + ((GhostSystemConstants.SnapshotHistorySize+31)>>5)) + 15) & (~15);

            // Buffers, due to their dynamic nature, require another history container.
            // The buffers contents are stored in a dynamic array that can grow to accomodate more data as needed. Also, the snapshot dynamic storage
            // can handle different kind of DynamicBuffer element type. Each serialized buffer contents size (len * ComponentSnapshotSize) is aligned to 16 bytes
            // The memory layout is:
            // begin array[GhostSystemConstants.SnapshotHistorySize]
            //     uint dynamicDataSize[capacity]  total buffers data used by each entity in the chunk, aligned to 16 bytes
            //     beginArray[current buffers in the chunk]
            //         uints[Len*ChangeBitMaskUintSize] the elements changemask, aligned to 16 bytes
            //         byte[Len*ComponentSnapshotSize] all the raw elements snapshot data
            //     end
            // end
            private byte* snapshotDynamicData;
            private int snapshotDynamicCapacity;


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
                snapshotDynamicData = null;
                snapshotDynamicCapacity = 0;
            }

            public void FreeSnapshotData()
            {
                UnsafeUtility.Free(snapshotData, Allocator.Persistent);
                if(snapshotDynamicData != null)
                    UnsafeUtility.Free(snapshotDynamicData, Allocator.Persistent);
                snapshotData = null;
                snapshotDynamicData = null;
                snapshotDynamicCapacity = 0;
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


            private static int CalculateSize(int serializerDataSize, int chunkCapacity)
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

            /// <summary>
            /// Return the pointer to the dynamic data snapshot storage for the given history position or null if the storage
            /// is not present or not initialized
            /// </summary>
            /// <param name="historyPosition"></param>
            /// <param name="capacity"></param>
            /// <param name="chunkCapacity"></param>
            /// <returns></returns>
            /// <exception cref="InvalidOperationException"></exception>
            /// <exception cref="IndexOutOfRangeException"></exception>
            public byte* GetDynamicDataPtr(int historyPosition, int chunkCapacity, out int capacity)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (historyPosition < 0 || historyPosition >= GhostSystemConstants.SnapshotHistorySize)
                    throw new IndexOutOfRangeException("Reading invalid history position");
#endif
                //If the chunk state has just been created the dynamic data ptr must be allocated
                //once we collected the necessary capacity
                if (snapshotDynamicData == null)
                {
                    capacity = 0;
                    return null;
                }
                var headerSize = GetDynamicDataHeaderSize(chunkCapacity);
                var slotStride = snapshotDynamicCapacity / GhostSystemConstants.SnapshotHistorySize;
                capacity = slotStride - headerSize;
                return snapshotDynamicData + slotStride*historyPosition;
            }

            static public int GetDynamicDataHeaderSize(int chunkCapacity)
            {
                return GhostCollectionSystem.SnapshotSizeAligned(sizeof(uint) * chunkCapacity);
            }

            public void EnsureDynamicDataCapacity(int historySlotCapacity, int chunkCapacity)
            {
                //Get the next pow2 that fit the required size
                var headerSize = GetDynamicDataHeaderSize(chunkCapacity);
                var wantedSize = GhostCollectionSystem.SnapshotSizeAligned(historySlotCapacity + headerSize);
                var newCapacity = math.ceilpow2(wantedSize * GhostSystemConstants.SnapshotHistorySize);
                if (snapshotDynamicCapacity < newCapacity)
                {
                    var temp = (byte*)UnsafeUtility.Malloc(newCapacity, 16, Allocator.Persistent);
                    //Copy the old content
                    if (snapshotDynamicData != null)
                    {
                        var slotSize = snapshotDynamicCapacity / GhostSystemConstants.SnapshotHistorySize;
                        var newSlotSize = newCapacity / GhostSystemConstants.SnapshotHistorySize;
                        var sourcePtr = snapshotDynamicData;
                        var destPtr = temp;
                        for (int i = 0; i < GhostSystemConstants.SnapshotHistorySize; ++i)
                        {
                            UnsafeUtility.MemCpy(destPtr, sourcePtr,slotSize);
                            destPtr += newSlotSize;
                            sourcePtr += slotSize;
                        }
                        UnsafeUtility.Free(snapshotDynamicData, Allocator.Persistent);
                    }
                    snapshotDynamicCapacity = newCapacity;
                    snapshotDynamicData = temp;
                }
            }
        }

        struct ConnectionStateData : IDisposable
        {
            public void Dispose()
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

            RequireSingletonForUpdate<GhostCollection>();
            RequireSingletonForUpdate<NetworkStreamInGame>();

            m_ChildEntityQuery = GetEntityQuery(ComponentType.ReadOnly<GhostChildEntityComponent>());
            m_ChildEntityLookup = new NativeHashMap<Entity, EntityChunkLookup>(1024, Allocator.Persistent);

            m_GhostRelevancySet = new NativeHashMap<RelevantGhostForConnection, int>(1024, Allocator.Persistent);
            m_ConnectionRelevantCount = new NativeList<int>(16, Allocator.Persistent);
            m_ConnectionsToProcess = new NativeList<ConnectionStateData>(16, Allocator.Persistent);

            m_GhostMap = new NativeHashMap<SpawnedGhost, Entity>(1024, Allocator.Persistent);
            m_FreeSpawnedGhostQueue = new NativeQueue<SpawnedGhost>(Allocator.Persistent);
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
            m_ConnectionRelevantCount.Dispose();
            m_ConnectionsToProcess.Dispose();

            LastGhostMapWriter.Complete();
            m_GhostMap.Dispose();
            m_FreeSpawnedGhostQueue.Dispose();
        }

        [BurstCompile]
        struct SpawnGhostJob : IJob
        {
            [ReadOnly] public NativeArray<ConnectionStateData> connectionState;
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefab> GhostCollectionFromEntity;
            [ReadOnly] public NativeArray<ArchetypeChunk> spawnChunks;
            public NativeList<PrioChunk> serialSpawnChunks;
            [ReadOnly] public EntityTypeHandle entityType;
            public ComponentTypeHandle<GhostComponent> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostChildEntityComponent> ghostChildEntityComponentType;
            public NativeQueue<int> freeGhostIds;
            public NativeArray<int> allocatedGhostIds;
            public EntityCommandBuffer commandBuffer;
            public NativeHashMap<SpawnedGhost, Entity> ghostMap;

            [ReadOnly] public ComponentDataFromEntity<GhostTypeComponent> ghostTypeFromEntity;
            public uint serverTick;

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
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            UnityEngine.Debug.LogError($"GhostID {newId} already present in the ghost entity map");
#endif
                            ghostMap[spawnedGhost] = entities[ent];
                        }

                        var ghostState = new GhostSystemStateComponent
                        {
                            ghostId = newId, despawnTick = 0, spawnTick = serverTick
                        };
                        commandBuffer.AddComponent(entities[ent], ghostState);
                    }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (GhostTypeCollection[ghostType].PredictionOwnerOffset != 0)
                    {
                        if (!spawnChunks[chunk].Has(ghostOwnerComponentType))
                        {
                            UnityEngine.Debug.LogError($"Ghost type is owner predicted but does not have a GhostOwnerComponent {ghostType}, {ghostTypeComponent.guid0}");
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
                                   UnityEngine.Debug.LogError("Trying to spawn an owner predicted ghost which does not have a valid owner set. When using owner prediction you must set GhostOwnerComponent.NetworkId when spawning the ghost. If the ghost is not owned by a player you can set NetworkId to -1.");
                                   continue;
                               }
                            }
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
        unsafe struct SerializeJob32 : IJobParallelForDeferRef
        {
            public DynamicTypeList32 List;
            public SerializeJob Job;
            public void Execute(int idx)
            {
                Job.Execute(idx, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        unsafe struct SerializeJob64 : IJobParallelForDeferRef
        {
            public DynamicTypeList64 List;
            public SerializeJob Job;
            public void Execute(int idx)
            {
                Job.Execute(idx, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        unsafe struct SerializeJob128 : IJobParallelForDeferRef
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

            [ReadOnly] public NativeList<PrioChunk> serialSpawnChunks;

            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<GhostComponent> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostSystemStateComponent> ghostSystemStateType;
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
            [ReadOnly] public ComponentDataFromEntity<GhostTypeComponent> ghostTypeFromEntity;
            [ReadOnly] public NativeList<int> prespawnDespawns;

            [ReadOnly] public NativeHashMap<Entity, EntityChunkLookup> childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            public uint CurrentSystemVersion;


            Entity connectionEntity;
            UnsafeHashMap<ArchetypeChunk, SerializationState> chunkSerializationData;
            UnsafeHashMap<int, uint> clearHistoryData;
            int connectionIdx;

            public unsafe void Execute(int idx, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];

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
                var result = 0;
                while (!success)
                {
                    // If the requested packet size if larger than one MTU we have to use the fragmentation pipeline
                    var pipelineToUse = (targetSnapshotSize <= maxSnapshotSizeWithoutFragmentation) ? unreliablePipeline : unreliableFragmentedPipeline;

                    if (driver.BeginSend(pipelineToUse, connectionId, out var dataStream, targetSnapshotSize) == 0)
                    {
                        if (!dataStream.IsCreated)
                            throw new InvalidOperationException("Failed to send a snapshot to a client");
                        success = sendEntities(ref dataStream, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength);
                        if (success)
                        {
                            if ((result = driver.EndSend(dataStream)) < 0)
                            {
                                UnityEngine.Debug.LogWarning($"An error occured during EndSend. ErrorCode: {result}");
                            }
                        }
                        else
                            driver.AbortSend(dataStream);
                    }

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
                // Write the list of ghost snapshots the client has not acked yet
                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                uint numLoadedPrefabs = snapshotAck.NumLoadedPrefabs;
                if (numLoadedPrefabs > (uint)GhostCollection.Length)
                {
                    // The received ghosts by remove might not have been updated yet
                    numLoadedPrefabs = 0;
                }
                uint numNewPrefabs = math.min((uint)GhostCollection.Length - numLoadedPrefabs, GhostSystemConstants.MaxNewPrefabsPerSnapshot);
                dataStream.WritePackedUInt(numNewPrefabs, compressionModel);
                if (numNewPrefabs > 0)
                {
                    dataStream.WriteUInt(numLoadedPrefabs);
                    int prefabNum = (int)numLoadedPrefabs;
                    for (var i = 0; i < numNewPrefabs; ++i)
                    {
                        dataStream.WriteUInt(GhostCollection[prefabNum].GhostType.guid0);
                        dataStream.WriteUInt(GhostCollection[prefabNum].GhostType.guid1);
                        dataStream.WriteUInt(GhostCollection[prefabNum].GhostType.guid2);
                        dataStream.WriteUInt(GhostCollection[prefabNum].GhostType.guid3);
                        dataStream.WriteULong(GhostCollection[prefabNum].Hash);
                        ++prefabNum;
                    }
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
                dataStream.WriteUInt(0);
                dataStream.WriteUInt(0);
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

                //Compute the largest dynamic element size and create a temporary empty baseline
                //This is only used as empty/default memory buffer to serialize the dynamic buffer data if the baseline and
                //current snapshot buffers lengths are different. This is why we only need to store a memory region large
                //as the biggest component snapshot size
                int maxDynamicSnapshotSize = 0;
                for (int i = 0; i < GhostTypeCollection.Length; ++i)
                    maxDynamicSnapshotSize = math.max(maxDynamicSnapshotSize, GhostTypeCollection[i].MaxBufferSnapshotSize);
                byte* tempDynamicBaselineData = (byte*)UnsafeUtility.Malloc(maxDynamicSnapshotSize, 16, Allocator.Temp);
                UnsafeUtility.MemClear(tempDynamicBaselineData, maxDynamicSnapshotSize);

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

                    // Do not send entities with a ghost type which the client has not acked yet
                    if (ghostType >= numLoadedPrefabs)
                        continue;

                    Entity* currentSnapshotEntity = null;
                    byte* currentSnapshotData = null;
                    byte* currentSnapshotFlags = null;
                    SerializationState chunkState;
                    availableBaselines.Clear();

                    byte* currentSnapshotDynamicData = null;
                    int currentDynamicDataCapacity = 0;
                    int snapshotDynamicDataSize = 0;

                    // Ghosts tagged with "optimize for static" set this to 1 to disable delta prediction and enable not sending data for unchanged chunks
                    int targetBaselines = GhostTypeCollection[ghostType].StaticOptimization ? 1 : 3;

                    int snapshotSize = GhostTypeCollection[ghostType].SnapshotSize;
                    int connectionId = networkIdFromEntity[connectionEntity].Value;

                    if (GhostTypeCollection[ghostType].NumBuffers > 0)
                    {
                        //Dynamic buffer contents are always stored from the beginning of the dynamic storage buffer (for the specific history slot).
                        //That because each snapshot is only relative to the entities ranges startIndex-endIndex, the outer ranges are invalidate (0-StartIndex and count-Capacity).
                        //This is why we gather the buffer size starting from startIndex position instead of 0 here.

                        //FIXME: this operation is costly (we traverse the whole chunk and child entities too), do that only if something changed. Backup the current size and version in the
                        //chunk state. It is a non trivial check in general, due to the entity children they might be in another chunk)
                        snapshotDynamicDataSize = GatherDynamicBufferSize(chunk, serialChunks[pc].startIndex, ghostType, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength);
                    }
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
                                    bool isRelevant = (relevantGhostForConnection.ContainsKey(key) == setIsRelevant);

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

                        // Find the acked snapshot to delta against, setup pointer to current and previous entity* and data*
                        // Remember to bump writeIndex when done
                        currentSnapshotData =
                            chunkState.GetData(snapshotSize, chunk.Capacity, chunkState.snapshotWriteIndex);
                        currentSnapshotEntity =
                            chunkState.GetEntity(snapshotSize, chunk.Capacity, chunkState.snapshotWriteIndex);
                        currentSnapshotDynamicData = chunkState.GetDynamicDataPtr(chunkState.snapshotWriteIndex, chunk.Capacity, out currentDynamicDataCapacity);
                        //Resize the snapshot dynamic data storage to fit the chunk buffers contents.
                        if (currentSnapshotDynamicData == null || (snapshotDynamicDataSize > currentDynamicDataCapacity))
                        {
                            chunkState.EnsureDynamicDataCapacity(snapshotDynamicDataSize, chunk.Capacity);
                            //Update the chunk state
                            chunkSerializationData[chunk] = chunkState;
                            currentSnapshotDynamicData = chunkState.GetDynamicDataPtr(chunkState.snapshotWriteIndex, chunk.Capacity, out currentDynamicDataCapacity);
                            if(currentSnapshotDynamicData == null)
                                throw new InvalidOperationException("failed to create history snapshot storage for dynamic data buffer");
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
                                    entity = chunkState.GetEntity(snapshotSize, chunk.Capacity, baseline),
                                    dynamicData = chunkState.GetDynamicDataPtr(baseline, chunk.Capacity, out var _),
                                });
                            }

                            baseline = (GhostSystemConstants.SnapshotHistorySize + baseline - 1) %
                                       GhostSystemConstants.SnapshotHistorySize;
                        }
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
                            bool isRelevant = (relevantGhostForConnection.ContainsKey(key) == setIsRelevant);
                            if (!isRelevant || clearHistoryData.ContainsKey(ghost[ent].ghostId))
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
                            tempBaselineData = tempBaselineData,
                            currentSnapshotDynamicData = currentSnapshotDynamicData,
                            currentDynamicDataCapacity = currentDynamicDataCapacity,
                            snapshotDynamicDataSize = snapshotDynamicDataSize,
                            tempBaselineDynamicData = tempDynamicBaselineData
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
                    UnityEngine.Debug.LogError("Could not find ghost type in the collection");
                    return -1;
                }
                return ghostType;
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
                        var chunkGhostType = ghosts[0].ghostType;
                        // Pre spawned ghosts might not have a proper ghost type index yet, we calculate it here for pre spawns
                        if (chunkGhostType < 0)
                        {
                            var ghostEntities = ghostChunks[chunk].GetNativeArray(entityType);
                            chunkGhostType = FindGhostTypeIndex(ghostEntities[0]);
                            if (chunkGhostType < 0)
                                continue;
                        }
                        if (chunkGhostType >= GhostTypeCollection.Length)
                            continue;
                        chunkState.lastUpdate = currentTick - 1;
                        chunkState.startIndex = 0;
                        chunkState.ghostType = chunkGhostType;
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
                    if (!existingChunks.ContainsKey(oldChunks[i]))
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

                int dynamicDataHeaderSize = SerializationState.GetDynamicDataHeaderSize(data.chunk.Capacity);
                int dynamicSnapshotDataOffset = dynamicDataHeaderSize;
                int dynamicSnapshotDataCapacity = data.currentDynamicDataCapacity;
                byte* snapshotDynamicDataPtr = data.currentSnapshotDynamicData;
                //This condition is possible when we spawn new entities and we send the chunck the first time
                if (typeData.NumBuffers > 0 && data.currentSnapshotDynamicData == null && data.snapshotDynamicDataSize > 0)
                {
                    snapshotDynamicDataPtr = (byte*)UnsafeUtility.Malloc(data.snapshotDynamicDataSize + dynamicDataHeaderSize, 16, Allocator.Temp);
                    dynamicSnapshotDataCapacity = data.snapshotDynamicDataSize;
                }

                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
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
                        if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            var compSize = GhostComponentCollection[serializerIdx].ComponentSize;
                            var compData = (byte*)data.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                            compData += data.startIndex * compSize;
                            GhostComponentCollection[serializerIdx].CopyToSnapshot.Ptr.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState), (IntPtr)snapshot, snapshotOffset, snapshotSize, (IntPtr)compData, compSize, data.chunk.Count - data.startIndex);
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                        }
                        else
                        {
                            var compSize = GhostComponentCollection[serializerIdx].ComponentSize;
                            var dynamicDataSize = GhostComponentCollection[serializerIdx].SnapshotSize;
                            var bufData = data.chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                            for (int i = 0; i < data.chunk.Count - data.startIndex; ++i)
                            {
                                var bufferPointer = (System.IntPtr)bufData.GetUnsafeReadOnlyPtrAndLength(data.startIndex+i, out var len);
                                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[serializerIdx].ChangeMaskBits, len);
                                //Set the elements count and the buffer content offset inside the dynamic data history buffer
                                *(uint*)(snapshot + snapshotOffset + i* snapshotSize) = (uint)len;
                                *(uint*)(snapshot + snapshotOffset + i* snapshotSize + sizeof(int)) = (uint)dynamicSnapshotDataOffset;
    #if ENABLE_UNITY_COLLECTIONS_CHECKS
                                if((dynamicSnapshotDataOffset + maskSize + len*dynamicDataSize) > (dynamicSnapshotDataCapacity + dynamicDataHeaderSize))
                                    throw new InvalidOperationException("writing snapshot dyanmicdata outside of memory history buffer memory boundary");
    #endif
                                //Copy the buffer contents
                                GhostComponentCollection[serializerIdx].CopyToSnapshot.Ptr.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState),(IntPtr)snapshotDynamicDataPtr + maskSize,
                                    dynamicSnapshotDataOffset,dynamicDataSize,bufferPointer, compSize, len);
                                dynamicSnapshotDataOffset += GhostCollectionSystem.SnapshotSizeAligned(maskSize + dynamicDataSize * len);
                            }
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                        }
                    }
                    else
                    {
                        //Also clear the snapshot data for that entity range, since chunk does not contains this component anymore
                        var componentSnapshotSize = !GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                            ? GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize)
                            : GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                        var snapshotIndexOffset = 0;
                        for (ent = data.startIndex; ent < data.endIndex; ++ent)
                        {
                            UnsafeUtility.MemClear(snapshot + snapshotIndexOffset + snapshotOffset, componentSnapshotSize);
                            snapshotIndexOffset += snapshotSize;
                        }
                        snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(componentSnapshotSize);
                    }
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = data.chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new InvalidOperationException("Component index out of range");
#endif
                        var compSize = GhostComponentCollection[serializerIdx].ComponentSize;
                        if(!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            for (ent = data.startIndex; ent < data.endIndex; ++ent)
                            {
                                var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                var snapshotIndexOffset = (ent - data.startIndex) * snapshotSize;
                                //We can skip here, becase the memory buffer offset is computed using the start-end entity indices
                                if (!childEntityLookup.TryGetValue(childEnt, out var childChunk) || !childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                                {
                                    var componentSnapshotSize = GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                                    UnsafeUtility.MemClear(snapshot + snapshotIndexOffset + snapshotOffset, componentSnapshotSize);
                                    continue;
                                }
                                var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                compData += childChunk.index * compSize;
                                GhostComponentCollection[serializerIdx].CopyToSnapshot.Ptr.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState), (IntPtr)(snapshot + snapshotIndexOffset), snapshotOffset, snapshotSize, (IntPtr)compData, compSize, 1);
                            }
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                        }
                        else
                        {
                            for (ent = data.startIndex; ent < data.endIndex; ++ent)
                            {
                                var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                var snapshotIndexOffset = (ent - data.startIndex) * snapshotSize;
                                //We can skip here, becase the memory buffer offset is computed using the start-end entity indices
                                if (!childEntityLookup.TryGetValue(childEnt, out var childChunk) || !childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                                {
                                    //use direct assignment instead of memcpy (should be faster)
                                    ((ulong*)(snapshot + snapshotIndexOffset + snapshotOffset))[0] = 0;
                                    continue;
                                }
                                var dynamicDataSize = GhostComponentCollection[serializerIdx].SnapshotSize;
                                var bufData = childChunk.chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                var bufferPointer = (System.IntPtr)bufData.GetUnsafeReadOnlyPtrAndLength(childChunk.index, out var len);
                                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[serializerIdx].ChangeMaskBits, len);
                                //Set the elements count and the buffer content offset inside the dynamic data history buffer
                                *(uint*)(snapshot + snapshotOffset + snapshotIndexOffset) = (uint)len;
                                *(uint*)(snapshot + snapshotOffset + snapshotIndexOffset + sizeof(int)) = (uint)dynamicSnapshotDataOffset;
        #if ENABLE_UNITY_COLLECTIONS_CHECKS
                                if((dynamicSnapshotDataOffset + maskSize + len*dynamicDataSize) > (dynamicSnapshotDataCapacity + dynamicDataHeaderSize))
                                        throw new InvalidOperationException("writing snapshot dyanmicdata outside of memory history buffer memory boundary");
        #endif
                                //Copy the buffer contents
                                GhostComponentCollection[serializerIdx].CopyToSnapshot.Ptr.Invoke((IntPtr) UnsafeUtility.AddressOf(ref serializerState),(IntPtr) snapshotDynamicDataPtr + maskSize,
                                    dynamicSnapshotDataOffset,dynamicDataSize,bufferPointer, compSize, len);
                                dynamicSnapshotDataOffset += GhostCollectionSystem.SnapshotSizeAligned(maskSize + dynamicDataSize * len);
                            }
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                        }
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
                    if (data.ghostStates != null && data.ghosts[ent].ghostType != data.ghostType && data.ghosts[ent].ghostType >= 0)
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
                        baselineSnapshotData0 = (data.availableBaselines[baseline0].snapshot) + ent*snapshotSize;
                    }

                    byte* baselineSnapshotData1 = null;
                    byte* baselineSnapshotData2 = null;
                    if (baseline2 >= 0)
                    {
                        baselineSnapshotData1 = (data.availableBaselines[baseline1].snapshot) + ent*snapshotSize;
                        baselineSnapshotData2 = (data.availableBaselines[baseline2].snapshot) + ent*snapshotSize;
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
                            int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                            //Buffers does not implement delta prediction for the buffer len
                            if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            {
                                GhostComponentCollection[serializerIdx].PredictDelta.Ptr.Invoke((IntPtr)(baseline+snapshotOffset), (IntPtr)(baselineSnapshotData1+snapshotOffset), (IntPtr)(baselineSnapshotData2+snapshotOffset), ref predictor);
                                snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                            }
                            else
                            {
                                snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                            }
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

                    //Buffers only use the baseline0 for sake of delta compression
                    byte* baselineDynamicData = null;
                    if (baselineSnapshotData0 != null && data.availableBaselines[baseline0].dynamicData != null)
                        baselineDynamicData = data.availableBaselines[baseline0].dynamicData;

                    int maskOffset = 0;
                    snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);

                    GhostComponentSerializer.SendMask serializeMask = GhostComponentSerializer.SendMask.Interpolated | GhostComponentSerializer.SendMask.Predicted;
                    var sendToOwner = SendToOwnerType.All;
                    if (typeData.PredictionOwnerOffset !=0)
                    {
                        var isOwner = (data.NetworkId == *(int*) (snapshot + typeData.PredictionOwnerOffset));
                        sendToOwner = isOwner ? SendToOwnerType.SendToOwner : SendToOwnerType.SendToNonOwner;
                        if (typeData.PartialComponents != 0 && typeData.OwnerPredicted != 0)
                            serializeMask = isOwner ? GhostComponentSerializer.SendMask.Predicted : GhostComponentSerializer.SendMask.Interpolated;
                    }
                    // Clear the change masks since we will not write all of them
                    UnsafeUtility.MemClear(snapshot + 4, changeMaskUints*4);

                    //If the ghost contains buffers, the total serialized dynamic data size is collected while computing the mask.
                    //Server send this information to the client to let them prepare the necessary storage to deserialize the contents
                    int entityRequiredDynamicDataSize = 0;
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        var sendCondition= (serializeMask & GhostComponentIndex[typeData.FirstComponent + comp].SendMask) != 0 &&
                                           (sendToOwner & GhostComponentCollection[serializerIdx].SendToOwner) != 0;

                        if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            // By not setting the is changed mask in this case we make sure the affected fields are not sent
                            if (sendCondition)
                                GhostComponentCollection[serializerIdx].CalculateChangeMask.Ptr.Invoke((IntPtr)(snapshot+snapshotOffset), (IntPtr)(baseline+snapshotOffset), (IntPtr)(snapshot+4), maskOffset);
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                            maskOffset += GhostComponentCollection[serializerIdx].ChangeMaskBits;
                        }
                        else
                        {
                            if (sendCondition)
                            {
                                //For buffers, the snapshot change bit as special meaning: it state if any of the buffer element or the bufffer size has changed.
                                //This is necessary to make the zero-change optmization work.
                                uint bufLen = *(uint*) (snapshot + snapshotOffset);
                                uint offset = *(uint*) (snapshot + snapshotOffset + sizeof(uint));
                                uint baseBufLen = *(uint*) (baseline + snapshotOffset);
                                uint baselineOffset = *(uint*) (baseline + snapshotOffset + sizeof(uint));
                                uint changeMask = 0;
                                //loop through all the elements and calculate the change masks.
                                var dynamicMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts((int)(GhostComponentCollection[serializerIdx].ChangeMaskBits * bufLen));
                                var maskSize = GhostCollectionSystem.SnapshotSizeAligned(dynamicMaskUints*4);
                                var dynamicDataSize = (uint)GhostComponentCollection[serializerIdx].SnapshotSize;
                                if (bufLen == baseBufLen)
                                {
                                    var dynamicMaskOffset = 0;
                                    var dynamicMaskBitsPtr = snapshotDynamicDataPtr + offset;
    #if ENABLE_UNITY_COLLECTIONS_CHECKS
                                    if((offset + maskSize + bufLen*dynamicDataSize) > (dynamicSnapshotDataCapacity + dynamicDataHeaderSize))
                                        throw new InvalidOperationException("reading snapshot dyanmicdata outside of memory history buffer memory boundary");
    #endif
                                    uint anyChange = 0;
                                    UnsafeUtility.MemClear(snapshotDynamicDataPtr + offset, maskSize);
                                    for (int i = 0; i < bufLen; ++i)
                                    {
    #if ENABLE_UNITY_COLLECTIONS_CHECKS
                                        if(dynamicMaskOffset > maskSize*8)
                                            throw new InvalidOperationException("writing dynamic mask bits outside out of bound");
    #endif
                                        GhostComponentCollection[serializerIdx].CalculateChangeMask.Ptr.Invoke(
                                            (IntPtr) (snapshotDynamicDataPtr + maskSize + offset),
                                            (IntPtr) (baselineDynamicData + maskSize + baselineOffset),
                                            (IntPtr) dynamicMaskBitsPtr, dynamicMaskOffset);
                                        offset += dynamicDataSize;
                                        baselineOffset += dynamicDataSize;
                                        dynamicMaskOffset += GhostComponentCollection[serializerIdx].ChangeMaskBits;
                                    }
                                    //Collet any changes in the element masks and set the change bit
                                    for (int i = 0; i < dynamicMaskUints; ++i)
                                        anyChange |= ((uint*) dynamicMaskBitsPtr)[i];
                                    changeMask |= anyChange != 0 ? 1u : 0;
                                }
                                else
                                {
                                    changeMask = 3u;
                                    //Reset the change mask as "all changed" since the length has been modified and we cannot
                                    //rely anymore on the changed bits for sake of delta compressing the masks
                                    UnsafeUtility.MemSet(snapshotDynamicDataPtr + offset, 0xff, maskSize);
                                }

                                GhostComponentSerializer.CopyToChangeMask((IntPtr)snapshot+4, changeMask, maskOffset, GhostSystemConstants.DynamicBufferComponentMaskBits);
                                entityRequiredDynamicDataSize += GhostCollectionSystem.SnapshotSizeAligned((int)(maskSize + bufLen * dynamicDataSize));
                            }
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                            maskOffset += GhostSystemConstants.DynamicBufferComponentMaskBits;
                        }
                    }

                    //write the dynamic data size in the snapshot and sent it delta compressed against the current available baseline
                    if (typeData.NumBuffers != 0)
                    {
                        if(entityRequiredDynamicDataSize > dynamicSnapshotDataCapacity)
                            throw new InvalidOperationException("dynamic data size larger then the buffer capacity");
                        //Can be null if buffer has been removed from the chunk
                        if (snapshotDynamicDataPtr != null)
                        {
                            var dataSize = (uint) entityRequiredDynamicDataSize;
                            //Store the used dynamic size for that entity in the snapshot data. It is used for delta compression
                            ((uint*) snapshotDynamicDataPtr)[ent] = dataSize;
                            if (baselineDynamicData != null)
                            {
                                uint prevDynamicSize = ((uint*) baselineDynamicData)[ent];
                                dataStream.WritePackedUIntDelta(dataSize, prevDynamicSize, compressionModel);
                            }
                            else
                            {
                                dataStream.WritePackedUInt(dataSize, compressionModel);
                            }
                        }
                        else
                        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            UnityEngine.Debug.Assert(entityRequiredDynamicDataSize==0);
                            UnityEngine.Debug.Assert(data.snapshotDynamicDataSize==0);
#endif
                            dataStream.WritePackedUInt(0, compressionModel);
                        }
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
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            GhostComponentCollection[serializerIdx].Serialize.Ptr.Invoke((IntPtr)(snapshot+snapshotOffset), (IntPtr)(baseline+snapshotOffset), ref dataStream, ref compressionModel, (IntPtr)(snapshot+4), maskOffset);
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                            maskOffset += GhostComponentCollection[serializerIdx].ChangeMaskBits;
                        }
                        else
                        {
                            //Delta encode length (offset is not sent)
                            uint bufLen = *(uint*)(snapshot + snapshotOffset);
                            uint dynamicDataOffset = *(uint*) (snapshot + snapshotOffset + sizeof(uint));
                            uint baseBufLen = *(uint*) (baseline + snapshotOffset);
                            uint baselinedynamicDataOffset = *(uint*) (baseline + snapshotOffset + sizeof(uint));
                            var dynamicDataSize = (uint)GhostComponentCollection[serializerIdx].SnapshotSize;
                            var dynamicMaskPtr = snapshotDynamicDataPtr + dynamicDataOffset;
                            uint changeMask = GhostComponentSerializer.CopyFromChangeMask((IntPtr)snapshot+4, maskOffset, GhostSystemConstants.DynamicBufferComponentMaskBits);
                            //Same length but content changed: write the masks
                            if ((changeMask & 0x3) == 1)
                            {
                                var dynamicMaskOffset = 0;
                                var dynamicMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts((int)(GhostComponentCollection[serializerIdx].ChangeMaskBits * bufLen));
                                var maskSize = GhostCollectionSystem.SnapshotSizeAligned(dynamicMaskUints*4);
                                //Write the masks for each elements first
                                for (int mi = 0; mi < dynamicMaskUints; ++mi)
                                {
                                    uint changeMaskUint = ((uint*)(snapshotDynamicDataPtr+dynamicDataOffset))[mi];
                                    uint changeBaseMaskUint = ((uint*) (baselineDynamicData + baselinedynamicDataOffset))[mi];
                                    anyChangeMask |= changeMaskUint;
                                    dataStream.WritePackedUIntDelta(changeMaskUint,changeBaseMaskUint,compressionModel);
                                }
                                //Serialize the elements contents
                                for (int i = 0; i < bufLen; ++i)
                                {
                                    GhostComponentCollection[serializerIdx].Serialize.Ptr.Invoke(
                                        (IntPtr) (snapshotDynamicDataPtr + maskSize + dynamicDataOffset),
                                        (IntPtr) (baselineDynamicData + maskSize + baselinedynamicDataOffset),
                                        ref dataStream,
                                        ref compressionModel,
                                        (IntPtr)dynamicMaskPtr, dynamicMaskOffset);
                                    dynamicDataOffset += dynamicDataSize;
                                    baselinedynamicDataOffset += dynamicDataSize;
                                    dynamicMaskOffset += GhostComponentCollection[serializerIdx].ChangeMaskBits;
                                }
                            }
                            else if ((changeMask & 0x2) != 0)
                            {
                                //If len are different, don't send any change mask for the contents and delta encode using the
                                //default dynamic baseline (all 0).
                                //we can still try to a delta compression using the available baseline. However the risk is that
                                //the content has shifted and so the delta compressed values aren't small anymore
                                dataStream.WritePackedUIntDelta(bufLen, baseBufLen, compressionModel);
                                var dynamicMaskOffset = 0;
                                var dynamicMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts((int)(GhostComponentCollection[serializerIdx].ChangeMaskBits * bufLen));
                                var maskSize = GhostCollectionSystem.SnapshotSizeAligned(dynamicMaskUints*4);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                if((dynamicDataOffset + maskSize + bufLen*dynamicDataSize) > dynamicSnapshotDataCapacity + dynamicDataHeaderSize)
                                    throw new InvalidOperationException("reading snapshot dyanmicdata outside of memory history buffer memory boundary");
#endif
                                for (int i = 0; i < bufLen; ++i)
                                {
                                    GhostComponentCollection[serializerIdx].Serialize.Ptr.Invoke(
                                        (IntPtr) (snapshotDynamicDataPtr + maskSize + dynamicDataOffset),
                                        (IntPtr)data.tempBaselineDynamicData,
                                        ref dataStream,
                                        ref compressionModel,
                                        (IntPtr)dynamicMaskPtr, dynamicMaskOffset);
                                    dynamicDataOffset += dynamicDataSize;
                                    dynamicMaskOffset += GhostComponentCollection[serializerIdx].ChangeMaskBits;
                                }
                            }
                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                            maskOffset += GhostSystemConstants.DynamicBufferComponentMaskBits;
                        }
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
                            SerializationState chunkState;
                            // FIXME: use delta compression
                            var ghostComp = groupChunk.chunk.GetNativeArray(ghostComponentType);
                            var childGhostType = ghostComp[groupChunk.index].ghostType;
                            if (childGhostType < 0)
                            {
                                if (chunkSerializationData.TryGetValue(groupChunk.chunk, out chunkState))
                                    childGhostType = chunkState.ghostType;
                                else
                                {
                                    childGhostType = FindGhostTypeIndex(ghostGroup[groupChunk.index].Value);
                                    if (childGhostType < 0)
                                        throw new InvalidOperationException("Could not find ghost type for group member.");
                                }
                            }
                            #if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if (GhostTypeCollection[childGhostType].IsGhostGroup != 0)
                            {
                                throw new InvalidOperationException("Nested ghost groups are not supported, non-root members of a group cannot be roots for their own groups.");
                            }
                            #endif
                            dataStream.WritePackedUInt((uint)childGhostType, compressionModel);
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
                            byte* currentDynamicSnapshotData = null;
                            int currentDynamicSnapshotCapacity = 0;
                            int snapshotDynamicDataSize = 0;
                            var availableBaselines = new NativeList<SnapshotBaseline>(3, Allocator.Temp);
                            if (GhostTypeCollection[ghostComp[groupChunk.index].ghostType].NumBuffers > 0)
                            {
                                snapshotDynamicDataSize = GatherDynamicBufferSize(groupChunk.chunk, groupChunk.index, groupChunk.index + 1,
                                    ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength);
                            }
                            if(chunkSerializationData.TryGetValue(groupChunk.chunk, out chunkState))
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
                                }
                                else
                                {
                                    // already bumped, so use previous value
                                    writeIndex = baselineIndex;
                                    baselineIndex = (GhostSystemConstants.SnapshotHistorySize + writeIndex - 1) %
                                            GhostSystemConstants.SnapshotHistorySize;
                                    clearEntityArray = false;
                                }

                                //Resize the snapshot dynamic data storage to fit the chunk buffers contents.
                                if (currentDynamicSnapshotData == null || snapshotDynamicDataSize > currentDynamicSnapshotCapacity)
                                {
                                    chunkState.EnsureDynamicDataCapacity(snapshotDynamicDataSize, groupChunk.chunk.Capacity);
                                    currentDynamicSnapshotData = chunkState.GetDynamicDataPtr(chunkState.snapshotWriteIndex,
                                        groupChunk.chunk.Capacity, out currentDynamicSnapshotCapacity);
                                    if (currentDynamicSnapshotData == null)
                                        throw new InvalidOperationException("failed to create history snapshot storage for dynamic data buffer");
                                }
                                chunkSerializationData[groupChunk.chunk] = chunkState;

                                // Find the acked snapshot to delta against, setup pointer to current and previous entity* and data*
                                currentSnapshotData =
                                    chunkState.GetData(dataSize, groupChunk.chunk.Capacity, writeIndex);
                                currentSnapshotEntity =
                                    chunkState.GetEntity(dataSize, groupChunk.chunk.Capacity, writeIndex);
                                currentSnapshotFlags =
                                    chunkState.GetFlags(groupChunk.chunk.Capacity);
                                currentDynamicSnapshotData =
                                    chunkState.GetDynamicDataPtr(writeIndex, groupChunk.chunk.Capacity, out currentDynamicSnapshotCapacity);
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
                                                dynamicData = chunkState.GetDynamicDataPtr(baselineIndex, groupChunk.chunk.Capacity, out var _),
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

                            GhostSystemStateComponent* ghostState = null;
                            if (groupChunk.chunk.Has(ghostSystemStateType))
                                ghostState = (GhostSystemStateComponent*) groupChunk.chunk.GetNativeArray(ghostSystemStateType).GetUnsafeReadOnlyPtr();

                            var groupData = new SerializeData
                            {
                                ghostType = childGhostType,
                                chunk = groupChunk.chunk,
                                startIndex = groupChunk.index,
                                endIndex = groupChunk.index+1,
                                currentTick = data.currentTick,
                                currentSnapshotEntity = currentSnapshotEntity,
                                currentSnapshotData = currentSnapshotData,
                                currentSnapshotFlags = currentSnapshotFlags,
                                ghosts = (GhostComponent*)ghostComp.GetUnsafeReadOnlyPtr(),
                                ghostStates = ghostState,
                                ghostEntities = groupChunk.chunk.GetNativeArray(entityType),
                                baselinePerEntity = baselinePerEntity,
                                availableBaselines = availableBaselines,
                                compressionModel = data.compressionModel,
                                serializerState = data.serializerState,
                                tempBaselineData = data.tempBaselineData,
                                currentSnapshotDynamicData = currentDynamicSnapshotData,
                                currentDynamicDataCapacity = currentDynamicSnapshotCapacity,
                                snapshotDynamicDataSize = snapshotDynamicDataSize,
                                tempBaselineDynamicData = data.tempBaselineDynamicData
                            };
                            if (SerializeChunk(ref dataStream, groupData, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength, out var _, out var _) != groupChunk.index+1)
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

            //Cycle over all the components for the given entity range in the chunk and compute the capacity
            //to store all the dynamic buffer contents (if any)
            unsafe int GatherDynamicBufferSize(in ArchetypeChunk chunk, int startIndex, int ghostType, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                var typeData = GhostTypeCollection[ghostType];
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                int requiredSize = 0;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        continue;

                    if (!chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                        continue;

                    for (int ent = startIndex; ent < chunk.Count; ++ent)
                    {
                        var bufferAccessor = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                        var bufferLen = bufferAccessor.GetBufferLength(ent);
                        var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[serializerIdx].ChangeMaskBits, bufferLen);
                        requiredSize += GhostCollectionSystem.SnapshotSizeAligned(maskSize + bufferLen * GhostComponentCollection[serializerIdx].SnapshotSize);
                    }
                }

                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new InvalidOperationException("Component index out of range");
#endif
                        if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            continue;

                        for (int ent = startIndex; ent < chunk.Count; ++ent)
                        {
                            var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                            var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                            //We can skip here, becase the memory buffer offset is computed using the start-end entity indices
                            if (!childEntityLookup.TryGetValue(childEnt, out var childChunk) || !childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx])) {
                                continue;
                            }
                            var bufferAccessor = childChunk.chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                            var bufferLen = bufferAccessor.GetBufferLength(childChunk.index);
                            var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[serializerIdx].ChangeMaskBits, bufferLen);
                            requiredSize += GhostCollectionSystem.SnapshotSizeAligned(maskSize + bufferLen * GhostComponentCollection[serializerIdx].SnapshotSize);
                        }
                    }
                }
                return requiredSize;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void UpdateNetStats(out int netStatSize, out int netStatStride)
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

        protected override void OnUpdate()
        {
            // If the ghost collection has not been initialized yet the send ystem can not process any ghosts
            if (!GetSingleton<GhostCollection>().IsInGame)
                return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UpdateNetStats(out var netStatSize, out var netStatStride);
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

            // Make sure the list of connections and connection state is up to date
            var connections = connectionGroup.ToEntityArrayAsync(Allocator.TempJob, out var connectionHandle);
            var connectionStates = m_ConnectionStates;
            var connectionStateLookup = m_ConnectionStateLookup;
            var networkIdFromEntity = GetComponentDataFromEntity<NetworkIdComponent>(true);
            var ackFromEntity = GetComponentDataFromEntity<NetworkSnapshotAckComponent>(true);
            var connectionRelevantCount = m_ConnectionRelevantCount;
            var relevancySet = m_GhostRelevancySet;
            bool relevancyEnabled = (GhostRelevancyMode != GhostRelevancyMode.Disabled);
            // Find the latest tick which has been acknowledged by all clients and cleanup all ghosts destroyed before that
            uint currentTick = m_ServerSimulation.ServerTick;
            // Find the latest tick received by all connections
            var ackedByAll = m_AckedByAllTick;
            ackedByAll[0] = currentTick;
            var connectionsToProcess = m_ConnectionsToProcess;
            connectionsToProcess.Clear();
            Dependency = Job
                .WithDisposeOnCompletion(connections)
                .WithReadOnly(networkIdFromEntity)
                .WithReadOnly(ackFromEntity)
                .WithCode(() => {
                var existing = new NativeHashMap<Entity, int>(connections.Length, Allocator.Temp);
                int maxConnectionId = 0;
                for (int i = 0; i < connections.Length; ++i)
                {
                    existing.TryAdd(connections[i], 1);
                    int stateIndex;
                    if (!connectionStateLookup.TryGetValue(connections[i], out stateIndex))
                    {
                        connectionStates.Add(new ConnectionStateData
                        {
                            Entity = connections[i],
                            SerializationState =
                                new UnsafeHashMap<ArchetypeChunk, SerializationState>(1024, Allocator.Persistent),
                            ClearHistory = new UnsafeHashMap<int, uint>(256, Allocator.Persistent)
                        });
                        connectionStateLookup.TryAdd(connections[i], connectionStates.Length - 1);
                    }
                    maxConnectionId = math.max(maxConnectionId, networkIdFromEntity[connections[i]].Value);

                    uint ackedByAllTick = ackedByAll[0];
                    var ack = ackFromEntity[connections[i]];
                    var snapshot = ack.LastReceivedSnapshotByRemote;
                    if (snapshot == 0)
                        ackedByAllTick = 0;
                    else if (ackedByAllTick != 0 && SequenceHelpers.IsNewer(ackedByAllTick, snapshot))
                        ackedByAllTick = snapshot;
                    ackedByAll[0] = ackedByAllTick;
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

                connectionRelevantCount.ResizeUninitialized(maxConnectionId+1);
                for (int i = 0; i < connectionRelevantCount.Length; ++i)
                    connectionRelevantCount[i] = 0;

                // go through all keys in the relevancy set, +1 to the connection idx array
                if (relevancyEnabled)
                {
                    var values = relevancySet.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < values.Length; ++i)
                    {
                        var cid = values[i].Connection;
                        connectionRelevantCount[cid] = connectionRelevantCount[cid] + 1;
                    }
                }
                var sendPerFrame = (connectionStates.Length + netTickInterval - 1) / netTickInterval;
                var sendStartPos = sendPerFrame * (int) (currentTick % netTickInterval);

                if (sendStartPos + sendPerFrame > connectionStates.Length)
                    sendPerFrame = connectionStates.Length - sendStartPos;
                for (int i = 0; i < sendPerFrame; ++i)
                    connectionsToProcess.Add(connectionStates[sendStartPos + i]);
            }).Schedule(JobHandle.CombineDependencies(Dependency, connectionHandle));

            // Prepare a command buffer
            EntityCommandBuffer commandBuffer = m_Barrier.CreateCommandBuffer();
            var commandBufferConcurrent = commandBuffer.AsParallelWriter();

            // Find the highest presspawn ghost id if any
            int highestPrespawnId = 0;
            if (HasSingleton<HighestPrespawnIDAllocated>())
                highestPrespawnId = GetSingleton<HighestPrespawnIDAllocated>().GhostId;

            // Setup the tick at which ghosts were despawned, cleanup ghosts which have been despawned and acked by al connections
            var freeGhostIds = m_FreeGhostIds.AsParallelWriter();
            var prespawnDespawn = m_DestroyedPrespawnsQueue.AsParallelWriter();
            var freeSpawendGhosts = m_FreeSpawnedGhostQueue.AsParallelWriter();
            var ghostMap = m_GhostMap;
            Entities
                .WithReadOnly(ackedByAll)
                .WithReadOnly(ghostMap)
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
                //Remove the ghost from the mapping as soon as possible, regardless of clients acknowledge
                var spawnedGhost = new SpawnedGhost {ghostId = ghost.ghostId, spawnTick = ghost.spawnTick};
                if(ghostMap.ContainsKey(spawnedGhost))
                    freeSpawendGhosts.Enqueue(spawnedGhost);

                    if (ghost.ghostId <= highestPrespawnId)
                        prespawnDespawn.Enqueue(ghost.ghostId);
                }).ScheduleParallel();

            // Copy destroyed entities in the parallel write queue populated by ghost cleanup to a single list
            // and free despawned ghosts from map
            var despawnQueue = m_DestroyedPrespawnsQueue;
            var despawnList = m_DestroyedPrespawns;
            var freeSpawnQueue = m_FreeSpawnedGhostQueue;
            var despawnListJobHandle = Job.WithCode(() => {
                if (despawnQueue.TryDequeue(out int destroyed))
                {
                    if (!despawnList.Contains(destroyed))
                        despawnList.Add(destroyed);
                }
                while (freeSpawnQueue.TryDequeue(out var spawnedGhost))
                    ghostMap.Remove(spawnedGhost);
            }).Schedule(Dependency);
            LastGhostMapWriter = despawnListJobHandle;
            Dependency = JobHandle.CombineDependencies(Dependency, despawnListJobHandle);

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
            var ghostComponentType = GetComponentTypeHandle<GhostComponent>();
            var ghostChildEntityComponentType = GetComponentTypeHandle<GhostChildEntityComponent>(true);
            var ghostGroupType = GetBufferTypeHandle<GhostGroup>(true);
            var ghostOwnerComponentType = GetComponentTypeHandle<GhostOwnerComponent>(true);

            // Extract all newly spawned ghosts and set their ghost ids
            m_SerialSpawnChunks.Clear();
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
                serialSpawnChunks = m_SerialSpawnChunks,
                entityType = entityType,
                ghostComponentType = ghostComponentType,
                ghostChildEntityComponentType = ghostChildEntityComponentType,
                freeGhostIds = m_FreeGhostIds,
                allocatedGhostIds = m_AllocatedGhostIds,
                commandBuffer = commandBuffer,
                ghostMap = ghostMap,
                ghostTypeFromEntity = ghostTypeFromEntity,
                serverTick = currentTick,
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

            // If there are any connections to send data to, serilize the data fo them in parallel
            var serializeJob = new SerializeJob
            {
                GhostCollectionSingleton = ghostCollectionSingleton,
                GhostComponentCollectionFromEntity = GetBufferFromEntity<GhostComponentSerializer.State>(true),
                GhostTypeCollectionFromEntity = ghostTypeCollectionFromEntity,
                GhostComponentIndexFromEntity = GetBufferFromEntity<GhostCollectionComponentIndex>(true),
                GhostCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefab>(true),

                driver = m_ReceiveSystem.ConcurrentDriver,
                unreliablePipeline = m_ReceiveSystem.UnreliablePipeline,
                unreliableFragmentedPipeline = m_ReceiveSystem.UnreliableFragmentedPipeline,
                despawnChunks = despawnChunks,
                ghostChunks = ghostChunks,
                connectionState = m_ConnectionsToProcess.AsDeferredJobArray(),
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

                prespawnDespawns = m_DestroyedPrespawns,
                childEntityLookup = m_ChildEntityLookup,
                linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(true),
                CurrentSystemVersion = GlobalSystemVersion
            };

            Dependency = JobHandle.CombineDependencies(Dependency, m_ReceiveSystem.LastDriverWriter, GhostRelevancySetWriteHandle);
            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(ghostCollectionSingleton);
            var listLength = ghostComponentCollection.Length;
            if (listLength <= 32)
            {
                var dynamicListJob = new SerializeJob32 {Job = serializeJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.Schedule(m_ConnectionsToProcess, 1, Dependency);
            }
            else if (listLength <= 64)
            {
                var dynamicListJob = new SerializeJob64 {Job = serializeJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.Schedule(m_ConnectionsToProcess, 1, Dependency);
            }
            else if (listLength <= 128)
            {
                var dynamicListJob = new SerializeJob128 {Job = serializeJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.Schedule(m_ConnectionsToProcess, 1, Dependency);
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
            // Only the spawn job is using the commandBuffer, but the serialize job is using the same chunks - so we must wait for that too before we can modify them
            m_Barrier.AddJobHandleForProducer(Dependency);
        }

        struct PrioChunk : IComparable<PrioChunk>
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

        //Add spawned ghosts to the mapping
        internal void AddSpawnedGhosts(NativeArray<SpawnedGhostMapping> spawnedGhosts)
        {
            foreach (var g in spawnedGhosts)
            {
                if (!m_GhostMap.TryAdd(g.ghost, g.entity))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.LogError($"GhostID {g.ghost.ghostId} already present in the ghost entity map");
#endif
                    m_GhostMap[g.ghost] = g.entity;
                }
            }
        }
    }
}

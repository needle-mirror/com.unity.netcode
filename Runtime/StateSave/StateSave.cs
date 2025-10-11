using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.NetCode.LowLevel.StateSave;
using Unity.Profiling;
using UnityEngine;

[assembly: RegisterGenericJobType(typeof(StateSaveJob<DirectStateSaveStrategy>))]
[assembly: RegisterGenericJobType(typeof(StateSaveJob<IndexedByGhostSaveStrategy>))]

namespace Unity.NetCode.LowLevel.StateSave
{
    // if we want to handle non ghosts, we shouldn't tie our indexing to SpawnedGhost. Using a temporary type for this for now
    internal struct SavedEntityID : IEquatable<SavedEntityID>
    {
        public SpawnedGhost value;

        public SavedEntityID(in GhostInstance ghostInstance)
        {
            value = new SpawnedGhost(ghostInstance);
        }
        // TODO should have a way to have a few preceding bits to identify custom IDs vs ghost IDs
        // public SavedEntityID(int customID)
        // {
        //     value = new SpawnedGhost();
        //     value.ghostId = customID; // TODO this is a hack. Should just store int? if SavedEntityID doesn't mean a ghost anymore, this is misleading...
        // }

        public bool Equals(SavedEntityID other)
        {
            return value.Equals(other.value);
        }

        public override bool Equals(object obj)
        {
            return obj is SavedEntityID other && Equals(other);
        }

        public override int GetHashCode()
        {
            return value.GetHashCode();
        }

        public override string ToString()
        {
            var worldToUse = ClientServerBootstrap.ClientWorld ?? ClientServerBootstrap.ServerWorld;
            string name = "";
            if (worldToUse != null)
            {
                using var singletonQuery = worldToUse.EntityManager.CreateEntityQuery(typeof(SpawnedGhostEntityMap));
                if (singletonQuery.HasSingleton<SpawnedGhostEntityMap>())
                {
                    var map = singletonQuery.GetSingleton<SpawnedGhostEntityMap>();
                    if (map.Value.ContainsKey(value))
                    {
                        var clientEntity = map.Value[value];
                        name = worldToUse.EntityManager.GetName(clientEntity);
                    }
                }
            }

            return $"Ghost:{name}:{value.ghostId}:spawnTick:{value.spawnTick}";
        }
    }

    /// <summary>
    /// Buffer data offset and length data for buffer types used inside the entity component space in the container save.
    /// </summary>
    internal struct BufferHandle
    {
        /// <summary>
        /// Length of the buffer or the buffer element count.
        /// </summary>
        public int Length;

        /// <summary>
        /// Offset to the whole buffer stored behind the component data, size will be the entity count * buffer length
        /// </summary>
        public int Offset;
    }

    /// <summary>
    /// Some basic extension methods for configuring the state save. Could potentially add new extension methods in other assemblies to create more state save filters. e.g. WithAllGhosts could go through all ghost types and gather all ghost components
    /// </summary>
    internal static class WorldStateSaveExtensions
    {
        public static WorldStateSave WithRequiredTypes(this WorldStateSave self, in NativeHashSet<ComponentType> requiredTypesToSave)
        {
            foreach (var componentType in requiredTypesToSave)
            {
                self.RequiredTypesToSaveConfig.Add(componentType);
            }
            return self;
        }

        public static WorldStateSave WithOptionalTypes(this WorldStateSave self, in NativeHashSet<ComponentType> optionalTypesToSave)
        {

            foreach (var componentType in optionalTypesToSave)
            {
                self.OptionalTypesToSaveConfig.Add(componentType);
            }
            return self;
        }
    }

    // Container in charge of tracking a world's state save given a set of component types to save
    // design note: this should eventually be created on the main thread too and composed of non entities. I could save some random struct in there too.
    [DebuggerDisplay("Entity Count = {m_EntityCount}, allocation size = {m_AllocationSize} B")]
    internal unsafe struct WorldStateSave : IDisposable, IEnumerable<WorldStateSave.StateSaveEntry>
    {
        internal struct WorldSaveParallelWriter
        {
            public NativeParallelHashMap<SavedEntityID, (StateSaveContainer stateSave, IntPtr entityPtr)>.ParallelWriter entityIndexWriter;
            public NativeArray<StateSaveContainer> m_AllStateSaveContainers;

            static readonly  ProfilerMarker s_Marker = new ProfilerMarker("RegisterNewGhost");
            public void RegisterNewEntity(in SavedEntityID entity, in StateSaveContainer containerSave, int entIndex)
            {
                // TODO do this from main thread? and avoid parallel hash map?
                using var a = s_Marker.Auto();
                var objAdrSpan = containerSave.GetObjectAdrInSave(entIndex);
                byte* objAdr = (byte*)UnsafeUtility.AddressOf(ref objAdrSpan[0]);
                entityIndexWriter.TryAdd(entity, (containerSave, new IntPtr(objAdr)));
            }
        }

        StateSaveHeader* GetHeader()
        {
            return (StateSaveHeader*)m_BaseStateSaveAddress;
        }

        internal struct StateSaveHeader
        {
            public int ContainerCount;
        }

        // Temporary tracker used when calculating how much memory all the containers will need
        struct ChunkInformation
        {
            public NativeArray<ComponentType> AllComponentTypes;
            public long OffsetInParentAllocation;
            public int EntityCount;
            public int BufferSizeForComponentTypes;
        }

        public bool Initialized { get; private set; }
        readonly void CheckInitialized() { if (!Initialized) throw new ObjectDisposedException($"{nameof(WorldStateSave)} not initialized, don't forget to call {nameof(Initialize)}"); }

        #region Main Allocation
        // Buffers: these are stored in the entity component data space as the buffer header, containing the offset to the buffer data.
        //          buffer data is then stored behind all the component data (end of component data is the offset) for the current Container/chunk
        // Enableable: types which are enableable will have their enabled status stored behind the component data (an extra byte)
        // Main allocation
        // Structure of the main allocation (B is an enableable component, C is a buffer type):
        // |                                                                       Main allocation                                                                       |
        // |                 |                                     container                                               |       container       |      container      | // containers are ptrs to parts of the main alloc
        // | container count | container header | component types list |          entity data          |    buffer data    |          |            |                     | // the part has a header for the types list, then per entity data, then buffer data at the end
        // |                 |                  | A B C                | A1|   B1E   | C1B |A2|   B2E  | C1-12345    | C2B | AB       | C2-123     | |  | A C  |  | | |  | // types have different sizes. ordered by entity. 'E' is enabled status. 'B' is the buffer handle info
        [NativeDisableUnsafePtrRestriction]
        void* m_BaseStateSaveAddress;
        long m_AllocationSize;

        internal Span<byte> AsSpan
        {
            get
            {
                CheckInitialized();
                return new Span<byte>(m_BaseStateSaveAddress, (int)m_AllocationSize);
            }
        }
        Allocator m_Allocator;
        #endregion

        // Main allocation is divided in sub containers
        NativeArray<StateSaveContainer> m_AllStateSaveContainers;
        // index to access entity data directly without having to iterate through all entities
        // TODO instead of hashmap, we can know the max ghost ID and then just have a native array of size maxCount, with the items the actual offset inside the container.
        // this way no perf heavy hashmap. e.g.: if my max ghost ID is 1000, then I'd have a 1000 long array, each item in the array would be the above tuple, or some pointer to a part of the allocation
        NativeParallelHashMap<SavedEntityID, (StateSaveContainer stateSave, IntPtr entityPtr)> m_EntityIndex;
        bool m_IsEmpty;
        public NativeHashSet<ComponentType> RequiredTypesToSaveConfig;
        public NativeHashSet<ComponentType> OptionalTypesToSaveConfig;
        NativeArray<ComponentType> m_RequiredTypesToSave; // order is important after initialization
        NativeArray<ComponentType> m_OptionalTypesToSave; // order is important after initialization
        [NativeDisableUnsafePtrRestriction] EntityQuery m_ToSaveQuery;
        int m_EntityCount;
        bool m_CreatedFromBuffer;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        AtomicSafetyHandle m_SafetyHandle; // TODO make sure we can't dispose while a job is not finished yet
        // TODO make sure APIs check for safety using this ^^^
#endif

        public int EntityCount
        {
            get
            {
                CheckInitialized();
                return m_EntityCount;
            }
            private set => m_EntityCount = value;
        }

        public int Size => (int)m_AllocationSize;

        static readonly ProfilerMarker s_PerChunkMarker = new("Per Chunk");
        static readonly ProfilerMarker s_MainStateAlloc = new ProfilerMarker("Main State Alloc");
        static readonly ProfilerMarker s_QueryMarker = new("To Arch Chunk Array");
        static readonly ProfilerMarker s_ChunkCalculation = new ProfilerMarker("Pre allocate destination memory");

        // single chunk state save. helps creating state saves on main thread, with no need for job scheduling
        // TODO really needed? for bisecting custom traces inside a system?
        // public WorldStateSave(int allocationSizeBytes, int entityCount, in NativeArray<ComponentType> componentTypes, Allocator allocator)
        // {
        //     m_AllStateSaveContainers = new(1, allocator);
        //     m_AllStateSaveContainers[0] = new StateSaveContainer(componentTypes, 0, entityCount, allocator);
        //     m_GhostIndex = new(10, allocator);
        //     m_IsEmpty = false;
        //     m_RequiredTypesToSave = default;
        //     m_OptionalTypesToSave = default;
        //     m_ToSaveQuery = default;
        //     BaseStateSaveAddress = UnsafeUtility.Malloc(allocationSizeBytes, 16, allocator);
        //     m_AllocationSize = allocationSizeBytes;
        //     m_Allocator = allocator;
        //     Initialized = true;
        // }

        public WorldStateSave(Allocator allocator)
        {
            m_Allocator = allocator;
            m_BaseStateSaveAddress = null;
            m_AllocationSize = 0;
            m_AllStateSaveContainers = default;
            m_EntityIndex = default;
            m_IsEmpty = false;
            RequiredTypesToSaveConfig = new (1, m_Allocator);
            OptionalTypesToSaveConfig = new (1, m_Allocator);
            m_RequiredTypesToSave = default;
            m_OptionalTypesToSave = default;
            m_ToSaveQuery = default;
            m_EntityCount = 0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_SafetyHandle = default;
#endif
            Initialized = false;
            m_CreatedFromBuffer = false;
        }

        /// <summary>
        /// Initialize the WorldStateSave from a buffer created by another state save.
        /// It will contain all the entity data collected by the state save before but not the
        /// required/optional types or any queries internally, only the data.
        /// </summary>
        public WorldStateSave InitializeFromBuffer(NativeArray<byte> stateSave)
        {
            return InitializeFromBuffer(stateSave.GetUnsafePtr(), stateSave.Length);
        }

        /// <inheritdoc cref="InitializeFromBuffer(NativeArray{byte})"/>
        public WorldStateSave InitializeFromBuffer(void* stateSaveAddress, int length)
        {
            m_BaseStateSaveAddress = stateSaveAddress;
            m_AllocationSize = length;
            Initialized = true;
            m_CreatedFromBuffer = true;
            m_AllStateSaveContainers = InitializeContainersFromBuffer(stateSaveAddress);
            return this;
        }

        /// <summary>
        /// Initialize the containers from the given state save buffer, it will recreate the
        /// WorldStateSave.m_AllStateSaveContainers list of containers from the buffer according
        /// to the data layout used. From this it's possible to iterate over all the entity
        /// data inside the all the containers.
        /// </summary>
        NativeArray<StateSaveContainer> InitializeContainersFromBuffer(void* stateSaveAddress)
        {
            var stateSaveHeader = *(StateSaveHeader*)stateSaveAddress;
            var containerCount = stateSaveHeader.ContainerCount;
            var containers = new NativeArray<StateSaveContainer>(containerCount, Allocator.Persistent);
            var containerOffsetInStateSave = sizeof(StateSaveHeader); // Start reading containers after the world state save header
            for (int i = 0; i < containerCount; i++)
            {
                var containerAddress = (byte*)stateSaveAddress + containerOffsetInStateSave;
                var container = new StateSaveContainer(containerAddress);
                containers[i] = container;
                containerOffsetInStateSave += container.ContainerSize;
                m_EntityCount += container.EntityCount;
            }
            return containers;
        }

        /// <inheritdoc cref="Initialize"/>
        /// Default Initialize
        public WorldStateSave Initialize(ref SystemState state)
        {
            return Initialize(ref state, new DirectStateSaveStrategy());
        }

        static readonly ProfilerMarker s_InitializeMarker = new("WorldStateSave.Initialize");
        // this can't be in the constructor, C# doesn't allow generic type parameters in constructor
        /// <summary>
        ///
        /// </summary>
        /// <param name="state"></param>
        /// <param name="requiredTypesToSave"></param>
        /// <param name="optionalTypesToSave">If no required types is passed, uses WithAny filter in the background. Else, truly optional, you could have 0 entity matching these optional types and would still match witht he required types</param>
        /// <param name="stateSaveStrategy"></param>
        /// <param name="allocator"></param>
        /// <typeparam name="TStrategy"></typeparam>
        /// <returns></returns>
        public WorldStateSave Initialize<TStrategy>(ref SystemState state, in TStrategy stateSaveStrategy) where TStrategy : IStateSaveStrategy
        {
            using var a = s_InitializeMarker.Auto();

            if (Initialized)
                throw new InvalidOperationException($"{nameof(WorldStateSave)} already initialized, make sure to call {nameof(Reset)} if you intend to reuse the allocation and not dispose it.");
            if (this.OptionalTypesToSaveConfig.Count == 0 && this.RequiredTypesToSaveConfig.Count == 0)
            {
                throw new ArgumentException($"you need to specify at least one required or optional type to save. Please use {OptionalTypesToSaveConfig} or {nameof(RequiredTypesToSaveConfig)}");
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            this.m_SafetyHandle = AtomicSafetyHandle.Create();
#endif
            stateSaveStrategy.UpdateTypesToTrack(ref this.RequiredTypesToSaveConfig, ref this.OptionalTypesToSaveConfig);

            // Generating entity query for this state save
            if (RequiredTypesToSaveConfig.Count > 0)
                this.m_RequiredTypesToSave = RequiredTypesToSaveConfig.ToNativeArray(m_Allocator);
            else
                this.m_RequiredTypesToSave = new(0, m_Allocator);
            if (OptionalTypesToSaveConfig.Count > 0)
                this.m_OptionalTypesToSave = OptionalTypesToSaveConfig.ToNativeArray(m_Allocator);
            else
                this.m_OptionalTypesToSave = new(0, m_Allocator);

            for (int i = 0; i < m_RequiredTypesToSave.Length; i++)
            {
                if (m_RequiredTypesToSave[i].AccessModeType != ComponentType.AccessMode.ReadOnly)
                {
                    var t = m_RequiredTypesToSave[i];
                    t.AccessModeType = ComponentType.AccessMode.ReadOnly;
                    m_RequiredTypesToSave[i] = t;
                }
            }
            for (int i = 0; i < m_OptionalTypesToSave.Length; i++)
            {
                if (m_OptionalTypesToSave[i].AccessModeType != ComponentType.AccessMode.ReadOnly)
                {
                    var t = m_OptionalTypesToSave[i];
                    t.AccessModeType = ComponentType.AccessMode.ReadOnly;
                    m_OptionalTypesToSave[i] = t;
                }
            }

            var requiredTypesList = new NativeList<ComponentType>(m_RequiredTypesToSave.Length, Allocator.Temp);
            requiredTypesList.AddRange(m_RequiredTypesToSave);
            var optionalTypesList = new NativeList<ComponentType>(m_OptionalTypesToSave.Length, Allocator.Temp);
            optionalTypesList.AddRange(m_OptionalTypesToSave);
            foreach (var optionalType in m_OptionalTypesToSave)
            {
                if (m_RequiredTypesToSave.Contains(optionalType))
                {
                    throw new ArgumentException($"Duplicate type found in both required and optional types sets {optionalType}. Types can only be one of required or optional.");
                }
            }

            // we're not exposing this builder since the order of required + optional needs to be controlled for when we save components. We instead rely on a limited set of filters like required and optional to simplify this
            using var builder = new EntityQueryBuilder(Allocator.Temp);
            // Using WithPresent will include entities which have disabled components, WithAll will completely omit the whole entity if one of the enablable components is disabled
            if (requiredTypesList.Length != 0)
                builder.WithPresent(ref requiredTypesList);
            else
                builder.WithAny(ref optionalTypesList); // we're already iterating over all required types and checking in the IJobChunk if it contains the optional types. However, if we want to track entities with completely different sets of components with no overlap, we can set no required and just iterate WithAny
            m_ToSaveQuery = state.EntityManager.CreateEntityQuery(builder);

            this.m_IsEmpty = m_ToSaveQuery.IsEmpty;
            var chunkCount = m_ToSaveQuery.CalculateChunkCount();
            m_EntityCount =  m_ToSaveQuery.CalculateEntityCount();
            m_AllStateSaveContainers = new (chunkCount, m_Allocator);
            m_EntityIndex = new(m_EntityCount, m_Allocator);

            // Pre calculating and pre allocating destination save state memory
            // It's faster to do persistent allocations from the main thread, so doing all our allocs from here. See thread here https://unity.slack.com/archives/C3H8JSB5E/p1743427468083499
            // It's going to be the case for Unity 6, improvements coming in U7, but need to work around it for now
            s_ChunkCalculation.Begin();
            long totalSizeBytesNeeded = 0;
            long singleEntityRequiredSize = 0;
            // First calculate requiredTypes component size (without buffer data, only buffer header)
            for (int i = 0; i < m_RequiredTypesToSave.Length; i++)
            {
                if (m_RequiredTypesToSave[i].IsBuffer)
                    singleEntityRequiredSize += sizeof(BufferHandle);
                else
                    singleEntityRequiredSize += TypeManager.GetTypeInfo(m_RequiredTypesToSave[i].TypeIndex).SizeInChunk;
                if (m_RequiredTypesToSave[i].IsEnableable)
                    singleEntityRequiredSize++; // save one byte at the end of the component data for enableable status. TODO could save chunk's bitfield instead?
            }

            // this assumes the job will execute right after calculating this with no structural change
            s_QueryMarker.Begin();
            using var chunks = m_ToSaveQuery.ToArchetypeChunkArray(Allocator.Temp);
            s_QueryMarker.End();
            EntityArchetype previousArchetype = default; // the order of each chunk is per archetype, so can fast path if the previous archetype is the same as the current one, just reuse the same sizes
            using var allComponentTypesInContainer = new NativeList<ComponentType>(Allocator.Temp);
            var chunkInformation = new NativeArray<ChunkInformation>(chunkCount, Allocator.Temp);
            var singleEntityOptionalSize = 0;
            s_PerChunkMarker.Begin();
            for (int i = 0; i < chunks.Length; i++)
            {
                ArchetypeChunk chunk = chunks[i];
                var entityCountInChunk = chunk.Count;
                // calculate optional size for current chunk
                if (chunk.Archetype != previousArchetype)
                {
                    previousArchetype = chunk.Archetype;
                    using var chunkTypes = chunk.Archetype.GetComponentTypes(Allocator.Temp);
                    allComponentTypesInContainer.Clear();
                    allComponentTypesInContainer.AddRange(m_RequiredTypesToSave);
                    singleEntityOptionalSize = 0;
                    for (int j = 0; j < chunkTypes.Length; j++)
                    {
                        var t = chunkTypes[j];
                        t.AccessModeType = ComponentType.AccessMode.ReadOnly; // for the Contains() below
                        if (m_OptionalTypesToSave.Contains(t))
                        {
                            if (t.IsBuffer)
                                singleEntityOptionalSize += sizeof(BufferHandle);
                            else
                                singleEntityOptionalSize += TypeManager.GetTypeInfo(t.TypeIndex).SizeInChunk;
                            if (t.IsEnableable)
                                singleEntityOptionalSize++;
                            allComponentTypesInContainer.Add(t);
                        }
                    }
                }

                // Collect buffer types and complete dependency them as we need to read their data
                var bufferTypes = new NativeList<ComponentType>(Allocator.Temp);
                for (int j = 0; j < allComponentTypesInContainer.Length; j++)
                {
                    var type = allComponentTypesInContainer[j];
                    if (!type.IsBuffer) continue;
                    bufferTypes.Add(type);
                }
                var bufferQueryBuilder = new EntityQueryBuilder(Allocator.Temp);
                bufferQueryBuilder.WithAny(ref bufferTypes);
                using var bufferQuery = state.EntityManager.CreateEntityQuery(bufferQueryBuilder);
                bufferQuery.CompleteDependency();

                // Calculate buffer size used by this container, for each type check how many elements are present on each entity
                var bufferSizeForComponentTypes = 0;
                for (int j = 0; j < bufferTypes.Length; j++)
                {
                    var type = bufferTypes[j];
                    var typeHandle = state.EntityManager.GetDynamicComponentTypeHandle(type);
                    var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                    for (int k = 0; k < chunk.Count; k++)
                        bufferSizeForComponentTypes += bufferData.GetBufferLength(k) * TypeManager.GetTypeInfo(type.TypeIndex).ElementSize;
                }

                // Each state save chunk is actually pointing to consecutive parts of the same big memory allocation
                // Record the information we'll need to create a container with the chunk data at an offset in the state save main allocation
                chunkInformation[i] = new ChunkInformation()
                {
                    AllComponentTypes = allComponentTypesInContainer.ToArray(Allocator.Temp),
                    OffsetInParentAllocation = totalSizeBytesNeeded + sizeof(StateSaveHeader),
                    EntityCount = entityCountInChunk,
                    BufferSizeForComponentTypes = bufferSizeForComponentTypes
                };
                // Need to add the container header to the total size calculation, which is the header struct followed by the component type list
                var componentTypeListSize = allComponentTypesInContainer.Length * sizeof(ComponentType);
                var stableTypeHashListSize = allComponentTypesInContainer.Length * sizeof(ulong);
                var containerHeaderSpace = sizeof(StateSaveContainer.ContainerHeader) + componentTypeListSize + stableTypeHashListSize;
                totalSizeBytesNeeded += entityCountInChunk * (singleEntityOptionalSize + singleEntityRequiredSize) + bufferSizeForComponentTypes + containerHeaderSpace;
            }
            s_PerChunkMarker.End();

            // Make room for the container count value at the start of the state save buffer
            totalSizeBytesNeeded += sizeof(StateSaveHeader);

            s_MainStateAlloc.Begin();

            bool reuseAllocation = false;
            if (m_BaseStateSaveAddress != null && m_AllocationSize >= totalSizeBytesNeeded)
            {
                // we have enough memory already allocated
                reuseAllocation = true;
                if (m_AllocationSize > totalSizeBytesNeeded * 2)
                {
                    // we have too much memory allocated, we don't want this to grow forever. Shrinking it.
                    reuseAllocation = false;
                }
            }
            if (!reuseAllocation && m_BaseStateSaveAddress != null)
            {
                // we tried to keep the allocation, but couldn't, so free existing allocation to avoid leak
                UnsafeUtility.Free(m_BaseStateSaveAddress, m_Allocator);
            }

            if (reuseAllocation)
            {
                m_AllocationSize = totalSizeBytesNeeded;
            }
            else
            {
                // main allocation for this state save
                m_BaseStateSaveAddress = UnsafeUtility.Malloc(totalSizeBytesNeeded, 16, m_Allocator);
                m_AllocationSize = totalSizeBytesNeeded;
            }

            // Write down the amount of containers in this state save
            WriteStateSaveHeader();

            s_MainStateAlloc.End();
            for (int i = 0; i < chunkInformation.Length; i++)
            {
                var chunkInfo = chunkInformation[i];
                var containerAddress = (byte*)m_BaseStateSaveAddress + chunkInfo.OffsetInParentAllocation;
                var stateSaveContainer = new StateSaveContainer(containerAddress, chunkInfo.AllComponentTypes, chunkInfo.EntityCount, chunkInfo.BufferSizeForComponentTypes);
                m_AllStateSaveContainers[i] = stateSaveContainer;
                chunkInfo.AllComponentTypes.Dispose();
            }
            s_ChunkCalculation.End();
            chunkInformation.Dispose();
            requiredTypesList.Dispose();
            optionalTypesList.Dispose();

            Initialized = true;

            return this;
        }

        void WriteStateSaveHeader()
        {
            GetHeader()->ContainerCount = m_AllStateSaveContainers.Length;
        }

        // disposes internal metadata but keeps the main allocation for future use
        public void Reset()
        {
            // Keeps the main allocation there, but resets everything else
            foreach (var oneContainer in m_AllStateSaveContainers)
            {
                oneContainer.Dispose();
            }
            m_AllStateSaveContainers.Dispose();
            m_RequiredTypesToSave.Dispose();
            m_OptionalTypesToSave.Dispose();
            if (m_EntityIndex.IsCreated)
                m_EntityIndex.Dispose();
            if (!m_CreatedFromBuffer && Initialized)
                m_ToSaveQuery.Dispose();
            Initialized = false;
        }

        public void Dispose()
        {
            if (!m_CreatedFromBuffer && Initialized)
            {
                UnsafeUtility.Free(m_BaseStateSaveAddress, m_Allocator);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.Release(m_SafetyHandle);
#endif
            }
            m_BaseStateSaveAddress = null;
            if (RequiredTypesToSaveConfig.IsCreated)
                RequiredTypesToSaveConfig.Dispose();
            if (OptionalTypesToSaveConfig.IsCreated)
                OptionalTypesToSaveConfig.Dispose();
            Reset();
        }

        // TODO main thread state save
        // public void RegisterNewGhost(in SavedEntityID savedEntityID, int entIndex)
        // {
        //     Assert.IsTrue(m_AllStateSaveContainers.Length == 1, "Assumes this API is used for single chunk, single thread state saves. Else please use the ParallelWriter");
        //     var containerSave = m_AllStateSaveContainers[0];
        //     var objAdrSpan = containerSave.GetObjectAdrInSave(entIndex);
        //     fixed (byte* objAdr = objAdrSpan)
        //     {
        //         var res = m_GhostIndex.TryAdd(savedEntityID, (containerSave, new IntPtr(objAdr)));
        //     }
        // }
        //
        // public void SaveComponentData<T>(int entityIndex, T componentData) where T : struct
        // {
        //     m_AllStateSaveContainers[0].SaveCompForEntityIndex(entityIndex, ComponentType.ReadOnly<T>(), (byte*)UnsafeUtility.AddressOf(ref componentData));
        // }

        internal JobHandle ScheduleStateSaveJob(ref SystemState state)
        {
            return ScheduleStateSaveJob(ref state, new DirectStateSaveStrategy());
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="state"></param>
        /// <param name="stateSaveStrategy">The strategy to use for saving individual components. Can be used to skip certain entities or do extra operations like indexing for example.</param>
        /// <typeparam name="TStrategy"></typeparam>
        /// <returns></returns>
        internal JobHandle ScheduleStateSaveJob<TStrategy>(ref SystemState state, TStrategy stateSaveStrategy) where TStrategy : IStateSaveStrategy
        {
            CheckInitialized();
            if (m_IsEmpty) return state.Dependency;

            var dynamicHandles = new DynamicTypeList();
            using var typesToTrack = new NativeList<ComponentType>(Allocator.Temp);
            typesToTrack.AddRange(m_RequiredTypesToSave);
            typesToTrack.AddRange(m_OptionalTypesToSave);
            DynamicTypeList.PopulateListFromArray(ref state, typesToTrack.AsArray(), readOnly: true, ref dynamicHandles);
            var job = new StateSaveJob<TStrategy>()
            {
                dynamicTypeList = dynamicHandles,
                requiredTypes = m_RequiredTypesToSave,
                optionalTypes = m_OptionalTypesToSave,
                fullWorldStateSave = this.GetParallelWriter(),
                stateSaveStrategy = stateSaveStrategy,
            };
            var dep = job.ScheduleParallelByRef(m_ToSaveQuery, state.Dependency);
            return dep;
        }

        private WorldSaveParallelWriter GetParallelWriter()
        {
            CheckInitialized();
            return new WorldSaveParallelWriter() { m_AllStateSaveContainers = this.m_AllStateSaveContainers, entityIndexWriter = this.m_EntityIndex.AsParallelWriter() };
        }

        public readonly bool TryGetComponentData<T>(SavedEntityID entity, out T componentData) where T : struct
        {
            CheckInitialized();
            var indexEntry = this.m_EntityIndex[entity];
            return indexEntry.stateSave.TryGetSavedDataForPtr<T>((byte*)indexEntry.entityPtr, out componentData);
        }

        public readonly bool TryGetComponentData(SavedEntityID savedEntityID, ComponentType type, out byte* componentData)
        {
            CheckInitialized();
            var indexEntry = this.m_EntityIndex[savedEntityID];
            return indexEntry.stateSave.TryGetSavedDataForPtr((byte*)indexEntry.entityPtr, type, out componentData);
        }

        public bool HasComponent(SavedEntityID entity, ComponentType componentType)
        {
            CheckInitialized();
            var indexEntry = this.m_EntityIndex[entity];
            componentType.AccessModeType = ComponentType.AccessMode.ReadOnly;
            var containerStateSave = indexEntry.stateSave;
            foreach (var type in containerStateSave.ComponentTypesListHeader)
            {
                if (type == componentType) return true;
            }

            return false;
        }

        public NativeArray<SavedEntityID> GetAllEntities(Allocator allocator)
        {
            CheckInitialized();
            return m_EntityIndex.GetKeyArray(allocator);
        }

        public bool Exists(SavedEntityID savedEntityID)
        {
            CheckInitialized();
            return m_EntityIndex.ContainsKey(savedEntityID);
        }

        public readonly NativeArray<ComponentType> GetComponentTypes(SavedEntityID savedEntityID)
        {
            CheckInitialized();
            var containerStateSave = m_EntityIndex[savedEntityID].stateSave;
            byte* typesAdr = (byte*)UnsafeUtility.AddressOf(ref containerStateSave.ComponentTypesListHeader[0]);
            var toReturn = CollectionHelper.ConvertExistingDataToNativeArray<ComponentType>(typesAdr, containerStateSave.ComponentTypesListHeader.Length, Allocator.None); // Allocator.None since this memory isn't allocated and shouldn't be disposed by users.

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref toReturn, m_SafetyHandle); // reusing safety handle so we don't have to create a temp one all the time
#endif
            return toReturn;
        }

        public struct StateSaveEntry : IEnumerable<StateSaveEntry.SavedComponentData>, IEnumerator<StateSaveEntry.SavedComponentData>
        {
            // TODO add a way to get a specific component directly. Like GetComponentData<T>(). Could be useful for getting GhostInstance metadata before restoring a ghost for example
            public byte* entityBaseAdr;
            public byte* containerBaseAdr;
            public NativeArray<ComponentType> types; // points to existing memory in the main allocation. Shouldn't modify the content of this array.

            int m_CurrentIndex;
            int m_CurrentOffset;

            internal static int SizeInSave(ComponentType type)
            {
                int enabledBitSize = 0;
                if (type.IsEnableable)
                    enabledBitSize = 1;
                if (type.IsBuffer)
                    return sizeof(BufferHandle) + enabledBitSize;
                return TypeManager.GetTypeInfo(type.TypeIndex).SizeInChunk + enabledBitSize;
            }

            public struct SavedComponentData
            {
                public byte* ComponentAdr;
                public int Length;
                public ComponentType Type;
                public bool Enabled;

                public void ToConcrete<T>(out T data) where T : struct
                {
                    UnsafeUtility.CopyPtrToStructure(ComponentAdr, out data);
                }

                public void ToConcrete<T>(ref NativeList<T> data) where T : unmanaged, IBufferElementData
                {
                    var elementSize = TypeManager.GetTypeInfo(Type.TypeIndex).ElementSize;
                    for (int i = 0; i < Length; ++i)
                    {
                        UnsafeUtility.CopyPtrToStructure(ComponentAdr + (i*elementSize), out T element);
                        data.Add(element);
                    }
                }
            }

            void InitIterator()
            {
                m_CurrentIndex = -1;
            }

            public void Dispose()
            {

            }

            public StateSaveEntry GetEnumerator()
            {
                InitIterator();
                return this;
            }
            IEnumerator<SavedComponentData> IEnumerable<SavedComponentData>.GetEnumerator()
            {
                InitIterator();
                return this;
            }
            IEnumerator IEnumerable.GetEnumerator()
            {
                InitIterator();
                return this;
            }

            public bool MoveNext()
            {
                m_CurrentIndex++;
                if (m_CurrentIndex < types.Length && m_CurrentIndex > 0)
                {
                    m_CurrentOffset += SizeInSave(types[m_CurrentIndex - 1]); // we need to add the previous type's size to get the current offset
                }
                return m_CurrentIndex < types.Length;
            }
            public void Reset()
            {
                throw new NotImplementedException();
            }

            public SavedComponentData Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    var address = entityBaseAdr + m_CurrentOffset;
                    var type = types[m_CurrentIndex];
                    var length = 0;
                    byte enabledByte = 0;
                    byte* enabledAddress;
                    if (type.IsBuffer)
                    {
                        enabledAddress = address + sizeof(BufferHandle);
                        var bufHeader = (BufferHandle*)address;
                        length = bufHeader->Length;
                        address = containerBaseAdr + bufHeader->Offset;
                    }
                    else
                    {
                        enabledAddress = address + TypeManager.GetTypeInfo(type.TypeIndex).SizeInChunk;
                    }
                    if (type.IsEnableable)
                        UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref enabledByte), enabledAddress, 1);

                    return new SavedComponentData { ComponentAdr = address, Length = length, Type = type, Enabled = enabledByte == 1};
                }
            }

            object IEnumerator.Current => Current;
        }

        public struct StateIterator : IEnumerator<StateSaveEntry>
        {
            int m_CurrentContainerIndex;
            int m_CurrentEntityIndexInContainer;
            NativeArray<StateSaveContainer> m_AllContainers;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            readonly AtomicSafetyHandle m_SafetyHandle; // TODO use this for the main world state save allocation as well?
#endif
            readonly WorldStateSave m_ParentStateSave;

            public StateIterator(WorldStateSave parentStateSave)
            {
                m_ParentStateSave = parentStateSave;
                m_ParentStateSave.CheckInitialized();
                m_AllContainers = parentStateSave.m_AllStateSaveContainers;
                m_CurrentContainerIndex = 0;
                m_CurrentEntityIndexInContainer = -1;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_SafetyHandle = AtomicSafetyHandle.Create();
#endif
            }
            public bool MoveNext()
            {
                m_ParentStateSave.CheckInitialized();
                if (m_AllContainers.Length == 0) return false;
                m_CurrentEntityIndexInContainer++;
                if (m_CurrentEntityIndexInContainer >= CurrentContainer.EntityCount)
                {
                    m_CurrentContainerIndex++;
                    m_CurrentEntityIndexInContainer = 0;
                }

                if (m_CurrentContainerIndex >= m_AllContainers.Length) return false;

                return true;
            }

            StateSaveContainer CurrentContainer => m_AllContainers[m_CurrentContainerIndex];
            public void Reset()
            {
                throw new NotImplementedException(); // from microsoft's doc, sounds like Reset shouldn't be used anywhere, only there for COM interoperability

                // m_CurrentContainerIndex = 0;
                // m_CurrentEntityIndexInContainer = -1;
            }

            public StateSaveEntry Current
            {
                get
                {
                    var currentContainer = CurrentContainer;
                    var componentTypesSpan = currentContainer.ComponentTypesListHeader;
                    var saveContainerSpan = currentContainer.StateSave;
                    NativeArray<ComponentType> typesForCurrentContainer;

                    ComponentType* typesAdr = (ComponentType*)UnsafeUtility.AddressOf(ref componentTypesSpan[0]);
                    typesForCurrentContainer = CollectionHelper.ConvertExistingDataToNativeArray<ComponentType>(typesAdr, componentTypesSpan.Length, Allocator.None); // None allocator, this should no-op

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref typesForCurrentContainer, m_SafetyHandle); // reusing safety handle over all those generated native arrays.
#endif

                    var currentAsSpan = saveContainerSpan.Slice(m_CurrentEntityIndexInContainer * currentContainer.SingleEntitySize, currentContainer.SingleEntitySize);
                    byte* currentAdr = (byte*)UnsafeUtility.AddressOf(ref currentAsSpan[0]);
                    byte* containerAdr = (byte*)UnsafeUtility.AddressOf(ref saveContainerSpan[0]);

                    return new() { entityBaseAdr = currentAdr, containerBaseAdr = containerAdr,types = typesForCurrentContainer };
                }
            }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.Release(m_SafetyHandle);
#endif
            }
        }

        // to make this burst compatible, we need this explicit return type for GetEnumerator
        public StateIterator GetEnumerator()
        {
            return new StateIterator(this);
        }

        IEnumerator<StateSaveEntry> IEnumerable<StateSaveEntry>.GetEnumerator()
        {
            return new StateIterator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new StateIterator(this);
        }
    }

    // maps 1:1 to a chunk while saving. one chunk's content is copied to a container. That container is just a smart pointer for an address in the world state save
    internal unsafe struct StateSaveContainer : IDisposable
    {
        internal struct ContainerHeader
        {
            public byte ComponentTypeCount;
            public byte EntityCount;
            public int TotalBufferSize;
            public int ContainerSize;
        }
        // Data Layout
        // ContainerHeader
        // ListOfComponentTypes
        // ComponentData --> list of entities, each being a list of the same n component data. e.g.[compA for entity 1, compB for entity 1, compC for entity 1, compA for entity 2, compB for entity 2, compC for entity 2]
        // BufferData --> buffer data laid out like the component data
        byte* m_ContainerStateSaveAdr;
        internal int StableTypeHashListSize;
        internal int ComponentTypeListSize;
        internal readonly int HeaderSize => ComponentTypeListSize + StableTypeHashListSize + UnsafeUtility.SizeOf<ContainerHeader>();

        // The stable type hash list contains the same types as the component type list, but is safe to use when the
        // container is coming from a buffer as opposed to being constructed in the same WorldStateSave which created it.
        // This is just used to re-initialize the component types when the container is being restored from a buffer
        readonly Span<ulong> StableTypeHashListHeader => new(m_ContainerStateSaveAdr + sizeof(ContainerHeader), StableTypeHashListSize / UnsafeUtility.SizeOf<ulong>());
        public readonly Span<ComponentType> ComponentTypesListHeader => new(m_ContainerStateSaveAdr + sizeof(ContainerHeader) + StableTypeHashListSize, ComponentTypeListSize / UnsafeUtility.SizeOf<ComponentType>());
        public Span<byte> StateSave => new(m_ContainerStateSaveAdr + HeaderSize, SingleEntitySize * EntityCount + TotalBufferSize);

        readonly byte* GetContainerSaveDataAddress
        {
            get
            {
                CheckInitialized();
                return m_ContainerStateSaveAdr + HeaderSize;
            }
        }
        bool m_Initialized;
        int m_NextBufferOffset;
        internal int SingleEntitySize;

        readonly void CheckInitialized() { if (!m_Initialized) throw new ObjectDisposedException("Container disposed or not initialized."); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ContainerHeader* GetHeader()
        {
            return (ContainerHeader*)m_ContainerStateSaveAdr;
        }

        internal int TotalBufferSize => GetHeader()->TotalBufferSize;
        internal int ContainerSize => GetHeader()->ContainerSize;
        public int EntityCount => GetHeader()->EntityCount;

        static readonly ProfilerMarker s_StateSaveConstructorMarker = new($"{nameof(StateSaveContainer)} Constructor");
        internal StateSaveContainer(byte* containerAddress, in NativeArray<ComponentType> componentTypes, int entityCount, int totalBufferSize)
        {
            using var a = s_StateSaveConstructorMarker.Auto();
            StableTypeHashListSize = componentTypes.Length * sizeof(ulong);
            ComponentTypeListSize = componentTypes.Length * sizeof(ComponentType);
            SingleEntitySize = 0;

            m_ContainerStateSaveAdr = containerAddress;
            m_NextBufferOffset = 0;
            m_Initialized = true;

            if (containerAddress == null)
            {
                UnityEngine.Debug.LogError($"Invalid memory address for {nameof(StateSaveContainer)}");
                return;
            }
            SingleEntitySize = GetSingleEntitySize(componentTypes.AsSpan());
            GetHeader()->ComponentTypeCount = (byte)componentTypes.Length;
            GetHeader()->TotalBufferSize = totalBufferSize;
            GetHeader()->ContainerSize = ComponentTypeListSize + StableTypeHashListSize + sizeof(ContainerHeader) + entityCount * SingleEntitySize + totalBufferSize;
            GetHeader()->EntityCount = (byte)entityCount;
        }

        public StateSaveContainer(byte* containerAddress)
        {
            using var a = s_StateSaveConstructorMarker.Auto();
            StableTypeHashListSize = 0;
            ComponentTypeListSize = 0;
            SingleEntitySize = 0;

            m_ContainerStateSaveAdr = containerAddress;
            m_NextBufferOffset = 0;
            m_Initialized = true;

            ComponentTypeListSize = GetHeader()->ComponentTypeCount * sizeof(ComponentType);
            StableTypeHashListSize = GetHeader()->ComponentTypeCount * sizeof(ulong);

            // Reinitialize the component type values based on the stable type hashes (if buffer came from another
            // process these values have changed)
            for (int i = 0; i < StableTypeHashListHeader.Length; ++i)
            {
                var stableHash = StableTypeHashListHeader[i];
                var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(stableHash);
                var componentType = ComponentType.FromTypeIndex(typeIndex);
                componentType.AccessModeType = ComponentType.AccessMode.ReadOnly;
                ComponentTypesListHeader[i] = componentType;
            }

            SingleEntitySize = GetSingleEntitySize(ComponentTypesListHeader);
        }

        /// <summary>
        /// Calculate the size this entity type will need in the component data portion of the container memory.
        /// </summary>
        int GetSingleEntitySize(Span<ComponentType> componentTypes)
        {
            var singleEntitySize = 0;
            foreach (var type in componentTypes)
            {
                var dotsTypeInfo = TypeManager.GetTypeInfo(type.TypeIndex);
                if (!type.IsBuffer)
                {
                    singleEntitySize += dotsTypeInfo.SizeInChunk;
                }
                else
                {
                    singleEntitySize += sizeof(BufferHandle); // Store length + offset for each buffer type in the entity component data part
                }
                if (type.IsEnableable)
                    singleEntitySize++;
            }
            return singleEntitySize;
        }

        public void Dispose()
        {
            CheckInitialized();
            // container doesn't own the memory associated with it, so we don't touch it here
            m_Initialized = false;
        }

        public void SaveCompForEntityIndex(in int entIndex, in ComponentType componentType, in byte* chunkCompData, bool enableBitIsSet)
        {
            CheckInitialized();
            TryGetOffsetForComponentType(componentType, out var compOffset);
            // TODO cache these offsets and sizes
            var size = TypeManager.GetTypeInfo(componentType.TypeIndex).SizeInChunk;
            var dstAdrSpan = GetObjectAdrInSave(entIndex).Slice(compOffset, size);
            byte* srcAdr = chunkCompData + entIndex * size;

            byte* dstAdr = (byte*)UnsafeUtility.AddressOf(ref dstAdrSpan[0]);
            UnsafeUtility.MemCpy(dstAdr, srcAdr, size);
            if (componentType.IsEnableable)
            {
                // Enabled state is written into the byte behind the component data
                var enableBitAddress = dstAdr + size;
                UnsafeUtility.MemCpy(enableBitAddress, &enableBitIsSet, 1);
            }
        }

        public void SaveBufferForEntityIndex(in WorldStateSave.WorldSaveParallelWriter fullWorldStateSave, in int entIndex, in ComponentType componentType, in byte* chunkCompData, int bufferElementCount, bool enableBitIsSet)
        {
            CheckInitialized();
            TryGetOffsetForComponentType(componentType, out var compOffset);
            var size = TypeManager.GetTypeInfo(componentType.TypeIndex).ElementSize * bufferElementCount;

            var dstAdrSpan = GetObjectAdrInSave(entIndex).Slice(compOffset, sizeof(BufferHandle));
            byte* dstAdr = (byte*)UnsafeUtility.AddressOf(ref dstAdrSpan[0]);

            var bufferHeader = (BufferHandle*)dstAdr;
            if (bufferElementCount == 0)
            {
                bufferHeader->Length = 0;
                bufferHeader->Offset = -1;
                return;
            }

            if (componentType.IsEnableable)
            {
                // Enabled state is written into the byte behind the buffer header data
                var enableBitAddress = dstAdr + sizeof(BufferHandle);
                UnsafeUtility.MemCpy(enableBitAddress, &enableBitIsSet, 1);
            }

            // Start at end of last buffer registered so we continue copying into buffer space for each entity index
            if (m_NextBufferOffset == 0)
                m_NextBufferOffset = EntityCount * SingleEntitySize;  // Initialize to start of buffer space for the first copied buffer
            bufferHeader->Offset = m_NextBufferOffset;
            bufferHeader->Length = bufferElementCount;
            m_NextBufferOffset += size;

            dstAdr = GetContainerSaveDataAddress + bufferHeader->Offset;
            UnsafeUtility.MemCpy(dstAdr, chunkCompData, size);
        }

        internal readonly Span<byte> GetObjectAdrInSave(int entIndex)
        {
            CheckInitialized();
            return new (GetContainerSaveDataAddress + entIndex * SingleEntitySize, SingleEntitySize);
        }

        internal bool TryGetSavedDataForPtr<T>(byte* entityPtr, out T data) where T : struct
        {
            CheckInitialized();
            var found = TryGetSavedDataForPtr(entityPtr, ComponentType.ReadOnly<T>(), out var dataPtr);
            if (found)
                UnsafeUtility.CopyPtrToStructure(dataPtr, out data);
            else
                data = default;
            return found;
        }

        internal bool TryGetSavedDataForPtr(byte* entityPtr, ComponentType type, out byte* data)
        {
            CheckInitialized();
            var found = TryGetOffsetForComponentType(type, out var offset);
            data = entityPtr + offset;
            return found;
        }

        // TODO save this in header too. really not great perf for this
        private bool TryGetOffsetForComponentType(ComponentType type, out int offset)
        {
            CheckInitialized();
            type.AccessModeType = ComponentType.AccessMode.ReadOnly;
            offset = 0;
            foreach (var containedType in this.ComponentTypesListHeader)
            {
                if (containedType == type)
                    return true;

                // Component part will contain the buffer header only (which will contain the offset into the buffer space)
                if (containedType.IsBuffer)
                    offset += sizeof(BufferHandle);
                else
                    offset += TypeManager.GetTypeInfo(containedType.TypeIndex).SizeInChunk;
                if (containedType.IsEnableable)
                    offset++;
            }

            offset = -1;
            return false;
        }

        public void AddComponentType(int index, ComponentType currentCompType)
        {
            CheckInitialized();
            if (index >= ComponentTypesListHeader.Length)
            {
                UnityEngine.Debug.LogError($"Component type index out of bounds while adding component {currentCompType}");
                return;
            }
            ComponentTypesListHeader[index] = currentCompType;
            var stableHash = TypeManager.GetTypeInfo(currentCompType.TypeIndex).StableTypeHash;
            StableTypeHashListHeader[index] = stableHash;
        }
    }

    internal unsafe interface IStateSaveStrategy
    {
        void UpdateTypesToTrack(ref NativeHashSet<ComponentType> requiredTypes, ref NativeHashSet<ComponentType> optionalTypes);
        void SaveEntity(ref StateSaveContainer currentStateSave, ref WorldStateSave.WorldSaveParallelWriter fullWorldStateSave, in ArchetypeChunk chunk, in int unfilteredChunkIndex, in int entIndex, in ComponentType currentCompType, in byte* toCopyPtr, in int bufferElementCount, in int compIndex, bool enableBitIsSet);
    }

    /// <summary>
    /// Directly saves in the memory allocation, with no additional work done
    /// No indexing here. Should spend less time setting hashmap entries and make state saving more performant when indexing isn't needed
    /// </summary>
    internal unsafe struct DirectStateSaveStrategy : IStateSaveStrategy
    {
        static readonly ProfilerMarker s_Marker = new("DefaultStateSaveStrategy.SaveEntity");
        static readonly ProfilerMarker s_MarkerProfiler = new("DefaultStateSaveStrategy.SaveEntity.profilerMarker");
        public void SaveEntity(ref StateSaveContainer currentStateSave, ref WorldStateSave.WorldSaveParallelWriter fullWorldStateSave, in ArchetypeChunk chunk, in int unfilteredChunkIndex, in int entIndex, in ComponentType currentCompType, in byte* toCopyPtr, in int bufferElementCount, in int compIndex, bool enableBitIsSet)
        {
            if (currentCompType.IsBuffer)
                currentStateSave.SaveBufferForEntityIndex(fullWorldStateSave, entIndex, currentCompType, toCopyPtr, bufferElementCount, enableBitIsSet);
            else
                currentStateSave.SaveCompForEntityIndex(entIndex: entIndex, componentType: currentCompType, toCopyPtr, enableBitIsSet);
        }

        public void UpdateTypesToTrack(ref NativeHashSet<ComponentType> requiredTypes, ref NativeHashSet<ComponentType> optionalTypes)
        {

        }
    }

    // TODO move the per ghost indexer hash map here? If registering is optional, shouldn't be handled by WorldStateSave at all?
    internal unsafe struct IndexedByGhostSaveStrategy : IStateSaveStrategy
    {
        [ReadOnly] public ComponentTypeHandle<GhostInstance> ghostInstanceHandle;

        // required constructor, this strategy requires this handle to index by ghost id
        public IndexedByGhostSaveStrategy(in ComponentTypeHandle<GhostInstance> handle)
        {
            ghostInstanceHandle = handle;
        }

        public void SaveEntity(ref StateSaveContainer currentStateSave, ref WorldStateSave.WorldSaveParallelWriter fullWorldStateSave, in ArchetypeChunk chunk, in int unfilteredChunkIndex, in int entIndex, in ComponentType currentCompType, in byte* toCopyPtr, in int bufferElementCount, in int compIndex, bool enableBitIsSet)
        {
            if (currentCompType.IsBuffer)
                currentStateSave.SaveBufferForEntityIndex(fullWorldStateSave, entIndex, currentCompType, toCopyPtr, bufferElementCount, enableBitIsSet);
            else
                currentStateSave.SaveCompForEntityIndex(entIndex: entIndex, componentType: currentCompType, toCopyPtr, enableBitIsSet);
            GhostInstance* ghostInstancePtr = (GhostInstance*)chunk.GetRequiredComponentDataPtrRO(ref ghostInstanceHandle);
            var ghostInstance = ghostInstancePtr[entIndex];
            if (compIndex == 0)
            {
                var ghost = new SavedEntityID(ghostInstance);
                fullWorldStateSave.RegisterNewEntity(ghost, currentStateSave, entIndex);
            }
        }

        public void UpdateTypesToTrack(ref NativeHashSet<ComponentType> requiredTypes, ref NativeHashSet<ComponentType> optionalTypes)
        {
            requiredTypes.Add(ComponentType.ReadOnly<GhostInstance>());
        }
    }

    [BurstCompile]
    internal unsafe struct StateSaveJob<T> : IJobChunk where T : IStateSaveStrategy
    {
        [ReadOnly] public DynamicTypeList dynamicTypeList; // for dependency management. Equivalent to required + optional, in that order
        [ReadOnly] public NativeArray<ComponentType> requiredTypes;
        [ReadOnly] public NativeArray<ComponentType> optionalTypes;

        // destination state save
        public WorldStateSave.WorldSaveParallelWriter fullWorldStateSave; // includes all containers

        [ReadOnly] public T stateSaveStrategy;

        static readonly ProfilerMarker s_StateSaveJobMarker = new ProfilerMarker("StateSaveJob");
        static readonly ProfilerMarker s_StateSaveJobMarker1 = new ProfilerMarker("StateSaveJob1");
        static readonly ProfilerMarker s_StateSaveJobMarker2 = new ProfilerMarker("StateSaveJob2");
        [BurstCompile]
        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            using var a1 = s_StateSaveJobMarker.Auto();
            s_StateSaveJobMarker1.Begin();
            var entityCountInChunk = chunk.Count;

            // determine which component types we should save from this chunk
            using NativeList<int> optionalTypesPresentInChunk = new NativeList<int>(Allocator.Temp);
            using NativeList<ComponentType> allComponentTypesInChunk = new(Allocator.Temp);
            using NativeList<int> indicesInDynamicTypeList = new NativeList<int>(Allocator.Temp);
            allComponentTypesInChunk.AddRange(requiredTypes);
            for (var i = 0; i < requiredTypes.Length; i++)
            {
                indicesInDynamicTypeList.Add(i);
            }

            // find the optional type's index inside the dynamicTypeList
            // TODO cache this ^^^ ?
            for (int i = 0; i < optionalTypes.Length; i++)
            {
                var optionalIndex = i + requiredTypes.Length;
                if (chunk.Has(ref dynamicTypeList.AsSpan()[optionalIndex]))
                {
                    optionalTypesPresentInChunk.Add(i);
                    allComponentTypesInChunk.Add(optionalTypes[i]);
                    indicesInDynamicTypeList.Add(optionalIndex);
                }
            }
            s_StateSaveJobMarker1.End();
            s_StateSaveJobMarker2.Begin();
            var currentStateSaveContainer = fullWorldStateSave.m_AllStateSaveContainers[unfilteredChunkIndex];
            s_StateSaveJobMarker2.End();

            // start copying
            for (int compIndex = 0; compIndex < allComponentTypesInChunk.Length; compIndex++)
            {
                var currentCompType = allComponentTypesInChunk[compIndex];
                currentStateSaveContainer.AddComponentType(compIndex, currentCompType);
                var typeInfo = TypeManager.GetTypeInfo(currentCompType.TypeIndex);
                ref var typeHandle = ref dynamicTypeList.AsSpan()[indicesInDynamicTypeList[compIndex]];
                for (int entIndex = 0; entIndex < entityCountInChunk; entIndex++)
                {
                    var array = chunk.GetEnableableBits(ref typeHandle);
                    var bitArray = new UnsafeBitArray(&array, 2 * sizeof(ulong));
                    var enableBitIsSet = bitArray.IsSet(entIndex);
                    if (currentCompType.IsBuffer)
                    {
                        // Save the whole buffer for current entity
                        var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                        var bufferPtr = bufferData.GetUnsafeReadOnlyPtrAndLength(entIndex, out var length);
                        stateSaveStrategy.SaveEntity(ref currentStateSaveContainer, ref fullWorldStateSave, chunk, unfilteredChunkIndex, entIndex, currentCompType, (byte*)bufferPtr, length, compIndex, enableBitIsSet);
                    }
                    else
                    {
                        var compSize = typeInfo.SizeInChunk;
                        byte* toCopyPtr = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref dynamicTypeList.AsSpan()[indicesInDynamicTypeList[compIndex]], compSize).GetUnsafeReadOnlyPtr();
                        stateSaveStrategy.SaveEntity(ref currentStateSaveContainer, ref fullWorldStateSave, chunk, unfilteredChunkIndex, entIndex, currentCompType,  toCopyPtr, bufferElementCount: 0, compIndex, enableBitIsSet);
                    }
                }
            }
        }
    }
}

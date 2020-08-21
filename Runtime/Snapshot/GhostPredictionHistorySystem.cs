using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Jobs;

namespace Unity.NetCode
{
    // The header of prediction backup state
    // The header is followed by:
    // Entity[Capacity] the entity this history applies to (to prevent errors on structural changes)
    // byte*[Capacity * sizeof(IComponentData)] the raw backup data for all replicated components in this ghost type
    internal unsafe struct PredictionBackupState
    {
        // If ghost type has changed the data must be discarded as the chunk is now used for something else
        public int ghostType;
        public int entityCapacity;

        public static int GetHeaderSize()
        {
            return (UnsafeUtility.SizeOf<PredictionBackupState>() + 15) & (~15);
        }
        public static int GetEntitiesSize(int chunkCapacity, out int singleEntitySize)
        {
            singleEntitySize = UnsafeUtility.SizeOf<Entity>();
            return ((singleEntitySize * chunkCapacity) + 15) & (~15);
        }
        public static int GetDataSize(int componentSize, int chunkCapacity)
        {
            return (componentSize * chunkCapacity + 15) &(~15);
        }
        public static Entity* GetEntities(System.IntPtr state, int headerSize)
        {
            return (Entity*)(((byte*)state) + headerSize);
        }
        public static byte* GetData(System.IntPtr state, int headerSize, int entitiesSize)
        {
            return ((byte*)state) + headerSize + entitiesSize;
        }
        public static byte* GetNextData(byte* data, int componentSize, int chunkCapacity)
        {
            return data + GetDataSize(componentSize, chunkCapacity);
        }
    }
    /// <summary>
    /// A system used to make a backup o the current predicted state right after the last full (not fractional)
    /// tick in a prediction loop for a frame has been completed.
    /// The backup does a memcopy of all components which are rolled back as part of prediction to a separate
    /// memory area connected to the chunk.
    /// The backup is used to restore the last full tick to continue prediction when no new data has arrived,
    /// when that happens only the fields which are actually serialized as part of the snapshot are copied back,
    /// not the full component.
    /// The backup data is also used to detect errors in the prediction as well as to add smoothing of predicted
    /// values.
    /// </summary>
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostPredictionSystemGroup), OrderLast = true)]
    public unsafe class GhostPredictionHistorySystem : SystemBase
    {
        struct PredictionStateEntry
        {
            public ArchetypeChunk chunk;
            public System.IntPtr data;
        }

        internal NativeHashMap<ArchetypeChunk, System.IntPtr> PredictionState;
        internal JobHandle PredictionStateWriteJobHandle {get; private set;}
        JobHandle m_PredictionStateReadJobHandle;
        NativeHashMap<ArchetypeChunk, int> m_StillUsedPredictionState;
        NativeQueue<PredictionStateEntry> m_NewPredictionState;
        EntityQuery m_PredictionQuery;
        EntityQuery m_ChildEntityQuery;
        NativeHashMap<Entity, EntityChunkLookup> m_ChildEntityLookup;

        GhostCollectionSystem m_GhostCollectionSystem;

        GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
        ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        public uint LastBackupTick;
        internal void AddPredictionStateReader(JobHandle handle)
        {
            m_PredictionStateReadJobHandle = JobHandle.CombineDependencies(m_PredictionStateReadJobHandle, handle);
        }
        protected override void OnCreate()
        {
            m_GhostPredictionSystemGroup = World.GetExistingSystem<GhostPredictionSystemGroup>();
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();

            PredictionState = new NativeHashMap<ArchetypeChunk, System.IntPtr>(128, Allocator.Persistent);
            m_StillUsedPredictionState = new NativeHashMap<ArchetypeChunk, int>(128, Allocator.Persistent);
            m_NewPredictionState = new NativeQueue<PredictionStateEntry>(Allocator.Persistent);
            m_PredictionQuery = GetEntityQuery(ComponentType.ReadOnly<PredictedGhostComponent>(), ComponentType.ReadOnly<GhostComponent>());

            m_GhostCollectionSystem = World.GetOrCreateSystem<GhostCollectionSystem>();
            m_ChildEntityLookup = new NativeHashMap<Entity, EntityChunkLookup>(1024, Allocator.Persistent);
            m_ChildEntityQuery = GetEntityQuery(ComponentType.ReadOnly<GhostChildEntityComponent>());
        }
        protected override void OnDestroy()
        {
            var values = PredictionState.GetValueArray(Allocator.Temp);
            for (int i = 0; i < values.Length; ++i)
            {
                UnsafeUtility.Free((void*)values[i], Allocator.Persistent);
            }
            PredictionState.Dispose();
            m_StillUsedPredictionState.Dispose();
            m_NewPredictionState.Dispose();
            m_ChildEntityLookup.Dispose();
        }
        protected override void OnUpdate()
        {
            if (!m_GhostCollectionSystem.m_GhostTypeCollection.IsCreated)
                return;

            var serverTick = m_ClientSimulationSystemGroup.ServerTick;
            if (m_ClientSimulationSystemGroup.ServerTickFraction < 1)
                --serverTick;
            if (serverTick != m_GhostPredictionSystemGroup.PredictingTick)
                return;
            LastBackupTick = serverTick;

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

            var predictionState = PredictionState;
            var newPredictionState = m_NewPredictionState;
            var stillUsedPredictionState = m_StillUsedPredictionState;
            stillUsedPredictionState.Clear();
            if (stillUsedPredictionState.Capacity < predictionState.Capacity)
                stillUsedPredictionState.Capacity = predictionState.Capacity;
            var backupJob = new PredictionBackupJob
            {
                predictionState = predictionState,
                stillUsedPredictionState = stillUsedPredictionState.AsParallelWriter(),
                newPredictionState = newPredictionState.AsParallelWriter(),
                ghostType = GetComponentTypeHandle<GhostComponent>(true),
                entityType = GetEntityTypeHandle(),

                GhostComponentCollection = m_GhostCollectionSystem.m_GhostComponentCollection,
                GhostTypeCollection = m_GhostCollectionSystem.m_GhostTypeCollection,
                GhostComponentIndex = m_GhostCollectionSystem.m_GhostComponentIndex,

                childEntityLookup = m_ChildEntityLookup,
                linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(),
            };

            Dependency = JobHandle.CombineDependencies(Dependency, m_PredictionStateReadJobHandle);
            m_PredictionStateReadJobHandle = default;

            var listLength = m_GhostCollectionSystem.m_GhostComponentCollection.Length;
            if (listLength <= 32)
            {
                var dynamicListJob = new PredictionBackupJob32 {Job = backupJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else if (listLength <= 64)
            {
                var dynamicListJob = new PredictionBackupJob64 {Job = backupJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else if (listLength <= 128)
            {
                var dynamicListJob = new PredictionBackupJob128 {Job = backupJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else
                throw new System.InvalidOperationException(
                    $"Too many ghost component types present in project, limit is {DynamicTypeList.MaxCapacity} types. This is any struct which has a field marked with GhostField attribute.");

            Job.WithCode(() => {
                var keys = predictionState.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; ++i)
                {
                    if (!stillUsedPredictionState.TryGetValue(keys[i], out var temp))
                    {
                        // Free the memory and remove the chunk from the lookup
                        predictionState.TryGetValue(keys[i], out var alloc);
                        UnsafeUtility.Free((void*)alloc, Allocator.Persistent);
                        predictionState.Remove(keys[i]);
                    }
                }
                while (newPredictionState.TryDequeue(out var newState))
                {
                    if (!predictionState.TryAdd(newState.chunk, newState.data))
                    {
                        // Remove the old value, free it and add the new one - this happens when a chunk is reused too quickly
                        predictionState.TryGetValue(newState.chunk, out var alloc);
                        UnsafeUtility.Free((void*)alloc, Allocator.Persistent);
                        predictionState.Remove(newState.chunk);
                        // And add it again
                        predictionState.TryAdd(newState.chunk, newState.data);
                    }
                }
            }).Schedule();

            PredictionStateWriteJobHandle = Dependency;
        }
        [BurstCompile]
        struct PredictionBackupJob32 : IJobChunk
        {
            public DynamicTypeList32 List;
            public PredictionBackupJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        struct PredictionBackupJob64 : IJobChunk
        {
            public DynamicTypeList64 List;
            public PredictionBackupJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        struct PredictionBackupJob128 : IJobChunk
        {
            public DynamicTypeList128 List;
            public PredictionBackupJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }

        struct PredictionBackupJob
        {
            [ReadOnly] public NativeHashMap<ArchetypeChunk, System.IntPtr> predictionState;
            public NativeHashMap<ArchetypeChunk, int>.ParallelWriter stillUsedPredictionState;
            public NativeQueue<PredictionStateEntry>.ParallelWriter newPredictionState;
            [ReadOnly] public ComponentTypeHandle<GhostComponent> ghostType;
            [ReadOnly] public EntityTypeHandle entityType;

            [ReadOnly] public NativeArray<GhostComponentSerializer.State> GhostComponentCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostTypeState> GhostTypeCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostComponentIndex> GhostComponentIndex;

            [ReadOnly] public NativeHashMap<Entity, EntityChunkLookup> childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            const GhostComponentSerializer.SendMask requiredSendMask = GhostComponentSerializer.SendMask.Predicted;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                var ghostComponents = chunk.GetNativeArray(ghostType);
                int ghostTypeId = ghostComponents[0].ghostType;
                var typeData = GhostTypeCollection[ghostTypeId];

                var headerSize = PredictionBackupState.GetHeaderSize();
                var entitySize = PredictionBackupState.GetEntitiesSize(chunk.Capacity, out var singleEntitySize);
                int baseOffset = typeData.FirstComponent;
                if (!predictionState.TryGetValue(chunk, out var state) ||
                    (*(PredictionBackupState*)state).ghostType != ghostTypeId ||
                    (*(PredictionBackupState*)state).entityCapacity != chunk.Capacity)
                {
                    int dataSize = 0;
                    // Sum up the size of all components rounded up
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif
                        if ((GhostComponentCollection[compIdx].SendMask&requiredSendMask) == 0)
                            continue;
                        dataSize += PredictionBackupState.GetDataSize(GhostComponentCollection[compIdx].ComponentSize, chunk.Capacity);
                    }
                    // Chunk does not exist in the history, or has changed ghost type in which case we need to create a new one
                    state = (System.IntPtr)UnsafeUtility.Malloc(headerSize + entitySize + dataSize, 16, Allocator.Persistent);
                    newPredictionState.Enqueue(new PredictionStateEntry{chunk = chunk, data = state});
                    (*(PredictionBackupState*)state).ghostType = ghostTypeId;
                    (*(PredictionBackupState*)state).entityCapacity = chunk.Capacity;
                }
                else
                {
                    stillUsedPredictionState.TryAdd(chunk, 1);
                }
                Entity* entities = PredictionBackupState.GetEntities(state, headerSize);
                var srcEntities = chunk.GetNativeArray(entityType).GetUnsafeReadOnlyPtr();
                UnsafeUtility.MemCpy(entities, srcEntities, chunk.Count * singleEntitySize);
                if (chunk.Count < chunk.Capacity)
                    UnsafeUtility.MemClear(entities + chunk.Count, (chunk.Capacity - chunk.Count) * singleEntitySize);

                byte* dataPtr = PredictionBackupState.GetData(state, headerSize, entitySize);
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException("Component index out of range");
#endif
                    if ((GhostComponentCollection[compIdx].SendMask&requiredSendMask) == 0)
                        continue;
                    var compSize = GhostComponentCollection[compIdx].ComponentSize;
                    if (chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                    {
                        var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                        UnsafeUtility.MemCpy(dataPtr, compData, chunk.Count * compSize);
                    }
                    else
                        UnsafeUtility.MemClear(dataPtr, chunk.Count * compSize);

                    dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif
                        if ((GhostComponentCollection[compIdx].SendMask&requiredSendMask) == 0)
                            continue;
                        var compSize = GhostComponentCollection[compIdx].ComponentSize;

                        for (int ent = 0; ent < chunk.Count; ++ent)
                        {
                            var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                            if (childEntityLookup.TryGetValue(linkedEntityGroup[GhostComponentIndex[baseOffset + comp].EntityIndex].Value, out var childChunk) &&
                                childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                            {
                                var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafePtr();
                                UnsafeUtility.MemCpy(dataPtr + ent * compSize, compData + ent * compSize, compSize);
                            }
                            else
                                UnsafeUtility.MemClear(dataPtr + ent * compSize, compSize);
                        }

                        dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                    }
                }
            }
        }
    }
}
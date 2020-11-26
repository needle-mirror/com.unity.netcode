using System;
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
    // byte*[Capacity * sizeof(IComponentData)] the raw backup data for all replicated components in this ghost type. For buffers an uint pair (size, offset) is stored instead.
    // [Opt]byte*[BuffersDataSize] the raw buffers element data present in the chunk if present. The total buffers size is computed at runtime and the
    // backup state resized accordingly. All buffers contents start to a 16 bytes aligned offset: Align(b1Elem*b1ElemSize, 16), Align(b2Elem*b2ElemSize, 16) ...

    internal unsafe struct PredictionBackupState
    {
        // If ghost type has changed the data must be discarded as the chunk is now used for something else
        public int ghostType;
        public int entityCapacity;
        public int entitiesOffset;
        //the ghost component serialized size
        public int dataOffset;
        public int dataSize;
        //the capacity for the dynamic data. Dynamic Buffers are store after the component backup
        public int bufferDataCapacity;
        public int bufferDataOffset;

        public static IntPtr AllocNew(int ghostTypeId, int dataSize, int entityCapacity, int buffersDataCapacity)
        {
            var entitiesSize = (ushort)GetEntitiesSize(entityCapacity, out var _);
            var headerSize = GetHeaderSize();
            var state = (PredictionBackupState*)UnsafeUtility.Malloc(headerSize + entitiesSize + dataSize + buffersDataCapacity, 16, Allocator.Persistent);
            state->ghostType = ghostTypeId;
            state->entityCapacity = entityCapacity;
            state->entitiesOffset = headerSize;
            state->dataOffset = headerSize + entitiesSize;
            state->dataSize = dataSize;
            state->bufferDataCapacity = buffersDataCapacity;
            state->bufferDataOffset = headerSize + entitiesSize + dataSize;
            return (IntPtr)state;
        }
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
        public static Entity* GetEntities(IntPtr state)
        {
            var ps = ((PredictionBackupState*) state);
            return (Entity*)(((byte*)state) + ps->entitiesOffset);
        }
        public static byte* GetData(IntPtr state)
        {
            var ps = ((PredictionBackupState*) state);
            return ((byte*) state) + ps->dataOffset;
        }

        public static int GetBufferDataCapacity(IntPtr state)
        {
            return ((PredictionBackupState*) state)->bufferDataCapacity;
        }
        public static byte* GetBufferDataPtr(IntPtr state)
        {
            var ps = ((PredictionBackupState*) state);
            return ((byte*) state) + ps->bufferDataOffset;
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
        NativeQueue<PredictionStateEntry> m_UpdatedPredictionState;
        EntityQuery m_PredictionQuery;
        EntityQuery m_ChildEntityQuery;
        NativeHashMap<Entity, EntityChunkLookup> m_ChildEntityLookup;

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
            m_UpdatedPredictionState = new NativeQueue<PredictionStateEntry>(Allocator.Persistent);
            m_PredictionQuery = GetEntityQuery(ComponentType.ReadOnly<PredictedGhostComponent>(), ComponentType.ReadOnly<GhostComponent>());

            m_ChildEntityLookup = new NativeHashMap<Entity, EntityChunkLookup>(1024, Allocator.Persistent);
            m_ChildEntityQuery = GetEntityQuery(ComponentType.ReadOnly<GhostChildEntityComponent>());

            RequireSingletonForUpdate<GhostCollection>();
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
            m_UpdatedPredictionState.Dispose();
            m_ChildEntityLookup.Dispose();
        }
        protected override void OnUpdate()
        {
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
            var updatedPredictionState = m_UpdatedPredictionState;
            stillUsedPredictionState.Clear();
            if (stillUsedPredictionState.Capacity < predictionState.Capacity)
                stillUsedPredictionState.Capacity = predictionState.Capacity;
            var backupJob = new PredictionBackupJob
            {
                predictionState = predictionState,
                stillUsedPredictionState = stillUsedPredictionState.AsParallelWriter(),
                newPredictionState = newPredictionState.AsParallelWriter(),
                updatedPredictionState = updatedPredictionState.AsParallelWriter(),
                ghostType = GetComponentTypeHandle<GhostComponent>(true),
                entityType = GetEntityTypeHandle(),

                GhostCollectionSingleton = GetSingletonEntity<GhostCollection>(),
                GhostComponentCollectionFromEntity = GetBufferFromEntity<GhostComponentSerializer.State>(true),
                GhostTypeCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefabSerializer>(true),
                GhostComponentIndexFromEntity = GetBufferFromEntity<GhostCollectionComponentIndex>(true),

                childEntityLookup = m_ChildEntityLookup,
                linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(),
            };

            Dependency = JobHandle.CombineDependencies(Dependency, m_PredictionStateReadJobHandle);
            m_PredictionStateReadJobHandle = default;

            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(backupJob.GhostCollectionSingleton);
            var listLength = ghostComponentCollection.Length;
            if (listLength <= 32)
            {
                var dynamicListJob = new PredictionBackupJob32 {Job = backupJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else if (listLength <= 64)
            {
                var dynamicListJob = new PredictionBackupJob64 {Job = backupJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else if (listLength <= 128)
            {
                var dynamicListJob = new PredictionBackupJob128 {Job = backupJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
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
                while (updatedPredictionState.TryDequeue(out var updatedState))
                {
                    if(!predictionState.ContainsKey(updatedState.chunk))
                        throw new InvalidOperationException("prediction backup state has been updated but is not present in the map");
                    predictionState[updatedState.chunk] = updatedState.data;
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
            [ReadOnly]public NativeHashMap<ArchetypeChunk, System.IntPtr> predictionState;
            public NativeHashMap<ArchetypeChunk, int>.ParallelWriter stillUsedPredictionState;
            public NativeQueue<PredictionStateEntry>.ParallelWriter newPredictionState;
            public NativeQueue<PredictionStateEntry>.ParallelWriter updatedPredictionState;
            [ReadOnly] public ComponentTypeHandle<GhostComponent> ghostType;
            [ReadOnly] public EntityTypeHandle entityType;

            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferFromEntity<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;

            [ReadOnly] public NativeHashMap<Entity, EntityChunkLookup> childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            const GhostComponentSerializer.SendMask requiredSendMask = GhostComponentSerializer.SendMask.Predicted;

            //Sum up all the dynamic buffers raw data content size. Each buffer content size is aligned to 16 bytes
            private int GetChunkBuffersDataSize(GhostCollectionPrefabSerializer typeData, ArchetypeChunk chunk,
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength, DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex, DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection)
            {
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                int bufferTotalSize = 0;
                int baseOffset = typeData.FirstComponent;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException("Component index out of range");
#endif
                    if ((GhostComponentIndex[baseOffset + comp].SendMask & requiredSendMask) == 0)
                        continue;

                    if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        continue;

                    if (chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                    {
                        var bufferData = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                        for (int i = 0; i < bufferData.Length; ++i)
                        {
                            bufferTotalSize += bufferData.GetBufferCapacity(i) * GhostComponentCollection[serializerIdx].ComponentSize;
                        }
                        bufferTotalSize = GhostCollectionSystem.SnapshotSizeAligned(bufferTotalSize);
                    }
                }

                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif
                        if ((GhostComponentIndex[baseOffset + comp].SendMask & requiredSendMask) == 0)
                            continue;

                        if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            continue;

                        for (int ent = 0; ent < chunk.Count; ++ent)
                        {
                            var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                            if (childEntityLookup.TryGetValue(linkedEntityGroup[GhostComponentIndex[baseOffset + comp].EntityIndex].Value,
                                    out var childChunk) && childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                            {
                                var bufferData = childChunk.chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                bufferTotalSize += bufferData.GetBufferCapacity(childChunk.index) * GhostComponentCollection[serializerIdx].ComponentSize;
                            }
                            bufferTotalSize = GhostCollectionSystem.SnapshotSizeAligned(bufferTotalSize);
                        }
                    }
                }

                return bufferTotalSize;
            }

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];
                var GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];

                var ghostComponents = chunk.GetNativeArray(ghostType);

                int ghostTypeId = ghostComponents.GetFirstGhostTypeId();
                if (ghostTypeId < 0)
                    return;
                var typeData = GhostTypeCollection[ghostTypeId];

                var singleEntitySize = UnsafeUtility.SizeOf<Entity>();
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
                        int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif
                        if ((GhostComponentIndex[baseOffset + comp].SendMask&requiredSendMask) == 0)
                            continue;

                        //for buffers we store a a pair of uint:
                        // uint length: the num of elements
                        // uint backupDataOffset: the start position in the backup buffer
                        if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            dataSize += PredictionBackupState.GetDataSize(
                                GhostComponentCollection[serializerIdx].ComponentSize, chunk.Capacity);
                        else
                            dataSize += PredictionBackupState.GetDataSize(GhostSystemConstants.DynamicBufferComponentSnapshotSize, chunk.Capacity);
                    }

                    //compute the space necessary to store the dynamic buffers data for the chunk
                    int buffersDataCapacity = 0;
                    if (typeData.NumBuffers > 0)
                        buffersDataCapacity = GetChunkBuffersDataSize(typeData, chunk, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength, GhostComponentIndex, GhostComponentCollection);

                    // Chunk does not exist in the history, or has changed ghost type in which case we need to create a new one
                    state = PredictionBackupState.AllocNew(ghostTypeId, dataSize, chunk.Capacity, buffersDataCapacity);
                    newPredictionState.Enqueue(new PredictionStateEntry{chunk = chunk, data = state});
                }
                else
                {
                    stillUsedPredictionState.TryAdd(chunk, 1);
                    if (typeData.NumBuffers > 0)
                    {
                        //resize the backup state to fit the dynamic buffers contents
                        var buffersDataCapacity = GetChunkBuffersDataSize(typeData, chunk, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength, GhostComponentIndex, GhostComponentCollection);
                        int bufferBackupDataCapacity = PredictionBackupState.GetBufferDataCapacity(state);
                        if (bufferBackupDataCapacity < buffersDataCapacity)
                        {
                            var dataSize = ((PredictionBackupState*)state)->dataSize;
                            var newState =  PredictionBackupState.AllocNew(ghostTypeId, dataSize, chunk.Capacity, buffersDataCapacity);
                            UnsafeUtility.Free((void*) state, Allocator.Persistent);
                            state = newState;
                            updatedPredictionState.Enqueue(new PredictionStateEntry{chunk = chunk, data = newState});
                        }
                    }
                }
                Entity* entities = PredictionBackupState.GetEntities(state);
                var srcEntities = chunk.GetNativeArray(entityType).GetUnsafeReadOnlyPtr();
                UnsafeUtility.MemCpy(entities, srcEntities, chunk.Count * singleEntitySize);
                if (chunk.Count < chunk.Capacity)
                    UnsafeUtility.MemClear(entities + chunk.Count, (chunk.Capacity - chunk.Count) * singleEntitySize);

                byte* dataPtr = PredictionBackupState.GetData(state);
                byte* bufferBackupDataPtr = PredictionBackupState.GetBufferDataPtr(state);

                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                int bufferBackupDataOffset = 0;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException("Component index out of range");
#endif
                    if ((GhostComponentIndex[baseOffset + comp].SendMask&requiredSendMask) == 0)
                        continue;

                    var compSize = GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                        ? GhostSystemConstants.DynamicBufferComponentSnapshotSize
                        : GhostComponentCollection[serializerIdx].ComponentSize;

                    if (!chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                    {
                        UnsafeUtility.MemClear(dataPtr, chunk.Count * compSize);
                    }
                    else if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                    {
                        var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                        UnsafeUtility.MemCpy(dataPtr, compData, chunk.Count * compSize);
                    }
                    else
                    {
                        var bufferData = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                        var bufElemSize = GhostComponentCollection[serializerIdx].ComponentSize;
                        //Use local variable to iterate and set the buffer offset and length. The dataptr must be
                        //advanced "per chunk" to the next correct position
                        var tempDataPtr = dataPtr;
                        for (int i = 0; i < bufferData.Length; ++i)
                        {
                            //Retrieve an copy each buffer data. Set size and offset in the backup buffer in the component backup
                            var bufferPtr = bufferData.GetUnsafeReadOnlyPtrAndLength(i, out var size);
                            ((int*) tempDataPtr)[0] = size;
                            ((int*) tempDataPtr)[1] = bufferBackupDataOffset;
                            if (size > 0)
                                UnsafeUtility.MemCpy(bufferBackupDataPtr+bufferBackupDataOffset, (byte*)bufferPtr, size*bufElemSize);
                            bufferBackupDataOffset += size*bufElemSize;
                            tempDataPtr += compSize;
                        }
                        bufferBackupDataOffset = GhostCollectionSystem.SnapshotSizeAligned(bufferBackupDataOffset);
                    }
                    dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif
                        if ((GhostComponentIndex[baseOffset + comp].SendMask&requiredSendMask) == 0)
                            continue;

                        var isBuffer = GhostComponentCollection[serializerIdx].ComponentType.IsBuffer;
                        var compSize = isBuffer ? GhostSystemConstants.DynamicBufferComponentSnapshotSize : GhostComponentCollection[serializerIdx].ComponentSize;
                        if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            //use a temporary for the iteration here. Otherwise when the dataptr is offset for the chunk, we
                            //end up in the wrong position
                            var tempDataPtr = dataPtr;
                            for (int ent = 0; ent < chunk.Count; ++ent)
                            {
                                var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                if (childEntityLookup.TryGetValue(linkedEntityGroup[GhostComponentIndex[baseOffset + comp].EntityIndex].Value, out var childChunk) &&
                                    childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                                {
                                    var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                    UnsafeUtility.MemCpy(tempDataPtr, compData + childChunk.index * compSize, compSize);
                                }
                                else
                                    UnsafeUtility.MemClear(tempDataPtr, compSize);
                                tempDataPtr += compSize;
                            }
                        }
                        else
                        {
                            var bufElemSize = GhostComponentCollection[serializerIdx].ComponentSize;
                            var tempDataPtr = dataPtr;
                            for (int ent = 0; ent < chunk.Count; ++ent)
                            {
                                var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                if (childEntityLookup.TryGetValue(linkedEntityGroup[GhostComponentIndex[baseOffset + comp].EntityIndex].Value, out var childChunk) &&
                                    childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                                {
                                    var bufferData = childChunk.chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                    //Retrieve an copy each buffer data. Set size and offset in the backup buffer in the component backup
                                    var bufferPtr = bufferData.GetUnsafeReadOnlyPtrAndLength(childChunk.index, out var size);
                                    ((int*) tempDataPtr)[0] = size;
                                    ((int*) tempDataPtr)[1] = bufferBackupDataOffset;
                                    if (size > 0)
                                        UnsafeUtility.MemCpy(bufferBackupDataPtr+bufferBackupDataOffset, (byte*)bufferPtr, size*bufElemSize);
                                    bufferBackupDataOffset += size*bufElemSize;
                                }
                                else
                                {
                                    //reset the entry to 0. Don't use memcpy in this case (is faster this way)
                                    ((long*) tempDataPtr)[0] = 0;
                                }
                                tempDataPtr += compSize;
                            }
                            bufferBackupDataOffset = GhostCollectionSystem.SnapshotSizeAligned(bufferBackupDataOffset);
                        }
                        dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                    }
                }
            }
        }
    }
}

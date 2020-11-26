using Unity.Burst;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using System;

namespace Unity.NetCode
{
    public struct PredictedGhostSpawnList : IComponentData
    {}
    public struct PredictedGhostSpawn : IBufferElementData
    {
        public Entity entity;
        public int ghostType;
        public uint spawnTick;
    }

    [UpdateInGroup(typeof(GhostSpawnSystemGroup))]
    [UpdateAfter(typeof(GhostSpawnSystem))]
    public class PredictedGhostSpawnSystem : SystemBase
    {
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private BeginSimulationEntityCommandBufferSystem m_BeginSimulationBarrier;
        private EntityQuery m_GhostInitQuery;
        private uint m_SpawnTick;
        private NativeHashMap<Entity, EntityChunkLookup> m_ChildEntityLookup;
        private EntityQuery m_ChildEntityQuery;

        [BurstCompile]
        unsafe struct InitGhostJob32 : IJobChunk
        {
            public DynamicTypeList32 List;
            public InitGhostJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        unsafe struct InitGhostJob64 : IJobChunk
        {
            public DynamicTypeList64 List;
            public InitGhostJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        unsafe struct InitGhostJob128 : IJobChunk
        {
            public DynamicTypeList128 List;
            public InitGhostJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }

        [BurstCompile]
        struct InitGhostJob
        {
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferFromEntity<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefab> GhostCollectionFromEntity;

            [ReadOnly] public EntityTypeHandle entityType;
            public ComponentTypeHandle<SnapshotData> snapshotDataType;
            public BufferTypeHandle<SnapshotDataBuffer> snapshotDataBufferType;
            public BufferTypeHandle<SnapshotDynamicDataBuffer> snapshotDynamicDataBufferType;

            public BufferFromEntity<PredictedGhostSpawn> spawnListFromEntity;
            public Entity spawnListEntity;

            public ComponentDataFromEntity<GhostComponent> ghostFromEntity;
            [ReadOnly] public ComponentDataFromEntity<GhostTypeComponent> ghostTypeFromEntity;

            public EntityCommandBuffer commandBuffer;
            public uint spawnTick;

            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            [ReadOnly] public NativeHashMap<Entity, EntityChunkLookup> childEntityLookup;

            private struct BufferDataEntry
            {
                public int offset;
                public int len;
                public int serializerIdx;
                public IntPtr bufferData;
            }

            public unsafe void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                var entityList = chunk.GetNativeArray(entityType);
                var snapshotDataList = chunk.GetNativeArray(snapshotDataType);
                var snapshotDataBufferList = chunk.GetBufferAccessor(snapshotDataBufferType);
                var snapshotDynamicDataBufferList = chunk.GetBufferAccessor(snapshotDynamicDataBufferType);

                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var ghostTypeComponent = ghostTypeFromEntity[entityList[0]];
                int ghostType;
                for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
                {
                    if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                        break;
                }
                if (ghostType >= GhostCollection.Length)
                    throw new InvalidOperationException("Could not find ghost type in the collection");
                if (ghostType >= GhostTypeCollection.Length)
                    return; // serialization data has not been loaded yet

                var GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                var GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];

                var spawnList = spawnListFromEntity[spawnListEntity];
                var typeData = GhostTypeCollection[ghostType];
                var snapshotSize = typeData.SnapshotSize;
                int changeMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                var snapshotBaseOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                var serializerState = new GhostSerializerState
                {
                    GhostFromEntity = ghostFromEntity
                };
                var buffersToSerialize = new NativeList<BufferDataEntry>(Allocator.Temp);

                for (int i = 0; i < entityList.Length; ++i)
                {
                    var entity = entityList[i];

                    var ghostComponent = ghostFromEntity[entity];
                    ghostComponent.ghostType = ghostType;
                    ghostFromEntity[entity] = ghostComponent;
                    // Set initial snapshot data
                    // Get the buffers, fill in snapshot size etc
                    snapshotDataList[i] = new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0};
                    var snapshotDataBuffer = snapshotDataBufferList[i];
                    snapshotDataBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    var snapshotPtr = (byte*)snapshotDataBuffer.GetUnsafePtr();
                    UnsafeUtility.MemClear(snapshotPtr, snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    *(uint*)snapshotPtr = spawnTick;
                    var snapshotOffset = snapshotBaseOffset;
                    var dynamicSnapshotDataOffset = 0;

                    //Loop through all the serializable components and copy their data into the snapshot.
                    //For buffers, we collect what we need to serialize and then we copy the content in a second
                    //step. This is necessary because we need to resize the dynamic snapshot buffer
                    int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                    for (int comp = 0; comp < numBaseComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new InvalidOperationException("Component index out of range");
#endif
                        var componentSnapshotSize = GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                            ? GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize)
                            : GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                        var compSize = GhostComponentCollection[serializerIdx].ComponentSize;

                        if (!chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                        {
                            UnsafeUtility.MemClear(snapshotPtr + snapshotOffset, componentSnapshotSize);
                        }
                        else if(!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                            GhostComponentCollection[serializerIdx].CopyToSnapshot.Ptr.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState), (IntPtr)snapshotPtr, snapshotOffset, snapshotSize, (IntPtr)(compData + i*compSize), compSize, 1);
                        }

                        else
                        {
                            // Collect the buffer data to serialize by storing pointers, offset and size.
                            var bufData = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                            var bufferPointer = (IntPtr)bufData.GetUnsafeReadOnlyPtrAndLength(i, out var bufferLen);
                            var snapshotData = (uint*) (snapshotPtr + snapshotOffset);
                            snapshotData[0] = (uint)bufferLen;
                            snapshotData[1] = (uint)dynamicSnapshotDataOffset;
                            buffersToSerialize.Add(new BufferDataEntry
                            {
                                offset = dynamicSnapshotDataOffset,
                                len = bufferLen,
                                serializerIdx = serializerIdx,
                                bufferData = bufferPointer
                            });
                            var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[serializerIdx].ChangeMaskBits, bufferLen);
                            dynamicSnapshotDataOffset += GhostCollectionSystem.SnapshotSizeAligned(maskSize + GhostComponentCollection[serializerIdx].ComponentSize * bufferLen);
                        }
                        snapshotOffset += componentSnapshotSize;
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
                                throw new System.InvalidOperationException("Component index out of range");
#endif
                            var compSize = GhostComponentCollection[serializerIdx].ComponentSize;

                            var linkedEntityGroup = linkedEntityGroupAccessor[i];
                            var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;

                            var componentSnapshotSize = GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                                ? GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize)
                                : GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);

                            //We can skip here, becase the memory buffer offset is computed using the start-end entity indices
                            if (!childEntityLookup.TryGetValue(childEnt, out var childChunk) || !childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                            {
                                UnsafeUtility.MemClear(snapshotPtr + snapshotOffset, componentSnapshotSize);
                            }
                            else if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            {
                                var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                compData += childChunk.index * compSize;
                                GhostComponentCollection[serializerIdx].CopyToSnapshot.Ptr.Invoke((System.IntPtr)UnsafeUtility.AddressOf(ref serializerState),
                                    (System.IntPtr)snapshotPtr, snapshotOffset, snapshotSize, (System.IntPtr)compData, compSize, 1);
                            }
                            else
                            {
                                // Collect the buffer data to serialize by storing pointers, offset and size.
                                var bufData = childChunk.chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                var bufferPointer = (IntPtr)bufData.GetUnsafeReadOnlyPtrAndLength(childChunk.index, out var bufferLen);
                                var snapshotData = (uint*) (snapshotPtr + snapshotOffset);
                                snapshotData[0] = (uint)bufferLen;
                                snapshotData[1] = (uint)dynamicSnapshotDataOffset;
                                buffersToSerialize.Add(new BufferDataEntry
                                {
                                    offset = dynamicSnapshotDataOffset,
                                    len = bufferLen,
                                    serializerIdx = serializerIdx,
                                    bufferData = bufferPointer
                                });
                                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[serializerIdx].ChangeMaskBits, bufferLen);
                                dynamicSnapshotDataOffset += GhostCollectionSystem.SnapshotSizeAligned(maskSize + GhostComponentCollection[serializerIdx].ComponentSize * bufferLen);
                            }
                            snapshotOffset += componentSnapshotSize;
                        }
                    }

                    //Second step (necessary only for buffers): resize the dynamicdata snapshot buffer and copy the buffer contents
                    if (GhostTypeCollection[ghostType].NumBuffers > 0 && buffersToSerialize.Length > 0)
                    {
                        var dynamicDataCapacity= SnapshotDynamicBuffersHelper.CalculateBufferCapacity((uint)dynamicSnapshotDataOffset, out _);
                        var headerSize = SnapshotDynamicBuffersHelper.GetHeaderSize();
                        var snapshotDynamicDataBuffer = snapshotDynamicDataBufferList[i];
                        snapshotDynamicDataBuffer.ResizeUninitialized((int)dynamicDataCapacity);
                        var snapshotDynamicDataBufferPtr = (byte*)snapshotDynamicDataBuffer.GetUnsafePtr() + headerSize;

                        ((uint*) snapshotDynamicDataBuffer.GetUnsafePtr())[0] = (uint)dynamicSnapshotDataOffset;
                        for(int buf=0;buf<buffersToSerialize.Length;++buf)
                        {
                            var entry = buffersToSerialize[buf];
                            var dynamicDataSize = GhostComponentCollection[entry.serializerIdx].SnapshotSize;
                            var compSize = GhostComponentCollection[entry.serializerIdx].ComponentSize;
                            var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[entry.serializerIdx].ChangeMaskBits, entry.len);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if((maskSize + entry.offset + dynamicDataSize * entry.len) > dynamicDataCapacity)
                                throw new System.InvalidOperationException("Overflow writing data to dynamic snapshot memory buffer");
#endif
                            GhostComponentCollection[entry.serializerIdx].CopyToSnapshot.Ptr.Invoke((System.IntPtr) UnsafeUtility.AddressOf(ref serializerState),
                                (System.IntPtr) snapshotDynamicDataBufferPtr + maskSize, entry.offset, dynamicDataSize,
                                entry.bufferData, compSize, entry.len);
                        }
                    }

                    // Remove request component
                    commandBuffer.RemoveComponent<PredictedGhostSpawnRequestComponent>(entity);
                    // Add to list of predictive spawn component - maybe use a singleton for this so spawn systems can just access it too
                    spawnList.Add(new PredictedGhostSpawn{entity = entity, ghostType = ghostType, spawnTick = spawnTick});
                }
            }
        }
        protected override void OnCreate()
        {
            var ent = EntityManager.CreateEntity();
            EntityManager.AddComponentData(ent, default(PredictedGhostSpawnList));
            EntityManager.AddBuffer<PredictedGhostSpawn>(ent);
            RequireSingletonForUpdate<PredictedGhostSpawnList>();
            m_BeginSimulationBarrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_GhostInitQuery = GetEntityQuery(ComponentType.ReadOnly<PredictedGhostSpawnRequestComponent>(),
                ComponentType.ReadOnly<GhostTypeComponent>(),
                ComponentType.ReadWrite<GhostComponent>());
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
            m_ChildEntityLookup = new NativeHashMap<Entity, EntityChunkLookup>(1024, Allocator.Persistent);
            m_ChildEntityQuery = GetEntityQuery(ComponentType.ReadOnly<GhostChildEntityComponent>());
        }

        protected override void OnDestroy()
        {
            m_ChildEntityLookup.Dispose();
        }

        protected override unsafe void OnUpdate()
        {
            EntityCommandBuffer commandBuffer = m_BeginSimulationBarrier.CreateCommandBuffer();
            var spawnListEntity = GetSingletonEntity<PredictedGhostSpawnList>();
            var spawnListFromEntity = GetBufferFromEntity<PredictedGhostSpawn>();

            if (!m_GhostInitQuery.IsEmptyIgnoreFilter)
            {
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
                var initJob = new InitGhostJob
                {
                    GhostCollectionSingleton = GetSingletonEntity<GhostCollection>(),
                    GhostComponentCollectionFromEntity = GetBufferFromEntity<GhostComponentSerializer.State>(true),
                    GhostTypeCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefabSerializer>(true),
                    GhostComponentIndexFromEntity = GetBufferFromEntity<GhostCollectionComponentIndex>(true),
                    GhostCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefab>(true),

                    entityType = GetEntityTypeHandle(),
                    snapshotDataType = GetComponentTypeHandle<SnapshotData>(),
                    snapshotDataBufferType = GetBufferTypeHandle<SnapshotDataBuffer>(),
                    snapshotDynamicDataBufferType = GetBufferTypeHandle<SnapshotDynamicDataBuffer>(),

                    spawnListFromEntity = spawnListFromEntity,
                    spawnListEntity = spawnListEntity,

                    ghostFromEntity = GetComponentDataFromEntity<GhostComponent>(),
                    ghostTypeFromEntity = GetComponentDataFromEntity<GhostTypeComponent>(true),

                    commandBuffer = commandBuffer,
                    spawnTick = m_SpawnTick,
                    linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(),
                    childEntityLookup = m_ChildEntityLookup
                };
                var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(initJob.GhostCollectionSingleton);
                var listLength = ghostComponentCollection.Length;
                if (listLength <= 32)
                {
                    var dynamicListJob = new InitGhostJob32 {Job = initJob};
                    DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                    Dependency = dynamicListJob.ScheduleSingle(m_GhostInitQuery, Dependency);
                }
                else if (listLength <= 64)
                {
                    var dynamicListJob = new InitGhostJob64 {Job = initJob};
                    DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                    Dependency = dynamicListJob.ScheduleSingle(m_GhostInitQuery, Dependency);
                }
                else if (listLength <= 128)
                {
                    var dynamicListJob = new InitGhostJob128 {Job = initJob};
                    DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                    Dependency = dynamicListJob.ScheduleSingle(m_GhostInitQuery, Dependency);
                }
                else
                    throw new System.InvalidOperationException(
                        $"Too many ghost component types present in project, limit is {DynamicTypeList.MaxCapacity} types. This is any struct which has a field marked with GhostField attribute.");
            }

            // Validate all ghosts in the list of predictive spawn ghosts and destroy the ones which are too old
            uint interpolatedTick = m_ClientSimulationSystemGroup.InterpolationTick;
            Dependency = Job.WithCode(() =>
            {
                var spawnList = spawnListFromEntity[spawnListEntity];
                for (int i = 0; i < spawnList.Length; ++i)
                {
                    var ghost = spawnList[i];
                    if (SequenceHelpers.IsNewer(interpolatedTick, ghost.spawnTick))
                    {
                        // Destroy entity and remove from list
                        commandBuffer.DestroyEntity(ghost.entity);
                        spawnList[i] = spawnList[spawnList.Length - 1];
                        spawnList.RemoveAt(spawnList.Length - 1);
                        --i;
                    }
                }
            }).Schedule(Dependency);
            m_BeginSimulationBarrier.AddJobHandleForProducer(Dependency);
            m_SpawnTick = m_ClientSimulationSystemGroup.ServerTick;
        }
    }
}

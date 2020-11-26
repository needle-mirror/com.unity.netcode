using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;

namespace Unity.NetCode
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostPredictionSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(GhostPredictionSmoothingSystem))]
    [UpdateBefore(typeof(GhostPredictionHistorySystem))]
    public unsafe class GhostPredictionDebugSystem : SystemBase
    {
        GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
        GhostPredictionHistorySystem m_GhostPredictionHistorySystem;

        NativeArray<float> m_PredictionErrors;

        EntityQuery m_PredictionQuery;
        EntityQuery m_ChildEntityQuery;
        NativeHashMap<Entity, EntityChunkLookup> m_ChildEntityLookup;
        protected override void OnCreate()
        {
            m_GhostPredictionSystemGroup = World.GetExistingSystem<GhostPredictionSystemGroup>();
            m_GhostPredictionHistorySystem = World.GetExistingSystem<GhostPredictionHistorySystem>();

            m_PredictionQuery = GetEntityQuery(ComponentType.ReadOnly<PredictedGhostComponent>(), ComponentType.ReadOnly<GhostComponent>());
            m_ChildEntityLookup = new NativeHashMap<Entity, EntityChunkLookup>(1024, Allocator.Persistent);
            m_ChildEntityQuery = GetEntityQuery(ComponentType.ReadOnly<GhostChildEntityComponent>());
            m_PredictionErrors = new NativeArray<float>(0, Allocator.Persistent);

            RequireSingletonForUpdate<GhostCollection>();
        }
        protected override void OnDestroy()
        {
            m_ChildEntityLookup.Dispose();
            m_PredictionErrors.Dispose();
        }
        protected override void OnUpdate()
        {
            if (m_GhostPredictionSystemGroup.PredictingTick != m_GhostPredictionHistorySystem.LastBackupTick)
                return;

            var predictionErrorCount = GetSingleton<GhostCollection>().NumPredictionErrorNames;
            if (m_PredictionErrors.Length != predictionErrorCount * JobsUtility.MaxJobThreadCount)
            {
                m_PredictionErrors.Dispose();
                m_PredictionErrors = new NativeArray<float>(predictionErrorCount * JobsUtility.MaxJobThreadCount, Allocator.Persistent);
            }
            for (int job = 1; job < JobsUtility.MaxJobThreadCount; ++job)
            {
                for (int i = 0; i < predictionErrorCount; ++i)
                {
                    m_PredictionErrors[i] = math.max(m_PredictionErrors[i], m_PredictionErrors[predictionErrorCount*job + i]);
                }
            }
            World.GetExistingSystem<GhostStatsCollectionSystem>().AddPredictionErrorStats(m_PredictionErrors.GetSubArray(0, predictionErrorCount));
            // Clear
            UnsafeUtility.MemClear(m_PredictionErrors.GetUnsafePtr(), 4 * predictionErrorCount * JobsUtility.MaxJobThreadCount);

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

            var GhostCollectionSingleton = GetSingletonEntity<GhostCollection>();
            var debugJob = new PredictionDebugJob
            {
                predictionState = m_GhostPredictionHistorySystem.PredictionState,
                ghostType = GetComponentTypeHandle<GhostComponent>(true),
                predictedGhostType = GetComponentTypeHandle<PredictedGhostComponent>(true),
                entityType = GetEntityTypeHandle(),

                GhostCollectionSingleton = GetSingletonEntity<GhostCollection>(),
                GhostComponentCollectionFromEntity = GetBufferFromEntity<GhostComponentSerializer.State>(true),
                GhostTypeCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefabSerializer>(true),
                GhostComponentIndexFromEntity = GetBufferFromEntity<GhostCollectionComponentIndex>(true),

                childEntityLookup = m_ChildEntityLookup,
                linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(),
                tick = m_GhostPredictionSystemGroup.PredictingTick,
                translationType = ComponentType.ReadWrite<Translation>(),

                predictionErrors = m_PredictionErrors,
                numPredictionErrors = predictionErrorCount
            };

            Dependency = JobHandle.CombineDependencies(Dependency, m_GhostPredictionHistorySystem.PredictionStateWriteJobHandle);

            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(GhostCollectionSingleton);
            var listLength = ghostComponentCollection.Length;
            if (listLength <= 32)
            {
                var dynamicListJob = new PredictionDebugJob32 {Job = debugJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else if (listLength <= 64)
            {
                var dynamicListJob = new PredictionDebugJob64 {Job = debugJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else if (listLength <= 128)
            {
                var dynamicListJob = new PredictionDebugJob128 {Job = debugJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else
                throw new System.InvalidOperationException(
                    $"Too many ghost component types present in project, limit is {DynamicTypeList.MaxCapacity} types. This is any struct which has a field marked with GhostField attribute.");

            m_GhostPredictionHistorySystem.AddPredictionStateReader(Dependency);
        }
        [BurstCompile]
        struct PredictionDebugJob32 : IJobChunk
        {
            public DynamicTypeList32 List;
            public PredictionDebugJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        struct PredictionDebugJob64 : IJobChunk
        {
            public DynamicTypeList64 List;
            public PredictionDebugJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        struct PredictionDebugJob128 : IJobChunk
        {
            public DynamicTypeList128 List;
            public PredictionDebugJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        struct PredictionDebugJob
        {
            [ReadOnly] public NativeHashMap<ArchetypeChunk, System.IntPtr> predictionState;

            [ReadOnly] public ComponentTypeHandle<GhostComponent> ghostType;
            [ReadOnly] public ComponentTypeHandle<PredictedGhostComponent> predictedGhostType;
            [ReadOnly] public EntityTypeHandle entityType;

            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferFromEntity<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;

            [ReadOnly] public NativeHashMap<Entity, EntityChunkLookup> childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            public uint tick;
            // FIXME: placeholder to show the idea behind prediction smoothing
            public ComponentType translationType;

            const GhostComponentSerializer.SendMask requiredSendMask = GhostComponentSerializer.SendMask.Predicted;

    #pragma warning disable 649
            [NativeSetThreadIndex] public int ThreadIndex;
    #pragma warning restore 649
            [NativeDisableParallelForRestriction] public NativeArray<float> predictionErrors;
            public int numPredictionErrors;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                if (!predictionState.TryGetValue(chunk, out var state) ||
                    (*(PredictionBackupState*)state).entityCapacity != chunk.Capacity)
                    return;

                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];
                var GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];

                var ghostComponents = chunk.GetNativeArray(ghostType);
                int ghostTypeId = ghostComponents.GetFirstGhostTypeId();
                if (ghostTypeId < 0)
                    return;
                var typeData = GhostTypeCollection[ghostTypeId];
                int baseOffset = typeData.FirstComponent;

                Entity* backupEntities = PredictionBackupState.GetEntities(state);
                var entities = chunk.GetNativeArray(entityType);

                var predictedGhostComponents = chunk.GetNativeArray(predictedGhostType);

                byte* dataPtr = PredictionBackupState.GetData(state);
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
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
                    if (chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                    {
                        if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                            for (int ent = 0; ent < entities.Length; ++ent)
                            {
                                // If this entity did not predict anything there was no rollback and no need to debug it
                                if (!GhostPredictionSystemGroup.ShouldPredict(tick, predictedGhostComponents[ent]))
                                    continue;
                                if (entities[ent] == backupEntities[ent])
                                {
                                    int errorIndex = GhostComponentIndex[baseOffset + comp].PredictionErrorBaseIndex;

                                    var errors = new UnsafeList<float>(((float*)predictionErrors.GetUnsafePtr()) + errorIndex + ThreadIndex * numPredictionErrors, numPredictionErrors - errorIndex);
                                    GhostComponentCollection[serializerIdx].ReportPredictionErrors.Ptr.Invoke((System.IntPtr)(compData + compSize * ent), (System.IntPtr)(dataPtr + compSize * ent), ref errors);
                                }
                            }
                        }
                        else
                        {
                            //FIXME Buffers need to report error for the size and an aggregate for each element in the buffer
                        }
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

                        var compSize = GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                            ? GhostSystemConstants.DynamicBufferComponentSnapshotSize
                            : GhostComponentCollection[serializerIdx].ComponentSize;
                        var entityIdx = GhostComponentIndex[baseOffset + comp].EntityIndex;

                        for (int ent = 0; ent < chunk.Count; ++ent)
                        {
                            // If this entity did not predict anything there was no rollback and no need to debug it
                            if (!GhostPredictionSystemGroup.ShouldPredict(tick, predictedGhostComponents[ent]))
                                continue;
                            if (entities[ent] != backupEntities[ent])
                                continue;
                            var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                            if (childEntityLookup.TryGetValue(linkedEntityGroup[entityIdx].Value, out var childChunk) &&
                                childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                            {
                                if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                                {
                                    var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                    int errorIndex = GhostComponentIndex[baseOffset + comp].PredictionErrorBaseIndex;
                                    var errors = new UnsafeList<float>(((float*)predictionErrors.GetUnsafePtr()) + errorIndex + ThreadIndex * numPredictionErrors, numPredictionErrors - errorIndex);
                                    GhostComponentCollection[serializerIdx].ReportPredictionErrors.Ptr.Invoke((System.IntPtr)(compData + compSize * childChunk.index), (System.IntPtr)(dataPtr + compSize * ent), ref errors);
                                }
                                else
                                {
                                    //FIXME Buffers need to report error for the size and an aggregate for each element in the buffer
                                }
                            }
                        }

                        dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                    }
                }
            }
        }
    }
#endif
}
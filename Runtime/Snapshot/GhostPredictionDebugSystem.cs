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
    //[UpdateBefore(typeof(GhostPredictionSmoothingSystem))]
    [UpdateBefore(typeof(GhostPredictionHistorySystem))]
    public unsafe class GhostPredictionDebugSystem : SystemBase
    {
        GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
        GhostPredictionHistorySystem m_GhostPredictionHistorySystem;
        GhostCollectionSystem m_GhostCollectionSystem;

        NativeArray<float> m_PredictionErrors;

        EntityQuery m_PredictionQuery;
        EntityQuery m_ChildEntityQuery;
        NativeHashMap<Entity, EntityChunkLookup> m_ChildEntityLookup;
        protected override void OnCreate()
        {
            m_GhostPredictionSystemGroup = World.GetExistingSystem<GhostPredictionSystemGroup>();
            m_GhostPredictionHistorySystem = World.GetExistingSystem<GhostPredictionHistorySystem>();
            m_GhostCollectionSystem = World.GetExistingSystem<GhostCollectionSystem>();

            m_PredictionQuery = GetEntityQuery(ComponentType.ReadOnly<PredictedGhostComponent>(), ComponentType.ReadOnly<GhostComponent>());
            m_ChildEntityLookup = new NativeHashMap<Entity, EntityChunkLookup>(1024, Allocator.Persistent);
            m_ChildEntityQuery = GetEntityQuery(ComponentType.ReadOnly<GhostChildEntityComponent>());
            m_PredictionErrors = new NativeArray<float>(0, Allocator.Persistent);
        }
        protected override void OnDestroy()
        {
            m_ChildEntityLookup.Dispose();
            m_PredictionErrors.Dispose();
        }
        protected override void OnUpdate()
        {
            if (!m_GhostCollectionSystem.m_GhostTypeCollection.IsCreated)
                return;
            if (m_GhostPredictionSystemGroup.PredictingTick != m_GhostPredictionHistorySystem.LastBackupTick)
                return;

            if (m_PredictionErrors.Length != m_GhostCollectionSystem.PredictionErrorCount * JobsUtility.MaxJobThreadCount)
            {
                m_PredictionErrors.Dispose();
                m_PredictionErrors = new NativeArray<float>(m_GhostCollectionSystem.PredictionErrorCount * JobsUtility.MaxJobThreadCount, Allocator.Persistent);
            }
            for (int job = 1; job < JobsUtility.MaxJobThreadCount; ++job)
            {
                for (int i = 0; i < m_GhostCollectionSystem.PredictionErrorCount; ++i)
                {
                    m_PredictionErrors[i] = math.max(m_PredictionErrors[i], m_PredictionErrors[m_GhostCollectionSystem.PredictionErrorCount*job + i]);
                }
            }
            World.GetExistingSystem<GhostStatsCollectionSystem>().AddPredictionErrorStats(m_PredictionErrors.GetSubArray(0, m_GhostCollectionSystem.PredictionErrorCount));
            // Clear
            UnsafeUtility.MemClear(m_PredictionErrors.GetUnsafePtr(), 4 * m_GhostCollectionSystem.PredictionErrorCount * JobsUtility.MaxJobThreadCount);

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

            var debugJob = new PredictionDebugJob
            {
                predictionState = m_GhostPredictionHistorySystem.PredictionState,
                ghostType = GetComponentTypeHandle<GhostComponent>(true),
                predictedGhostType = GetComponentTypeHandle<PredictedGhostComponent>(true),
                entityType = GetEntityTypeHandle(),

                GhostComponentCollection = m_GhostCollectionSystem.m_GhostComponentCollection,
                GhostTypeCollection = m_GhostCollectionSystem.m_GhostTypeCollection,
                GhostComponentIndex = m_GhostCollectionSystem.m_GhostComponentIndex,

                childEntityLookup = m_ChildEntityLookup,
                linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(),
                tick = m_GhostPredictionSystemGroup.PredictingTick,
                translationType = ComponentType.ReadWrite<Translation>(),

                predictionErrors = m_PredictionErrors,
                numPredictionErrors = m_GhostCollectionSystem.PredictionErrorCount
            };

            Dependency = JobHandle.CombineDependencies(Dependency, m_GhostPredictionHistorySystem.PredictionStateWriteJobHandle);

            var listLength = m_GhostCollectionSystem.m_GhostComponentCollection.Length;
            if (listLength <= 32)
            {
                var dynamicListJob = new PredictionDebugJob32 {Job = debugJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else if (listLength <= 64)
            {
                var dynamicListJob = new PredictionDebugJob64 {Job = debugJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else if (listLength <= 128)
            {
                var dynamicListJob = new PredictionDebugJob128 {Job = debugJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
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

            [ReadOnly] public NativeArray<GhostComponentSerializer.State> GhostComponentCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostTypeState> GhostTypeCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostComponentIndex> GhostComponentIndex;

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

                var ghostComponents = chunk.GetNativeArray(ghostType);
                int ghostTypeId = ghostComponents[0].ghostType;
                var typeData = GhostTypeCollection[ghostTypeId];

                var headerSize = PredictionBackupState.GetHeaderSize();
                var entitySize = PredictionBackupState.GetEntitiesSize(chunk.Capacity, out var singleEntitySize);
                int baseOffset = typeData.FirstComponent;

                Entity* backupEntities = PredictionBackupState.GetEntities(state, headerSize);
                var entities = chunk.GetNativeArray(entityType);

                var predictedGhostComponents = chunk.GetNativeArray(predictedGhostType);

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
                        for (int ent = 0; ent < entities.Length; ++ent)
                        {
                            // If this entity did not predict anything there was no rollback and no need to debug it
                            if (!GhostPredictionSystemGroup.ShouldPredict(tick, predictedGhostComponents[ent]))
                                continue;
                            if (entities[ent] == backupEntities[ent])
                            {
                                int errorIndex = GhostComponentIndex[baseOffset + comp].PredictionErrorBaseIndex;

                                var errors = new UnsafeList<float>(((float*)predictionErrors.GetUnsafePtr()) + errorIndex + ThreadIndex * numPredictionErrors, numPredictionErrors - errorIndex);
                                GhostComponentCollection[compIdx].ReportPredictionErrors.Ptr.Invoke((System.IntPtr)(compData + compSize * ent), (System.IntPtr)(dataPtr + compSize * ent), ref errors);
                            }
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
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif
                        if ((GhostComponentCollection[compIdx].SendMask&requiredSendMask) == 0)
                            continue;
                        var compSize = GhostComponentCollection[compIdx].ComponentSize;
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
                                var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafePtr();
                                int errorIndex = GhostComponentIndex[baseOffset + comp].PredictionErrorBaseIndex;
                                var errors = new UnsafeList<float>(((float*)predictionErrors.GetUnsafePtr()) + errorIndex + ThreadIndex * numPredictionErrors, numPredictionErrors - errorIndex);
                                GhostComponentCollection[compIdx].ReportPredictionErrors.Ptr.Invoke((System.IntPtr)(compData + compSize * ent), (System.IntPtr)(dataPtr + compSize * ent), ref errors);
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
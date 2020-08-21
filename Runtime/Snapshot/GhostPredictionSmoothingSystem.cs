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
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostPredictionSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(GhostPredictionHistorySystem))]
    [DisableAutoCreation]
    public unsafe class GhostPredictionSmoothingSystem : SystemBase
    {
        GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
        GhostPredictionHistorySystem m_GhostPredictionHistorySystem;
        GhostCollectionSystem m_GhostCollectionSystem;

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
        }
        protected override void OnDestroy()
        {
            m_ChildEntityLookup.Dispose();
        }
        protected override void OnUpdate()
        {
            if (!m_GhostCollectionSystem.m_GhostTypeCollection.IsCreated)
                return;
            if (m_GhostPredictionSystemGroup.PredictingTick != m_GhostPredictionHistorySystem.LastBackupTick)
                return;

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

            var smoothingJob = new PredictionSmoothingJob
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

                translationType = ComponentType.ReadWrite<Translation>()
            };

            Dependency = JobHandle.CombineDependencies(Dependency, m_GhostPredictionHistorySystem.PredictionStateWriteJobHandle);

            var listLength = m_GhostCollectionSystem.m_GhostComponentCollection.Length;
            if (listLength <= 32)
            {
                var dynamicListJob = new PredictionSmoothingJob32 {Job = smoothingJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else if (listLength <= 64)
            {
                var dynamicListJob = new PredictionSmoothingJob64 {Job = smoothingJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else if (listLength <= 128)
            {
                var dynamicListJob = new PredictionSmoothingJob128 {Job = smoothingJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_PredictionQuery, Dependency);
            }
            else
                throw new System.InvalidOperationException(
                    $"Too many ghost component types present in project, limit is {DynamicTypeList.MaxCapacity} types. This is any struct which has a field marked with GhostField attribute.");

            m_GhostPredictionHistorySystem.AddPredictionStateReader(Dependency);
        }
        [BurstCompile]
        struct PredictionSmoothingJob32 : IJobChunk
        {
            public DynamicTypeList32 List;
            public PredictionSmoothingJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        struct PredictionSmoothingJob64 : IJobChunk
        {
            public DynamicTypeList64 List;
            public PredictionSmoothingJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        struct PredictionSmoothingJob128 : IJobChunk
        {
            public DynamicTypeList128 List;
            public PredictionSmoothingJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        struct PredictionSmoothingJob
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
                                SmoothComponent(compData + compSize * ent, dataPtr + compSize * ent, GhostComponentCollection[compIdx].ComponentType);
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
                                SmoothComponent(compData + compSize * ent, dataPtr + compSize * ent, GhostComponentCollection[compIdx].ComponentType);
                            }
                        }

                        dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                    }
                }
            }
            void SmoothComponent(void* currentData, void* previousData, ComponentType componentType)
            {
                // FIXME: apply smoothing between the current value and the previous predicted value
                // FIXME: placeholder implementation hardcoded to only do something for translation
                if (componentType == translationType)
                {
                    ref var trans = ref UnsafeUtility.AsRef<Translation>(currentData);
                    ref var backup = ref UnsafeUtility.AsRef<Translation>(previousData);

                    var dist = math.distancesq(trans.Value, backup.Value);
                    if (dist < 1)
                    {
                        trans.Value = math.lerp(backup.Value, trans.Value, 1.0f - dist * 0.9f);
                    }
                }
            }
        }
    }
}
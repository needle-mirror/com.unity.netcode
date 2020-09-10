using Unity.Entities;
using Unity.Collections;
using Unity.Burst;
using Unity.Networking.Transport.Utilities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    public unsafe class GhostUpdateSystem : SystemBase
    {
        private EntityQuery m_ChildEntityQuery;
        private NativeHashMap<Entity, EntityChunkLookup> m_ChildEntityLookup;

        // There will be burst/IL problems with using generic job structs, so we're
        // laying out each job size type here manually
        [BurstCompile]
        struct GhostUpdateJob32 : IJobChunk
        {
            public DynamicTypeList32 List;
            public UpdateJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        struct GhostUpdateJob64 : IJobChunk
        {
            public DynamicTypeList64 List;
            public UpdateJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        struct GhostUpdateJob128 : IJobChunk
        {
            public DynamicTypeList128 List;
            public UpdateJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }

        [BurstCompile]
        struct UpdateJob
        {
            [ReadOnly] public NativeArray<GhostComponentSerializer.State> GhostComponentCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostTypeState> GhostTypeCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostComponentIndex> GhostComponentIndex;

            [ReadOnly] public NativeHashMap<SpawnedGhost, Entity> GhostMap;
    #if UNITY_EDITOR || DEVELOPMENT_BUILD
            [NativeDisableContainerSafetyRestriction] public NativeArray<uint> minMaxSnapshotTick;
    #endif
    #pragma warning disable 649
            [NativeSetThreadIndex] public int ThreadIndex;
    #pragma warning restore 649
            [ReadOnly] public ComponentTypeHandle<GhostComponent> ghostType;
            [ReadOnly] public ComponentTypeHandle<SnapshotData> ghostSnapshotDataType;
            public BufferTypeHandle<SnapshotDataBuffer> ghostSnapshotDataBufferType;

            public uint interpolatedTargetTick;
            public float interpolatedTargetTickFraction;
            public uint predictedTargetTick;

            [NativeDisableParallelForRestriction] public NativeArray<uint> minPredictedTick;
            public ComponentTypeHandle<PredictedGhostComponent> predictedGhostComponentType;
            public uint lastPredictedTick;
            public uint lastInterpolatedTick;

            [ReadOnly] public NativeHashMap<Entity, EntityChunkLookup> childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            public uint predictionStateBackupTick;
            [ReadOnly] public NativeHashMap<ArchetypeChunk, System.IntPtr> predictionStateBackup;
            [ReadOnly] public EntityTypeHandle entityType;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                bool predicted = chunk.Has(predictedGhostComponentType);
                uint targetTick = predicted ? predictedTargetTick : interpolatedTargetTick;
                float targetTickFraction = predicted ? 1.0f : interpolatedTargetTickFraction;

                var deserializerState = new GhostDeserializerState
                {
                    GhostMap = GhostMap
                };
                var ghostComponents = chunk.GetNativeArray(ghostType);
                int ghostTypeId = ghostComponents[0].ghostType;
                var typeData = GhostTypeCollection[ghostTypeId];
                var ghostSnapshotDataArray = chunk.GetNativeArray(ghostSnapshotDataType);
                var ghostSnapshotDataBufferArray = chunk.GetBufferAccessor(ghostSnapshotDataBufferType);

                int changeMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                int snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                int snapshotDataAtTickSize = UnsafeUtility.SizeOf<SnapshotData.DataAtTick>();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                var minMaxOffset = ThreadIndex * (JobsUtility.CacheLineSize/4);
#endif
                var dataAtTick = new NativeArray<SnapshotData.DataAtTick>(ghostComponents.Length, Allocator.Temp);
                var entityRange = new NativeList<int2>(ghostComponents.Length, Allocator.Temp);
                int2 nextRange = default;
                var predictedGhostComponentArray = chunk.GetNativeArray(predictedGhostComponentType);
                bool canBeStatic = typeData.StaticOptimization;
                // Find the ranges of entities which have data to apply, store the data to apply in an array while doing so
                for (int ent = 0; ent < ghostComponents.Length; ++ent)
                {
                    var snapshotDataBuffer = ghostSnapshotDataBufferArray[ent];
                    var ghostSnapshotData = ghostSnapshotDataArray[ent];
                    var latestTick = ghostSnapshotData.GetLatestTick(snapshotDataBuffer);
                    bool isStatic = canBeStatic && ghostSnapshotData.WasLatestTickZeroChange(snapshotDataBuffer, changeMaskUints);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (latestTick != 0 && !isStatic)
                    {
                        if (minMaxSnapshotTick[minMaxOffset] == 0 || SequenceHelpers.IsNewer(minMaxSnapshotTick[minMaxOffset], latestTick))
                            minMaxSnapshotTick[minMaxOffset] = latestTick;
                        if (minMaxSnapshotTick[minMaxOffset + 1] == 0 || SequenceHelpers.IsNewer(latestTick, minMaxSnapshotTick[minMaxOffset + 1]))
                            minMaxSnapshotTick[minMaxOffset + 1] = latestTick;
                    }
#endif
                    if (ghostSnapshotData.GetDataAtTick(targetTick, targetTickFraction, snapshotDataBuffer, out var data))
                    {
                        if (predicted)
                        {
                            // TODO: is this the right way to handle this?
                            data.InterpolationFactor = 0;
                            var snapshotTick = *(uint*)data.SnapshotBefore;
                            var predictedData = predictedGhostComponentArray[ent];
                            // We want to contiue prediction from the last full tick we predicted last time
                            var predictionStartTick = predictionStateBackupTick;
                            // If there is no history, try to use the tick where we left off last time, will only be a valid tick if we ended with a full prediction tick as opposed to a fractional one
                            if (predictionStartTick == 0)
                                predictionStartTick = lastPredictedTick;
                            // If we do not have a backup or we got more data since last time we run from the tick we have snapshot data for
                            if (predictionStartTick == 0 || predictedData.AppliedTick != snapshotTick)
                                predictionStartTick = snapshotTick;
                            // If we have newer or equally new data in the
                            else if (!SequenceHelpers.IsNewer(predictionStartTick, snapshotTick))
                                predictionStartTick = snapshotTick;

                            // If we want to continue prediction, and this is not the currently applied prediction state we must restore the state from the backup
                            if (predictionStartTick != snapshotTick && predictionStartTick != lastPredictedTick)
                            {
                                // If we cannot restore the backup and continue prediction we roll back and resimulate
                                if (!RestorePredictionBackup(chunk, ent, typeData, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength))
                                    predictionStartTick = snapshotTick;
                            }


                            if (minPredictedTick[ThreadIndex] == 0 || SequenceHelpers.IsNewer(minPredictedTick[ThreadIndex], predictionStartTick))
                                minPredictedTick[ThreadIndex] = predictionStartTick;

                            if (predictionStartTick != snapshotTick)
                            {
                                if (nextRange.y != 0)
                                    entityRange.Add(nextRange);
                                nextRange = default;
                            }
                            else
                            {
                                predictedData.AppliedTick = snapshotTick;
                                if (nextRange.y == 0)
                                    nextRange.x = ent;
                                nextRange.y = ent+1;
                            }
                            predictedData.PredictionStartTick = predictionStartTick;
                            predictedGhostComponentArray[ent] = predictedData;
                        }
                        else
                        {
                            // If this snapshot is static, and the data for the latest tick was applied during last interpolation update, we can just skip copying data
                            if (isStatic && !SequenceHelpers.IsNewer(latestTick, lastInterpolatedTick))
                            {
                                if (nextRange.y != 0)
                                    entityRange.Add(nextRange);
                                nextRange = default;
                            }
                            else
                            {
                                if (nextRange.y == 0)
                                    nextRange.x = ent;
                                nextRange.y = ent+1;
                            }
                        }
                        dataAtTick[ent] = data;
                    }
                    else if (nextRange.y != 0)
                    {
                        entityRange.Add(nextRange);
                        nextRange = default;
                        if (predicted)
                        {
                            var predictionStartTick = predictionStateBackupTick;
                            if (predictionStateBackupTick != lastPredictedTick)
                            {
                                // Try to back up the thing
                                if (!RestorePredictionBackup(chunk, ent, typeData, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength))
                                    predictionStartTick = 0;
                            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if (predictionStartTick == 0)
                                throw new System.InvalidOperationException("Trying to predict a ghost without having a state to roll back to");
#endif
                        }
                    }
                }
                if (nextRange.y != 0)
                    entityRange.Add(nextRange);

                var requiredSendMask = predicted ? GhostComponentSerializer.SendMask.Predicted : GhostComponentSerializer.SendMask.Interpolated;
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException("Component index out of range");
#endif
                    if (chunk.Has(ghostChunkComponentTypesPtr[compIdx]) &&
                        (GhostComponentCollection[compIdx].SendMask&requiredSendMask) != 0)
                    {
                        var compSize = GhostComponentCollection[compIdx].ComponentSize;
                        var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafePtr();
                        for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                        {
                            var range = entityRange[rangeIdx];
                            var snapshotData = (byte*)dataAtTick.GetUnsafeReadOnlyPtr();
                            snapshotData += snapshotDataAtTickSize * range.x;
                            GhostComponentCollection[compIdx].CopyFromSnapshot.Ptr.Invoke((System.IntPtr)UnsafeUtility.AddressOf(ref deserializerState), (System.IntPtr)snapshotData, snapshotOffset, snapshotDataAtTickSize, (System.IntPtr)(compData + range.x*compSize), compSize, range.y-range.x);
                        }
                    }
                    snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif
                        if ((GhostComponentCollection[compIdx].SendMask&requiredSendMask) != 0)
                        {
                            var compSize = GhostComponentCollection[compIdx].ComponentSize;
                            for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                            {
                                var range = entityRange[rangeIdx];
                                for (int ent = range.x; ent < range.y; ++ent)
                                {
                                    var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                    if (!childEntityLookup.TryGetValue(linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value, out var childChunk))
                                        continue;
                                    if (!childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                                        continue;
                                    var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafePtr();
                                    var snapshotData = (byte*)dataAtTick.GetUnsafeReadOnlyPtr();
                                    snapshotData += snapshotDataAtTickSize * ent;
                                    GhostComponentCollection[compIdx].CopyFromSnapshot.Ptr.Invoke((System.IntPtr)UnsafeUtility.AddressOf(ref deserializerState), (System.IntPtr)snapshotData, snapshotOffset, snapshotDataAtTickSize, (System.IntPtr)(compData + childChunk.index*compSize), compSize, 1);
                                }
                            }
                        }
                        snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
                    }
                }
            }
            bool RestorePredictionBackup(ArchetypeChunk chunk, int ent, in GhostCollectionSystem.GhostTypeState typeData, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                // Try to get the backup state
                if (!predictionStateBackup.TryGetValue(chunk, out var state) ||
                    (*(PredictionBackupState*)state).entityCapacity != chunk.Capacity)
                    return false;

                var headerSize = PredictionBackupState.GetHeaderSize();

                // Verify that the backup is for the correct entity
                Entity* entities = PredictionBackupState.GetEntities(state, headerSize);
                var entity = chunk.GetNativeArray(entityType)[ent];
                if (entity != entities[ent])
                    return false;

                var entitySize = PredictionBackupState.GetEntitiesSize(chunk.Capacity, out var singleEntitySize);
                int baseOffset = typeData.FirstComponent;
                const GhostComponentSerializer.SendMask requiredSendMask = GhostComponentSerializer.SendMask.Predicted;

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
                        GhostComponentCollection[compIdx].RestoreFromBackup.Ptr.Invoke((System.IntPtr)(compData + ent * compSize), (System.IntPtr)(dataPtr + ent * compSize));
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

                        var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                        if (childEntityLookup.TryGetValue(linkedEntityGroup[GhostComponentIndex[baseOffset + comp].EntityIndex].Value, out var childChunk) &&
                            childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                        {
                            var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafePtr();
                            GhostComponentCollection[compIdx].RestoreFromBackup.Ptr.Invoke((System.IntPtr)(compData + childChunk.index * compSize), (System.IntPtr)(dataPtr + ent * compSize));
                        }

                        dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                    }
                }

                return true;
            }
        }
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
        private EntityQuery m_ghostQuery;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private NativeArray<uint> m_ghostSnapshotTickMinMax;
        private GhostStatsCollectionSystem m_GhostStatsCollectionSystem;
#endif
        private GhostReceiveSystem m_GhostReceiveSystem;
        private uint m_LastPredictedTick;
        private uint m_LastInterpolatedTick;

        private GhostCollectionSystem m_GhostCollectionSystem;
        private GhostPredictionHistorySystem m_GhostPredictionHistorySystem;
        protected override void OnCreate()
        {
            m_GhostCollectionSystem = World.GetOrCreateSystem<GhostCollectionSystem>();
            m_GhostPredictionHistorySystem = World.GetOrCreateSystem<GhostPredictionHistorySystem>();

            m_GhostReceiveSystem = World.GetOrCreateSystem<GhostReceiveSystem>();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_ghostSnapshotTickMinMax = new NativeArray<uint>(JobsUtility.MaxJobThreadCount * JobsUtility.CacheLineSize/4, Allocator.Persistent);
            m_GhostStatsCollectionSystem = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif
            m_ClientSimulationSystemGroup = World.GetOrCreateSystem<ClientSimulationSystemGroup>();
            m_GhostPredictionSystemGroup = World.GetOrCreateSystem<GhostPredictionSystemGroup>();
            m_ghostQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new []{
                    ComponentType.ReadWrite<SnapshotDataBuffer>(),
                    ComponentType.ReadOnly<SnapshotData>(),
                    ComponentType.ReadOnly<GhostComponent>(),
                },
            });

            m_ChildEntityQuery = GetEntityQuery(ComponentType.ReadOnly<GhostChildEntityComponent>());
            m_ChildEntityLookup = new NativeHashMap<Entity, EntityChunkLookup>(1024, Allocator.Persistent);

            RequireSingletonForUpdate<NetworkStreamInGame>();
        }
        protected override void OnDestroy()
        {
            m_ChildEntityLookup.Dispose();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_ghostSnapshotTickMinMax.Dispose();
#endif
        }
        protected override void OnUpdate()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Gather the min/max age stats
            var intsPerCacheLine = JobsUtility.CacheLineSize/4;
            for (int i = 1; i < JobsUtility.MaxJobThreadCount; ++i)
            {
                if (m_ghostSnapshotTickMinMax[intsPerCacheLine*i] != 0 &&
                    (m_ghostSnapshotTickMinMax[0] == 0 ||
                    SequenceHelpers.IsNewer(m_ghostSnapshotTickMinMax[0], m_ghostSnapshotTickMinMax[intsPerCacheLine*i])))
                    m_ghostSnapshotTickMinMax[0] = m_ghostSnapshotTickMinMax[intsPerCacheLine*i];
                if (m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1] != 0 &&
                    (m_ghostSnapshotTickMinMax[1] == 0 ||
                    SequenceHelpers.IsNewer(m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1], m_ghostSnapshotTickMinMax[1])))
                    m_ghostSnapshotTickMinMax[1] = m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1];
                m_ghostSnapshotTickMinMax[intsPerCacheLine*i] = 0;
                m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1] = 0;
            }
            // Pass the min and max to stats collection
            m_GhostStatsCollectionSystem.SetSnapshotTick(m_ghostSnapshotTickMinMax[0], m_ghostSnapshotTickMinMax[1]);
            m_ghostSnapshotTickMinMax[0] = 0;
            m_ghostSnapshotTickMinMax[1] = 0;
#endif

            if (!m_GhostCollectionSystem.m_GhostTypeCollection.IsCreated)
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
            var updateJob = new UpdateJob
            {
                GhostComponentCollection = m_GhostCollectionSystem.m_GhostComponentCollection,
                GhostTypeCollection = m_GhostCollectionSystem.m_GhostTypeCollection,
                GhostComponentIndex = m_GhostCollectionSystem.m_GhostComponentIndex,

                GhostMap = m_GhostReceiveSystem.SpawnedGhostEntityMap,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                minMaxSnapshotTick = m_ghostSnapshotTickMinMax,
#endif

                interpolatedTargetTick = m_ClientSimulationSystemGroup.InterpolationTick,
                interpolatedTargetTickFraction = m_ClientSimulationSystemGroup.InterpolationTickFraction,

                predictedTargetTick = m_ClientSimulationSystemGroup.ServerTick,
                minPredictedTick = m_GhostPredictionSystemGroup.OldestPredictedTick,
                predictedGhostComponentType = GetComponentTypeHandle<PredictedGhostComponent>(),
                lastPredictedTick = m_LastPredictedTick,
                lastInterpolatedTick = m_LastInterpolatedTick,

                ghostType = GetComponentTypeHandle<GhostComponent>(true),
                ghostSnapshotDataType = GetComponentTypeHandle<SnapshotData>(true),
                ghostSnapshotDataBufferType = GetBufferTypeHandle<SnapshotDataBuffer>(), // FIXME: can't be read-only because we need GetUnsafePtr
                childEntityLookup = m_ChildEntityLookup,
                linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(true),

                predictionStateBackupTick = m_GhostPredictionHistorySystem.LastBackupTick,
                predictionStateBackup = m_GhostPredictionHistorySystem.PredictionState,
                entityType = GetEntityTypeHandle(),
            };
            Dependency = JobHandle.CombineDependencies(Dependency, m_GhostPredictionHistorySystem.PredictionStateWriteJobHandle, m_GhostReceiveSystem.LastGhostMapWriter);
            var listLength = m_GhostCollectionSystem.m_GhostComponentCollection.Length;
            if (listLength <= 32)
            {
                var dynamicListJob = new GhostUpdateJob32 {Job = updateJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, false, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_ghostQuery, Dependency);
            }
            else if (listLength <= 64)
            {
                var dynamicListJob = new GhostUpdateJob64 {Job = updateJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, false, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_ghostQuery, Dependency);
            }
            else if (listLength <= 128)
            {
                var dynamicListJob = new GhostUpdateJob128 {Job = updateJob};
                DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, false, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_ghostQuery, Dependency);
            }
            else
                throw new System.InvalidOperationException(
                    $"Too many ghost component types present in project, limit is {DynamicTypeList.MaxCapacity} types. This is any struct which has a field marked with GhostField attribute.");
            m_LastPredictedTick = m_ClientSimulationSystemGroup.ServerTick;
            if (m_ClientSimulationSystemGroup.ServerTickFraction < 1)
                m_LastPredictedTick = 0;

            // If the interpolation target for this frame was received we can update which the latest fully applied interpolation tick is
            var ack = GetSingleton<NetworkSnapshotAckComponent>();
            if (!SequenceHelpers.IsNewer(m_ClientSimulationSystemGroup.InterpolationTick, ack.LastReceivedSnapshotByLocal))
            {
                m_LastInterpolatedTick = m_ClientSimulationSystemGroup.InterpolationTick;
                // Make sure it is the last full interpolated tick. It is only used to see if a static ghost already has the latest state applied
                if (m_ClientSimulationSystemGroup.InterpolationTickFraction < 1)
                    --m_LastInterpolatedTick;
            }

            m_GhostReceiveSystem.LastGhostMapWriter = Dependency;
            m_GhostPredictionSystemGroup.AddPredictedTickWriter(Dependency);
            m_GhostPredictionHistorySystem.AddPredictionStateReader(Dependency);
        }
    }
}

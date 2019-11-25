using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using Unity.NetCode;
#region __GHOST_USING_STATEMENT__
using __GHOST_USING__;
#endregion

#region __END_HEADER__
#endregion
[UpdateInGroup(typeof(GhostUpdateSystemGroup))]
public class __GHOST_NAME__GhostUpdateSystem : JobComponentSystem
{
    [BurstCompile]
    struct UpdateInterpolatedJob : IJobChunk
    {
        [ReadOnly] public NativeHashMap<int, GhostEntity> GhostMap;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [NativeDisableContainerSafetyRestriction] public NativeArray<uint> minMaxSnapshotTick;
#pragma warning disable 649
        [NativeSetThreadIndex]
        public int ThreadIndex;
#pragma warning restore 649
#endif
        [ReadOnly] public ArchetypeChunkBufferType<__GHOST_NAME__SnapshotData> ghostSnapshotDataType;
        [ReadOnly] public ArchetypeChunkEntityType ghostEntityType;
        #region __GHOST_INTERPOLATED_COMPONENT_REF__
        public ArchetypeChunkComponentType<__GHOST_COMPONENT_TYPE__> ghost__GHOST_COMPONENT_TYPE_NAME__Type;
        #endregion
        #region __GHOST_INTERPOLATED_BUFFER_REF__
        [ReadOnly] public ArchetypeChunkBufferType<__GHOST_COMPONENT_TYPE__> ghost__GHOST_COMPONENT_TYPE_NAME__Type;
        #endregion
        #region __GHOST_INTERPOLATED_COMPONENT_CHILD_REF__
        [NativeDisableParallelForRestriction] public ComponentDataFromEntity<__GHOST_COMPONENT_TYPE__> ghost__GHOST_COMPONENT_FROM_ENTITY_NAME__FromEntity;
        #endregion

        public uint targetTick;
        public float targetTickFraction;
        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var deserializerState = new GhostDeserializerState
            {
                GhostMap = GhostMap
            };
            var ghostEntityArray = chunk.GetNativeArray(ghostEntityType);
            var ghostSnapshotDataArray = chunk.GetBufferAccessor(ghostSnapshotDataType);
            #region __GHOST_INTERPOLATED_COMPONENT_ARRAY__
            var ghost__GHOST_COMPONENT_TYPE_NAME__Array = chunk.GetNativeArray(ghost__GHOST_COMPONENT_TYPE_NAME__Type);
            #endregion
            #region __GHOST_INTERPOLATED_BUFFER_ARRAY__
            var ghost__GHOST_COMPONENT_TYPE_NAME__Array = chunk.GetBufferAccessor(ghost__GHOST_COMPONENT_TYPE_NAME__Type);
            #endregion
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var minMaxOffset = ThreadIndex * (JobsUtility.CacheLineSize/4);
#endif
            for (int entityIndex = 0; entityIndex < ghostEntityArray.Length; ++entityIndex)
            {
                var snapshot = ghostSnapshotDataArray[entityIndex];
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                var latestTick = snapshot.GetLatestTick();
                if (latestTick != 0)
                {
                    if (minMaxSnapshotTick[minMaxOffset] == 0 || SequenceHelpers.IsNewer(minMaxSnapshotTick[minMaxOffset], latestTick))
                        minMaxSnapshotTick[minMaxOffset] = latestTick;
                    if (minMaxSnapshotTick[minMaxOffset + 1] == 0 || SequenceHelpers.IsNewer(latestTick, minMaxSnapshotTick[minMaxOffset + 1]))
                        minMaxSnapshotTick[minMaxOffset + 1] = latestTick;
                }
#endif
                __GHOST_NAME__SnapshotData snapshotData;
                snapshot.GetDataAtTick(targetTick, targetTickFraction, out snapshotData);

                #region __GHOST_INTERPOLATED_BEGIN_ASSIGN__
                var ghost__GHOST_COMPONENT_TYPE_NAME__ = ghost__GHOST_COMPONENT_TYPE_NAME__Array[entityIndex];
                #endregion
                #region __GHOST_INTERPOLATED_BEGIN_ASSIGN_CHILD__
                var ghost__GHOST_COMPONENT_TYPE_NAME__ = ghost__GHOST_COMPONENT_FROM_ENTITY_NAME__FromEntity[ghostLinkedEntityGroupArray[entityIndex][__GHOST_ENTITY_INDEX__].Value];
                #endregion
                #region __GHOST_INTERPOLATED_ASSIGN__
                ghost__GHOST_COMPONENT_TYPE_NAME__.__GHOST_FIELD_NAME__ = snapshotData.Get__GHOST_COMPONENT_TYPE_NAME____GHOST_FIELD_NAME__(deserializerState);
                #endregion
                #region __GHOST_INTERPOLATED_END_ASSIGN_CHILD__
                ghost__GHOST_COMPONENT_FROM_ENTITY_NAME__FromEntity[ghostLinkedEntityGroupArray[entityIndex][__GHOST_ENTITY_INDEX__].Value] = ghost__GHOST_COMPONENT_TYPE_NAME__;
                #endregion
                #region __GHOST_INTERPOLATED_END_ASSIGN__
                ghost__GHOST_COMPONENT_TYPE_NAME__Array[entityIndex] = ghost__GHOST_COMPONENT_TYPE_NAME__;
                #endregion
            }
        }
    }
    [BurstCompile]
    struct UpdatePredictedJob : IJobChunk
    {
        [ReadOnly] public NativeHashMap<int, GhostEntity> GhostMap;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [NativeDisableContainerSafetyRestriction] public NativeArray<uint> minMaxSnapshotTick;
#endif
#pragma warning disable 649
        [NativeSetThreadIndex]
        public int ThreadIndex;
#pragma warning restore 649
        [NativeDisableParallelForRestriction] public NativeArray<uint> minPredictedTick;
        [ReadOnly] public ArchetypeChunkBufferType<__GHOST_NAME__SnapshotData> ghostSnapshotDataType;
        [ReadOnly] public ArchetypeChunkEntityType ghostEntityType;
        public ArchetypeChunkComponentType<PredictedGhostComponent> predictedGhostComponentType;
        #region __GHOST_PREDICTED_COMPONENT_REF__
        public ArchetypeChunkComponentType<__GHOST_COMPONENT_TYPE__> ghost__GHOST_COMPONENT_TYPE_NAME__Type;
        #endregion
        #region __GHOST_PREDICTED_BUFFER_REF__
        [ReadOnly] public ArchetypeChunkBufferType<__GHOST_COMPONENT_TYPE__> ghost__GHOST_COMPONENT_TYPE_NAME__Type;
        #endregion
        #region __GHOST_PREDICTED_COMPONENT_CHILD_REF__
        [NativeDisableParallelForRestriction] public ComponentDataFromEntity<__GHOST_COMPONENT_TYPE__> ghost__GHOST_COMPONENT_FROM_ENTITY_NAME__FromEntity;
        #endregion
        public uint targetTick;
        public uint lastPredictedTick;
        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var deserializerState = new GhostDeserializerState
            {
                GhostMap = GhostMap
            };
            var ghostEntityArray = chunk.GetNativeArray(ghostEntityType);
            var ghostSnapshotDataArray = chunk.GetBufferAccessor(ghostSnapshotDataType);
            var predictedGhostComponentArray = chunk.GetNativeArray(predictedGhostComponentType);
            #region __GHOST_PREDICTED_COMPONENT_ARRAY__
            var ghost__GHOST_COMPONENT_TYPE_NAME__Array = chunk.GetNativeArray(ghost__GHOST_COMPONENT_TYPE_NAME__Type);
            #endregion
            #region __GHOST_PREDICTED_BUFFER_ARRAY__
            var ghost__GHOST_COMPONENT_TYPE_NAME__Array = chunk.GetBufferAccessor(ghost__GHOST_COMPONENT_TYPE_NAME__Type);
            #endregion
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var minMaxOffset = ThreadIndex * (JobsUtility.CacheLineSize/4);
#endif
            for (int entityIndex = 0; entityIndex < ghostEntityArray.Length; ++entityIndex)
            {
                var snapshot = ghostSnapshotDataArray[entityIndex];
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                var latestTick = snapshot.GetLatestTick();
                if (latestTick != 0)
                {
                    if (minMaxSnapshotTick[minMaxOffset] == 0 || SequenceHelpers.IsNewer(minMaxSnapshotTick[minMaxOffset], latestTick))
                        minMaxSnapshotTick[minMaxOffset] = latestTick;
                    if (minMaxSnapshotTick[minMaxOffset + 1] == 0 || SequenceHelpers.IsNewer(latestTick, minMaxSnapshotTick[minMaxOffset + 1]))
                        minMaxSnapshotTick[minMaxOffset + 1] = latestTick;
                }
#endif
                __GHOST_NAME__SnapshotData snapshotData;
                snapshot.GetDataAtTick(targetTick, out snapshotData);

                var predictedData = predictedGhostComponentArray[entityIndex];
                var lastPredictedTickInst = lastPredictedTick;
                if (lastPredictedTickInst == 0 || predictedData.AppliedTick != snapshotData.Tick)
                    lastPredictedTickInst = snapshotData.Tick;
                else if (!SequenceHelpers.IsNewer(lastPredictedTickInst, snapshotData.Tick))
                    lastPredictedTickInst = snapshotData.Tick;
                if (minPredictedTick[ThreadIndex] == 0 || SequenceHelpers.IsNewer(minPredictedTick[ThreadIndex], lastPredictedTickInst))
                    minPredictedTick[ThreadIndex] = lastPredictedTickInst;
                predictedGhostComponentArray[entityIndex] = new PredictedGhostComponent{AppliedTick = snapshotData.Tick, PredictionStartTick = lastPredictedTickInst};
                if (lastPredictedTickInst != snapshotData.Tick)
                    continue;

                #region __GHOST_PREDICTED_BEGIN_ASSIGN__
                var ghost__GHOST_COMPONENT_TYPE_NAME__ = ghost__GHOST_COMPONENT_TYPE_NAME__Array[entityIndex];
                #endregion
                #region __GHOST_PREDICTED_BEGIN_ASSIGN_CHILD__
                var ghost__GHOST_COMPONENT_TYPE_NAME__ = ghost__GHOST_COMPONENT_FROM_ENTITY_NAME__FromEntity[ghostLinkedEntityGroupArray[entityIndex][__GHOST_ENTITY_INDEX__].Value];
                #endregion
                #region __GHOST_PREDICTED_ASSIGN__
                ghost__GHOST_COMPONENT_TYPE_NAME__.__GHOST_FIELD_NAME__ = snapshotData.Get__GHOST_COMPONENT_TYPE_NAME____GHOST_FIELD_NAME__(deserializerState);
                #endregion
                #region __GHOST_PREDICTED_END_ASSIGN_CHILD__
                ghost__GHOST_COMPONENT_FROM_ENTITY_NAME__FromEntity[ghostLinkedEntityGroupArray[entityIndex][__GHOST_ENTITY_INDEX__].Value] = ghost__GHOST_COMPONENT_TYPE_NAME__;
                #endregion
                #region __GHOST_PREDICTED_END_ASSIGN__
                ghost__GHOST_COMPONENT_TYPE_NAME__Array[entityIndex] = ghost__GHOST_COMPONENT_TYPE_NAME__;
                #endregion
            }
        }
    }
    private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
    private GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
    private EntityQuery m_interpolatedQuery;
    private EntityQuery m_predictedQuery;
    private NativeHashMap<int, GhostEntity> m_ghostEntityMap;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private NativeArray<uint> m_ghostMinMaxSnapshotTick;
#endif
    private GhostUpdateSystemGroup m_GhostUpdateSystemGroup;
    private uint m_LastPredictedTick;
    protected override void OnCreate()
    {
        m_GhostUpdateSystemGroup = World.GetOrCreateSystem<GhostUpdateSystemGroup>();
        m_ghostEntityMap = m_GhostUpdateSystemGroup.GhostEntityMap;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        m_ghostMinMaxSnapshotTick = m_GhostUpdateSystemGroup.GhostSnapshotTickMinMax;
#endif
        m_ClientSimulationSystemGroup = World.GetOrCreateSystem<ClientSimulationSystemGroup>();
        m_GhostPredictionSystemGroup = World.GetOrCreateSystem<GhostPredictionSystemGroup>();
        m_interpolatedQuery = GetEntityQuery(new EntityQueryDesc
        {
            All = new []{
                ComponentType.ReadWrite<__GHOST_NAME__SnapshotData>(),
                ComponentType.ReadOnly<GhostComponent>(),
                #region __GHOST_INTERPOLATED_COMPONENT_TYPE__
                ComponentType.ReadWrite<__GHOST_COMPONENT_TYPE__>(),
                #endregion
                #region __GHOST_INTERPOLATED_READ_ONLY_COMPONENT_TYPE__
                ComponentType.ReadOnly<__GHOST_COMPONENT_TYPE__>(),
                #endregion
            },
            None = new []{ComponentType.ReadWrite<PredictedGhostComponent>()}
        });
        m_predictedQuery = GetEntityQuery(new EntityQueryDesc
        {
            All = new []{
                ComponentType.ReadOnly<__GHOST_NAME__SnapshotData>(),
                ComponentType.ReadOnly<GhostComponent>(),
                ComponentType.ReadOnly<PredictedGhostComponent>(),
                #region __GHOST_PREDICTED_COMPONENT_TYPE__
                ComponentType.ReadWrite<__GHOST_COMPONENT_TYPE__>(),
                #endregion
                #region __GHOST_PREDICTED_READ_ONLY_COMPONENT_TYPE__
                ComponentType.ReadOnly<__GHOST_COMPONENT_TYPE__>(),
                #endregion
            }
        });
        RequireForUpdate(GetEntityQuery(ComponentType.ReadWrite<__GHOST_NAME__SnapshotData>(),
            ComponentType.ReadOnly<GhostComponent>()));
    }
    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        if (!m_predictedQuery.IsEmptyIgnoreFilter)
        {
            var updatePredictedJob = new UpdatePredictedJob
            {
                GhostMap = m_ghostEntityMap,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                minMaxSnapshotTick = m_ghostMinMaxSnapshotTick,
#endif
                minPredictedTick = m_GhostPredictionSystemGroup.OldestPredictedTick,
                ghostSnapshotDataType = GetArchetypeChunkBufferType<__GHOST_NAME__SnapshotData>(true),
                ghostEntityType = GetArchetypeChunkEntityType(),
                predictedGhostComponentType = GetArchetypeChunkComponentType<PredictedGhostComponent>(),
                #region __GHOST_PREDICTED_ASSIGN_COMPONENT_REF__
                ghost__GHOST_COMPONENT_TYPE_NAME__Type = GetArchetypeChunkComponentType<__GHOST_COMPONENT_TYPE__>(),
                #endregion
                #region __GHOST_PREDICTED_ASSIGN_BUFFER_REF__
                ghost__GHOST_COMPONENT_TYPE_NAME__Type = GetArchetypeChunkBufferType<__GHOST_COMPONENT_TYPE__>(true),
                #endregion
                #region __GHOST_PREDICTED_ASSIGN_COMPONENT_CHILD_REF__
                ghost__GHOST_COMPONENT_FROM_ENTITY_NAME__FromEntity = GetComponentDataFromEntity<__GHOST_COMPONENT_TYPE__>(),
                #endregion

                targetTick = m_ClientSimulationSystemGroup.ServerTick,
                lastPredictedTick = m_LastPredictedTick
            };
            m_LastPredictedTick = m_ClientSimulationSystemGroup.ServerTick;
            if (m_ClientSimulationSystemGroup.ServerTickFraction < 1)
                m_LastPredictedTick = 0;
            inputDeps = updatePredictedJob.Schedule(m_predictedQuery, JobHandle.CombineDependencies(inputDeps, m_GhostUpdateSystemGroup.LastGhostMapWriter));
            m_GhostPredictionSystemGroup.AddPredictedTickWriter(inputDeps);
        }
        if (!m_interpolatedQuery.IsEmptyIgnoreFilter)
        {
            var updateInterpolatedJob = new UpdateInterpolatedJob
            {
                GhostMap = m_ghostEntityMap,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                minMaxSnapshotTick = m_ghostMinMaxSnapshotTick,
#endif
                ghostSnapshotDataType = GetArchetypeChunkBufferType<__GHOST_NAME__SnapshotData>(true),
                ghostEntityType = GetArchetypeChunkEntityType(),
                #region __GHOST_INTERPOLATED_ASSIGN_COMPONENT_REF__
                ghost__GHOST_COMPONENT_TYPE_NAME__Type = GetArchetypeChunkComponentType<__GHOST_COMPONENT_TYPE__>(),
                #endregion
                #region __GHOST_INTERPOLATED_ASSIGN_BUFFER_REF__
                ghost__GHOST_COMPONENT_TYPE_NAME__Type = GetArchetypeChunkBufferType<__GHOST_COMPONENT_TYPE__>(true),
                #endregion
                #region __GHOST_INTERPOLATED_ASSIGN_COMPONENT_CHILD_REF__
                ghost__GHOST_COMPONENT_FROM_ENTITY_NAME__FromEntity = GetComponentDataFromEntity<__GHOST_COMPONENT_TYPE__>(),
                #endregion
                targetTick = m_ClientSimulationSystemGroup.InterpolationTick,
                targetTickFraction = m_ClientSimulationSystemGroup.InterpolationTickFraction
            };
            inputDeps = updateInterpolatedJob.Schedule(m_interpolatedQuery, JobHandle.CombineDependencies(inputDeps, m_GhostUpdateSystemGroup.LastGhostMapWriter));
        }
        return inputDeps;
    }
}
public partial class __GHOST_NAME__GhostSpawnSystem : DefaultGhostSpawnSystem<__GHOST_NAME__SnapshotData>
{
    #region __GHOST_PREDICTED_DEFAULT__
    struct SetPredictedDefault : IJobParallelFor
    {
        public NativeArray<int> predictionMask;
        public void Execute(int index)
        {
            predictionMask[index] = 1;
        }
    }
    protected override JobHandle SetPredictedGhostDefaults(NativeArray<__GHOST_NAME__SnapshotData> snapshots, NativeArray<int> predictionMask, JobHandle inputDeps)
    {
        var job = new SetPredictedDefault
        {
            predictionMask = predictionMask,
        };
        return job.Schedule(predictionMask.Length, 8, inputDeps);
    }
    #endregion
    #region __GHOST_OWNER_PREDICTED_DEFAULT__
    struct SetPredictedDefault : IJobParallelFor
    {
        [ReadOnly] public NativeArray<__GHOST_NAME__SnapshotData> snapshots;
        public NativeArray<int> predictionMask;
        [ReadOnly][DeallocateOnJobCompletion] public NativeArray<NetworkIdComponent> localPlayerId;
        public void Execute(int index)
        {
            if (localPlayerId.Length == 1 && snapshots[index].Get__GHOST_OWNER_FIELD__() == localPlayerId[0].Value)
                predictionMask[index] = 1;
        }
    }
    protected override JobHandle SetPredictedGhostDefaults(NativeArray<__GHOST_NAME__SnapshotData> snapshots, NativeArray<int> predictionMask, JobHandle inputDeps)
    {
        JobHandle playerHandle;
        var job = new SetPredictedDefault
        {
            snapshots = snapshots,
            predictionMask = predictionMask,
            localPlayerId = m_PlayerGroup.ToComponentDataArray<NetworkIdComponent>(Allocator.TempJob, out playerHandle),
        };
        return job.Schedule(predictionMask.Length, 8, JobHandle.CombineDependencies(playerHandle, inputDeps));
    }
    #endregion
}

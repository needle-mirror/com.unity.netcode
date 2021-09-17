using Unity.Burst;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using System;

namespace Unity.NetCode
{
    public struct PredictedGhostSpawnList : IComponentData
    {}

    /// <summary>
    /// Added to a 'PredictedGhostSpawnList' singleton entity.
    /// Contains a transient list of ghosts that should be pre-spawned.
    /// Expects to be handled during the <see cref="GhostSpawnClassificationSystem"/> step.
    /// InternalBufferCapacity allocated to almost max out chunk memory.
    /// In practice, this capacity just needs to hold the maximum number of client-authored
    /// ghost entities per frame, which is typically in the range 0 - 1.
    /// </summary>
    [InternalBufferCapacity(950)]
    public struct PredictedGhostSpawn : IBufferElementData
    {
        public Entity entity;
        public int ghostType;
        public uint spawnTick;
    }

    [UpdateInGroup(typeof(GhostSpawnSystemGroup))]
    [UpdateAfter(typeof(GhostSpawnSystem))]
    public partial class PredictedGhostSpawnSystem : SystemBase
    {
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private BeginSimulationEntityCommandBufferSystem m_BeginSimulationBarrier;
        private EntityQuery m_GhostInitQuery;
        private uint m_SpawnTick;
        private NativeArray<int> m_ListHasData;

        [BurstCompile]
        struct InitGhostJob : IJobChunk
        {
            public DynamicTypeList DynamicTypeList;

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
            public ComponentDataFromEntity<PredictedGhostComponent> predictedGhostFromEntity;
            [ReadOnly] public ComponentDataFromEntity<GhostTypeComponent> ghostTypeFromEntity;

            public EntityCommandBuffer commandBuffer;
            public uint spawnTick;

            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            [ReadOnly] public StorageInfoFromEntity childEntityLookup;

            public unsafe void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = DynamicTypeList.GetData();
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

                var spawnList = spawnListFromEntity[spawnListEntity];
                var typeData = GhostTypeCollection[ghostType];
                var snapshotSize = typeData.SnapshotSize;
                int changeMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                var snapshotBaseOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);

                var helper = new GhostSerializeHelper
                {
                    serializerState = new GhostSerializerState { GhostFromEntity = ghostFromEntity },
                    ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                    GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton],
                    GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton],
                    childEntityLookup = childEntityLookup,
                    linkedEntityGroupType = linkedEntityGroupType,
                    ghostChunkComponentTypesPtrLen = DynamicTypeList.Length
                };

                var bufferSizes = new NativeArray<int>(chunk.Count, Allocator.Temp);
                var hasBuffers = GhostTypeCollection[ghostType].NumBuffers > 0;
                if (hasBuffers)
                    helper.GatherBufferSize(chunk, 0, typeData, ref bufferSizes);

                for (int i = 0; i < entityList.Length; ++i)
                {
                    var entity = entityList[i];

                    var ghostComponent = ghostFromEntity[entity];
                    ghostComponent.ghostType = ghostType;
                    ghostFromEntity[entity] = ghostComponent;
                    predictedGhostFromEntity[entity] = new PredictedGhostComponent{AppliedTick = spawnTick, PredictionStartTick = spawnTick};
                    // Set initial snapshot data
                    // Get the buffers, fill in snapshot size etc
                    snapshotDataList[i] = new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0};
                    var snapshotDataBuffer = snapshotDataBufferList[i];
                    snapshotDataBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    var snapshotPtr = (byte*)snapshotDataBuffer.GetUnsafePtr();
                    UnsafeUtility.MemClear(snapshotPtr, snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    *(uint*)snapshotPtr = spawnTick;

                    helper.snapshotOffset = snapshotBaseOffset;
                    helper.snapshotPtr = snapshotPtr;
                    helper.snapshotCapacity = snapshotSize;
                    if (hasBuffers)
                    {
                        var dynamicDataCapacity= SnapshotDynamicBuffersHelper.CalculateBufferCapacity((uint)bufferSizes[i],
                            out var dynamicSnapshotSize);
                        var snapshotDynamicDataBuffer = snapshotDynamicDataBufferList[i];
                        var headerSize = SnapshotDynamicBuffersHelper.GetHeaderSize();
                        snapshotDynamicDataBuffer.ResizeUninitialized((int)dynamicDataCapacity);

                        helper.snapshotDynamicPtr = (byte*)snapshotDynamicDataBuffer.GetUnsafePtr();
                        helper.dynamicSnapshotDataOffset = (int)headerSize;
                        //add the header size so that the boundary check that into the consideration the header size
                        helper.dynamicSnapshotCapacity = (int)(dynamicSnapshotSize + headerSize);
                    }
                    helper.CopyEntityToSnapshot(chunk, i, typeData, GhostSerializeHelper.ClearOption.DontClear);

                    // Remove request component
                    commandBuffer.RemoveComponent<PredictedGhostSpawnRequestComponent>(entity);
                    // Add to list of predictive spawn component - maybe use a singleton for this so spawn systems can just access it too
                    spawnList.Add(new PredictedGhostSpawn{entity = entity, ghostType = ghostType, spawnTick = spawnTick});
                }

                bufferSizes.Dispose();
            }
        }
        protected override void OnCreate()
        {
            var ent = EntityManager.CreateEntity();
            EntityManager.SetName(ent, "PredictedGhostSpawnList");
            EntityManager.AddComponentData(ent, default(PredictedGhostSpawnList));
            EntityManager.AddBuffer<PredictedGhostSpawn>(ent);
            RequireSingletonForUpdate<PredictedGhostSpawnList>();
            m_BeginSimulationBarrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_GhostInitQuery = GetEntityQuery(ComponentType.ReadOnly<PredictedGhostSpawnRequestComponent>(),
                ComponentType.ReadOnly<GhostTypeComponent>(),
                ComponentType.ReadWrite<GhostComponent>());
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
            m_ListHasData = new NativeArray<int>(1, Allocator.Persistent);
        }
        protected override void OnDestroy()
        {
            m_ListHasData.Dispose();
        }

        protected override void OnUpdate()
        {
            bool hasExisting = m_ListHasData[0] != 0;
            bool hasNew = !m_GhostInitQuery.IsEmptyIgnoreFilter;

            if (!hasNew && !hasExisting)
            {
                m_SpawnTick = m_ClientSimulationSystemGroup.ServerTick;
                return;
            }

            var spawnListEntity = GetSingletonEntity<PredictedGhostSpawnList>();
            var spawnListFromEntity = GetBufferFromEntity<PredictedGhostSpawn>();

            EntityCommandBuffer commandBuffer = m_BeginSimulationBarrier.CreateCommandBuffer();
            if (hasNew)
            {
                m_ListHasData[0] = 1;
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
                    predictedGhostFromEntity = GetComponentDataFromEntity<PredictedGhostComponent>(),
                    ghostTypeFromEntity = GetComponentDataFromEntity<GhostTypeComponent>(true),

                    commandBuffer = commandBuffer,
                    spawnTick = m_SpawnTick,
                    linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(),
                    childEntityLookup = GetStorageInfoFromEntity()
                };
                var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(initJob.GhostCollectionSingleton);
                DynamicTypeList.PopulateList(this, ghostComponentCollection, true, ref initJob.DynamicTypeList);
                Dependency = initJob.ScheduleSingleByRef(m_GhostInitQuery, Dependency);
            }

            if (hasExisting)
            {
                // Validate all ghosts in the list of predictive spawn ghosts and destroy the ones which are too old
                uint interpolatedTick = m_ClientSimulationSystemGroup.InterpolationTick;
                var listHasData = m_ListHasData;
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
                    listHasData[0] = spawnList.Length;
                }).Schedule(Dependency);
            }
            m_BeginSimulationBarrier.AddJobHandleForProducer(Dependency);
            m_SpawnTick = m_ClientSimulationSystemGroup.ServerTick;
        }
    }
}

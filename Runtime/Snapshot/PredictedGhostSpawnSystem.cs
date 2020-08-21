using Unity.Burst;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;

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
        private GhostCollectionSystem m_GhostCollectionSystem;
        private EntityQuery m_GhostInitQuery;
        private uint m_SpawnTick;

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
            [ReadOnly] public NativeArray<GhostComponentSerializer.State> GhostComponentCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostTypeState> GhostTypeCollection;
            [ReadOnly] public NativeList<GhostCollectionSystem.GhostComponentIndex> GhostComponentIndex;

            [ReadOnly] public EntityTypeHandle entityType;
            public ComponentTypeHandle<SnapshotData> snapshotDataType;
            public BufferTypeHandle<SnapshotDataBuffer> snapshotDataBufferType;

            public BufferFromEntity<PredictedGhostSpawn> spawnListFromEntity;
            public Entity spawnListEntity;

            public ComponentDataFromEntity<GhostComponent> ghostFromEntity;
            [ReadOnly] public ComponentDataFromEntity<GhostTypeComponent> ghostTypeFromEntity;
            [ReadOnly] public BufferFromEntity<GhostPrefabBuffer> ghostPrefabBufferFromEntity;
            public Entity serverPrefabEntity;

            public EntityCommandBuffer commandBuffer;
            public uint spawnTick;
            public unsafe void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                var prefabList = ghostPrefabBufferFromEntity[serverPrefabEntity];
                var entityList = chunk.GetNativeArray(entityType);
                var snapshotDataList = chunk.GetNativeArray(snapshotDataType);
                var snapshotDataBufferList = chunk.GetBufferAccessor(snapshotDataBufferType);

                var ghostTypeComponent = ghostTypeFromEntity[entityList[0]];
                int ghostType;
                for (ghostType = 0; ghostType < prefabList.Length; ++ghostType)
                {
                    if (ghostTypeFromEntity[prefabList[ghostType].Value] == ghostTypeComponent)
                        break;
                }
                if (ghostType >= prefabList.Length)
                    throw new System.InvalidOperationException("Could not find ghost type in the collection");

                var spawnList = spawnListFromEntity[spawnListEntity];
                var typeData = GhostTypeCollection[ghostType];
                var snapshotSize = typeData.SnapshotSize;
                int changeMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                var snapshotBaseOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                var serializerState = new GhostSerializerState
                {
                    GhostFromEntity = ghostFromEntity
                };
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

                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif
                        if (chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                        {
                            var compSize = GhostComponentCollection[compIdx].ComponentSize;
                            var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                            GhostComponentCollection[compIdx].CopyToSnapshot.Ptr.Invoke((System.IntPtr)UnsafeUtility.AddressOf(ref serializerState), (System.IntPtr)snapshotPtr, snapshotOffset, snapshotSize, (System.IntPtr)(compData + i*compSize), compSize, 1);
                        }
                        snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[compIdx].SnapshotSize);
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
            m_GhostCollectionSystem = World.GetOrCreateSystem<GhostCollectionSystem>();
            m_GhostInitQuery = GetEntityQuery(ComponentType.ReadOnly<PredictedGhostSpawnRequestComponent>(),
                ComponentType.ReadOnly<GhostTypeComponent>(),
                ComponentType.ReadWrite<GhostComponent>());
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
        }

        protected override unsafe void OnUpdate()
        {
            EntityCommandBuffer commandBuffer = m_BeginSimulationBarrier.CreateCommandBuffer();
            var spawnListEntity = GetSingletonEntity<PredictedGhostSpawnList>();
            var spawnListFromEntity = GetBufferFromEntity<PredictedGhostSpawn>();

            if (!m_GhostInitQuery.IsEmptyIgnoreFilter)
            {
                var initJob = new InitGhostJob
                {
                    GhostComponentCollection = m_GhostCollectionSystem.m_GhostComponentCollection,
                    GhostTypeCollection = m_GhostCollectionSystem.m_GhostTypeCollection,
                    GhostComponentIndex = m_GhostCollectionSystem.m_GhostComponentIndex,

                    entityType = GetEntityTypeHandle(),
                    snapshotDataType = GetComponentTypeHandle<SnapshotData>(),
                    snapshotDataBufferType = GetBufferTypeHandle<SnapshotDataBuffer>(),

                    spawnListFromEntity = spawnListFromEntity,
                    spawnListEntity = spawnListEntity,

                    ghostFromEntity = GetComponentDataFromEntity<GhostComponent>(),
                    ghostTypeFromEntity = GetComponentDataFromEntity<GhostTypeComponent>(true),
                    ghostPrefabBufferFromEntity = GetBufferFromEntity<GhostPrefabBuffer>(true),
                    serverPrefabEntity = GetSingleton<GhostPrefabCollectionComponent>().serverPrefabs,

                    commandBuffer = commandBuffer,
                    spawnTick = m_SpawnTick
                };
                var listLength = m_GhostCollectionSystem.m_GhostComponentCollection.Length;
                if (listLength <= 32)
                {
                    var dynamicListJob = new InitGhostJob32 {Job = initJob};
                    DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                    Dependency = dynamicListJob.ScheduleSingle(m_GhostInitQuery, Dependency);
                }
                else if (listLength <= 64)
                {
                    var dynamicListJob = new InitGhostJob64 {Job = initJob};
                    DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
                    Dependency = dynamicListJob.ScheduleSingle(m_GhostInitQuery, Dependency);
                }
                else if (listLength <= 128)
                {
                    var dynamicListJob = new InitGhostJob128 {Job = initJob};
                    DynamicTypeList.PopulateList(this, m_GhostCollectionSystem.m_GhostComponentCollection, true, ref dynamicListJob.List);
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
                        commandBuffer.RemoveComponent<GhostComponent>(ghost.entity);
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
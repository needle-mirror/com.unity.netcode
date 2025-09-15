using Unity.Burst;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using System;
using Unity.Assertions;
using Unity.Burst.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// Tag added to the singleton entity that contains the <see cref="PredictedGhostSpawn"/> buffer.
    /// </summary>
    public struct PredictedGhostSpawnList : IComponentData
    {}

    /// <summary>
    /// Added to a <see cref="PredictedGhostSpawnList"/> singleton entity.
    /// Contains a transient list of ghosts that should be pre-spawned.
    /// Expects to be handled during the <see cref="GhostSpawnClassificationSystem"/> step.
    /// InternalBufferCapacity allocated to almost max out chunk memory.
    /// In practice, this capacity just needs to hold the maximum number of client-authored
    /// ghost entities per frame, which is typically in the range 0 - 1.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct PredictedGhostSpawn : IBufferElementData
    {
        /// <summary>
        /// The Entity that has been spawned.
        /// </summary>
        public Entity entity;
        /// <summary>
        /// The index of the ghost type in the <see cref="GhostCollectionPrefab"/> collection. Used to classify the ghost (<see cref="GhostSpawnClassificationSystem"/>).
        /// </summary>
        public int ghostType;
        /// <summary>
        /// The server tick the entity has been spawned.
        /// </summary>
        public NetworkTick spawnTick;

        /// <summary>Helper.</summary>
        /// <returns>Formatted informational string.</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString128Bytes ToFixedString() => $"PredictedGhostSpawn[ghostType:{ghostType},st:{spawnTick.ToFixedString()},ent:{entity.ToFixedString()}]";
        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }

    /// <summary>
    /// Parent group of all systems that need to process predicted spawned ghost entities inside the prediction
    /// group.
    /// The group execute after the <see cref="EndPredictedSimulationEntityCommandBufferSystem"/> to ensure new predicted
    /// ghost entities created by that command buffer are always initialized before the end of current the prediction tick.
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateAfter(typeof(EndPredictedSimulationEntityCommandBufferSystem))]
    public partial class PredictedSpawningSystemGroup : ComponentSystemGroup
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            var spawnSystem = World.GetExistingSystem<PredictedGhostSpawnSystem>();
            AddSystemToUpdateList(spawnSystem);
        }
    }

    /// <summary>
    /// Consume all the <see cref="PredictedGhostSpawnRequest"/> requests by initializing the predicted spawned ghost
    /// and adding it to the <see cref="PredictedGhostSpawn"/> buffer.
    /// All the predicted spawned ghosts are initialized with a invalid ghost id (-1) but a valid ghost type and spawnTick.
    /// </summary>
    [UpdateInGroup(typeof(GhostSpawnSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [BurstCompile]
    public partial struct PredictedGhostSpawnSystem : ISystem
    {
        [BurstCompile]
        struct InitJob : IJobChunk
        {
            public DynamicTypeList DynamicTypeList;

            public Entity GhostCollectionSingleton;
            public EntityCommandBuffer commandBuffer;
            [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentLookup<GhostType> ghostTypeFromEntity;
            public NativeHashMap<GhostType, int>.ReadOnly GhostTypeToColletionIndex;
            public ComponentTypeHandle<PredictedGhostSpawnRequest> predictedSpawnTypeHandle;

            public ComponentTypeHandle<SnapshotData> snapshotDataType;
            public BufferTypeHandle<SnapshotDataBuffer> snapshotDataBufferType;
            public BufferTypeHandle<SnapshotDynamicDataBuffer> snapshotDynamicDataBufferType;

            public BufferLookup<PredictedGhostSpawn> spawnListFromEntity;
            public Entity spawnListEntity;

            public ComponentLookup<GhostInstance> ghostFromEntity;
            public ComponentLookup<PredictedGhost> predictedGhostFromEntity;

            public NetworkTick spawnTick;
            public NetDebug netDebug;

            public unsafe void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // This job is not written to support queries with enableable component types.
                Assert.IsFalse(useEnabledMask);

                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = DynamicTypeList.GetData();
                var entityList = chunk.GetNativeArray(entityType);
                var snapshotDataList = chunk.GetNativeArray(ref snapshotDataType);
                var snapshotDataBufferList = chunk.GetBufferAccessor(ref snapshotDataBufferType);
                var snapshotDynamicDataBufferList = chunk.GetBufferAccessor(ref snapshotDynamicDataBufferType);

                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var ghostType = ghostTypeFromEntity[entityList[0]];
                if (!GhostTypeToColletionIndex.TryGetValue(ghostType, out var ghostTypeIndex))
                {
                    //there is no mapping for this ghost yet. The warning is a little spamming but at least it will get noticed.
                    //TODO Maybe limit to 3-4 time.
                    netDebug.LogError($"Failed to initialize predicted spawned ghost with type {(Hash128)ghostType}.\nThe ghost has been spawed before the client received from the server the required mapping (`GhostType -> index`),\nand the associated prefab loaded and processed by the GhostCollectionSystem.\nTo prevent this error/warning, you can check before spawning predicted ghosts that the GhostCollection.GhostTypeToColletionIndex hashmap contains a entry or the `GhostType` component assigned on the prefab.");
                    //Early exiting here will not add this ghost to the spawned list.
                    //What that means? It means that if the client spawn a ghost from a prefab the server didn't load
                    //this will never get detroyed and this error/warning continously reported.
                    //This was already the case. No behaviour has changed.
                    return;
                }
                //This condition can be true in case the prefab associated with the ghost is loaded but there are missing prefabs
                //before this in the server list. The GhostCollectionPrefabSerializer collection is populated "in-order".
                //So this condition it still hold.
                if(ghostTypeIndex >= GhostTypeCollection.Length)
                    return;
                var spawnList = spawnListFromEntity[spawnListEntity];
                var typeData = GhostTypeCollection[ghostTypeIndex];
                var snapshotSize = typeData.SnapshotSize;
                int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                int enableableMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);
                int snapshotBaseOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + changeMaskUints*sizeof(uint) + enableableMaskUints*sizeof(uint));

                var helper = new GhostSerializeHelper
                {
                    serializerState = new GhostSerializerState { GhostFromEntity = ghostFromEntity },
                    ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                    GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton],
                    GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton],
                    childEntityLookup = childEntityLookup,
                    linkedEntityGroupType = linkedEntityGroupType,
                    ghostChunkComponentTypesPtrLen = DynamicTypeList.Length,
                    changeMaskUints = changeMaskUints
                };

                var bufferSizes = new NativeArray<int>(chunk.Count, Allocator.Temp);
                var hasBuffers = GhostTypeCollection[ghostTypeIndex].NumBuffers > 0;
                if (hasBuffers)
                    helper.GatherBufferSize(chunk, 0, chunk.Count, typeData, ref bufferSizes);

                for (int i = 0; i < entityList.Length; ++i)
                {
                    var entity = entityList[i];

                    var ghostComponent = ghostFromEntity[entity];
                    //Set a valid spawn tick but invalid ghost id for predicted spawned ghosts.
                    //This will let distinguish them from invalid ghost instances
                    ghostComponent.ghostId = 0;
                    ghostComponent.ghostType = ghostTypeIndex;
                    ghostComponent.spawnTick = spawnTick;
                    ghostFromEntity[entity] = ghostComponent;
                    predictedGhostFromEntity[entity] = new PredictedGhost{AppliedTick = spawnTick, PredictionStartTick = spawnTick};
                    // Set initial snapshot data
                    // Get the buffers, fill in snapshot size etc
                    snapshotDataList[i] = new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0};
                    var snapshotDataBuffer = snapshotDataBufferList[i];
                    snapshotDataBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    var snapshotPtr = (byte*)snapshotDataBuffer.GetUnsafePtr();
                    UnsafeUtility.MemClear(snapshotPtr, snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    *(uint*)snapshotPtr = spawnTick.SerializedData;

                    helper.snapshotOffset = snapshotBaseOffset;
                    helper.snapshotPtr = snapshotPtr;
                    helper.snapshotSize = snapshotSize;
                    if (hasBuffers)
                    {
                        var dynamicDataCapacity= SnapshotDynamicBuffersHelper.CalculateBufferCapacity((uint)bufferSizes[i],
                            out var dynamicSnapshotSize);
                        var snapshotDynamicDataBuffer = snapshotDynamicDataBufferList[i];
                        var headerSize = SnapshotDynamicBuffersHelper.GetHeaderSize();
                        snapshotDynamicDataBuffer.ResizeUninitialized((int)dynamicDataCapacity);

                        //Explanation: on the client the dynamic buffer data offset is relative to the beginning of
                        //the dynamic data slot, not to the header.
                        //That means, the dynamicSnapshotDataOffset always start from 0, and the data instead
                        //start right after the header (for the first slot).
                        helper.snapshotDynamicPtr = (byte*)snapshotDynamicDataBuffer.GetUnsafePtr() + headerSize;
                        helper.snapshotDynamicHeaderPtr = (byte*)snapshotDynamicDataBuffer.GetUnsafePtr();
                        helper.dynamicSnapshotDataOffset = 0;
                        helper.dynamicSnapshotCapacity = (int)(dynamicSnapshotSize);
                    }
                    helper.CopyEntityToSnapshot(chunk, i, typeData, GhostSerializeHelper.ClearOption.DontClear);
                    // Remove request component
                    // Add to list of predictive spawn component - maybe use a singleton for this so spawn systems can just access it too
                    spawnList.Add(new PredictedGhostSpawn{entity = entity, ghostType = ghostTypeIndex, spawnTick = spawnTick});
                    commandBuffer.RemoveComponent<PredictedGhostSpawnRequest>(entity);
                }
                chunk.SetComponentEnabledForAll(ref predictedSpawnTypeHandle, true);
                bufferSizes.Dispose();
            }
        }

        EntityQuery m_GhostInitQuery;
        NetworkTick m_LastFrameFullTick;

        BufferLookup<PredictedGhostSpawn> m_PredictedGhostSpawnFromEntity;
        BufferLookup<GhostComponentSerializer.State> m_GhostComponentSerializerStateFromEntity;
        BufferLookup<GhostCollectionPrefabSerializer> m_GhostCollectionPrefabSerializerFromEntity;
        BufferLookup<GhostCollectionComponentIndex> m_GhostCollectionComponentIndexFromEntity;
        BufferLookup<GhostCollectionPrefab> m_GhostCollectionPrefabFromEntity;
        EntityTypeHandle m_EntityTypeHandle;
        ComponentTypeHandle<SnapshotData> m_SnapshotDataHandle;
        BufferTypeHandle<SnapshotDataBuffer> m_SnapshotDataBufferHandle;
        BufferTypeHandle<SnapshotDynamicDataBuffer> m_SnapshotDynamicDataBufferHandle;
        BufferTypeHandle<LinkedEntityGroup> m_LinkedEntityGroupHandle;
        ComponentLookup<GhostInstance> m_GhostComponentFromEntity;
        ComponentLookup<PredictedGhost> m_PredictedGhostFromEntity;
        ComponentLookup<GhostType> m_GhostTypeComponentFromEntity;
        ComponentTypeHandle<PredictedGhostSpawnRequest> m_PredictedSpawnTypeHandle;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var ent = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(ent, (FixedString64Bytes)"PredictedGhostSpawnList");
            state.EntityManager.AddComponentData(ent, new PredictedGhostSpawnList{});
            state.EntityManager.AddBuffer<PredictedGhostSpawn>(ent);
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostType>()
                .WithDisabled<PredictedGhostSpawnRequest>()
                .WithAllRW<GhostInstance>();
            m_GhostInitQuery = state.GetEntityQuery(builder);
            m_PredictedGhostSpawnFromEntity = state.GetBufferLookup<PredictedGhostSpawn>();

            m_GhostComponentSerializerStateFromEntity = state.GetBufferLookup<GhostComponentSerializer.State>(true);
            m_GhostCollectionPrefabSerializerFromEntity = state.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostCollectionComponentIndexFromEntity = state.GetBufferLookup<GhostCollectionComponentIndex>(true);
            m_GhostCollectionPrefabFromEntity = state.GetBufferLookup<GhostCollectionPrefab>(true);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_SnapshotDataHandle = state.GetComponentTypeHandle<SnapshotData>();
            m_SnapshotDataBufferHandle = state.GetBufferTypeHandle<SnapshotDataBuffer>();
            m_SnapshotDynamicDataBufferHandle = state.GetBufferTypeHandle<SnapshotDynamicDataBuffer>();
            m_LinkedEntityGroupHandle = state.GetBufferTypeHandle<LinkedEntityGroup>(true);
            m_PredictedSpawnTypeHandle = state.GetComponentTypeHandle<PredictedGhostSpawnRequest>();

            m_GhostComponentFromEntity = state.GetComponentLookup<GhostInstance>();
            m_PredictedGhostFromEntity = state.GetComponentLookup<PredictedGhost>();
            m_GhostTypeComponentFromEntity = state.GetComponentLookup<GhostType>(true);

            state.RequireForUpdate<PredictedGhostSpawnList>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (networkTime.IsInPredictionLoop && !networkTime.IsFirstTimeFullyPredictingTick)
                return;
            if (m_GhostInitQuery.IsEmpty)
            {
                m_LastFrameFullTick = NetworkTimeHelper.LastFullServerTick(SystemAPI.GetSingleton<NetworkTime>());
                return;
            }
            //Edge case scenario when the predicted spawn is at the very very first tick.
            //this can't happen if the ghost has been spawned inside a system in the
            //PredictedSimulation but may occurs if that is done outside and before
            //the first snapshot received.
            if(!m_LastFrameFullTick.IsValid)
                m_LastFrameFullTick = NetworkTimeHelper.LastFullServerTick(networkTime);
            //We need to infer when the client spawned ghosts, because we don't have this information.
            //The last full tick is the only value we can use here to match the last (or the first)
            //IsFirstTimefullyPredictedTick. But we can have many (depend on the elapsed delta time).
            //There are multiple cases (see documentation about problem with prediction) where
            //it is possible a command for tick T, cause a spawn at tick T+1 or T+2 (i.e if you have
            //a rate of fire). For those cases, we are assigning here a wrong spawning tick.
            //(usually 1 or 2 tick off).
            //In the normal (95% of the time) case scenerio where the game run at approximativel the
            //simulation tick rate or faster, the spawn tick is assigned correctly.
            NetworkTick spawnTick;
            if(networkTime.IsInPredictionLoop)
                spawnTick = networkTime.ServerTick;
            else
            {
                //Notice that the client and the server will always assign a different tick to entities when spawned outside
                //the prediction loop
                //The server assign the tick ath the end of the frame (GhostSendSystem)
                //The client assign the tick always at the begging of the frame (this system).
                //
                //To is best knowledge (client), the tick at which the entities spawned should be the last full tick
                //(done in the previous frame).
                //In all cases, the spawn tick is going to be different (1 tick less normally) depending on the elapsed time,
                //tick batching confituration etc.
                //This is why the default tick-based check (apart for reason depending on the input applied at different time)
                //use at leat a range of [-5,+5] ticks. That is quite a large window, but give the necesary room to match inconsistency
                //in the timing.
                //Why we can't increase here the spawn tick (so it will match) ? Because this is the tick associated with the
                //current state of the entity, not necessarily when the entity was actually spawned.
                //Because this tick is embedded into the snapshot, we are rewind and re-simulate from here for continuing prediction
                //(when force to do so all the time). As such need to be consistent.
                spawnTick = m_LastFrameFullTick;
            }
            m_LastFrameFullTick = NetworkTimeHelper.LastFullServerTick(networkTime);

            var spawnListEntity = SystemAPI.GetSingletonEntity<PredictedGhostSpawnList>();
            m_PredictedGhostSpawnFromEntity.Update(ref state);
            m_GhostComponentSerializerStateFromEntity.Update(ref state);
            m_GhostCollectionPrefabSerializerFromEntity.Update(ref state);
            m_GhostCollectionComponentIndexFromEntity.Update(ref state);
            m_GhostCollectionPrefabFromEntity.Update(ref state);

            m_EntityTypeHandle.Update(ref state);
            m_SnapshotDataHandle.Update(ref state);
            m_SnapshotDataBufferHandle.Update(ref state);
            m_SnapshotDynamicDataBufferHandle.Update(ref state);
            m_LinkedEntityGroupHandle.Update(ref state);

            m_GhostComponentFromEntity.Update(ref state);
            m_PredictedGhostFromEntity.Update(ref state);
            m_GhostTypeComponentFromEntity.Update(ref state);
            m_PredictedSpawnTypeHandle.Update(ref state);
            var ghostCollection = SystemAPI.GetSingletonEntity<GhostCollection>();
            EntityCommandBuffer commandBuffer;
            commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            var initJob = new InitJob
            {
                GhostCollectionSingleton = ghostCollection,
                GhostComponentCollectionFromEntity = m_GhostComponentSerializerStateFromEntity,
                GhostTypeCollectionFromEntity = m_GhostCollectionPrefabSerializerFromEntity,
                GhostComponentIndexFromEntity = m_GhostCollectionComponentIndexFromEntity,
                GhostCollectionFromEntity = m_GhostCollectionPrefabFromEntity,
                GhostTypeToColletionIndex = state.EntityManager.GetComponentData<GhostCollection>(ghostCollection).GhostTypeToColletionIndex,
                commandBuffer = commandBuffer,
                entityType = m_EntityTypeHandle,
                snapshotDataType = m_SnapshotDataHandle,
                snapshotDataBufferType = m_SnapshotDataBufferHandle,
                snapshotDynamicDataBufferType = m_SnapshotDynamicDataBufferHandle,
                predictedSpawnTypeHandle = m_PredictedSpawnTypeHandle,
                ghostFromEntity = m_GhostComponentFromEntity,
                predictedGhostFromEntity = m_PredictedGhostFromEntity,
                ghostTypeFromEntity = m_GhostTypeComponentFromEntity,
                spawnTick = spawnTick,
                linkedEntityGroupType = m_LinkedEntityGroupHandle,
                childEntityLookup = state.GetEntityStorageInfoLookup(),
                spawnListFromEntity = m_PredictedGhostSpawnFromEntity,
                spawnListEntity = spawnListEntity,
                netDebug = SystemAPI.GetSingleton<NetDebug>()
            };
            var ghostComponentCollection = state.EntityManager.GetBuffer<GhostCollectionComponentType>(initJob.GhostCollectionSingleton);
            DynamicTypeList.PopulateList(ref state, ghostComponentCollection, true, ref initJob.DynamicTypeList);
            // Intentionally using non-parallel .ScheduleByRef()
            state.Dependency = initJob.ScheduleByRef(m_GhostInitQuery, state.Dependency);
        }
    }

    /// <summary>
    /// Consume all the <see cref="PredictedGhostSpawnRequest"/> requests by initializing the predicted spawned ghost
    /// and adding it to the <see cref="PredictedGhostSpawn"/> buffer.
    /// All the predicted spawned ghosts are initialized with a invalid ghost id (-1) but a valid ghost type and spawnTick.
    /// </summary>
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateAfter(typeof(GhostDespawnSystem))]
    [BurstCompile]
    public partial struct PredictedGhostDespawnSystem : ISystem
    {
        /// <summary>
        ///     Destroy client predicted spawns which are too old.
        ///     I.e. The ones which did NOT get classified, and therefore were not already removed from this list.
        /// </summary>
        [BurstCompile]
        struct CleanupPredictedSpawns : IJob
        {
            public DynamicBuffer<PredictedGhostSpawn> spawnList;
            public NetworkTick destroyTick;
            public EntityCommandBuffer commandBuffer;
            public void Execute()
            {
                for (int i = 0; i < spawnList.Length; ++i)
                {
                    var ghost = spawnList[i];
                    if (Hint.Unlikely(destroyTick.IsNewerThan(ghost.spawnTick)))
                    {
                        // Destroy entity and remove from list
                        commandBuffer.DestroyEntity(ghost.entity);
                        spawnList.RemoveAtSwapBack(i);
                        --i;
                    }
                }
            }
        }

        BufferLookup<PredictedGhostSpawn> m_PredictedGhostSpawnLookup;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PredictedGhostSpawnList>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var spawnList = SystemAPI.GetSingletonBuffer<PredictedGhostSpawn>();
            if(spawnList.Length == 0)
                return;
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if(!networkTime.InterpolationTick.IsValid)
                return;
            EntityCommandBuffer commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            var destroyTick = networkTime.InterpolationTick;
            //WE SHOULD DESPAWN AT FULL INTERPOLATION TICKS
            if(networkTime.InterpolationTickFraction < 1)
                destroyTick.Decrement();
            if(!SystemAPI.TryGetSingleton(out ClientTickRate clientTickRate))
                clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            destroyTick.Subtract(clientTickRate.NumAdditionalClientPredictedGhostLifetimeTicks);
            var cleanupJob = new CleanupPredictedSpawns
            {
                spawnList = spawnList,
                destroyTick = destroyTick,
                commandBuffer = commandBuffer,
            };
            state.Dependency = cleanupJob.Schedule(state.Dependency);
        }
    }
}

using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport.Utilities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Transforms;
using Unity.Mathematics;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSpawnSystemGroup))]
    public partial class GhostSpawnSystem : SystemBase
    {
        private struct DelayedSpawnGhost
        {
            public int ghostId;
            public int ghostType;
            public uint clientSpawnTick;
            public uint serverSpawnTick;
            public Entity oldEntity;
        }
        private NativeQueue<DelayedSpawnGhost> m_DelayedSpawnQueue;
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private GhostReceiveSystem m_GhostReceiveSystem;
        private NetDebugSystem m_NetDebugSystem;
        private EntityQuery m_InGameGroup;
        GhostPredictionSystemGroup m_GhostPredictionSystemGroup;

        protected override void OnCreate()
        {
            m_DelayedSpawnQueue = new NativeQueue<DelayedSpawnGhost>(Allocator.Persistent);
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
            m_GhostReceiveSystem = World.GetOrCreateSystem<GhostReceiveSystem>();
            m_NetDebugSystem = World.GetOrCreateSystem<NetDebugSystem>();
            m_GhostPredictionSystemGroup = World.GetOrCreateSystem<GhostPredictionSystemGroup>();
            m_InGameGroup = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
            RequireSingletonForUpdate<GhostCollection>();
            RequireSingletonForUpdate<GhostSpawnQueueComponent>();
        }
        protected override void OnDestroy()
        {
            m_DelayedSpawnQueue.Dispose();
        }
        protected unsafe override void OnUpdate()
        {
            m_GhostReceiveSystem.LastGhostMapWriter.Complete();

            var interpolationTargetTick = m_ClientSimulationSystemGroup.InterpolationTick;
            if (m_ClientSimulationSystemGroup.InterpolationTickFraction < 1)
                --interpolationTargetTick;
            //var predictionTargetTick = m_ClientSimulationSystemGroup.ServerTick;
            var prefabsEntity = GetSingletonEntity<GhostCollection>();
            var prefabs = EntityManager.GetBuffer<GhostCollectionPrefab>(prefabsEntity).ToNativeArray(Allocator.Temp);

            var ghostSpawnEntity = GetSingletonEntity<GhostSpawnQueueComponent>();
            var ghostSpawnBufferComponent = EntityManager.GetBuffer<GhostSpawnBuffer>(ghostSpawnEntity);
            var snapshotDataBufferComponent = EntityManager.GetBuffer<SnapshotDataBuffer>(ghostSpawnEntity);

            //Avoid adding new ghost if the stream is not in game
            if (m_InGameGroup.IsEmptyIgnoreFilter)
            {
                ghostSpawnBufferComponent.ResizeUninitialized(0);
                snapshotDataBufferComponent.ResizeUninitialized(0);
                m_DelayedSpawnQueue.Clear();
                return;
            }

            var ghostSpawnBuffer = ghostSpawnBufferComponent.ToNativeArray(Allocator.Temp);
            var snapshotDataBuffer = snapshotDataBufferComponent.ToNativeArray(Allocator.Temp);
            ghostSpawnBufferComponent.ResizeUninitialized(0);
            snapshotDataBufferComponent.ResizeUninitialized(0);

            var spawnedGhosts = new NativeList<SpawnedGhostMapping>(16, Allocator.Temp);
            var nonSpawnedGhosts = new NativeList<NonSpawnedGhostMapping>(16, Allocator.Temp);
            var ghostCollectionSingleton = GetSingletonEntity<GhostCollection>();
            for (int i = 0; i < ghostSpawnBuffer.Length; ++i)
            {
                var ghost = ghostSpawnBuffer[i];
                Entity entity = Entity.Null;
                byte* snapshotData = null;
                var ghostTypeCollection = EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionSingleton);
                var snapshotSize = ghostTypeCollection[ghost.GhostType].SnapshotSize;
                bool hasBuffers = ghostTypeCollection[ghost.GhostType].NumBuffers > 0;
                if (ghost.SpawnType == GhostSpawnBuffer.Type.Interpolated)
                {
                    // Add to m_DelayedSpawnQueue
                    entity = EntityManager.CreateEntity();
                    EntityManager.AddComponentData(entity, new GhostComponent {ghostId = ghost.GhostID, ghostType = ghost.GhostType, spawnTick = ghost.ServerSpawnTick});
                    EntityManager.AddComponent<PendingSpawnPlaceholderComponent>(entity);
                    if (PrespawnHelper.IsPrespawGhostId(ghost.GhostID))
                        ConfigurePrespawnGhost(entity, ghost);
                    var newBuffer = EntityManager.AddBuffer<SnapshotDataBuffer>(entity);
                    newBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    snapshotData = (byte*)newBuffer.GetUnsafePtr();
                    //Add also the SnapshotDynamicDataBuffer if the entity has buffers to copy the dynamic contents
                    if (hasBuffers)
                        EntityManager.AddBuffer<SnapshotDynamicDataBuffer>(entity);
                    EntityManager.AddComponentData(entity, new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0});
                    m_DelayedSpawnQueue.Enqueue(new GhostSpawnSystem.DelayedSpawnGhost{ghostId = ghost.GhostID, ghostType = ghost.GhostType, clientSpawnTick = ghost.ClientSpawnTick, serverSpawnTick = ghost.ServerSpawnTick, oldEntity = entity});
                    nonSpawnedGhosts.Add(new NonSpawnedGhostMapping{ghostId = ghost.GhostID, entity = entity});
                }
                else if (ghost.SpawnType == GhostSpawnBuffer.Type.Predicted)
                {
                    // TODO: this could allow some time for the prefab to load before giving an error
                    if (prefabs[ghost.GhostType].GhostPrefab == Entity.Null)
                    {
                        ReportMissingPrefab();
                        continue;
                    }
                    // Spawn directly
                    entity = ghost.PredictedSpawnEntity != Entity.Null ? ghost.PredictedSpawnEntity : EntityManager.Instantiate(prefabs[ghost.GhostType].GhostPrefab);
                    if (EntityManager.HasComponent<GhostPrefabMetaDataComponent>(prefabs[ghost.GhostType].GhostPrefab))
                    {
                        ref var toRemove = ref EntityManager.GetComponentData<GhostPrefabMetaDataComponent>(prefabs[ghost.GhostType].GhostPrefab).Value.Value.DisableOnPredictedClient;
                        //Need copy because removing component will invalidate the buffer pointer, since introduce structural changes
                        var linkedEntityGroup = EntityManager.GetBuffer<LinkedEntityGroup>(entity).ToNativeArray(Allocator.Temp);
                        for (int rm = 0; rm < toRemove.Length; ++rm)
                        {
                            var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toRemove[rm].StableHash));
                            EntityManager.RemoveComponent(linkedEntityGroup[toRemove[rm].EntityIndex].Value, compType);
                        }
                    }
                    EntityManager.SetComponentData(entity, new GhostComponent {ghostId = ghost.GhostID, ghostType = ghost.GhostType, spawnTick = ghost.ServerSpawnTick});
                    if (PrespawnHelper.IsPrespawGhostId(ghost.GhostID))
                        ConfigurePrespawnGhost(entity, ghost);
                    var newBuffer = EntityManager.GetBuffer<SnapshotDataBuffer>(entity);
                    newBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    snapshotData = (byte*)newBuffer.GetUnsafePtr();
                    EntityManager.SetComponentData(entity, new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0});
                    spawnedGhosts.Add(new SpawnedGhostMapping{ghost = new SpawnedGhost{ghostId = ghost.GhostID, spawnTick = ghost.ServerSpawnTick}, entity = entity});
                }
                if (entity != Entity.Null)
                {
                    UnsafeUtility.MemClear(snapshotData, snapshotSize*GhostSystemConstants.SnapshotHistorySize);
                    UnsafeUtility.MemCpy(snapshotData, (byte*)snapshotDataBuffer.GetUnsafeReadOnlyPtr() + ghost.DataOffset, snapshotSize);
                    if (hasBuffers)
                    {
                        //Resize and copy the associated dynamic buffer snapshot data
                        var snapshotDynamicBuffer = EntityManager.GetBuffer<SnapshotDynamicDataBuffer>(entity);
                        var dynamicDataCapacity= SnapshotDynamicBuffersHelper.CalculateBufferCapacity(ghost.DynamicDataSize, out var _);
                        snapshotDynamicBuffer.ResizeUninitialized((int)dynamicDataCapacity);
                        var dynamicSnapshotData = (byte*)snapshotDynamicBuffer.GetUnsafePtr();
                        if(dynamicSnapshotData == null)
                            throw new InvalidOperationException("snapshot dynamic data buffer not initialized but ghost has dynamic buffer contents");

                        // Update the dynamic data header (uint[GhostSystemConstants.SnapshotHistorySize)]) by writing the used size for the current slot
                        // (for new spawned entity is 0). Is un-necessary to initialize all the header slots to 0 since that information is only used
                        // for sake of delta compression and, because that depend on the acked tick, only initialized and relevant slots are accessed in general.
                        // For more information about the layout, see SnapshotData.cs.
                        ((uint*)dynamicSnapshotData)[0] = ghost.DynamicDataSize;
                        var headerSize = SnapshotDynamicBuffersHelper.GetHeaderSize();
                        UnsafeUtility.MemCpy(dynamicSnapshotData + headerSize, (byte*)snapshotDataBuffer.GetUnsafeReadOnlyPtr() + ghost.DataOffset + snapshotSize, ghost.DynamicDataSize);
                    }
                }
            }
            m_GhostReceiveSystem.AddNonSpawnedGhosts(nonSpawnedGhosts);
            m_GhostReceiveSystem.AddSpawnedGhosts(spawnedGhosts);

            spawnedGhosts.Clear();
            while (m_DelayedSpawnQueue.Count > 0 &&
                   !SequenceHelpers.IsNewer(m_DelayedSpawnQueue.Peek().clientSpawnTick, interpolationTargetTick))
            {
                var ghost = m_DelayedSpawnQueue.Dequeue();
                // TODO: this could allow some time for the prefab to load before giving an error
                if (prefabs[ghost.ghostType].GhostPrefab == Entity.Null)
                {
                    ReportMissingPrefab();
                    continue;
                }
                //Entity has been destroyed meawhile it was in the queue
                if(!EntityManager.HasComponent<GhostComponent>(ghost.oldEntity))
                    continue;

                // Spawn actual entity
                Entity entity = EntityManager.Instantiate(prefabs[ghost.ghostType].GhostPrefab);
                if (EntityManager.HasComponent<GhostPrefabMetaDataComponent>(prefabs[ghost.ghostType].GhostPrefab))
                {
                    ref var toRemove = ref EntityManager.GetComponentData<GhostPrefabMetaDataComponent>(prefabs[ghost.ghostType].GhostPrefab).Value.Value.DisableOnInterpolatedClient;
                    var linkedEntityGroup = EntityManager.GetBuffer<LinkedEntityGroup>(entity).ToNativeArray(Allocator.Temp);
                    //Need copy because removing component will invalidate the buffer pointer, since introduce structural changes
                    for (int rm = 0; rm < toRemove.Length; ++rm)
                    {
                        var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toRemove[rm].StableHash));
                        EntityManager.RemoveComponent(linkedEntityGroup[toRemove[rm].EntityIndex].Value, compType);
                    }
                }
                EntityManager.SetComponentData(entity, EntityManager.GetComponentData<SnapshotData>(ghost.oldEntity));
                if (PrespawnHelper.IsPrespawGhostId(ghost.ghostId))
                {
                    EntityManager.AddComponentData(entity, EntityManager.GetComponentData<PreSpawnedGhostIndex>(ghost.oldEntity));
                    EntityManager.AddSharedComponentData(entity, EntityManager.GetSharedComponentData<SceneSection>(ghost.oldEntity));
                }
                var ghostComponentData = EntityManager.GetComponentData<GhostComponent>(ghost.oldEntity);
                EntityManager.SetComponentData(entity, ghostComponentData);
                var oldBuffer = EntityManager.GetBuffer<SnapshotDataBuffer>(ghost.oldEntity);
                var newBuffer = EntityManager.GetBuffer<SnapshotDataBuffer>(entity);
                newBuffer.ResizeUninitialized(oldBuffer.Length);
                UnsafeUtility.MemCpy(newBuffer.GetUnsafePtr(), oldBuffer.GetUnsafeReadOnlyPtr(), oldBuffer.Length);
                //copy the old buffers content to the new entity.
                //Perf FIXME: if we can introduce a "move" like concept for buffer to transfer ownership we can avoid a lot of copies and
                //allocations
                var ghostTypeCollection = EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionSingleton);
                bool hasBuffers = ghostTypeCollection[ghost.ghostType].NumBuffers > 0;
                if (hasBuffers)
                {
                    var oldDynamicBuffer = EntityManager.GetBuffer<SnapshotDynamicDataBuffer>(ghost.oldEntity);
                    var newDynamicBuffer = EntityManager.GetBuffer<SnapshotDynamicDataBuffer>(entity);
                    newDynamicBuffer.ResizeUninitialized(oldDynamicBuffer.Length);
                    UnsafeUtility.MemCpy(newDynamicBuffer.GetUnsafePtr(), oldDynamicBuffer.GetUnsafeReadOnlyPtr(), oldDynamicBuffer.Length);
                }
                EntityManager.DestroyEntity(ghost.oldEntity);

                spawnedGhosts.Add(new SpawnedGhostMapping{ghost = new SpawnedGhost{ghostId = ghost.ghostId, spawnTick = ghostComponentData.spawnTick}, entity = entity, previousEntity = ghost.oldEntity});
            }
            m_GhostReceiveSystem.UpdateSpawnedGhosts(spawnedGhosts);
        }

        /// <summary>
        /// Convert an interpolated ghost to a predicted ghost. The ghost must support both interpolated and predicted mode,
        /// and it cannot be owner predicted. The new components added as a result of this operation will have the inital
        /// values from the ghost prefab.
        /// </summary>
        public bool ConvertGhostToPredicted(Entity entity, float transitionDuration = 0)
        {
            if (m_GhostPredictionSystemGroup.IsInPredictionLoop)
            {
                m_NetDebugSystem.NetDebug.LogWarning("Trying to convert a ghost to predicted, but this is not allowed during the GhostPredictionSystemGroup phase. Call ConvertGhostToPredicted in a valid client SystemGroup (e.g. the ClientSimulationSystemGroup) instead.");
                return false;
            }
            var prefabsEntity = GetSingletonEntity<GhostCollection>();
            var prefabs = EntityManager.GetBuffer<GhostCollectionPrefab>(prefabsEntity).ToNativeArray(Allocator.Temp);
            var ghost = EntityManager.GetComponentData<GhostComponent>(entity);
            var prefab = prefabs[ghost.ghostType].GhostPrefab;
            if (!EntityManager.HasComponent<GhostPrefabMetaDataComponent>(prefab))
            {
                m_NetDebugSystem.NetDebug.LogWarning("Trying to convert a ghost to predicted, but did not find a prefab with meta data");
                return false;
            }
            if (EntityManager.HasComponent<PredictedGhostComponent>(entity))
            {
                m_NetDebugSystem.NetDebug.LogWarning("Trying to convert a ghost to predicted, but it is already predicted");
                return false;
            }

            ref var ghostMetaData = ref EntityManager.GetComponentData<GhostPrefabMetaDataComponent>(prefab).Value.Value;
            if (ghostMetaData.SupportedModes != GhostPrefabMetaData.GhostMode.Both)
            {
                m_NetDebugSystem.NetDebug.LogWarning("Trying to convert a ghost to predicted, but it does not support both modes");
                return false;
            }
            if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Both)
            {
                m_NetDebugSystem.NetDebug.LogWarning("Trying to convert a ghost to predicted, but it is owner predicted and owner predicted ghosts cannot be switched on demand");
                return false;
            }

            ref var toAdd = ref ghostMetaData.DisableOnInterpolatedClient;
            ref var toRemove = ref ghostMetaData.DisableOnPredictedClient;
            return AddRemoveComponents(entity, prefab, ref toAdd, ref toRemove, transitionDuration);
        }

        /// <summary>
        /// Convert a predicted ghost to an interpolated ghost. The ghost must support both interpolated and predicted mode,
        /// and it cannot be owner predicted. The new components added as a result of this operation will have the inital
        /// values from the ghost prefab.
        /// </summary>
        public bool ConvertGhostToInterpolated(Entity entity, float transitionDuration = 0)
        {
            if (m_GhostPredictionSystemGroup.IsInPredictionLoop)
            {
                m_NetDebugSystem.NetDebug.LogWarning("Trying to convert a ghost to interpolated, but this is not allowed during the GhostPredictionSystemGroup phase. Call ConvertGhostToInterpolated in a valid client SystemGroup (e.g. the ClientSimulationSystemGroup) instead.");
                return false;
            }
            var prefabsEntity = GetSingletonEntity<GhostCollection>();
            var prefabs = EntityManager.GetBuffer<GhostCollectionPrefab>(prefabsEntity).ToNativeArray(Allocator.Temp);
            var ghost = EntityManager.GetComponentData<GhostComponent>(entity);
            var prefab = prefabs[ghost.ghostType].GhostPrefab;
            if (!EntityManager.HasComponent<GhostPrefabMetaDataComponent>(prefab))
            {
                m_NetDebugSystem.NetDebug.LogWarning("Trying to convert a ghost to interpolated, but did not find a prefab with meta data");
                return false;
            }
            if (!EntityManager.HasComponent<PredictedGhostComponent>(entity))
            {
                m_NetDebugSystem.NetDebug.LogWarning("Trying to convert a ghost to interpolated, but it is already interpolated");
                return false;
            }

            ref var ghostMetaData = ref EntityManager.GetComponentData<GhostPrefabMetaDataComponent>(prefab).Value.Value;
            if (ghostMetaData.SupportedModes != GhostPrefabMetaData.GhostMode.Both)
            {
                m_NetDebugSystem.NetDebug.LogWarning("Trying to convert a ghost to interpolated, but it does not support both modes");
                return false;
            }
            if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Both)
            {
                m_NetDebugSystem.NetDebug.LogWarning("Trying to convert a ghost to interpolated, but it is owner predicted and owner predicted ghosts cannot be switched on demand");
                return false;
            }

            ref var toAdd = ref ghostMetaData.DisableOnPredictedClient;
            ref var toRemove = ref ghostMetaData.DisableOnInterpolatedClient;
            return AddRemoveComponents(entity, prefab, ref toAdd, ref toRemove, transitionDuration);
        }

        private unsafe bool AddRemoveComponents(Entity entity, Entity prefab, ref BlobArray<GhostPrefabMetaData.ComponentReference> toAdd, ref BlobArray<GhostPrefabMetaData.ComponentReference> toRemove, float duration)
        {
            var linkedEntityGroup = EntityManager.GetBuffer<LinkedEntityGroup>(entity).ToNativeArray(Allocator.Temp);
            var prefabLinkedEntityGroup = EntityManager.GetBuffer<LinkedEntityGroup>(prefab).ToNativeArray(Allocator.Temp);
            //Need copy because removing component will invalidate the buffer pointer, since introduce structural changes
            for (int add = 0; add < toAdd.Length; ++add)
            {
                var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toAdd[add].StableHash));
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (compType.IsChunkComponent || compType.IsSharedComponent)
                {
                    throw new InvalidOperationException("Ghosts with chunk or shared components cannot switch prediction");
                }
#endif
                EntityManager.AddComponent(linkedEntityGroup[toAdd[add].EntityIndex].Value, compType);
                if (compType.IsZeroSized)
                    continue;
                var typeInfo = TypeManager.GetTypeInfo(compType.TypeIndex);
                var typeHandle = EntityManager.GetDynamicComponentTypeHandle(compType);
                var sizeInChunk = typeInfo.SizeInChunk;
                var srcInfo = EntityManager.GetStorageInfo(prefabLinkedEntityGroup[toAdd[add].EntityIndex].Value);
                var dstInfo = EntityManager.GetStorageInfo(linkedEntityGroup[toAdd[add].EntityIndex].Value);
                if (compType.IsBuffer)
                {
                    var srcBuffer = srcInfo.Chunk.GetUntypedBufferAccessor(ref typeHandle);
                    var dstBuffer = dstInfo.Chunk.GetUntypedBufferAccessor(ref typeHandle);
                    dstBuffer.ResizeUninitialized(dstInfo.IndexInChunk, srcBuffer.Length);
                    var dstDataPtr = dstBuffer.GetUnsafeReadOnlyPtr(dstInfo.IndexInChunk);
                    var srcDataPtr = srcBuffer.GetUnsafeReadOnlyPtrAndLength(srcInfo.IndexInChunk, out var bufLen);
                    UnsafeUtility.MemCpy(dstDataPtr, srcDataPtr, typeInfo.ElementSize * bufLen);
                }
                else
                {
                    byte* src = (byte*)srcInfo.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(typeHandle, sizeInChunk).GetUnsafeReadOnlyPtr();
                    byte* dst = (byte*)dstInfo.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(typeHandle, sizeInChunk).GetUnsafePtr();
                    UnsafeUtility.MemCpy(dst + dstInfo.IndexInChunk*sizeInChunk, src + srcInfo.IndexInChunk*sizeInChunk, sizeInChunk);
                }
            }
            for (int rm = 0; rm < toRemove.Length; ++rm)
            {
                var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toRemove[rm].StableHash));
                EntityManager.RemoveComponent(linkedEntityGroup[toRemove[rm].EntityIndex].Value, compType);
            }
            if (duration > 0 &&
                EntityManager.HasComponent<LocalToWorld>(entity) &&
                EntityManager.HasComponent<Translation>(entity) &&
                EntityManager.HasComponent<Rotation>(entity))
            {
                EntityManager.AddComponentData(entity, new SwitchPredictionSmoothing
                    {
                        InitialPosition = EntityManager.GetComponentData<Translation>(entity).Value,
                        InitialRotation = EntityManager.GetComponentData<Rotation>(entity).Value,
                        CurrentFactor = 0,
                        Duration = duration,
                        SkipVersion = World.GetExistingSystem<GhostUpdateSystem>().LastSystemVersion
                    });
            }
            return true;
        }

        private void ConfigurePrespawnGhost(Entity entity, in GhostSpawnBuffer ghost)
        {
            if(ghost.PrespawnIndex == -1)
                throw new InvalidOperationException("respawning a pre-spawned ghost requires a valid prespawn index");
            EntityManager.AddComponentData(entity, new PreSpawnedGhostIndex {Value = ghost.PrespawnIndex});
            EntityManager.AddSharedComponentData(entity, new SceneSection
            {
                SceneGUID = ghost.SceneGUID,
                Section = ghost.SectionIndex
            });
        }

        private void ReportMissingPrefab()
        {
            m_NetDebugSystem.NetDebug.LogError($"Trying to spawn with a prefab which is not loaded");
            Entities
                .WithAll<NetworkIdComponent>()
                .WithNone<NetworkStreamDisconnected>()
                .WithoutBurst()
                .WithStructuralChanges()
                .ForEach((Entity entity) => {
                EntityManager.AddComponentData(entity, new NetworkStreamRequestDisconnect{Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
            }).Run();

        }
    }
}

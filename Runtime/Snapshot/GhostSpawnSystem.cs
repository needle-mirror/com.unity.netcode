using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport.Utilities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSpawnSystemGroup))]
    public class GhostSpawnSystem : SystemBase
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


        protected override void OnCreate()
        {
            m_DelayedSpawnQueue = new NativeQueue<DelayedSpawnGhost>(Allocator.Persistent);
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
            m_GhostReceiveSystem = World.GetOrCreateSystem<GhostReceiveSystem>();
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
                var ghostComponentData = EntityManager.GetComponentData<GhostComponent>(ghost.oldEntity);
                EntityManager.SetComponentData(entity, ghostComponentData);
                var oldBuffer = EntityManager.GetBuffer<SnapshotDataBuffer>(ghost.oldEntity).ToNativeArray(Allocator.Temp);
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
        private void ReportMissingPrefab()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.LogError($"Trying to spawn with a prefab which is not loaded");
#endif
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

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport.Utilities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Jobs.LowLevel.Unsafe;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSpawnSystemGroup))]
    [AlwaysUpdateSystem]
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
        private GhostCollectionSystem m_GhostCollectionSystem;


        protected override void OnCreate()
        {
            m_GhostCollectionSystem = World.GetOrCreateSystem<GhostCollectionSystem>();
            m_DelayedSpawnQueue = new NativeQueue<DelayedSpawnGhost>(Allocator.Persistent);
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
            m_GhostReceiveSystem = World.GetOrCreateSystem<GhostReceiveSystem>();
        }
        protected override void OnDestroy()
        {
            m_DelayedSpawnQueue.Dispose();
        }
        protected unsafe override void OnUpdate()
        {
            if (!HasSingleton<GhostPrefabCollectionComponent>() || !HasSingleton<GhostSpawnQueueComponent>())
                return;

            m_GhostReceiveSystem.LastGhostMapWriter.Complete();

            var interpolationTargetTick = m_ClientSimulationSystemGroup.InterpolationTick;
            if (m_ClientSimulationSystemGroup.InterpolationTickFraction < 1)
                --interpolationTargetTick;
            //var predictionTargetTick = m_ClientSimulationSystemGroup.ServerTick;
            var prefabs = GetSingleton<GhostPrefabCollectionComponent>();
            var interpolatedPrefabs = EntityManager.GetBuffer<GhostPrefabBuffer>(prefabs.clientInterpolatedPrefabs).ToNativeArray(Allocator.Temp);
            var predictedPrefabs = EntityManager.GetBuffer<GhostPrefabBuffer>(prefabs.clientPredictedPrefabs).ToNativeArray(Allocator.Temp);

            var ghostSpawnEntity = GetSingletonEntity<GhostSpawnQueueComponent>();
            var ghostSpawnBufferComponent = EntityManager.GetBuffer<GhostSpawnBuffer>(ghostSpawnEntity);
            var snapshotDataBufferComponent = EntityManager.GetBuffer<SnapshotDataBuffer>(ghostSpawnEntity);
            var ghostSpawnBuffer = ghostSpawnBufferComponent.ToNativeArray(Allocator.Temp);
            var snapshotDataBuffer = snapshotDataBufferComponent.ToNativeArray(Allocator.Temp);
            ghostSpawnBufferComponent.ResizeUninitialized(0);
            snapshotDataBufferComponent.ResizeUninitialized(0);

            var spawnedGhosts = new NativeList<SpawnedGhostMapping>(16, Allocator.Temp);
            var nonSpawnedGhosts = new NativeList<NonSpawnedGhostMapping>(16, Allocator.Temp);
            for (int i = 0; i < ghostSpawnBuffer.Length; ++i)
            {
                var ghost = ghostSpawnBuffer[i];
                Entity entity = Entity.Null;
                byte* snapshotData = null;
                var snapshotSize = m_GhostCollectionSystem.m_GhostTypeCollection[ghost.GhostType].SnapshotSize;
                if (ghost.SpawnType == GhostSpawnBuffer.Type.Interpolated)
                {
                    // Add to m_DelayedSpawnQueue
                    entity = EntityManager.CreateEntity();
                    EntityManager.AddComponentData(entity, new GhostComponent {ghostId = ghost.GhostID, ghostType = ghost.GhostType, spawnTick = ghost.ServerSpawnTick});
                    var newBuffer = EntityManager.AddBuffer<SnapshotDataBuffer>(entity);
                    newBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    snapshotData = (byte*)newBuffer.GetUnsafePtr();
                    EntityManager.AddComponentData(entity, new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0});
                    m_DelayedSpawnQueue.Enqueue(new GhostSpawnSystem.DelayedSpawnGhost{ghostId = ghost.GhostID, ghostType = ghost.GhostType, clientSpawnTick = ghost.ClientSpawnTick, serverSpawnTick = ghost.ServerSpawnTick, oldEntity = entity});
                    nonSpawnedGhosts.Add(new NonSpawnedGhostMapping{ghostId = ghost.GhostID, entity = entity});
                }
                else if (ghost.SpawnType == GhostSpawnBuffer.Type.Predicted)
                {
                    // Spawn directly
                    entity = ghost.PredictedSpawnEntity != Entity.Null ? ghost.PredictedSpawnEntity : EntityManager.Instantiate(predictedPrefabs[ghost.GhostType].Value);
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
                }
            }
            m_GhostReceiveSystem.AddNonSpawnedGhosts(nonSpawnedGhosts);
            m_GhostReceiveSystem.AddSpawnedGhosts(spawnedGhosts);

            spawnedGhosts.Clear();
            while (m_DelayedSpawnQueue.Count > 0 &&
                   !SequenceHelpers.IsNewer(m_DelayedSpawnQueue.Peek().clientSpawnTick, interpolationTargetTick))
            {
                var ghost = m_DelayedSpawnQueue.Dequeue();
                // Spawn actual entity
                Entity entity = EntityManager.Instantiate(interpolatedPrefabs[ghost.ghostType].Value);
                EntityManager.SetComponentData(entity, EntityManager.GetComponentData<SnapshotData>(ghost.oldEntity));
                var ghostComponentData = EntityManager.GetComponentData<GhostComponent>(ghost.oldEntity);
                EntityManager.SetComponentData(entity, ghostComponentData);
                var oldBuffer = EntityManager.GetBuffer<SnapshotDataBuffer>(ghost.oldEntity).ToNativeArray(Allocator.Temp);
                var newBuffer = EntityManager.GetBuffer<SnapshotDataBuffer>(entity);
                newBuffer.ResizeUninitialized(oldBuffer.Length);
                UnsafeUtility.MemCpy(newBuffer.GetUnsafePtr(), oldBuffer.GetUnsafeReadOnlyPtr(), oldBuffer.Length);
                EntityManager.DestroyEntity(ghost.oldEntity);

                spawnedGhosts.Add(new SpawnedGhostMapping{ghost = new SpawnedGhost{ghostId = ghost.ghostId, spawnTick = ghostComponentData.spawnTick}, entity = entity, previousEntity = ghost.oldEntity});
            }
            m_GhostReceiveSystem.UpdateSpawnedGhosts(spawnedGhosts);
        }
    }
}

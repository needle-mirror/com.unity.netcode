using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.NetCode
{
    public struct GhostDistancePartition : IComponentData
    {
        public int3 Index;
    }
    public struct GhostDistancePartitionShared : ISharedComponentData
    {
        public int3 Index;
    }

    [UpdateInWorld(TargetWorld.Server)]
    // Update before almost everything to make sure there is no DestroyEntity pending in the command buffer
    [UpdateInGroup(typeof(GhostSimulationSystemGroup), OrderFirst = true)]
    public partial class GhostDistancePartitioningSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            while (m_sharedComponentModificationQueue.TryDequeue(out var mod))
            {
                if (EntityManager.HasComponent<GhostDistancePartitionShared>(mod.entity))
                    EntityManager.SetSharedComponentData(mod.entity, new GhostDistancePartitionShared {Index = mod.index});
            }
            var config = GetSingleton<GhostDistanceImportance>();
            var barrier = World.GetExistingSystem<EndSimulationEntityCommandBufferSystem>();
            var commandBuffer = barrier.CreateCommandBuffer();
            var concurrentCommandBuffer = commandBuffer.AsParallelWriter();
            // FIXME: GhostComponent should use WithAll, but that requires a bugfix in entities
            Entities.WithoutBurst().WithNone<GhostDistancePartition>().ForEach((Entity ent, int entityInQueryIndex, in Translation trans, in GhostComponent ghost) =>
            {
                var tileIndex = ((int3) trans.Value - config.TileCenter) / config.TileSize;
                concurrentCommandBuffer.AddComponent(entityInQueryIndex, ent, new GhostDistancePartition{Index = tileIndex});
                concurrentCommandBuffer.AddSharedComponent(entityInQueryIndex, ent, new GhostDistancePartitionShared{Index = tileIndex});
            }).Schedule();
            barrier.AddJobHandleForProducer(Dependency);
            var queue = m_sharedComponentModificationQueue;
            var parallelQueue = queue.AsParallelWriter();
            Entities.ForEach((Entity ent, ref GhostDistancePartition tile, in Translation trans, in GhostComponent ghost) =>
            {
                var origTilePos = tile.Index * config.TileSize + config.TileCenter;
                if (math.all(trans.Value >= origTilePos - config.TileBorderWidth) &&
                    math.all(trans.Value <= origTilePos + config.TileSize + config.TileBorderWidth))
                    return;
                var tileIndex = ((int3) trans.Value - config.TileCenter) / config.TileSize;
                if (math.any(tile.Index != tileIndex))
                {
                    parallelQueue.Enqueue(new SharedMod
                    {
                        entity = ent,
                        index = tileIndex
                    });
                    tile.Index = tileIndex;
                }
            }).ScheduleParallel();
        }

        struct SharedMod
        {
            public Entity entity;
            public int3 index;
        }
        private NativeQueue<SharedMod> m_sharedComponentModificationQueue;

        protected override void OnCreate()
        {
            m_sharedComponentModificationQueue = new NativeQueue<SharedMod>(Allocator.Persistent);
            RequireSingletonForUpdate<GhostDistanceImportance>();
        }

        protected override void OnDestroy()
        {
            m_sharedComponentModificationQueue.Dispose();
        }
    }
}
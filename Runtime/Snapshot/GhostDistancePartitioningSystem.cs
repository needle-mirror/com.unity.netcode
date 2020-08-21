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

    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    public class GhostDistancePartitioningSystem : JobComponentSystem
    {
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var config = GetSingleton<GhostDistanceImportance>();
            var barrier = World.GetExistingSystem<EndSimulationEntityCommandBufferSystem>();
            var commandBuffer = barrier.CreateCommandBuffer();
            var concurrentCommandBuffer = commandBuffer.AsParallelWriter();
            // FIXME: GhostComponent should use WithAll, but that requires a bugfix in entities
            inputDeps = Entities.WithoutBurst().WithNone<GhostDistancePartition>().ForEach((Entity ent, int entityInQueryIndex, in Translation trans, in GhostComponent ghost) =>
            {
                var tileIndex = ((int3) trans.Value - config.TileCenter) / config.TileSize;
                concurrentCommandBuffer.AddComponent(entityInQueryIndex, ent, new GhostDistancePartition{Index = tileIndex});
                concurrentCommandBuffer.AddSharedComponent(entityInQueryIndex, ent, new GhostDistancePartitionShared{Index = tileIndex});
            }).Schedule(inputDeps);
            var queue = m_sharedComponentModificationQueue;
            var parallelQueue = queue.AsParallelWriter();
            inputDeps = Entities.ForEach((Entity ent, ref GhostDistancePartition tile, in Translation trans, in GhostComponent ghost) =>
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
            }).Schedule(inputDeps);
            var applyJob = new ApplySharedMod
            {
                queue = queue,
                commandBuffer = commandBuffer
            };
            inputDeps = applyJob.Schedule(inputDeps);
            barrier.AddJobHandleForProducer(inputDeps);

            return inputDeps;
        }

        struct SharedMod
        {
            public Entity entity;
            public int3 index;
        }
        private NativeQueue<SharedMod> m_sharedComponentModificationQueue;

        struct ApplySharedMod : IJob
        {
            public NativeQueue<SharedMod> queue;
            public EntityCommandBuffer commandBuffer;
            public void Execute()
            {
                while (queue.TryDequeue(out var mod))
                    commandBuffer.SetSharedComponent(mod.entity, new GhostDistancePartitionShared {Index = mod.index});
            }
        }

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
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Assertions;

namespace Unity.NetCode
{
    /// <summary>
    /// Index information per entity used for distance based Importance scaling.
    /// </summary>
    public struct GhostDistancePartitionShared : ISharedComponentData
    {
        /// <summary>
        /// Determines which tile index the entity belongs to.
        /// </summary>
        public int3 Index;
    }

    /// <summary>
    /// Computes index for each entity. The translation is used to compute the right tile index to assign to the <see cref="GhostDistancePartitionShared"/>.
    /// A tiles border width is used to allow for a buffer in which it will not swap over.
    /// Meaning that when an entity has crossed the border width over the end of the tile,
    /// the entity will be assign the neighboring tile index.
    /// To cross back the same border width distance must be traveled back to be reassigned to the original tile index.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    // Update before almost everything to make sure there is no DestroyEntity pending in the command buffer
    [UpdateInGroup(typeof(GhostSimulationSystemGroup), OrderFirst = true)]
    [BurstCompile]
    public partial struct GhostDistancePartitioningSystem : ISystem
    {
        EntityQuery m_EntityQuery;
        EntityTypeHandle m_EntityTypeHandle;
        ComponentTypeHandle<Translation> m_Translation;
        SharedComponentTypeHandle<GhostDistancePartitionShared> m_SharedPartition;

        [BurstCompile]
        struct UpdateTileIndexJob : IJobChunk
        {
            [ReadOnly] public SharedComponentTypeHandle<GhostDistancePartitionShared> TileTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Translation> TransHandle;
            [ReadOnly] public EntityTypeHandle EntityTypeHandle;
            public GhostDistanceData Config;
            public EntityCommandBuffer.ParallelWriter Ecb;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                var tile = chunk.GetSharedComponent(TileTypeHandle);
                var translations = chunk.GetNativeArray(TransHandle);
                var entities = chunk.GetNativeArray(EntityTypeHandle);

                for (var index = 0; index < translations.Length; index++)
                {
                    var translation = translations[index];
                    var origTilePos = tile.Index * Config.TileSize + Config.TileCenter;
                    if (math.all(translation.Value >= origTilePos - Config.TileBorderWidth) &&
                        math.all(translation.Value <= origTilePos + Config.TileSize + Config.TileBorderWidth))
                    {
                        continue;
                    }

                    var tileIndex = ((int3)translation.Value - Config.TileCenter) / Config.TileSize;
                    if (math.all(tile.Index == tileIndex))
                    {
                        continue;
                    }

                    var entity = entities[index];
                    Ecb.SetSharedComponent(unfilteredChunkIndex, entity, new GhostDistancePartitionShared { Index = tileIndex });
                }
            }
        }

        [BurstCompile]
        partial struct AddSharedDistancePartitionJob : IJobEntity
        {
            public GhostDistanceData Config;
            public EntityCommandBuffer.ParallelWriter ConcurrentCommandBuffer;

            void Execute(Entity ent, [EntityInQueryIndex]int entityInQueryIndex, in Translation trans, in GhostComponent ghost)
            {
                var tileIndex = ((int3) trans.Value - Config.TileCenter) / Config.TileSize;
                ConcurrentCommandBuffer.AddSharedComponent(entityInQueryIndex, ent, new GhostDistancePartitionShared{Index = tileIndex});
            }
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<GhostDistanceData>();

            var barrier = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>();
            var sharedPartitionHandle = new AddSharedDistancePartitionJob
            {
                ConcurrentCommandBuffer = barrier.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                Config = config,
            }.Schedule(state.Dependency);

            m_EntityTypeHandle.Update(ref state);
            m_Translation.Update(ref state);
            m_SharedPartition.Update(ref state);
            state.Dependency = new UpdateTileIndexJob
            {
                Config = config,
                Ecb = barrier.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                EntityTypeHandle = m_EntityTypeHandle,
                TileTypeHandle = m_SharedPartition,
                TransHandle = m_Translation,
            }.ScheduleParallel(m_EntityQuery, sharedPartitionHandle);
        }

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_Translation = state.GetComponentTypeHandle<Translation>(true);
            m_SharedPartition = state.GetSharedComponentTypeHandle<GhostDistancePartitionShared>();
            state.RequireForUpdate<GhostImportance>();
            state.RequireForUpdate<GhostDistanceData>();
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostDistancePartitionShared, Translation, GhostComponent>();
            m_EntityQuery = state.WorldUnmanaged.EntityManager.CreateEntityQuery(builder);
        }
    }
}

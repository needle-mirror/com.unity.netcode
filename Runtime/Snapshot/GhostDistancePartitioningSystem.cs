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
#if !ENABLE_TRANSFORM_V1
        ComponentTypeHandle<LocalTransform> m_Transform;
#else
        ComponentTypeHandle<Translation> m_Translation;
#endif
        SharedComponentTypeHandle<GhostDistancePartitionShared> m_SharedPartition;

        [BurstCompile]
        struct UpdateTileIndexJob : IJobChunk
        {
            [ReadOnly] public SharedComponentTypeHandle<GhostDistancePartitionShared> TileTypeHandle;
#if !ENABLE_TRANSFORM_V1
            [ReadOnly] public ComponentTypeHandle<LocalTransform> TransHandle;
#else
            [ReadOnly] public ComponentTypeHandle<Translation> TransHandle;
#endif
            [ReadOnly] public EntityTypeHandle EntityTypeHandle;
            public GhostDistanceData Config;
            public EntityCommandBuffer.ParallelWriter Ecb;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                var tile = chunk.GetSharedComponent(TileTypeHandle);
#if !ENABLE_TRANSFORM_V1
                var transforms = chunk.GetNativeArray(ref TransHandle);
#else
                var translations = chunk.GetNativeArray(ref TransHandle);
#endif
                var entities = chunk.GetNativeArray(EntityTypeHandle);

#if !ENABLE_TRANSFORM_V1
                for (var index = 0; index < transforms.Length; index++)
                {
                    var transform = transforms[index];
                    var origTilePos = tile.Index * Config.TileSize + Config.TileCenter;
                    if (math.all(transform.Position >= origTilePos - Config.TileBorderWidth) &&
                        math.all(transform.Position <= origTilePos + Config.TileSize + Config.TileBorderWidth))
#else
                for (var index = 0; index < translations.Length; index++)
                {
                    var translation = translations[index];
                    var origTilePos = tile.Index * Config.TileSize + Config.TileCenter;
                    if (math.all(translation.Value >= origTilePos - Config.TileBorderWidth) &&
                        math.all(translation.Value <= origTilePos + Config.TileSize + Config.TileBorderWidth))
#endif
                    {
                        continue;
                    }

#if !ENABLE_TRANSFORM_V1
                    var tileIndex = ((int3)transform.Position - Config.TileCenter) / Config.TileSize;
#else
                    var tileIndex = ((int3)translation.Value - Config.TileCenter) / Config.TileSize;
#endif
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

#if !ENABLE_TRANSFORM_V1
            void Execute(Entity ent, [EntityIndexInQuery]int entityIndexInQuery, in LocalTransform trans, in GhostComponent ghost)
            {
                var tileIndex = ((int3) trans.Position - Config.TileCenter) / Config.TileSize;
                ConcurrentCommandBuffer.AddSharedComponent(entityIndexInQuery, ent, new GhostDistancePartitionShared{Index = tileIndex});
            }
        }
#else
            void Execute(Entity ent, [EntityIndexInQuery]int entityIndexInQuery, in Translation trans, in GhostComponent ghost)
            {
                var tileIndex = ((int3) trans.Value - Config.TileCenter) / Config.TileSize;
                ConcurrentCommandBuffer.AddSharedComponent(entityIndexInQuery, ent, new GhostDistancePartitionShared{Index = tileIndex});
            }
        }
#endif
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<GhostDistanceData>();
#if ENABLE_UNITY_COLLECTIONS_CHECKS || NETCODE_DEBUG
            //Validate that the DistanceData contains valid ranges and values
            if (config.TileSize.Equals(int3.zero))
            {
                var netDebug = SystemAPI.GetSingleton<NetDebug>();
                netDebug.LogError("GhostDistanceData.TileSize must always be different than int3.zero. You must specify a non zero tile size for at least one of the axis.");
                return;
            }
            if (config.TileSize.x < 0 || config.TileSize.y < 0 || config.TileSize.z < 0)
            {
                var netDebug = SystemAPI.GetSingleton<NetDebug>();
                netDebug.LogError($"Invalid GhostDistanceData.TileSize ({config.TileSize}) set for GhostDistanceData singleton.\nThe tile size for each individual axis must be a value greater than or equals zero");
                return;
            }
#endif
            var barrier = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>();
            var sharedPartitionHandle = new AddSharedDistancePartitionJob
            {
                ConcurrentCommandBuffer = barrier.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                Config = config,
            }.Schedule(state.Dependency);

            m_EntityTypeHandle.Update(ref state);
#if !ENABLE_TRANSFORM_V1
            m_Transform.Update(ref state);
#else
            m_Translation.Update(ref state);
#endif
            m_SharedPartition.Update(ref state);

            state.Dependency = new UpdateTileIndexJob
            {
                Config = config,
                Ecb = barrier.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                EntityTypeHandle = m_EntityTypeHandle,
                TileTypeHandle = m_SharedPartition,
#if !ENABLE_TRANSFORM_V1
                TransHandle = m_Transform,
#else
                TransHandle = m_Translation,
#endif
            }.ScheduleParallel(m_EntityQuery, sharedPartitionHandle);
        }

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_EntityTypeHandle = state.GetEntityTypeHandle();
#if !ENABLE_TRANSFORM_V1
            m_Transform = state.GetComponentTypeHandle<LocalTransform>(true);
#else
            m_Translation = state.GetComponentTypeHandle<Translation>(true);
#endif
            m_SharedPartition = state.GetSharedComponentTypeHandle<GhostDistancePartitionShared>();
            state.RequireForUpdate<GhostImportance>();
            state.RequireForUpdate<GhostDistanceData>();
            var builder = new EntityQueryBuilder(Allocator.Temp)
#if !ENABLE_TRANSFORM_V1
                .WithAll<GhostDistancePartitionShared, LocalTransform, GhostComponent>();
#else
                .WithAll<GhostDistancePartitionShared, Translation, GhostComponent>();
#endif
            m_EntityQuery = state.WorldUnmanaged.EntityManager.CreateEntityQuery(builder);
        }
    }
}

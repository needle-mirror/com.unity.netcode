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
    ///     Automatically adds the <see cref="GhostDistancePartitionShared" /> shared component to each ghost instance on the
    ///     server (a structural change), and then updates said component - for each ghost instance - if its <see cref="LocalTransform.Position" />
    ///     changes to a new tile (which is also a structural change, as it needs to update a shared component value).
    ///     It does this every tick.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This system only operates if it detects the existence of the <see cref="GhostDistanceData" /> configuration
    ///         singleton component within a ServerWorld.
    ///     </para>
    ///     <para>
    ///         Note that adding the <see cref="GhostDistancePartitionShared" /> shared component to each ghost instance will
    ///         almost certainly exacerbate <see cref="ArchetypeChunk"/> entity fragmentation, as we're using the <see cref="ArchetypeChunk" />
    ///         system to spatially partition ghost instances. I.e. If there are (for example) just two ghosts of the same
    ///         archetype within a specific tile, the maximum utilization of their chunk will by 2.
    ///     </para>
    ///     <para>
    ///         Note that, due to; a) the number of changed positions this system needs to check; b) the frequency of
    ///         structural changes created by this system; and c) the fragmentation caused by the shared component itself,
    ///         the impact of enabling importance scaling should be measured, and benchmarked against other possible solutions.
    ///     </para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    // Update before almost everything to make sure there is no DestroyEntity pending in the command buffer
    [UpdateInGroup(typeof(GhostSimulationSystemGroup), OrderFirst = true)]
    [BurstCompile]
    public partial struct GhostDistancePartitioningSystem : ISystem, ISystemStartStop
    {
        EntityQuery m_DistancePartitionedEntitiesQuery;
        EntityTypeHandle m_EntityTypeHandle;
        ComponentTypeHandle<LocalTransform> m_Transform;
        SharedComponentTypeHandle<GhostDistancePartitionShared> m_SharedPartition;

        [BurstCompile]
        [WithChangeFilter(typeof(LocalTransform), typeof(GhostDistancePartitionShared))]
        // WithChangeFilter optimization; there is no need to re-calculate the tile index of each entity within this chunk if none of them have moved.
        struct UpdateTileIndexJob : IJobChunk
        {
            [ReadOnly] public SharedComponentTypeHandle<GhostDistancePartitionShared> TileTypeHandle;
            [ReadOnly] public ComponentTypeHandle<LocalTransform> TransHandle;
            [ReadOnly] public EntityTypeHandle EntityTypeHandle;
            public GhostDistanceData Config;
            public EntityCommandBuffer.ParallelWriter Ecb;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                var tile = chunk.GetSharedComponent(TileTypeHandle);
                var transforms = chunk.GetNativeArray(ref TransHandle);
                var entities = chunk.GetNativeArray(EntityTypeHandle);

                for (var index = 0; index < transforms.Length; index++)
                {
                    var transform = transforms[index];
                    var origTilePos = tile.Index * Config.TileSize + Config.TileCenter;
                    if (math.all(transform.Position >= origTilePos - Config.TileBorderWidth) &&
                        math.all(transform.Position <= origTilePos + Config.TileSize + Config.TileBorderWidth))
                    {
                        continue;
                    }

                    var tileIndex = ((int3)transform.Position - Config.TileCenter) / Config.TileSize;
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
        [WithAll(typeof(GhostInstance))]
        [WithAbsent(typeof(GhostDistancePartitionShared))]
        partial struct AddSharedDistancePartitionJob : IJobEntity
        {
            public GhostDistanceData Config;
            public EntityCommandBuffer.ParallelWriter ConcurrentCommandBuffer;

            void Execute(Entity ent, [ChunkIndexInQuery]int chunkIndexInQuery, in LocalTransform trans)
            {
                var tileIndex = ((int3) trans.Position - Config.TileCenter) / Config.TileSize;
                ConcurrentCommandBuffer.AddSharedComponent(chunkIndexInQuery, ent, new GhostDistancePartitionShared{Index = tileIndex});
            }
        }

        /// <inheritdoc/>
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
            }.Schedule(state.Dependency); // Using ScheduleParallel here reduces wall time - on ticks where hundreds of new ghosts spawn - by more than half.
                                          // However, it increases the mean time by ~7% due to scheduling overhead, seen also through worsening UpdateTileIndexJob timings.
                                          // Therefore, it is likely not worth it.

            m_EntityTypeHandle.Update(ref state);
            m_Transform.Update(ref state);
            m_SharedPartition.Update(ref state);

            state.Dependency = new UpdateTileIndexJob
            {
                Config = config,
                Ecb = barrier.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                EntityTypeHandle = m_EntityTypeHandle,
                TileTypeHandle = m_SharedPartition,
                TransHandle = m_Transform,
            }.ScheduleParallel(m_DistancePartitionedEntitiesQuery, sharedPartitionHandle);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_Transform = state.GetComponentTypeHandle<LocalTransform>(true);
            m_SharedPartition = state.GetSharedComponentTypeHandle<GhostDistancePartitionShared>();
            state.RequireForUpdate<GhostImportance>();
            state.RequireForUpdate<GhostDistanceData>();
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostDistancePartitionShared, LocalTransform, GhostInstance>();
            m_DistancePartitionedEntitiesQuery = state.GetEntityQuery(builder);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnStartRunning(ref SystemState state)
        {
        }

        /// <summary>
        /// Clean up any/all GhostDistancePartitionShared components that we've added.
        /// Note: This will not de-frag fragmented chunks automatically.
        /// </summary>
        /// <inheritdoc/>
        [BurstCompile]
        public void OnStopRunning(ref SystemState state)
        {
            state.EntityManager.RemoveComponent<GhostDistancePartitionShared>(m_DistancePartitionedEntitiesQuery);
        }
    }
}

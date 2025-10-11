using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// Add this component to each connection to determine which tiles the connection should prioritize.
    /// This will be passed as argument to the built-in scale function to compute Importance.
    /// See <see cref="GhostDistanceImportance"/> implementation.
    /// </summary>
    public struct GhostConnectionPosition : IComponentData
    {
        /// <summary>
        /// Position of the tile in world coordinates
        /// </summary>
        public float3 Position;
        /// <summary>
        /// Currently not updated by any systems. Made available for custom importance implementations.
        /// </summary>
        public quaternion Rotation;
        /// <summary>
        /// Currently not updated by any systems. Made available for custom importance implementations.
        /// </summary>
        public float4 ViewSize;
    }

    /// <summary>
    /// The default configuration data for <see cref="GhostImportance"/>.
    /// Uses tiling to group entities into spatial chunks, allowing chunks to be prioritized based on distance (via the
    /// <see cref="GhostDistancePartitioningSystem"/>), effectively giving you performant distance-based importance scaling.
    /// </summary>
    [Serializable]
    public struct GhostDistanceData : IComponentData
    {
        /// <summary>
        /// Dimensions of the tile.
        /// </summary>
        public int3 TileSize;
        /// <summary>
        /// Offset of the tile center
        /// </summary>
        public int3 TileCenter;
        /// <summary>
        /// An optimization. Denotes the width of each tiles border (each way i.e. plus AND minus).
        /// When deciding whether an entity has moved to another tile, this border value is added as an additional distance
        /// threshold requirement, reducing the frequency of expensive structural changes for ghosts that commonly move
        /// around a lot within a small area.
        /// </summary>
        public float3 TileBorderWidth;
    }

    /// <summary>
    /// This is the default implementation of the <see cref="GhostImportance"/> API. It computes a distance-based importance scaling factor.
    /// I.e. Entities far away from a clients importance focal point (via <see cref="GhostConnectionPosition"/>) will be sent less often.
    /// Further reading: https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/optimizations.html#importance-scaling
    /// </summary>
    [BurstCompile]
    public struct GhostDistanceImportance
    {
        /// <summary>
        /// Pointer to the <see cref="BatchScale"/> static method.
        /// </summary>
        public static readonly PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate> BatchScaleFunctionPointer =
            new PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate>(BatchScale);
        /// <summary>
        /// Pointer to the <see cref="BatchScaleWithRelevancy"/> static method.
        /// </summary>
        public static readonly PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate> BatchScaleWithRelevancyFunctionPointer =
            new PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate>(BatchScaleWithRelevancy);

        /// <summary>
        /// Pointer to the <see cref="CalculateDefaultScaledPriority"/> static method.
        /// </summary>
#pragma warning disable CS0618 // Type or member is obsolete
        public static readonly PortableFunctionPointer<GhostImportance.ScaleImportanceDelegate> ScaleFunctionPointer =
            new PortableFunctionPointer<GhostImportance.ScaleImportanceDelegate>(Scale);
#pragma warning restore CS0618 // Type or member is obsolete

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(GhostImportance.ScaleImportanceDelegate))]
        [Obsolete("Prefer `BatchScale` as it significantly reduces the total number of function pointer calls. RemoveAfter 1.x")]
        private static int Scale(IntPtr connectionDataPtr, IntPtr distanceDataPtr, IntPtr chunkTilePtr, int basePriority)
        {
            var distanceData = GhostComponentSerializer.TypeCast<GhostDistanceData>(distanceDataPtr);
            var centerTile = GhostDistancePartitioningSystem.CalculateTile(in distanceData, in GhostComponentSerializer.TypeCast<GhostConnectionPosition>(connectionDataPtr).Position);
            var chunkTile = GhostComponentSerializer.TypeCast<GhostDistancePartitionShared>(chunkTilePtr);
            return CalculateDefaultScaledPriority(basePriority, chunkTile, centerTile);
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(GhostImportance.BatchScaleImportanceDelegate))]
        private static unsafe void BatchScale(IntPtr connectionDataPtr, IntPtr distanceDataPtr, IntPtr sharedComponentTypeHandlePtr,
            ref UnsafeList<PrioChunk> chunks)
        {
            var distanceData = GhostComponentSerializer.TypeCast<GhostDistanceData>(distanceDataPtr);
            var centerTile = (int3)((GhostComponentSerializer.TypeCast<GhostConnectionPosition>(connectionDataPtr).Position - distanceData.TileCenter) / distanceData.TileSize);
            var sharedType = GhostComponentSerializer.TypeCast<DynamicSharedComponentTypeHandle>(sharedComponentTypeHandlePtr);
            for (int i = 0; i < chunks.Length; ++i)
            {
                ref var data = ref chunks.ElementAt(i);
                if (!data.chunk.Has(ref sharedType)) continue;
                var chunkTile = (GhostDistancePartitionShared*)data.chunk.GetDynamicSharedComponentDataAddress(ref sharedType);
                data.priority = CalculateDefaultScaledPriority(data.priority, in *chunkTile, centerTile);
            }
        }

        /// <summary>
        /// Default implementation of the distance scaling function.
        /// </summary>
        /// <param name="priority">The base priority</param>
        /// <param name="chunkTile">The chunk tile</param>
        /// <param name="centerTile">The center tile</param>
        /// <returns>The resulting priority</returns>
        public static int CalculateDefaultScaledPriority(int priority, in GhostDistancePartitionShared chunkTile, in int3 centerTile)
        {
            var delta = chunkTile.Index - centerTile;
            var distSq = math.dot(delta, delta);

            // 3 makes sure all adjacent tiles are considered the same as the tile the connection is in - required since it might be close to the edge
            if (distSq > 3)
                priority /= distSq;

            return priority;
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(GhostImportance.BatchScaleImportanceDelegate))]
        private static unsafe void BatchScaleWithRelevancy(IntPtr connectionDataPtr, IntPtr distanceDataPtr, IntPtr sharedComponentTypeHandlePtr,
            ref UnsafeList<PrioChunk> chunks)
        {
            var distanceData = GhostComponentSerializer.TypeCast<GhostDistanceData>(distanceDataPtr);
            var centerTile = (int3)((GhostComponentSerializer.TypeCast<GhostConnectionPosition>(connectionDataPtr).Position - distanceData.TileCenter) / distanceData.TileSize);
            var sharedType = GhostComponentSerializer.TypeCast<DynamicSharedComponentTypeHandle>(sharedComponentTypeHandlePtr);
            for (int i = 0; i < chunks.Length ; ++i)
            {
                ref var data = ref chunks.ElementAt(i);
                var basePriority = data.priority;
                if (data.chunk.Has(ref sharedType))
                {
                    var chunkTile = (GhostDistancePartitionShared*) data.chunk.GetDynamicSharedComponentDataAddress(ref sharedType);
                    var delta = chunkTile->Index - centerTile;
                    var distSq = math.dot(delta, delta);
                    basePriority *= 1000;
                    // 3 makes sure all adjacent tiles are considered the same as the tile the connection is in - required since it might be close to the edge
                    basePriority = math.select(basePriority, basePriority / math.max(1, distSq), distSq > 3);
                    data.priority = basePriority;
                    // Any chunks greater than 4 tiles from the player will be irrelevant (unless explicitly added to the `GhostRelevancySet`).
                    data.isRelevant = distSq <= 16;
                }
            }
        }
    }
}

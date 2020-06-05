using AOT;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.NetCode
{
    public struct GhostConnectionPosition : IComponentData
    {
        public float3 Position;
        public quaternion Rotation;
        public float4 ViewSize;
    }
    /// <summary>
    /// Singleton component used to control distance based importance settings
    /// </summary>
    [BurstCompile]
    public struct GhostDistanceImportance : IComponentData
    {
        public delegate int ScaleImportanceByDistanceDelegate(ref GhostConnectionPosition connectionPosition, ref int3 TileSize, ref int3 TileCenter, ref int3 chunkTile, int basePriority);
        public static PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate> DefaultScaleFunctionPointer => s_DefaultScaleFunctionPointer;
        public static PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate> NoScaleFunctionPointer => s_NoScaleFunctionPointer;
        private static PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate> s_DefaultScaleFunctionPointer =
            new PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate>(DefaultScale);
        private static PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate> s_NoScaleFunctionPointer =
            new PortableFunctionPointer<GhostDistanceImportance.ScaleImportanceByDistanceDelegate>(NoScale);
        [BurstCompile]
        [MonoPInvokeCallback(typeof(ScaleImportanceByDistanceDelegate))]
        private static int DefaultScale(ref GhostConnectionPosition connectionPosition, ref int3 TileSize, ref int3 TileCenter, ref int3 chunkTile, int basePriority)
        {
            var centerTile = ((int3) connectionPosition.Position - TileCenter) / TileSize;
            var delta = chunkTile - centerTile;
            var distSq = math.dot(delta, delta);
            basePriority *= 1000;
            // 3 makes sure all adjacent tiles are considered the same as the tile the connection is in - required since it might be close to the edge
            if (distSq > 3)
                basePriority /= distSq;
            return basePriority;
        }
        [BurstCompile]
        [MonoPInvokeCallback(typeof(ScaleImportanceByDistanceDelegate))]
        private static int NoScale(ref GhostConnectionPosition connectionPosition, ref int3 TileSize, ref int3 TileCenter, ref int3 pos, int basePrio)
        {
            return basePrio;
        }

        public PortableFunctionPointer<ScaleImportanceByDistanceDelegate> ScaleImportanceByDistance;
        public int3 TileSize;
        public int3 TileCenter;
        public float3 TileBorderWidth;
    }
}
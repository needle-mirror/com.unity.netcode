#if UNITY_EDITOR || NETCODE_DEBUG

using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    static class GhostStatsUtils
    {
        internal static ComponentType ComponentType(this UnsafeGhostStatsSnapshot.PerComponentStats self, int serializerIndex, in NativeArray<GhostComponentSerializer.State> serializerSingleton)
        {
            return serializerIndex == -1 ? default : serializerSingleton[serializerIndex].ComponentType;
        }

        internal static int SerializerIndex(this UnsafeGhostStatsSnapshot.PerComponentStats self, int index, in GhostCollectionPrefabSerializer typeData, in NativeArray<GhostComponentSerializer.State> serializerSingleton, in NativeArray<GhostCollectionComponentIndex> ghostComponentIndex)
        {
            if (typeData.FirstComponent + index >= ghostComponentIndex.Length) return -1;
            return ghostComponentIndex[typeData.FirstComponent + index].SerializerIndex;
        }

        internal static int SnapshotSize(this UnsafeGhostStatsSnapshot.PerComponentStats self, int serializerIndex, in NativeArray<GhostComponentSerializer.State> serializerSingleton)
        {
            return serializerIndex == -1 ? 0 : serializerSingleton[serializerIndex].SnapshotSize;
        }

        internal static FixedString512Bytes Name(this UnsafeGhostStatsSnapshot.PerGhostTypeStats self, int index, in NativeArray<GhostNames> names)
        {
            if (index < names.Length)
                return names[index].Name;
            return "[invalid index]";
        }

        internal static void IncrementWith(this NativeList<UnsafeGhostStatsSnapshot.PerComponentStats> self, in NativeList<UnsafeGhostStatsSnapshot.PerComponentStats> other)
        {
            self.Resize(other.Length, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < other.Length; i++)
            {
                self.ElementAt(i).IncrementWith(other[i]);
            }
        }

        internal static void ResetToDefault(this NativeList<UnsafeGhostStatsSnapshot.PerComponentStats> self)
        {
            for (int i = 0; i < self.Length; i++)
            {
                self.ElementAt(i).ResetToDefault();
            }
        }
    }
}
#endif

using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Wrapper struct to hold all the emitted metadata for a single frame in the Netcode profiler.
    /// </summary>
    struct ProfilerFrameMetadata
    {
        internal ProfilerMetrics ProfilerMetrics;
        internal NativeArray<UncompressedSizesPerType> UncompressedSizesPerType;
        internal NativeArray<GhostCollectionPrefabSerializer> PrefabSerializers;
        internal NativeArray<GhostComponentSerializer.State> SerializerStates;
        internal NativeArray<GhostCollectionComponentIndex> ComponentIndices;
        internal NativeArray<GhostNames> GhostNames;
        internal NetworkMetrics NetworkMetrics;
        internal NativeArray<PredictionErrorNames> PredictionErrors;
        internal NativeArray<PredictionErrorMetrics> PredictionErrorMetrics;
        internal NativeArray<uint> CommandStats;
    }
}

#if UNITY_EDITOR || NETCODE_DEBUG
using System;
using Unity.Collections;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Struct to hold the data for a single frame in the Netcode profiler.
    /// Created and filled whenever a frame is selected in the Netcode profiler.
    /// Data is provided by the ProfilerMetricsCollector system.
    /// </summary>
    [Serializable]
    struct NetcodeFrameData
    {
        internal uint frameCount;
        internal uint totalSizeSentByServerInBits;
        internal uint totalSizeReceivedByClientInBits;
        internal uint totalPacketCountSentByServer;
        internal uint totalPacketCountReceivedByClient;
        internal NetworkTick serverTickSent;
        internal NativeArray<TickData> tickData;
        internal float jitter;
        internal float rtt;
    }

    /// <summary>
    /// Struct to hold the data for a single tick in the Netcode profiler.
    /// Created and filled whenever a frame is selected in the Netcode profiler.
    /// Data is provided by the ProfilerMetricsCollector system.
    /// </summary>
    [Serializable]
    struct TickData
    {
        internal NetworkTick tick;
        internal uint packetCount;
        internal uint snapshotSizeInBits;
        internal uint totalInstanceCount;
        internal uint overheadSize;
        internal float timeScale;
        internal float interpolationDelay;
        internal float interpolationScale;
        internal float snapshotAgeMin;
        internal float snapshotAgeMax;
        internal NetworkTick inputTargetTick;
        internal uint commandSizeInBits;
        internal float commandAge;
        internal uint discardedPackets;
        internal NativeArray<ProfilerGhostTypeData> ghostTypeData;
        internal NativeArray<PredictionErrorData> predictionErrors;
    }

    /// <summary>
    /// Struct to hold the data for a single Ghost Type or Ghost Component Type in the Netcode profiler.
    /// Created and filled whenever a frame is selected in the Netcode profiler.
    /// Data is provided by the ProfilerMetricsCollector system.
    /// </summary>
    [Serializable]
    struct ProfilerGhostTypeData
    {
        internal FixedString128Bytes name;
        internal uint sizeInBits;
        internal int instanceCount;
        internal uint overheadSize;
        internal float combinedCompressionEfficiency;
        internal float avgSizePerEntity;
        internal NativeArray<ProfilerGhostTypeData> componentsPerType;
        internal bool needsOverheadIcon;
        internal uint newInstancesCount;
    }

    [Serializable]
    struct PredictionErrorData
    {
        internal FixedString128Bytes name;
        internal float errorValue;
    }
}
#endif

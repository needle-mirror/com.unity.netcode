using System;
using Unity.Collections;
using Unity.Entities;

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
        internal bool isValid;
        internal uint totalSizeSentByServerInBits;
        internal uint totalSizeReceivedByClientInBits;
        internal uint totalSnapshotCountSentByServer;
        internal uint totalSnapshotCountReceivedByClient;
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
        internal NetworkTick interpolationTick;
        internal uint packetCount;
        internal uint snapshotCount;
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
        internal int percentageOfSnapshot; // Stored as an integer percentage (0-100)
        internal int instanceCount;
        internal uint snapshotCount;
        internal uint overheadSize;
        internal float combinedCompressionEfficiency;
        internal float avgSizePerEntity;
        internal NativeArray<ProfilerGhostTypeData> componentsPerType;
        internal OverheadType overheadType;
        internal uint newInstancesCount;
        internal bool isGhostPrefab;
        internal TypeIndex typeIndex;
    }

    [Serializable]
    struct PredictionErrorData
    {
        internal FixedString128Bytes name;
        internal float errorValue;
    }
}

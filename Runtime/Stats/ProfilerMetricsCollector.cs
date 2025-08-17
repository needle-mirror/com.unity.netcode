#if (UNITY_EDITOR || NETCODE_DEBUG) && (NETCODE_PROFILER_ENABLED && UNITY_6000_0_OR_NEWER)
using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode
{
    // Profiler counters and additional metrics
    struct ProfilerMetrics : IComponentData
    {
        // Size sent by server accumulated over the whole profiler run
        internal uint TotalSizeSentByServerInBits;

        // Total packet count sent by server over the whole profiler run
        internal uint TotalPacketCountSentByServer;

        // Size received by client accumulated over the whole profiler run
        internal uint TotalSizeReceivedByClientInBits;

        // Total packet count received by client over the whole profiler run
        internal uint TotalPacketCountReceivedByClient;

        // Server World Counters
        internal ProfilerCounterValue<uint> ServerGhostInstancesCounter;
        internal ProfilerCounterValue<uint> ServerGhostSnapshotCounter;

        // Client World Counters
        internal ProfilerCounterValue<uint> ClientGhostInstancesCounter;
        internal ProfilerCounterValue<uint> ClientGhostSnapshotCounter;
        internal ProfilerCounterValue<float> JitterCounter;
        internal ProfilerCounterValue<float> RttCounter;
        internal ProfilerCounterValue<float> SnapshotAgeMinCounter;
        internal ProfilerCounterValue<float> SnapshotAgeMaxCounter;

        // ServerTick
        internal NetworkTick ServerTick;
    }

    /// <summary>
    /// Contains the uncompressed size in bits for each ghost type.
    /// This is used to calculate the compression efficiency in the N4E profiler modules.
    /// Only the size of every type in the GhostCollectionPrefab is needed so we just save the snapshot size for each
    /// entry of the GhostCollectionPrefabSerializer.
    /// </summary>
    [InternalBufferCapacity(0)]
    struct UncompressedSizesPerType : IBufferElementData
    {
        internal uint SizeInBytes;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    partial class ProfilerMetricsCollector : SystemBase
    {
        static readonly ComponentType[] k_RequiredStatsComponents =
        {
            ComponentType.ReadOnly<GhostMetricsMonitor>(),
            ComponentType.ReadOnly<NetworkMetrics>(),
            ComponentType.ReadOnly<GhostNames>(),
            ComponentType.ReadOnly<GhostMetrics>(),
            ComponentType.ReadOnly<PredictionErrorNames>(),
            ComponentType.ReadOnly<PredictionErrorMetrics>()
        };

        // Flag to know if we have already initialized the metrics collection.
        bool m_MetricsCollectionEnabled;
        // Flag to know if we are waiting for a connection in order to set the uncompressed sizes per type once we have one.
        bool m_WaitForConnection;
        // Flag to know if we need to clean up the profiler metrics.
        bool m_IsCleanedUp = true;

        void Initialize()
        {
            m_IsCleanedUp = false;

            if (!SystemAPI.TryGetSingletonEntity<ProfilerMetrics>(out var profilerMetricsSingleton))
                profilerMetricsSingleton = EntityManager.CreateSingleton<ProfilerMetrics>("ProfilerMetrics");

            if (!SystemAPI.TryGetSingletonEntity<UncompressedSizesPerType>(out _))
                EntityManager.CreateSingletonBuffer<UncompressedSizesPerType>("UncompressedSizesPerType");

            var profilerMetrics = new ProfilerMetrics
            {
                TotalSizeSentByServerInBits = 0,
                TotalPacketCountSentByServer = 0,
                TotalSizeReceivedByClientInBits = 0,
                TotalPacketCountReceivedByClient = 0,
                ServerTick = new NetworkTick()
            };

            if (World.IsServer())
            {
                profilerMetrics.ServerGhostInstancesCounter = new ProfilerCounterValue<uint>(ProfilerCategory.Network, ProfilerMetricsConstants.GhostInstancesCounterNameServer, ProfilerMarkerDataUnit.Count);
                profilerMetrics.ServerGhostSnapshotCounter = new ProfilerCounterValue<uint>(ProfilerCategory.Network, ProfilerMetricsConstants.GhostSnapshotsCounterNameServer, ProfilerMarkerDataUnit.Bytes);
            }
            else
            {
                profilerMetrics.ClientGhostInstancesCounter = new ProfilerCounterValue<uint>(ProfilerCategory.Network, ProfilerMetricsConstants.GhostInstancesCounterNameClient, ProfilerMarkerDataUnit.Count);
                profilerMetrics.ClientGhostSnapshotCounter = new ProfilerCounterValue<uint>(ProfilerCategory.Network, ProfilerMetricsConstants.GhostSnapshotsCounterNameClient, ProfilerMarkerDataUnit.Bytes);
                profilerMetrics.JitterCounter = new ProfilerCounterValue<float>(ProfilerCategory.Network, ProfilerMetricsConstants.JitterCounterName, ProfilerMarkerDataUnit.TimeNanoseconds);
                profilerMetrics.RttCounter = new ProfilerCounterValue<float>(ProfilerCategory.Network, ProfilerMetricsConstants.RTTCounterName, ProfilerMarkerDataUnit.TimeNanoseconds);
                profilerMetrics.SnapshotAgeMinCounter = new ProfilerCounterValue<float>(ProfilerCategory.Network, ProfilerMetricsConstants.SnapshotAgeMinCounterName, ProfilerMarkerDataUnit.Count);
                profilerMetrics.SnapshotAgeMaxCounter = new ProfilerCounterValue<float>(ProfilerCategory.Network, ProfilerMetricsConstants.SnapshotAgeMaxCounterName, ProfilerMarkerDataUnit.Count);
            }

            EntityManager.AddComponentData(profilerMetricsSingleton, profilerMetrics);

            if (!EntityManager.CreateEntityQuery(typeof(GhostMetricsMonitor)).TryGetSingletonEntity<GhostMetricsMonitor>(out var singletonEntity))
            {
                // Create a new GhostMetricsMonitor singleton entity if it doesn't exist.
                CreateGhostMetricsMonitorSingleton();
            }
            else
            {
                // In this case there was already a GhostMetricsMonitor singleton created by the user.
                // We will notify the user that their GhostMetricsMonitor will be destroyed and that they need to
                // recreate it when the profiler is disabled.
                Debug.LogWarning("A GhostMetricsMonitor singleton already exists in the world.\n " +
                    "This will be destroyed and recreated by the ProfilerMetricsCollector system.\n " +
                    "Please recreate your GhostMetricsMonitor after disabling the profiler.");

                EntityManager.DestroyEntity(singletonEntity);
                CreateGhostMetricsMonitorSingleton();
            }

            m_MetricsCollectionEnabled = true;
        }

        void CreateGhostMetricsMonitorSingleton()
        {
            var typeList = new NativeArray<ComponentType>(k_RequiredStatsComponents, Allocator.Temp);
            var metricSingleton = EntityManager.CreateEntity(EntityManager.CreateArchetype(typeList));
            EntityManager.SetName(metricSingleton, "MetricsMonitor");
        }

        protected override void OnUpdate()
        {
            if (!Profiler.enabled)
            {
                // We have no way to get notified when the profiler is disabled, so we clean up the metrics once and set a flag.
                Cleanup();
                return;
            }

            if (!m_MetricsCollectionEnabled)
                Initialize();

            // This also checks for NetworkStreamInGame, so it's important to call it before we potentially early-out
            // due to empty stats.
            SetUncompressedSizesPerType();

            var ghostStatsSnapshot = SystemAPI.GetSingleton<GhostStatsSnapshotSingleton>().GetAsyncStatsReader();
            var ghostTypeStats = ghostStatsSnapshot.PerGhostTypeStatsListRO;
            var hasSnapshotStats = ghostTypeStats.IsCreated && ghostTypeStats.Length > 0;
            if (!hasSnapshotStats) return;

            var ghostMetrics = SystemAPI.GetSingletonBuffer<GhostMetrics>();
            var hasGhostMetrics = ghostMetrics.IsCreated && ghostMetrics.Length > 0;
            if (!hasGhostMetrics) return;

            var profilerMetrics = SystemAPI.GetSingleton<ProfilerMetrics>();
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            profilerMetrics.ServerTick = networkTime.ServerTick;

            UpdateProfilerCounters(ghostStatsSnapshot, ref profilerMetrics);

            if (World.IsServer())
            {
                profilerMetrics.TotalSizeSentByServerInBits += ghostStatsSnapshot.SnapshotTotalSizeInBits;
                profilerMetrics.TotalPacketCountSentByServer += ghostStatsSnapshot.PacketsCount;
            }
            else
            {
                profilerMetrics.TotalSizeReceivedByClientInBits += ghostStatsSnapshot.SnapshotTotalSizeInBits;
                profilerMetrics.TotalPacketCountReceivedByClient += ghostStatsSnapshot.PacketsCount;
            }

            SystemAPI.SetSingleton(profilerMetrics);

            // Serialize component stats.
            var serializedGhostStatsSnapshot = ghostStatsSnapshot.ToBlittableData(Allocator.Temp);

            var guid = World.IsServer() ? ProfilerMetricsConstants.ServerGuid : ProfilerMetricsConstants.ClientGuid;

            // Send the data to the profiler.
            EmitNetcodeFrameMetaData(guid, serializedGhostStatsSnapshot);
        }

        void SetUncompressedSizesPerType()
        {
            var uncompressedSizesPerType = SystemAPI.GetSingletonBuffer<UncompressedSizesPerType>();

            // We only set uncompressed sizes at the start and when they change.
            if (uncompressedSizesPerType.IsEmpty && !m_WaitForConnection)
            {
                // Initial setup
                var serializers = SystemAPI.GetSingletonBuffer<GhostCollectionPrefabSerializer>();
                if (serializers.IsEmpty)
                    return;

                uncompressedSizesPerType.Resize(serializers.Length, NativeArrayOptions.ClearMemory);
                for (var i = 0; i < serializers.Length; i++)
                {
                    uncompressedSizesPerType.ElementAt(i).SizeInBytes = (uint)serializers[i].SnapshotSize;
                }

                return;
            }

            // Check if we need to rebuild the buffer.
            if (SystemAPI.QueryBuilder().WithAll<NetworkStreamInGame>().Build().CalculateEntityCount() == 0)
            {
                // Wait until we have a connection again so we can rebuild the buffer.
                m_WaitForConnection = true;
                uncompressedSizesPerType.Clear();
            }
            else
            {
                if (m_WaitForConnection)
                {
                    m_WaitForConnection = false;
                    SetUncompressedSizesPerType();
                }
            }
        }

        void UpdateProfilerCounters(UnsafeGhostStatsSnapshot ghostStatsSnapshot, ref ProfilerMetrics profilerMetrics)
        {
            var ghostTypeStats = ghostStatsSnapshot.PerGhostTypeStatsListRO;
            uint instancesCount = 0;
            for (var i = 0; i < ghostTypeStats.Length; i++)
            {
                instancesCount += ghostTypeStats[i].EntityCount;
            }

            // Update Graph Counters
            if (World.IsServer())
            {
                profilerMetrics.ServerGhostInstancesCounter.Value = instancesCount;
                profilerMetrics.ServerGhostSnapshotCounter.Value = ghostStatsSnapshot.SnapshotTotalSizeInBits >> 3; // Convert to bytes;
            }
            else
            {
                profilerMetrics.ClientGhostInstancesCounter.Value = instancesCount;
                profilerMetrics.ClientGhostSnapshotCounter.Value = ghostStatsSnapshot.SnapshotTotalSizeInBits >> 3; // Convert to bytes

                var networkMetrics = SystemAPI.GetSingleton<NetworkMetrics>();
                // Profiler expects nanoseconds as base unit
                profilerMetrics.JitterCounter.Value = networkMetrics.Jitter * 1_000_000f;
                profilerMetrics.RttCounter.Value = networkMetrics.Rtt * 1_000_000f;
                profilerMetrics.SnapshotAgeMinCounter.Value = networkMetrics.SnapshotAgeMin;
                profilerMetrics.SnapshotAgeMaxCounter.Value = networkMetrics.SnapshotAgeMax;
            }
        }

        [Conditional("ENABLE_PROFILER")]
        void EmitNetcodeFrameMetaData(Guid guid, NativeArray<byte> serializedGhostStatsSnapshot)
        {
            var serializers = SystemAPI.GetSingletonBuffer<GhostCollectionPrefabSerializer>();
            var serializerStates = SystemAPI.GetSingletonBuffer<GhostComponentSerializer.State>();
            var ghostCollectionComponentIndices = SystemAPI.GetSingletonBuffer<GhostCollectionComponentIndex>();
            var commandStats = SystemAPI.GetComponent<GhostStatsCollectionCommand>(SystemAPI.GetSingletonEntity<GhostStatsCollectionCommand>());
            var targetTick = commandStats.Value[0];
            var commandStatsSize = commandStats.Value[1];
            var discardedPackets = commandStats.Value[2];

            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.NetworkMetricsTag, new[] { SystemAPI.GetSingleton<NetworkMetrics>() });
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.GhostNamesTag, SystemAPI.GetSingletonBuffer<GhostNames>().AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.GhostMetricsTag, SystemAPI.GetSingletonBuffer<GhostMetrics>().AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.PredictionErrorNamesTag, SystemAPI.GetSingletonBuffer<PredictionErrorNames>().AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.PredictionErrorMetricsTag, SystemAPI.GetSingletonBuffer<PredictionErrorMetrics>().AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.ProfilerMetricsTag, new[] { SystemAPI.GetSingleton<ProfilerMetrics>() });
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.UncompressedSizesPerTypeTag, SystemAPI.GetSingletonBuffer<UncompressedSizesPerType>().AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.SerializedGhostStatsSnapshotTag, serializedGhostStatsSnapshot);
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.PrefabSerializersTag, serializers.AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.SerializerStatesTag, serializerStates.AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.ComponentIndicesTag, ghostCollectionComponentIndices.AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.CommandStatsTag, new [] { targetTick, commandStatsSize, discardedPackets });
        }

        void Cleanup()
        {
            if (m_IsCleanedUp)
                return;

            DestroySingletonEntity<GhostMetricsMonitor>();

            m_MetricsCollectionEnabled = false;
            m_IsCleanedUp = true;
        }

        protected override void OnDestroy()
        {
            Cleanup();
            DestroySingletonEntity<ProfilerMetrics>();
        }

        void DestroySingletonEntity<T>() where T : unmanaged, IComponentData
        {
            if (SystemAPI.TryGetSingletonEntity<T>(out var singletonEntity))
                EntityManager.DestroyEntity(singletonEntity);
        }
    }
}
#endif

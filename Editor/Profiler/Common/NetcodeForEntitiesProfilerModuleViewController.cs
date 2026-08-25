using System;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Profiling.Editor;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    [Serializable]
    class NetcodeForEntitiesProfilerModuleViewController : ProfilerModuleViewController
    {
        const string k_StyleSheetPath = "Packages/com.unity.netcode/Editor/Profiler/netcode-profiler.uss";
        const string k_VariablesDarkPath = "Packages/com.unity.netcode/Editor/Profiler/profiler-vars-dark.uss";
        const string k_VariablesLightPath = "Packages/com.unity.netcode/Editor/Profiler/profiler-vars-light.uss";

        NetworkRole m_NetworkRole;
        TabView m_TabView;
        GhostSnapshotsTab m_GhostSnapshotTab;
        FrameOverviewTab m_FrameOverViewTab;
        PredictionInterpolationTab m_PredictionInterpolationTab;
        NativeArray<UncompressedSizesPerType> m_UncompressedSizesArrayServer;
        NativeArray<UncompressedSizesPerType> m_UncompressedSizesArrayClient;
        MetricsHeader m_MetricsHeader;
        string m_CachedWorldName;

        bool m_CachedIsHostMode;
        bool m_CachedHasConnectedClients;
        bool m_WorldConfigFlagsCached;

        internal NetcodeForEntitiesProfilerModuleViewController(ProfilerWindow profilerWindow, NetworkRole networkRole)
            : base(profilerWindow)
        {
            m_NetworkRole = networkRole;
            SnapshotTickMappingSingleton.instance.Initialize();
        }

        // Initialization of the view controller for events and UI.
        protected override VisualElement CreateView()
        {
            ProfilerWindow.SelectedFrameIndexChanged += OnSelectedFrameIndexChanged;
            ProfilerDriver.profileCleared += OnProfileCleared;

            var container = new VisualElement();
            var ussFile = AssetDatabase.LoadAssetAtPath<StyleSheet>(k_StyleSheetPath);

            var ussVariables = EditorGUIUtility.isProSkin
                ? AssetDatabase.LoadAssetAtPath<StyleSheet>(k_VariablesDarkPath)
                : AssetDatabase.LoadAssetAtPath<StyleSheet>(k_VariablesLightPath);

            container.styleSheets.Add(ussFile);
            container.styleSheets.Add(ussVariables);
            var networkRolePrefix = m_NetworkRole.ToString();
            m_TabView ??= new TabView { viewDataKey = $"N4E{networkRolePrefix}ProfilerTabView" };

            m_FrameOverViewTab ??= new FrameOverviewTab(m_NetworkRole);
            m_FrameOverViewTab.EnableSnapshotViewDetailsCallback(ActivateGhostSnapshotTab, true);
            m_TabView.Add(m_FrameOverViewTab);

            m_GhostSnapshotTab ??= new GhostSnapshotsTab(m_NetworkRole);
            m_TabView.Add(m_GhostSnapshotTab);

            m_PredictionInterpolationTab ??= new PredictionInterpolationTab(m_NetworkRole);
            m_TabView.Add(m_PredictionInterpolationTab);

            container.Add(m_TabView);

            m_MetricsHeader = new MetricsHeader(m_NetworkRole);
            ((NetcodeProfilerTab)m_TabView.activeTab).AddMetricsHeader(m_MetricsHeader);

            m_TabView.activeTabChanged += OnActiveTabChanged;

            var frameToSelect = ProfilerWindow.selectedFrameIndex == -1 ? ProfilerWindow.lastAvailableFrameIndex : ProfilerWindow.selectedFrameIndex;

            // unfortunately we need to wait a bit before we can select the frame index
            // because the profiler window is not fully initialized yet.
            container.schedule.Execute(() =>
            {
                OnSelectedFrameIndexChanged(frameToSelect);
            }).ExecuteLater(10);

            return container;
        }

        void OnActiveTabChanged(Tab oldTab, Tab newTab)
        {
            ((NetcodeProfilerTab)newTab).AddMetricsHeader(m_MetricsHeader);
        }

        void ActivateGhostSnapshotTab()
        {
            m_TabView.activeTab = m_GhostSnapshotTab;
        }

        void UpdateTabs(NetcodeFrameData frameData)
        {
            m_GhostSnapshotTab.Update(frameData);
            m_FrameOverViewTab.Update(frameData);
            m_PredictionInterpolationTab.Update(frameData);
            UpdateMetricsHeader(frameData, m_NetworkRole);
        }

        void UpdateMetricsHeader(NetcodeFrameData frameData, NetworkRole networkRole)
        {
            if (m_MetricsHeader == null)
                return;

            // Use world name from frame data, fallback to default if empty
            var worldName = !string.IsNullOrEmpty(frameData.worldName)
                ? frameData.worldName
                : ProfilerUtils.GetWorldName(networkRole);

            m_MetricsHeader.SetWorldName(worldName);

            // Update the network role icon based on host mode
            if (frameData.isHostMode)
            {
                m_MetricsHeader.SetNetworkRoleIcon(MetricsHeader.k_IconHostUssClass);
            }
            else
            {
                var roleIconClass = networkRole == NetworkRole.Server ? MetricsHeader.k_IconServerUssClass : MetricsHeader.k_IconClientUssClass;
                m_MetricsHeader.SetNetworkRoleIcon(roleIconClass);
            }

            if (!frameData.isValid)
            {
                m_MetricsHeader.SetSnapshotTick("None");
                return;
            }

            m_MetricsHeader.SetSnapshotTick(frameData.tickData[0].tick);
            if (networkRole == NetworkRole.Client)
            {
                m_MetricsHeader.SetTotalSize(ProfilerUtils.FormatBitsToBytes(frameData.totalSizeReceivedByClientInBits));
                m_MetricsHeader.SetTotalPackets(frameData.totalSnapshotCountReceivedByClient);
                m_MetricsHeader.SetJitter(frameData.jitter);
                m_MetricsHeader.SetRtt(frameData.rtt);
            }
            else
            {
                m_MetricsHeader.SetTotalSize(ProfilerUtils.FormatBitsToBytes(frameData.totalSizeSentByServerInBits));
                m_MetricsHeader.SetTotalPackets(frameData.totalSnapshotCountSentByServer);
            }
        }

        void OnProfileCleared()
        {
            // Clear the header values
            m_MetricsHeader.ClearValues();

            // Clear cached world name and config flags for next capture session
            m_CachedWorldName = null;
            m_WorldConfigFlagsCached = false;
            m_CachedIsHostMode = false;
            m_CachedHasConnectedClients = false;

            // Reset icon to default based on network role
            var roleIconClass = m_NetworkRole == NetworkRole.Server ? MetricsHeader.k_IconServerUssClass : MetricsHeader.k_IconClientUssClass;
            m_MetricsHeader.SetNetworkRoleIcon(roleIconClass);

            if (m_UncompressedSizesArrayServer.IsCreated)
                m_UncompressedSizesArrayServer.Dispose();

            if (m_UncompressedSizesArrayClient.IsCreated)
                m_UncompressedSizesArrayClient.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            DisposeTabs();
            m_FrameOverViewTab.EnableSnapshotViewDetailsCallback(ActivateGhostSnapshotTab, false);
            ProfilerWindow.SelectedFrameIndexChanged -= OnSelectedFrameIndexChanged;
            ProfilerDriver.profileCleared -= OnProfileCleared;
            base.Dispose(true);
        }

        void DisposeTabs()
        {
            m_FrameOverViewTab.Dispose();
            m_GhostSnapshotTab.Dispose();
            m_PredictionInterpolationTab.Dispose();
        }

        // Called when the selected frame index changes in the profiler window.
        // Also called continuously during a profiler run.
        void OnSelectedFrameIndexChanged(long selectedFrameIndex)
        {
            if (selectedFrameIndex == -1)
                return;

            var frameData = BuildFrameData(selectedFrameIndex);
            UpdateTabs(frameData);
        }

        // Main method to build the relevant netcode frame data based on the selected frame.
        internal NetcodeFrameData BuildFrameData(long selectedFrameIndex)
        {
            using (var frameDataView = ProfilerDriver.GetRawFrameDataView((int)selectedFrameIndex, 0))
            {
                if (frameDataView is not { valid: true })
                {
                    return new NetcodeFrameData
                    {
                        isValid = false,
                        worldName = GetWorldName(frameDataView),
                        hasWorldMetadata = m_WorldConfigFlagsCached
                    };
                }

                // Get the correct GUID to get frame metadata based on the active profiler module
                var guid = GetGUID();

                // Always try to read world configuration flags (host mode, connected clients)
                // even when there's no snapshot data, so we can show appropriate info messages
                var (isHostMode, hasConnectedClients) = GetWorldConfigFlags(frameDataView);

                var invalidNetcodeFrameData = new NetcodeFrameData
                {
                    isValid = false,
                    isHostMode = isHostMode,
                    hasConnectedClients = hasConnectedClients,
                    worldName = GetWorldName(frameDataView),
                    hasWorldMetadata = m_WorldConfigFlagsCached
                };

                // Get the serialized ghost stats
                var serializedGhostStatsSnapshot = frameDataView.GetFrameMetaData<byte>(guid, ProfilerMetricsConstants.SerializedGhostStatsSnapshotTag);
                if (serializedGhostStatsSnapshot.Length == 0)
                {
                    return invalidNetcodeFrameData;
                }

                // Deserialize the ghost stats
                var ghostStatsSnapshot = UnsafeGhostStatsSnapshot.FromBlittableData(Allocator.Temp, serializedGhostStatsSnapshot);
                if (!ghostStatsSnapshot.Tick.IsValid)
                {
                    return invalidNetcodeFrameData;
                }

                // Check if this tick was sent or received in a previous frame already
                if (!SnapshotTickMappingSingleton.instance.FrameBelongsToTick((int)selectedFrameIndex, m_NetworkRole, ghostStatsSnapshot.Tick))
                {
                    return invalidNetcodeFrameData;
                }

                var perGhostTypeStats = ghostStatsSnapshot.PerGhostTypeStatsListRO;

                var profilerFrameMetaData = GetProfilerFrameMetaData(frameDataView, guid);
                var uncompressedSizes = GetUncompressedSizes(profilerFrameMetaData.UncompressedSizesPerType);
                var tickData = CreateTickData(ghostStatsSnapshot, profilerFrameMetaData);

                uint ghostTypesSizeInBits = 0;
                uint totalInstanceCount = 0;

                // Iterate ghosts
                for (var ghostIndex = 0; ghostIndex < profilerFrameMetaData.GhostNames.Length; ghostIndex++)
                {
                    var ghostTypeStats = perGhostTypeStats[ghostIndex];

                    uint sumComponentTypeSizePerType = 0;
                    ghostTypesSizeInBits += ghostTypeStats.SizeInBits;
                    totalInstanceCount += ghostTypeStats.EntityCount;

                    // Get component stats for this ghost type
                    var componentTypesData = CreateComponentTypesData(ghostTypeStats,
                        profilerFrameMetaData,
                        ghostIndex,
                        tickData.snapshotSizeInBits,
                        ref sumComponentTypeSizePerType,
                        ghostStatsSnapshot.SnapshotCount);

                    var ghostTypeData = CreateGhostTypeData(profilerFrameMetaData, ghostIndex, ghostTypeStats, componentTypesData, uncompressedSizes, sumComponentTypeSizePerType, ghostStatsSnapshot.SnapshotTotalSizeInBits, ghostStatsSnapshot.SnapshotCount);

                    tickData.ghostTypeData[ghostIndex] = ghostTypeData;
                }

                tickData.snapshotSizeInBits = ghostStatsSnapshot.SnapshotTotalSizeInBits;
                tickData.overheadSize = ghostStatsSnapshot.SnapshotTotalSizeInBits - ghostTypesSizeInBits;
                tickData.totalInstanceCount = totalInstanceCount;

                // Prediction errors
                for (var i = 0; i < profilerFrameMetaData.PredictionErrors.Length; i++)
                {
                    var predictionErrorData = new PredictionErrorData();
                    predictionErrorData.name = profilerFrameMetaData.PredictionErrors[i].Name;
                    if (i < profilerFrameMetaData.PredictionErrorMetrics.Length)
                        predictionErrorData.errorValue = profilerFrameMetaData.PredictionErrorMetrics[i].Value;
                    else
                        predictionErrorData.errorValue = 0;

                    tickData.predictionErrors[i] = predictionErrorData;
                }

                var frameData = new NetcodeFrameData
                {
                    tickData = new NativeArray<TickData>(1, Allocator.Temp)
                    {
                        [0] = tickData
                    },
                    isValid = true,
                    jitter = profilerFrameMetaData.NetworkMetrics.Jitter,
                    rtt = profilerFrameMetaData.NetworkMetrics.Rtt,
                    totalSizeSentByServerInBits = profilerFrameMetaData.ProfilerMetrics.TotalSizeSentByServerInBits,
                    totalSnapshotCountSentByServer = profilerFrameMetaData.ProfilerMetrics.TotalSnapshotCountSentByServer,
                    totalSizeReceivedByClientInBits = profilerFrameMetaData.ProfilerMetrics.TotalSizeReceivedByClientInBits,
                    totalSnapshotCountReceivedByClient = profilerFrameMetaData.ProfilerMetrics.TotalSnapshotCountReceivedByClient,
                    isHostMode = profilerFrameMetaData.ProfilerMetrics.IsHostMode != 0,
                    hasConnectedClients = profilerFrameMetaData.ProfilerMetrics.HasConnectedClients != 0,
                    worldName = GetWorldName(frameDataView),
                    hasWorldMetadata = true
                };

                return frameData;
            }
        }

        Guid GetGUID()
        {
            return m_NetworkRole == NetworkRole.Server ? ProfilerMetricsConstants.ServerGuid : ProfilerMetricsConstants.ClientGuid;
        }

        // Helper method that tries to read metadata from current GUID, falling back to server GUID if client.
        // In single-world host mode, metadata is emitted under server GUID only.
        bool TryGetMetadata<T>(RawFrameDataView frameDataView, Func<Guid, NativeArray<T>> fetchFunc, out T result) where T : struct
        {
            return TryGetMetadata(frameDataView, fetchFunc, out result, out _);
        }

        // Overload that also reports which GUID was used (true if primary GUID, false if fallback)
        bool TryGetMetadata<T>(RawFrameDataView frameDataView, Func<Guid, NativeArray<T>> fetchFunc, out T result, out bool usedPrimaryGuid) where T : struct
        {
            // Check frameDataView validity before attempting to query metadata
            // to avoid performance overhead from exceptions and IDE first-chance exception clutter
            if (frameDataView == null || !frameDataView.valid)
            {
                result = default;
                usedPrimaryGuid = false;
                return false;
            }

            // Try primary GUID first
            var data = fetchFunc(GetGUID());
            if (data.Length > 0)
            {
                result = data[0];
                usedPrimaryGuid = true;
                return true;
            }

            // For client role, try fallback to server GUID for single-world host compatibility
            if (m_NetworkRole == NetworkRole.Client)
            {
                data = fetchFunc(ProfilerMetricsConstants.ServerGuid);
                if (data.Length > 0)
                {
                    result = data[0];
                    usedPrimaryGuid = false;
                    return true;
                }
            }

            result = default;
            usedPrimaryGuid = false;
            return false;
        }

        // Helper method to safely read world configuration flags from ProfilerMetrics.
        // Caches the last known values to handle frames where metadata isn't emitted.
        internal (bool, bool) GetWorldConfigFlags(RawFrameDataView frameDataView)
        {
            if (TryGetMetadata(frameDataView, g => frameDataView.GetFrameMetaData<ProfilerMetrics>(g, ProfilerMetricsConstants.ProfilerMetricsTag), out var profilerMetrics, out var usedPrimaryGuid))
            {
                // Update cache when we successfully read metadata
                m_CachedIsHostMode = profilerMetrics.IsHostMode != 0;
                m_CachedHasConnectedClients = profilerMetrics.HasConnectedClients != 0;

                // Mark world metadata as cached if:
                // 1. We found it from our primary GUID, OR
                // 2. We're client module reading from fallback and it's host mode (single-world host)
                if (usedPrimaryGuid || (m_NetworkRole == NetworkRole.Client && m_CachedIsHostMode))
                    m_WorldConfigFlagsCached = true;

                return (m_CachedIsHostMode, m_CachedHasConnectedClients);
            }

            // Use cached values if available (for frames without metadata)
            if (m_WorldConfigFlagsCached)
                return (m_CachedIsHostMode, m_CachedHasConnectedClients);

            // No metadata and no cache - return defaults
            return (false, false);
        }

        // Helper method to read world name from session metadata.
        string GetWorldName(RawFrameDataView frameDataView)
        {
            if (!string.IsNullOrEmpty(m_CachedWorldName))
                return m_CachedWorldName;

            if (TryGetMetadata(frameDataView, g => frameDataView.GetSessionMetaData<FixedString128Bytes>(g,
                        ProfilerMetricsConstants.WorldNameTag), out var worldName))
            {
                m_CachedWorldName = worldName.ToString();
                return m_CachedWorldName;
            }

            var (isHost, _) = GetWorldConfigFlags(frameDataView);
            return ProfilerUtils.GetWorldName(m_NetworkRole, isHost);
        }

        // Helper methods to build ProfilerGhostTypeData from per-frame emitted profiler data.
        static ProfilerGhostTypeData CreateGhostTypeData(ProfilerFrameMetadata profilerFrameMetaData,
            int ghostIndex,
            UnsafeGhostStatsSnapshot.PerGhostTypeStats ghostTypeStats,
            NativeArray<ProfilerGhostTypeData> componentsStats,
            NativeArray<UncompressedSizesPerType> uncompressedSizes,
            uint sumComponentTypeSizePerType,
            uint totalSnapshotSizeInBits,
            uint snapshotCount)
        {
            var ghostTypeData = new ProfilerGhostTypeData
            {
                name = profilerFrameMetaData.GhostNames[ghostIndex].Name,
                sizeInBits = ghostTypeStats.SizeInBits,
                instanceCount = (int)ghostTypeStats.EntityCount,
                snapshotCount = snapshotCount,
                componentsPerType = componentsStats,
                newInstancesCount = ghostTypeStats.UncompressedCount,
                isGhostPrefab = true
            };

            if (ghostTypeStats.SizeInBits != 0 && ghostTypeStats.EntityCount != 0)
            {
                var sizePerInstance = (float)ghostTypeStats.SizeInBits / ghostTypeStats.EntityCount;
                ghostTypeData.avgSizePerEntity = (float)Math.Ceiling(sizePerInstance);
                // It can happen that the buffer for uncompressed sizes is not created yet, in that case we just skip the compression efficiency calculation.
                if (uncompressedSizes.Length > ghostIndex && uncompressedSizes[ghostIndex].SizeInBytes > 0)
                    ghostTypeData.combinedCompressionEfficiency = (float)Math.Round(1f - sizePerInstance / (uncompressedSizes[ghostIndex].SizeInBytes * 8f), 2) * 100f;
            }

            ghostTypeData.overheadSize = ghostTypeStats.SizeInBits - sumComponentTypeSizePerType;

            ghostTypeData.percentageOfSnapshot = ProfilerUtils.GetPercentageOfSnapshot(totalSnapshotSizeInBits, ghostTypeData);

            return ghostTypeData;
        }

        // Helper methods to build data for component types from per-frame emitted profiler data.
        static NativeArray<ProfilerGhostTypeData> CreateComponentTypesData(
            UnsafeGhostStatsSnapshot.PerGhostTypeStats ghostTypeStats,
            ProfilerFrameMetadata profilerFrameMetaData,
            int ghostIndex,
            uint snapshotSize,
            ref uint sumComponentTypeSizePerType,
            uint snapshotCount)
        {
            var componentsPerType = new NativeArray<ProfilerGhostTypeData>(ghostTypeStats.PerComponentStatsList.Length, Allocator.Temp);

            // Iterate components per ghost type
            for (var componentIndex = 0; componentIndex < ghostTypeStats.PerComponentStatsList.Length; componentIndex++)
            {
                var componentTypeStat = ghostTypeStats.PerComponentStatsList[componentIndex];
                var serializerIndex = componentTypeStat.SerializerIndex(componentIndex,
                    profilerFrameMetaData.PrefabSerializers[ghostIndex], profilerFrameMetaData.SerializerStates,
                    profilerFrameMetaData.ComponentIndices);
                var type = componentTypeStat.ComponentType(serializerIndex, profilerFrameMetaData.SerializerStates);
                var uncompressedSize = componentTypeStat.SnapshotSize(serializerIndex, profilerFrameMetaData.SerializerStates);

                var compressionEfficiency = -1f;
                if (uncompressedSize > 0 && componentTypeStat.SizeInSnapshotInBits > 0)
                    compressionEfficiency = (float)Math.Round(1f - componentTypeStat.SizeInSnapshotInBits / (uncompressedSize * 8f * ghostTypeStats.EntityCount), 2) * 100f;

                var sizePerComponent = (float)componentTypeStat.SizeInSnapshotInBits / ghostTypeStats.EntityCount;

                var ghostTypeComponentData = new ProfilerGhostTypeData
                {
                    sizeInBits = componentTypeStat.SizeInSnapshotInBits,
                    name = type.ToString(),
                    instanceCount = (int)ghostTypeStats.EntityCount,
                    snapshotCount = snapshotCount,
                    combinedCompressionEfficiency = compressionEfficiency,
                    avgSizePerEntity = (float)Math.Ceiling(sizePerComponent),
                    typeIndex = type.TypeIndex
                };

                ghostTypeComponentData.percentageOfSnapshot = ProfilerUtils.GetPercentageOfSnapshot(snapshotSize, ghostTypeComponentData);

                sumComponentTypeSizePerType += ghostTypeComponentData.sizeInBits;
                componentsPerType[componentIndex] = ghostTypeComponentData;
            }

            return componentsPerType;
        }

        // Helper method to create TickData from per-frame emitted profiler data.
        static TickData CreateTickData(UnsafeGhostStatsSnapshot ghostStatsSnapshot, ProfilerFrameMetadata profilerFrameMetaData)
        {
            var inputTargetTick = new NetworkTick { SerializedData = profilerFrameMetaData.CommandStats[0] };
            var tickData = new TickData
            {
                tick = ghostStatsSnapshot.Tick,
                predictionTick = profilerFrameMetaData.ServerTick,
                interpolationTick = profilerFrameMetaData.InterpolationTick,
                packetCount = ghostStatsSnapshot.PacketsCount,
                snapshotCount = ghostStatsSnapshot.SnapshotCount,
                snapshotSizeInBits = ghostStatsSnapshot.SnapshotTotalSizeInBits,
                timeScale = profilerFrameMetaData.NetworkMetrics.TimeScale,
                interpolationDelay = profilerFrameMetaData.NetworkMetrics.InterpolationOffset,
                interpolationScale = profilerFrameMetaData.NetworkMetrics.InterpolationScale,
                snapshotAgeMin = profilerFrameMetaData.NetworkMetrics.SnapshotAgeMin,
                snapshotAgeMax = profilerFrameMetaData.NetworkMetrics.SnapshotAgeMax,
                inputTargetTick = inputTargetTick,
                commandSizeInBits = profilerFrameMetaData.CommandStats[1],
                commandAge = profilerFrameMetaData.NetworkMetrics.CommandAge,
                discardedPackets = profilerFrameMetaData.CommandStats[2],
                ghostTypeData = new NativeArray<ProfilerGhostTypeData>(profilerFrameMetaData.GhostNames.Length, Allocator.Temp),
                predictionErrors = new NativeArray<PredictionErrorData>(profilerFrameMetaData.PredictionErrors.Length, Allocator.Temp)
            };
            return tickData;
        }

        // Get all the stats data that we emitted per frame for the profiler.
        internal static ProfilerFrameMetadata GetProfilerFrameMetaData(RawFrameDataView frameDataView, Guid guid)
        {
            // Get the profiler metrics and other metadata
            var profilerFrameMetaData = new ProfilerFrameMetadata();
            profilerFrameMetaData.ProfilerMetrics = frameDataView.GetFrameMetaData<ProfilerMetrics>(guid, ProfilerMetricsConstants.ProfilerMetricsTag)[0];
            profilerFrameMetaData.UncompressedSizesPerType = frameDataView.GetFrameMetaData<UncompressedSizesPerType>(guid, ProfilerMetricsConstants.UncompressedSizesPerTypeTag);
            profilerFrameMetaData.PrefabSerializers = frameDataView.GetFrameMetaData<GhostCollectionPrefabSerializer>(guid, ProfilerMetricsConstants.PrefabSerializersTag);
            profilerFrameMetaData.SerializerStates = frameDataView.GetFrameMetaData<GhostComponentSerializer.State>(guid, ProfilerMetricsConstants.SerializerStatesTag);
            profilerFrameMetaData.ComponentIndices = frameDataView.GetFrameMetaData<GhostCollectionComponentIndex>(guid, ProfilerMetricsConstants.ComponentIndicesTag);
            profilerFrameMetaData.GhostNames = frameDataView.GetFrameMetaData<GhostNames>(guid, ProfilerMetricsConstants.GhostNamesTag);
            profilerFrameMetaData.NetworkMetrics = frameDataView.GetFrameMetaData<NetworkMetrics>(guid, ProfilerMetricsConstants.NetworkMetricsTag)[0];
            profilerFrameMetaData.PredictionErrors = frameDataView.GetFrameMetaData<PredictionErrorNames>(guid, ProfilerMetricsConstants.PredictionErrorNamesTag);
            profilerFrameMetaData.PredictionErrorMetrics = frameDataView.GetFrameMetaData<PredictionErrorMetrics>(guid, ProfilerMetricsConstants.PredictionErrorMetricsTag);
            profilerFrameMetaData.CommandStats = frameDataView.GetFrameMetaData<uint>(guid, ProfilerMetricsConstants.CommandStatsTag);
            profilerFrameMetaData.ServerTick = frameDataView.GetFrameMetaData<NetworkTick>(guid, ProfilerMetricsConstants.ServerTickTag)[0];
            profilerFrameMetaData.InterpolationTick = frameDataView.GetFrameMetaData<NetworkTick>(guid, ProfilerMetricsConstants.InterpolationTickTag)[0];
            return profilerFrameMetaData;
        }

        // Helper method to get uncompressed sizes per type for either server or client.
        NativeArray<UncompressedSizesPerType> GetUncompressedSizes(NativeArray<UncompressedSizesPerType> uncompressedSizesPerType)
        {
            // Per-session data, get uncompressed sizes only if it's not already created
            NativeArray<UncompressedSizesPerType> uncompressedSizes;
            switch (m_NetworkRole)
            {
                case NetworkRole.Server:
                {
                    if (!m_UncompressedSizesArrayServer.IsCreated)
                    {
                        m_UncompressedSizesArrayServer = new NativeArray<UncompressedSizesPerType>(uncompressedSizesPerType.Length, Allocator.Persistent);
                        m_UncompressedSizesArrayServer.CopyFrom(uncompressedSizesPerType);
                    }
                    uncompressedSizes = m_UncompressedSizesArrayServer;
                    break;
                }
                case NetworkRole.Client:
                {
                    if (!m_UncompressedSizesArrayClient.IsCreated)
                    {
                        m_UncompressedSizesArrayClient = new NativeArray<UncompressedSizesPerType>(uncompressedSizesPerType.Length, Allocator.Persistent);
                        m_UncompressedSizesArrayClient.CopyFrom(uncompressedSizesPerType);
                    }
                    uncompressedSizes = m_UncompressedSizesArrayClient;
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return uncompressedSizes;
        }
    }
}

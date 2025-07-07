using System;
using Unity.NetCode.Editor;
using UnityEngine;
using UnityEngine.Analytics;
using UnityEngine.Serialization;
using Prefs = Unity.NetCode.MultiplayerPlayModePreferences;

namespace Unity.NetCode.Analytics
{
    [Serializable]
#if UNITY_2023_2_OR_NEWER
    internal class MultiplayerPlaymodePreferencesUpdatedData : IAnalytic.IData, IEquatable<MultiplayerPlaymodePreferencesUpdatedData>
#else
    internal class MultiplayerPlaymodePreferencesUpdatedData : IEquatable<MultiplayerPlaymodePreferencesUpdatedData>
#endif
    {
        public bool simulatorEnabled;
        public string requestedSimulatorView;
        public string requestedPlayType;
        public bool simulateDedicatedServer;
        public int packetDelayMs;
        public int packetJitterMs;
        public int packetDropPercentage;
        public int packetFuzzPercentage;
        public int requestedNumThinClients;
        public float thinClientCreationFrequency;
        public string autoConnectionAddress;
        public int autoConnectionPort;
        public string currentNetworkSimulatorPreset;
        public bool isCurrentNetworkSimulatorPresetCustom;
        public int lagSpikeDurationMs;
        public bool applyLoggerSettings;
        public bool warnBatchedTicks;
        public int warnBatchedTicksRollingWindow;
        public float warnAboveAverageBatchedTicksPerFrame;
        public string targetLogLevel;
        public bool targetShouldDumpPackets;
        public bool showAllSimulatorPresets;

        public bool Equals(MultiplayerPlaymodePreferencesUpdatedData other)
        {
            if (other == null)
                return false;

            return simulatorEnabled == other.simulatorEnabled &&
                requestedSimulatorView == other.requestedSimulatorView &&
                requestedPlayType == other.requestedPlayType &&
                simulateDedicatedServer == other.simulateDedicatedServer &&
                packetDelayMs == other.packetDelayMs &&
                packetJitterMs == other.packetJitterMs &&
                packetDropPercentage == other.packetDropPercentage &&
                packetFuzzPercentage == other.packetFuzzPercentage &&
                requestedNumThinClients == other.requestedNumThinClients &&
                Mathf.Approximately(thinClientCreationFrequency, other.thinClientCreationFrequency) &&
                autoConnectionAddress == other.autoConnectionAddress &&
                autoConnectionPort == other.autoConnectionPort &&
                currentNetworkSimulatorPreset == other.currentNetworkSimulatorPreset &&
                isCurrentNetworkSimulatorPresetCustom == other.isCurrentNetworkSimulatorPresetCustom &&
                lagSpikeDurationMs == other.lagSpikeDurationMs &&
                applyLoggerSettings == other.applyLoggerSettings &&
                warnBatchedTicks == other.warnBatchedTicks &&
                warnBatchedTicksRollingWindow == other.warnBatchedTicksRollingWindow &&
                Mathf.Approximately(warnAboveAverageBatchedTicksPerFrame, other.warnAboveAverageBatchedTicksPerFrame) &&
                targetLogLevel == other.targetLogLevel &&
                targetShouldDumpPackets == other.targetShouldDumpPackets &&
                showAllSimulatorPresets == other.showAllSimulatorPresets;
        }

        internal static MultiplayerPlaymodePreferencesUpdatedData CurrentPlayModePreferences()
        {
            return new MultiplayerPlaymodePreferencesUpdatedData
            {
                simulatorEnabled = Prefs.SimulatorEnabled,
                requestedSimulatorView = Prefs.RequestedSimulatorView.ToString(),
                requestedPlayType = Prefs.RequestedPlayType.ToString(),
                simulateDedicatedServer = Prefs.SimulateDedicatedServer,
                packetDelayMs = Prefs.PacketDelayMs,
                packetJitterMs = Prefs.PacketJitterMs,
                packetDropPercentage = Prefs.PacketDropPercentage,
                packetFuzzPercentage = Prefs.PacketFuzzPercentage,
                requestedNumThinClients = Prefs.RequestedNumThinClients,
                thinClientCreationFrequency = Prefs.ThinClientCreationFrequency,
                autoConnectionAddress = Prefs.AutoConnectionAddress,
                autoConnectionPort = Prefs.AutoConnectionPort,
                currentNetworkSimulatorPreset = Prefs.CurrentNetworkSimulatorPreset,
                isCurrentNetworkSimulatorPresetCustom = Prefs.IsCurrentNetworkSimulatorPresetCustom,
                lagSpikeDurationMs = MultiplayerPlayModeWindow.k_LagSpikeDurationsSeconds[Prefs.LagSpikeSelectionIndex],
                applyLoggerSettings = Prefs.ApplyLoggerSettings,
                warnBatchedTicks = Prefs.WarnBatchedTicks,
                warnBatchedTicksRollingWindow = Prefs.WarnBatchedTicksRollingWindow,
                warnAboveAverageBatchedTicksPerFrame = Prefs.WarnAboveAverageBatchedTicksPerFrame,
                targetLogLevel = Prefs.TargetLogLevel.ToString(),
                targetShouldDumpPackets = Prefs.TargetShouldDumpPackets,
                showAllSimulatorPresets = Prefs.ShowAllSimulatorPresets,
            };
        }
    }

#if UNITY_2023_2_OR_NEWER
    // Schema: com.unity3d.data.schemas.editor.analytics.n4eToolsMPMPrefsUpdated_v1
    // Taxonomy: editor.analytics.n4eToolsMPMPrefsUpdated.v1
    [AnalyticInfo(eventName: "n4eToolsMPMPrefsUpdated", vendorKey: "unity.netcode", version:1, maxEventsPerHour: 100)]
    internal class MultiplayerPlayModePreferencesUpdatedAnalytic : IAnalytic
#else
    internal class MultiplayerPlayModePreferencesUpdatedAnalytic
#endif
    {
        public MultiplayerPlayModePreferencesUpdatedAnalytic(MultiplayerPlaymodePreferencesUpdatedData data)
        {
            m_Data = data;
        }

#if UNITY_2023_2_OR_NEWER
        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            error = null;
            data = m_Data;
            return true;
        }
#endif

        private MultiplayerPlaymodePreferencesUpdatedData m_Data;
    }
}

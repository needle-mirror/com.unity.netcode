#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Scenes;
using UnityEditor;
using UnityEngine;

#if UNITY_USE_MULTIPLAYER_ROLES
using Unity.Multiplayer;
#endif

namespace Unity.NetCode
{
    /// <summary>Developer preferences for the `MultiplayerPlayModeWindow`. Only applicable in editor.</summary>
    public static class MultiplayerPlayModePreferences
    {
        public const bool DefaultSimulatorEnabled = true;
        public const SimulatorView DefaultSimulatorView = SimulatorView.PingView;

        const int k_MaxPacketDelayMs = 2000;
        const int k_MaxPacketJitterMs = 200;
        const int k_DefaultSimulatorMaxPacketCount = 300;

        static string s_PrefsKeyPrefix = $"MultiplayerPlayMode_{Application.productName}_";
        static string s_PlayModeTypeKey = s_PrefsKeyPrefix + "PlayMode_Type";

        static string s_SimulatorEnabledKey = s_PrefsKeyPrefix + "SimulatorEnabled";
        static string s_RequestedSimulatorViewKey = s_PrefsKeyPrefix + "SimulatorView";
        static string s_SimulatorPreset = s_PrefsKeyPrefix + "SimulatorPreset";

        static string s_PacketDelayMsKey = s_PrefsKeyPrefix + "PacketDelayMs";
        static string s_PacketJitterMsKey = s_PrefsKeyPrefix + "PacketJitterMs";
        static string s_PacketDropPercentageKey = s_PrefsKeyPrefix + "PacketDropRate";
        static string s_PacketFuzzPercentageKey = s_PrefsKeyPrefix + "PacketFuzzRate";

        static string s_RequestedNumThinClientsKey = s_PrefsKeyPrefix + "NumThinClients";
        static string s_StaggerThinClientCreationKey = s_PrefsKeyPrefix + "StaggerThinClientCreation";

        static string s_AutoConnectionAddressKey = s_PrefsKeyPrefix + "AutoConnection_Address";
        static string s_AutoConnectionPortKey = s_PrefsKeyPrefix + "AutoConnection_Port";

        static string s_LagSpikeDurationSelectionKey = s_PrefsKeyPrefix + "LagSpikeDurationSelection";

        static string s_ApplyLoggerSettings = s_PrefsKeyPrefix + "NetDebugLogger_ApplyOverload";
        static string s_LoggerLevelType = s_PrefsKeyPrefix + "NetDebugLogger_LogLevelType";
        static string s_TargetShouldDumpPackets = s_PrefsKeyPrefix + "NetDebugLogger_ShouldDumpPackets";
        static string s_ShowAllSimulatorPresets = s_PrefsKeyPrefix + "ShowAllSimulatorPresets";
        static string s_WarnBatchedTicks = s_PrefsKeyPrefix + "NetDebugLogger_WarnBacthedTicks";
        static string s_WarnBatchedTicksRollingWindow = s_PrefsKeyPrefix + "NetDebugLogger_WarnBatchedTicksRollingWindow";
        static string s_WarnAboveAverageTicksPerFrame = s_PrefsKeyPrefix + "NetDebugLogger_WarnAboveAverageTicksPerFrame";

        /// <summary>Stores whether or not the user wishes to use the client simulator UTP module.
        /// </summary>
        public static bool SimulatorEnabled
        {
            get => EditorPrefs.GetBool(s_SimulatorEnabledKey, DefaultSimulatorEnabled);
            set => EditorPrefs.SetBool(s_SimulatorEnabledKey, value);
        }

        /// <summary>Editor "mode". Stores the preferred mode that the Simulator is in.</summary>
        public static SimulatorView RequestedSimulatorView
        {
            get => (SimulatorView) EditorPrefs.GetInt(s_RequestedSimulatorViewKey, (int) DefaultSimulatorView);
            set
            {
#pragma warning disable CS0618
                if (value == SimulatorView.Disabled)
#pragma warning restore CS0618
                {
                    SimulatorEnabled = false;
                    return;
                }
                EditorPrefs.SetInt(s_RequestedSimulatorViewKey, (int) value);
            }
        }

        /// <inheritdoc cref="SimulatorUtility.Parameters"/>
        public static SimulatorUtility.Parameters ClientSimulatorParameters => new SimulatorUtility.Parameters
        {
            Mode = ApplyMode.AllPackets, MaxPacketSize = NetworkParameterConstants.MaxMessageSize, MaxPacketCount = k_DefaultSimulatorMaxPacketCount,
            PacketDelayMs = PacketDelayMs, PacketJitterMs = PacketJitterMs,
            PacketDropPercentage = PacketDropPercentage, FuzzFactor = PacketFuzzPercentage, PacketDuplicationPercentage = 0,
        };

#if UNITY_USE_MULTIPLAYER_ROLES
        private static ClientServerBootstrap.PlayType MultiplayerRoleFlagsToPlayType(MultiplayerRoleFlags roleFlags)
        {
            switch (roleFlags)
            {
                case MultiplayerRoleFlags.Server:
                    return ClientServerBootstrap.PlayType.Server;
                case MultiplayerRoleFlags.Client:
                    return ClientServerBootstrap.PlayType.Client;
                case MultiplayerRoleFlags.ClientAndServer:
                    return ClientServerBootstrap.PlayType.ClientAndServer;
                default:
                    throw new ArgumentOutOfRangeException(nameof(roleFlags), roleFlags, null);
            }
        }

        private static MultiplayerRoleFlags PlayTypeToMultiplayerRoleFlags(ClientServerBootstrap.PlayType playType)
        {
            switch (playType)
            {
                case ClientServerBootstrap.PlayType.Server:
                    return MultiplayerRoleFlags.Server;
                case ClientServerBootstrap.PlayType.Client:
                    return MultiplayerRoleFlags.Client;
                case ClientServerBootstrap.PlayType.ClientAndServer:
                    return MultiplayerRoleFlags.ClientAndServer;
                default:
                    throw new ArgumentOutOfRangeException(nameof(playType), playType, null);
            }
        }
#endif

        /// <summary>Denotes what type of worlds are created by <see cref="ClientServerBootstrap"/> when entering playmode in the editor.</summary>
        public static ClientServerBootstrap.PlayType RequestedPlayType
        {
            get
            {
#if UNITY_USE_MULTIPLAYER_ROLES
                if (Unity.Multiplayer.Editor.EditorMultiplayerRolesManager.EnableMultiplayerRoles)
                {
                    return MultiplayerRoleFlagsToPlayType(Unity.Multiplayer.Editor.EditorMultiplayerRolesManager.ActiveMultiplayerRoleMask);
                }
#endif
                return (ClientServerBootstrap.PlayType) EditorPrefs.GetInt(s_PlayModeTypeKey, (int) ClientServerBootstrap.PlayType.ClientAndServer);
            }
            set
            {
#if UNITY_USE_MULTIPLAYER_ROLES
                if (Unity.Multiplayer.Editor.EditorMultiplayerRolesManager.EnableMultiplayerRoles)
                {
                    Unity.Multiplayer.Editor.EditorMultiplayerRolesManager.ActiveMultiplayerRoleMask = PlayTypeToMultiplayerRoleFlags(value);
                    return;
                }
#endif
                EditorPrefs.SetInt(s_PlayModeTypeKey, (int) value);
            }
        }

        private static string s_SimulateDedicatedServer = s_PrefsKeyPrefix + "SimulateDedicatedServer";
        public static bool SimulateDedicatedServer
        {
            get => EditorPrefs.GetBool(s_SimulateDedicatedServer, false);
            set => EditorPrefs.SetBool(s_SimulateDedicatedServer, value);
        }

        /// <inheritdoc cref="SimulatorUtility.Parameters.PacketDelayMs"/>
        public static int PacketDelayMs
        {
            get => math.clamp(EditorPrefs.GetInt(s_PacketDelayMsKey, 0), 0, k_MaxPacketDelayMs);
            set => EditorPrefs.SetInt(s_PacketDelayMsKey, math.clamp(value, 0, k_MaxPacketDelayMs));
        }

        /// <inheritdoc cref="SimulatorUtility.Parameters.PacketJitterMs"/>
        public static int PacketJitterMs
        {
            get => math.clamp(EditorPrefs.GetInt(s_PacketJitterMsKey, 0), 0, k_MaxPacketJitterMs);
            set => EditorPrefs.SetInt(s_PacketJitterMsKey, math.clamp(value, 0, k_MaxPacketJitterMs));
        }

        /// <inheritdoc cref="SimulatorUtility.Parameters.PacketDropPercentage"/>
        public static int PacketDropPercentage
        {
            get => math.clamp(EditorPrefs.GetInt(s_PacketDropPercentageKey, 0), 0, 100);
            set => EditorPrefs.SetInt(s_PacketDropPercentageKey, math.clamp(value, 0, 100));
        }

        /// <inheritdoc cref="SimulatorUtility.Parameters.FuzzFactor"/>
        public static int PacketFuzzPercentage
        {
            get => math.clamp(EditorPrefs.GetInt(s_PacketFuzzPercentageKey, 0), 0, 100);
            set => EditorPrefs.SetInt(s_PacketFuzzPercentageKey, math.clamp(value, 0, 100));
        }

        /// <summary>
        /// Denotes how many thin client worlds are created in the editor (via the <see cref="AutomaticThinClientWorldsUtility"/> utility),
        /// assuming that feature is enabled.
        /// </summary>
        public static int RequestedNumThinClients
        {
            get => math.clamp(EditorPrefs.GetInt(s_RequestedNumThinClientsKey, 0), 0, ClientServerBootstrap.k_MaxNumThinClients);
            set => EditorPrefs.SetInt(s_RequestedNumThinClientsKey, math.clamp(value, 0, ClientServerBootstrap.k_MaxNumThinClients));
        }

        /// <summary>
        /// Denotes how many thin client worlds to spawn per second when in the editor (via the <see cref="AutomaticThinClientWorldsUtility"/> utility),
        /// assuming that feature is enabled.
        /// </summary>
        public static float ThinClientCreationFrequency
        {
            get => math.clamp(EditorPrefs.GetFloat(s_StaggerThinClientCreationKey, 2), 0f, 1_000);
            set => EditorPrefs.SetFloat(s_StaggerThinClientCreationKey, value);
        }

        public static string AutoConnectionAddress
        {
            get => EditorPrefs.GetString(s_AutoConnectionAddressKey, "127.0.0.1");
            set => EditorPrefs.SetString(s_AutoConnectionAddressKey, value);
        }

        public static ushort AutoConnectionPort
        {
            get => (ushort) EditorPrefs.GetInt(s_AutoConnectionPortKey, 0);
            set => EditorPrefs.SetInt(s_AutoConnectionPortKey, value);
        }

        /// <summary>Maps to a <see cref="SimulatorPreset"/>.</summary>
        public static string CurrentNetworkSimulatorPreset
        {
            get => EditorPrefs.GetString(s_SimulatorPreset, null);
            set => EditorPrefs.SetString(s_SimulatorPreset, value);
        }

        /// <summary>True if is user-defined, custom preset.</summary>
        public static bool IsCurrentNetworkSimulatorPresetCustom => SimulatorPreset.k_CustomProfileKey.Equals(CurrentNetworkSimulatorPreset, StringComparison.OrdinalIgnoreCase);

        /// <summary>There is a hardcoded list of lag spike values. This is the saved indexer.</summary>
        public static int LagSpikeSelectionIndex
        {
            get => EditorPrefs.GetInt(s_LagSpikeDurationSelectionKey, 4); // Default 1s.
            set => EditorPrefs.SetInt(s_LagSpikeDurationSelectionKey, value);
        }

        /// <summary>If true, will force <see cref="NetDebugSystem"/> to set these values on boot.</summary>
        public static bool ApplyLoggerSettings
        {
            get => EditorPrefs.GetBool(s_ApplyLoggerSettings, false);
            set => EditorPrefs.SetBool(s_ApplyLoggerSettings, value);
        }

        /// <summary>If true, will force <see cref="NetDebugSystem"/> to display a warning when prediction ticks are batched.</summary>
        public static bool WarnBatchedTicks
        {
            get => EditorPrefs.GetBool(s_WarnBatchedTicks, true);
            set
            {
                EditorPrefs.SetBool(s_WarnBatchedTicks, value);

                foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
                {
                    if (!serverWorld.IsCreated) continue;
                    using var netDebugQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>());
                    netDebugQuery.GetSingletonRW<NetDebug>().ValueRW.WarnBatchedTicks = value;
                }
            }
        }

        /// <summary>Specifies the number of frames the rolling average is calculated over.</summary>
        public static int WarnBatchedTicksRollingWindow
        {
            get => EditorPrefs.GetInt(s_WarnBatchedTicksRollingWindow, 4);
            set
            {
                EditorPrefs.SetInt(s_WarnBatchedTicksRollingWindow, value);

                foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
                {
                    using var netDebugQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>());
                    netDebugQuery.GetSingletonRW<NetDebug>().ValueRW.WarnBatchedTicksRollingWindowSize = value;
                }
            }
        }

        /// <summary>If the average is above this percent a warning will be displayed. Set to 0 to always warn when ticks are batched.</summary>
        public static float WarnAboveAverageBatchedTicksPerFrame
        {
            get => EditorPrefs.GetFloat(s_WarnAboveAverageTicksPerFrame, 1.2f);
            set
            {
                EditorPrefs.SetFloat(s_WarnAboveAverageTicksPerFrame, value);

                foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
                {
                    using var netDebugQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>());
                    netDebugQuery.GetSingletonRW<NetDebug>().ValueRW.WarnAboveAverageBatchedTicksPerFrame = value;
                }
            }
        }



        /// <summary>If <see cref="ApplyLoggerSettings"/>, forces all <see cref="NetDebugSystem"/> loggers to this log level.</summary>
        public static NetDebug.LogLevelType TargetLogLevel
        {
            get => (NetDebug.LogLevelType) EditorPrefs.GetInt(s_LoggerLevelType, (int) NetDebug.LogLevelType.Notify);
            set => EditorPrefs.SetInt(s_LoggerLevelType, (int)value);
        }

        /// <summary>If <see cref="ApplyLoggerSettings"/>, forces all <see cref="NetDebugSystem"/> loggers to have this value for ShouldDumpPackets.</summary>
        public static bool TargetShouldDumpPackets
        {
            get
            {
#if NETCODE_NDEBUG
                return false;
#else
                return EditorPrefs.GetBool(s_TargetShouldDumpPackets, false);
#endif
            }
            set
            {
#if !NETCODE_NDEBUG // Prevent writing the above force-false.
                EditorPrefs.SetBool(s_TargetShouldDumpPackets, value);
#endif
            }
        }

        /// <summary>If true, all simulator presets will be visible, rather than only platform specific ones.</summary>
        public static bool ShowAllSimulatorPresets
        {
            get => EditorPrefs.GetBool(s_ShowAllSimulatorPresets, false);
            set => EditorPrefs.SetBool(s_ShowAllSimulatorPresets, value);
        }

        /// <summary>Returns true if the editor-inputted address is a valid connection address.</summary>
        public static bool IsEditorInputtedAddressValidForConnect(out NetworkEndpoint ep)
        {
            if (AutoConnectionPort != 0 && NetworkEndpoint.TryParse(AutoConnectionAddress, AutoConnectionPort, out ep, NetworkFamily.Ipv4) && !ep.IsAny)
                return true;

            if (AutoConnectionPort != 0 && NetworkEndpoint.TryParse(AutoConnectionAddress, AutoConnectionPort, out ep, NetworkFamily.Ipv6) && !ep.IsAny)
                return true;

            ep = default;
            return false;
        }

        /// <summary>Apply the selected preset to the static, saved fields.
        /// Clobbers any custom values the user may have entered.</summary>
        /// <param name="preset">Preset to apply.</param>
        public static void ApplySimulatorPresetToPrefs(SimulatorPreset preset)
        {
            if (!preset.IsCustom)
            {
                PacketDelayMs = preset.PacketDelayMs;
                PacketJitterMs = preset.PacketJitterMs;
                PacketDropPercentage = math.clamp(preset.PacketLossPercent, 0, 100);
                PacketFuzzPercentage = math.clamp(preset.PacketFuzzPercent, 0, 100);
            }
        }
    }

    /// <summary>For the PlayMode Tools Window.</summary>
    public enum SimulatorView
    {
        [Obsolete("Disabled is no longer supported. Use MultiplayerPlayModePreferences.SimulatorEnabled instead. RemovedAfter Entities 1.x")]
        Disabled = 0,
        PingView = 1,
        PerPacketView = 2,
    }
}
#endif

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Scenes;
using UnityEditor;
using UnityEngine;
using Unity.Entities.Build;
using Unity.Jobs;
using Unity.NetCode.Analytics;
using Unity.NetCode.Editor.Analytics;
using Unity.NetCode.Hybrid;
using UnityEngine.Analytics;
using Prefs = Unity.NetCode.MultiplayerPlayModePreferences;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// "PlayMode Tools" Window. Provides controls for:
    /// - Configuring PlayMode World creation & configuration.
    /// - Bespoke views for netcode related Client, ThinClient, and Server worlds.
    /// - Controls to aid in testing of netcode, including a Simulator utility.
    /// </summary>
    internal class MultiplayerPlayModeWindow : EditorWindow, IHasCustomMenu
    {
        const string k_Title = "PlayMode Tools";
        const int k_MaxWorldsToDisplay = 8;
        const string k_ToggleLagSpikeSimulatorBindingKey = "Main Menu/Multiplayer/Toggle Lag Spike Simulation";
        const string k_SimulatorPresetCaveat = "\n\n<i>Note: The simulator can only <b>add</b> additional latency to a given connection, and it does so naively. Therefore, poor editor performance will exacerbate the delay (and is not compensated for).</i>";
        const string k_ProjectSettingsConfigPath = "<i>ProjectSettings > Entities > Build</i>";

        static Color s_Blue => new Color(0.5f, 0.84f, 0.99f); // TODO: netCode color into this view. GhostAuthoringComponentEditor.netcodeColor;
        static Color s_Green => new Color(0.51f, 0.85f, 0.49f);
        static Color s_Red => new Color(1f, 0.25f, 0.22f);
        static Color s_Orange => new Color(1f, 0.68f, 0f);
        static Color s_Pink => new Color(1f, .49f, 0.95f);

        static GUILayoutOption s_PingWidth = GUILayout.Width(100);
        static GUILayoutOption s_NetworkIdWidth = GUILayout.Width(30);
        static GUILayoutOption s_SimulatorViewWidth = GUILayout.Width(120);
        static GUILayoutOption s_WorldNameWidth = GUILayout.Width(120);

        static GUIContent s_TitleContent = new GUIContent(k_Title, "Netcode for Entities editor playmode tools. View and control world creation, connection status and flows etc.\n\n<i>It has no impact on builds.</i>");
        static GUIContent s_PlayModeType = new GUIContent("PlayMode Type", "During multiplayer development, it's useful to modify and run the client and server at the same time, in the same process (i.e. \"in-proc\"). DOTS Multiplayer supports this out of the box via the DOTS Entities \"Worlds\" feature.\n\nUse this toggle to determine which mode of operation is used for this Editor playmode session. <i>Has no impact on builds.</i>\n\n\"Client & Server\" is recommended for most workflows.");
        static GUIContent s_ServerEmulation = new GUIContent("Server Emulation", $"Denotes how the ServerWorld should load data when in PlayMode in the Editor. This setting does not affect builds (see {k_ProjectSettingsConfigPath} for build configuration).");
        static GUIContent[] s_ServerEmulationContents;
        static GUIContent s_NumThinClients = new GUIContent("Num Thin Clients", "Thin clients are clients that receive snapshots, but do not attempt to process game logic. They can send arbitrary inputs though, and are useful to simulate opponents (to test connection & game logic).\n\nThin clients are instantiated on boot and at runtime via the <b>AutomaticThinClientWorldsUtility</b>. I.e. This value can be tweaked during Play Mode.");
        static GUIContent s_InstantiationFrequency = new GUIContent("Instantiation Frequency", "How many thin client worlds to instantiate per second (via the <b>AutomaticThinClientWorldsUtility</b>). Runtime thin client instantiation can be disabled by setting <b>AutomaticThinClientWorldsUtility.RuntimeThinClientWorldInitialization</b> to null.");
        static GUIContent s_RuntimeInstantiationDisabled = new GUIContent("AutomaticThinClientWorldsUtility Disabled", "Enable it by setting the <b>AutomaticThinClientWorldsUtility.RuntimeThinClientWorldInitialization</b> delegate.");
        static GUIContent s_Auto = new GUIContent("[Auto]", "Denotes that this world is managed by the <b>AutomaticThinClientWorldsUtility</b>.");

        static GUIContent s_AutoConnectionAddress = new GUIContent("Auto Connect Address", "The ClientServerBootstrapper will attempt to automatically connect the created client world to this address on boot.");
        static GUIContent s_AutoConnectionPort = new GUIContent("Auto Connect Port", "The ClientServerBootstrapper will attempt to automatically connect the created client world to this port on boot.");

        static GUIContent s_SimulatorTitle = new GUIContent("Client Network Emulation", "Enabling this allows you to emulate various realistic network conditions.\n\nIn practice, this toggle denotes whether or not all Client Worlds will pass Unity Transport's <b>SimulatorUtility.Parameter</b> into the <b>NetworkSettings</b> during driver construction.\n\nFor this reason, toggling Network Emulation requires a PlayMode restart.");
        static GUIContent s_SimulatorPreset = new GUIContent("?? Presets", "Simulate a variety of connection types & server locations.\n\nThese presets have been created by Multiplayer devs.\n\n<b>We strongly recommend that you test every new multiplayer feature with this simulator enabled.</b>\n\nBy default, switching platform will change which presets are available to you. To toggle showing all presets, use the context menu. Alternatively, you can inject your own presets by modifying the <b>InUseSimulatorPresets</b> delegate.");
        static GUIContent s_ShowAllSimulatorPresets = new GUIContent("Show All Simulator Presets", "Toggle to view all simulator presets, or only your platform specific ones?");

        static GUIContent s_DriverDisplayInfo = new GUIContent("", @"Denotes <b>DriverStore</b> driver instance information for this world, as well as the target <b>NetworkEndpoint</b> address (if applicable).

<b>IPC | Intra-Process Communication</b>
Unity's UTP (Unity Transport Package) <b>IPCNetworkInterface</b> implementation. IPC is an in-proc, in-memory, socket-like wrapper, emulating the Transport API, but without any OS overhead and unreliability. IPC operations are instantaneous, but can only be used to communicate with other <b>NetworkDriver</b> instances inside the same process (which is why IPC really means 'intra-process' and not 'inter-process' here).

<b>UDP | User Datagram Protocol</b>
Unity's UTP <b>UDPNetworkInterface</b> implementation (formerly 'baselib'). Unreliable by default (see UTP Pipelining).

<b>WebSocket</b>
Unity's UTP <b>WebSocketNetworkInterface</b> implementation.

<b>Custom</b>
Denotes a user-specified, custom <b>INetworkInterface</b> is being used.

<b>BoundOnly</b> (Server-Only)
Denotes that the driver bound successfully, but <b>Listen</b> either did not succeed, or was never invoked. <i>Note: Client drivers do also call <b>Bind</b> when they call <b>Connect</b>, but this isn't currently displayed.</i>

<b>Closed</b> (Server-Only)
Denotes that the server driver is closed i.e. not currently listening.
");
        static GUIContent s_PendingDc = new GUIContent("[Pending DC]", "You triggered a disconnect on this client. Waiting for said disconnect request to trigger transport driver change.");
        static GUIContent s_Awaiting = new GUIContent(string.Empty, "We must wait for the previous <b>NetworkStreamConnection</b> to be disposed, before we can connect this client to this address.");
        static GUIContent s_LagSpikeOccuring = new GUIContent("[Lag Spike]", "Denotes that a lag spike is currently occuring. No packets are getting through.");
        static GUIContent s_NetworkEmulation = new GUIContent(string.Empty, "Denotes whether or not this client uses <b>Client Network Emulation</b> with the above settings.");
        static GUIContent s_NoNetworkConnectionEntity = new GUIContent("[No Connection Entity]", "No entity exists containing a <b>NetworkStreamConnection</b> component. Call <b>Connect</b> to create one.");

        static GUIContent s_SimulatorView = new GUIContent(string.Empty, string.Empty);
        const string s_SimulatorExplanation = "The simulator works by adding a delay before processing all packets sent from - and received by - the ClientWorld's Socket Driver.\n\nIn this view, you can observe and modify ";
        static GUIContent[] s_SimulatorViewContents = {
            new GUIContent("Ping View",s_SimulatorExplanation + "the sum of both the sent and received delays, which therefore becomes an estimation of the \"ping\" (i.e. \"RTT\") value. Thus, per-packet values will be roughly half these values.  Switch to the \"Per-Packet View\" to observe this."),
            new GUIContent("Per-Packet View",s_SimulatorExplanation + "the emulator values applied to each packet (i.e. each way). Note that the effect on \"ping\" (i.e. \"RTT\") is therefore at least doubled. Switch to the \"Ping View\" to observe this."),
        };

        static GUIContent s_PacketDelay = new GUIContent("Packet Delay (ms)", "Fixed delay applied to each incoming/outgoing client world packet before it is processed. Simulates real network delay.");
        static GUIContent s_PacketJitter = new GUIContent("Packet Jitter (±ms)", "Random delay 'added to' or 'subtracted from' each packets delay (min 0). Simulates network jitter (where packets sent in order \"A > B > C\" can arrive \"A > C > B\".");
        static GUIContent s_PacketDelayRange = new GUIContent("", "Denotes your clients min and max delay for each packet, calculated as \"Delay ± Jitter\". Your ping will be roughly double, plus an additional delay incurred during frame processing, as well as any real packet delay.");

        static GUIContent s_RttDelay = new GUIContent("RTT Delay (+ms)", "A fixed delay is calculated and applied to each incoming/outgoing client world packet so that the sum of the delay (each way) adds up to this value, thus simulating your \"RTT\" or \"Ping\". Simulates real network delay.");
        static GUIContent s_RttJitter = new GUIContent("RTT Jitter (±ms)", "A random delay calculated and 'added to' or 'subtracted from' from each packets delay (min 0) so that the max jitter (i.e. variance) equals this value.\n\nSimulates network jitter (where packets sent in order \"A > B > C\" can arrive \"A > C > B\".");
        static GUIContent s_RttDelayRange = new GUIContent("", "Denotes your clients min and max simulated ping, calculated as \"Delay ± Jitter\".\n\nNote that your actual ping will be higher due to the delay incurred during frame processing, and any real packet delay.");

        static GUIContent s_PacketDrop = new GUIContent("Packet Drop (%)", "Denotes the percentage of packets - sent or received - that will be dropped by the client world. Simulates interruptions in UDP packet flow.");
        static GUIContent s_FuzzyPacket = new GUIContent("Packet Fuzz (%)", "Denotes the percentage of packets - sent or received - that will have random bits flipped (i.e. \"fuzzed\" / \"corrupted\") by the client world. Fuzzed packets trigger (often catastrophic) errors in deserialization code (both yours, and ours).\n\nI.e. This tool is used for security testing, and simulates malicious MitM attacks, and thus, error recovery.\n\nNote: These packets will PASS packet CRC validation checks, so cannot be easily discarded.");

        static GUIContent[] s_InUseSimulatorPresetContents;
        static List<SimulatorPreset> s_InUseSimulatorPresetsCache = new List<SimulatorPreset>(32);

        const string k_PlayModeTooltip = "\n\n<i>This dropdown determines the behaviour of the Netcode for Entities bootstrapping, for this Editor playmode session. It has no impact on builds.</i>";
        static readonly GUIContent[] k_PlayModeStrings =
        {
            new GUIContent("Client & Server", "Instantiates a server instance alongside a client, with a configurable number of thin clients." + k_PlayModeTooltip),
            new GUIContent("Client", "Only instantiate a client (with a configurable number of thin clients) that'll automatically attempt to connect to the listed address and port." + k_PlayModeTooltip),
            new GUIContent("Server", "Only instantiate a server. Expects that clients will be instantiated in another process." + k_PlayModeTooltip),
        };
        static readonly GUIContent[] k_PlayModeStringsSingleWorld =
        {
            new GUIContent("Host (Client & Server)", "Instantiates a single host world instance, with a configurable number of accompanying thin client world instances." + k_PlayModeTooltip),
            new GUIContent("Client", "Only instantiate a client world (with a configurable number of accompanying thin client worlds), which will attempt to connect to the listed address and port." + k_PlayModeTooltip),
        };
        static GUIContent s_WorldName = new GUIContent("", "The <b>World.Name</b>.");
        static GUIContent s_NetworkId = new GUIContent("", "The <b>NetworkId</b> associated with this client. The server uses the reserved value 0.");
        internal static GUIContent s_ServerStats = new GUIContent("", "<b>Client Connections</b> | <b>Connections In-Game</b> (via <b>NetworkStreamInGame</b>)");
        static GUIContent s_ClientConnect = new GUIContent("", "Trigger all clients to disconnect from the server they're connected to and [re]connect to the specified address and port.");
        static GUIContent s_ClientConnectionState = new GUIContent("", "Denotes the <b>ConnectionState.State</b> enum value for this <b>NetworkStreamConnection</b>.");
        static GUIContent s_ServerDcAllClients = new GUIContent("DC All", "Trigger the server to attempt to gracefully disconnect all clients. Useful to batch-test a bunch of client disconnect scenarios (e.g. mid-game).");
        static GUIContent s_ServerReconnectAllClients = new GUIContent("Reconnect All", "Trigger the server to attempt to gracefully disconnect all clients, then have them automatically reconnect. Useful to batch-test player rejoining scenarios (e.g. people dropping out mid-match).\n\nNote that clients will also disconnect themselves from the server in the same frame as they're attempting to reconnect, so you can test same frame DCing.");
        static GUIContent s_ServerLogRelevancy = new GUIContent("Log Relevancy", "Log the current relevancy rules for this server. Useful to debug why a client is not receiving a specific ghost.");
        static GUIContent s_ServerLogCommandStats = new GUIContent("Log Command Stats", "Logs `CommandArrivalStatistics` for all connected clients, which can be used to fine-tune server ingress bandwidth (which scales with player count).");
        static GUIContent s_ClientReconnect = new GUIContent("Client Reconnect", "Attempt to gracefully disconnect from the server, followed by an immediate reconnect attempt.");
        static GUIContent s_ClientDc = new GUIContent("Client DC", "Attempt to gracefully disconnect from the server. Triggered by the client (e.g. a player closing the application).");
        static GUIContent s_ServerDc = new GUIContent("Server DC", "Trigger the server to attempt to gracefully disconnect this client, identified by their 'NetworkId'. Server-authored (e.g. like a server kicking a client when the match has ended).");
        static GUIContent s_Timeout = new GUIContent("Force Timeout", "Simulate a timeout (i.e. the client and server stop communicating instantly, and critically, <b>without</b> either being able to send graceful disconnect control messages). A.k.a. An \"ungraceful\" disconnection or \"Server unreachable\".\n\n- Clients should notify the player of the internet issue, and provide automatic (or triggerable) reconnect or quit flows.\n\n - Servers should ensure they handle clients timing out as a valid form of disconnection, and (if supported) ensure that 'same client reconnections' are properly handled.\n\n - Transport settings will inform how quickly all parties detect a lost connection.");

        static GUIContent s_LogFileLocation = new GUIContent("Open Log Folder", string.Empty);
        static GUIContent s_ForceLogLevel = new GUIContent("Force Log Settings", "Force all <b>NetDebug</b> loggers to a specified setting, clobbering any <b>NetCodeDebugConfig</b> singleton.");
        const string k_NetcodeNDebugTooltip = "\n\nDisable this functionality (and related CPU overhead) by defining <b>NETCODE_NDEBUG</b> in your project.";
        static GUIContent s_LogLevel = new GUIContent("Log Level", "Every <b>NetDebug</b> log is raised with a specific severity. Use this to discard logs below this severity level." + k_NetcodeNDebugTooltip);
        static GUIContent s_DumpPacketLogs = new GUIContent("Dump Packet Logs", "Denotes whether Netcode will dump packet logs to <b>NetDebug.LogFolderForPlatform</b>.\n\nIf 'Force Log Settings' is disabled, the editor will use whatever logging configuration values are already set." + k_NetcodeNDebugTooltip);
        static GUIContent s_LagSpike = new GUIContent("", "In playmode, press the shortcut key to toggle 'total packet loss' for the specified duration.\n\nUseful when testing short periods of lost connection (e.g. while in a tunnel) and to see how well your client and server handle an \"ungraceful\" disconnect (e.g. internet going down).\n\n- This window must be open for this tool to work.\n- Will only be applied to the \"full\" (i.e.: rendering) clients.\n- Depending on timeouts specified, this may cause the actual driver to timeout. Ensure you handle reconnections.");

        static GUIContent s_WarnBatchedTicks = new GUIContent("Warn When Ticks Batched", "Display a warning in the console when network ticks are batched.\nThis can be useful for tracking down if 'stuttering' issues are linked to tick batching.\n\nBatching occurs when the server is unable to process enough 'network ticks' to keep up with the simulation rate. If a network tick represent say 33ms of time but takes 66ms to process the server will fall behind. It now needs to make up a frame so next update it will simulate 2 ticks, one for the current time slice and one to make up for the lost time, if these two frames can't be processed in time then a spiral will occur where each 'tick' requires more and more 'network ticks' to keep up. This is mitigated via batching, instead of calculating multiple ticks, ticks are batched by increasing the length of a 'network tick' to simulate enough time to keep pace with the desired update rate (66ms in this case to capture two 33ms updates). While effective as curbing runaway performance issues this can cause many interpolation issues - expect client input loss, and a reduction in gameplay, physics, prediction and interpolation quality.");
        static GUIContent s_WarnBatchedTicksRollingWindow = new GUIContent("Rolling Avg Window Size", "Specifies the number of frames the average is calculated over.");
        static GUIContent s_WarnAboveAverageBatchedTicksPerFrame = new GUIContent("Above Ticks Per Frame", "Sets the minimum number of batched ticks per frame for displaying the warning. A value of 1 will display a warning for every batched tick.");

        static readonly string[] k_LagSpikeDurationStrings = { "10ms", "100ms", "200ms", "500ms", "1s", "2s", "5s", "10s", "30s", "1m", "2m"};
        internal static readonly int[] k_LagSpikeDurationsSeconds = { 10, 100, 200, 500, 1_000, 2_000, 5_000, 10_000, 30_000, 60_000, 120_000 };

        static GUIStyle s_BoxStyleHack;
        static GUILayoutOption s_RightButtonWidth = GUILayout.Width(120);
        static DateTime s_LastWrittenUtc;
        static DateTime s_LastRepaintedUtc;
        static DateTime s_HighlightWarnBatchedTicksTime;
        static bool s_HighlightWarnBatchedTicks;
        static bool s_ShouldUpdateStatusTexts;
        public static bool s_ForceRepaint;
        Vector2 m_WorldScrollPosition;
        int m_PreviousFrameCount;

        MultiplayerPlaymodePreferencesUpdatedData m_PreferencesData;

        public delegate void SimulatorPresetsSelectionDelegate(out string presetGroupName, List<SimulatorPreset> appendPresets);

        /// <summary>If your team would prefer to use other Simulator Presets, override this.
        /// Defaults to: <see cref="SimulatorPreset.DefaultInUseSimulatorPresets"/></summary>
        public static SimulatorPresetsSelectionDelegate InUseSimulatorPresets = SimulatorPreset.DefaultInUseSimulatorPresets;

        [MenuItem("Window/Multiplayer/PlayMode Tools", priority = 3007)]
        private static void ShowWindow()
        {
            GetWindow<MultiplayerPlayModeWindow>(false, k_Title, true);
        }

        [InitializeOnLoadMethod]
        private static void RegisterHyperLinkHandler()
        {
            EditorGUI.hyperLinkClicked += HandleHyperLinkClicked;
        }

        void OnEnable()
        {
            m_PreferencesData = MultiplayerPlaymodePreferencesUpdatedData.CurrentPlayModePreferences();
            titleContent = s_TitleContent;
            s_BoxStyleHack = null;
            EditorApplication.playModeStateChanged += PlayModeStateChanged;
            PlayModeStateChanged(EditorApplication.isPlaying ? PlayModeStateChange.EnteredPlayMode : PlayModeStateChange.ExitingPlayMode);

            s_LogFileLocation.tooltip = NetDebug.LogFolderForPlatform();
            RefreshSimulatorPresets();
            HandleSimulatorValuesChanged(Prefs.IsCurrentNetworkSimulatorPresetCustom);

            var dotsGlobalSettings = DotsGlobalSettings.Instance;
            var serverProvider = TryGetPath(dotsGlobalSettings.ServerProvider, "ProjectSettings/NetCodeServerSettings.asset");
            var clientAndServerProvider = TryGetPath(dotsGlobalSettings.ClientProvider, "ProjectSettings/NetCodeClientAndServerSettings.asset");
            s_ServerEmulationContents = new[]
            {
                new GUIContent("Client Hosted Server", $"Emulate a 'Client Hosted Executable' for the purposes of server data loading. I.e. Client-only types, assemblies and data will be loaded into the ServerWorld (by the server loading <i>{clientAndServerProvider}</i>, via {k_ProjectSettingsConfigPath})."),
                new GUIContent("Dedicated Server", $"Emulate a 'Dedicated Server Executable' for the purposes of server data loading. I.e. Client-only types, assemblies and data will be stripped from the ServerWorld (by the server loading <i>{serverProvider}</i>, via {k_ProjectSettingsConfigPath})."),
            };
            foreach (var guiContent in s_ServerEmulationContents)
            {
                s_ServerEmulation.tooltip += $"\n\n - <b>{guiContent.text}</b>: {guiContent.tooltip}";
            }

            if ( s_HighlightWarnBatchedTicks )
            {
                s_HighlightWarnBatchedTicksTime = DateTime.UtcNow + TimeSpan.FromSeconds(0.5f); // we need to defer the highlight as calling it here causes errors
                EditorApplication.update += HighlightWarnBatchedTicks;
            }
        }

        static string TryGetPath(DotsPlayerSettingsProvider dotsSettingsProvider, string fallback)
        {
            if (dotsSettingsProvider != null)
                return dotsSettingsProvider.GetSettingAsset().CustomDependency ?? fallback;
            return fallback;
        }

        static void RefreshSimulatorPresets()
        {
            s_InUseSimulatorPresetsCache.Clear();
            InUseSimulatorPresets(out var presetGroupName, s_InUseSimulatorPresetsCache);
            s_SimulatorPreset.text = presetGroupName;
            s_InUseSimulatorPresetContents = new GUIContent[s_InUseSimulatorPresetsCache.Count];
            for (var i = 0; i < s_InUseSimulatorPresetsCache.Count; i++)
            {
                var preset = s_InUseSimulatorPresetsCache[i];
                s_InUseSimulatorPresetContents[i] = new GUIContent(preset.Name, preset.Tooltip + k_SimulatorPresetCaveat);
            }
        }

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= PlayModeStateChanged;
        }

        internal void PlayModeStateChanged(PlayModeStateChange playModeStateChange)
        {
            var newPrefsData = MultiplayerPlaymodePreferencesUpdatedData.CurrentPlayModePreferences();
            if (!newPrefsData.Equals(m_PreferencesData))
            {
                m_PreferencesData = newPrefsData;
                NetCodeAnalytics.SendAnalytic(new MultiplayerPlayModePreferencesUpdatedAnalytic(m_PreferencesData));
            }
            EditorApplication.update -= PlayModeUpdate;
            if (playModeStateChange == PlayModeStateChange.EnteredPlayMode)
                EditorApplication.update += PlayModeUpdate;

            PlayModeUpdate();
            Repaint();
        }

        void PlayModeUpdate()
        {
            var didCreateOrDestroyWorlds = AutomaticThinClientWorldsUtility.UpdateAutomaticThinClientWorlds();
            s_ForceRepaint |= didCreateOrDestroyWorlds;

            var utcNow = DateTime.UtcNow;
            // Don't repaint if not playing, except when we tick.
            var frameCountChangedWhilePaused = false;
            var hitRepaintTimerWhileResumed = false;
            if (EditorApplication.isPaused)
            {
                var frameCount = Time.frameCount;
                frameCountChangedWhilePaused = frameCount != m_PreviousFrameCount;
                m_PreviousFrameCount = frameCount;
            }
            else
            {
                hitRepaintTimerWhileResumed = utcNow - s_LastRepaintedUtc >= TimeSpan.FromSeconds(1);
            }

            if (hitRepaintTimerWhileResumed || frameCountChangedWhilePaused || s_ForceRepaint)
            {
                s_ForceRepaint = false;
                Repaint();
            }
        }

        [MenuItem("Window/Multiplayer/Toggle Lag Spike Simulation _F12", priority = 3007)]
        static void ToggleLagSpikeSimulatorShortcut()
        {
            if (ClientServerBootstrap.ClientWorld != null)
            {
                var system = ClientServerBootstrap.ClientWorld.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>();
                system.ToggleLagSpikeSimulator();
                s_ForceRepaint = true;
            }
        }

        // This interface implementation is automatically called by Unity.
        void IHasCustomMenu.AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(s_ShowAllSimulatorPresets, Prefs.ShowAllSimulatorPresets, ToggleShowingAllSimulatorPresets);

            static void ToggleShowingAllSimulatorPresets()
            {
                InUseSimulatorPresets = SimulatorPreset.DefaultInUseSimulatorPresets;
                Prefs.ShowAllSimulatorPresets ^= true;
                RefreshSimulatorPresets();
                s_ForceRepaint = true;
            }
        }

        void OnGUI()
        {
            var utcNow = DateTime.UtcNow;
            s_LastRepaintedUtc = utcNow;
            s_ShouldUpdateStatusTexts = (utcNow - s_LastWrittenUtc) >= TimeSpan.FromSeconds(.98f) || s_ForceRepaint;
            if (s_ShouldUpdateStatusTexts) s_LastWrittenUtc = utcNow;

            HackFixBoxStyle();

            HandleWindowProperties();

            DrawPlayType();

            DrawThinClientSelector();

            DrawClientAutoConnect();

            DrawSeparator();

            m_WorldScrollPosition = GUILayout.BeginScrollView(m_WorldScrollPosition, false, false);
            {
                if (Prefs.RequestedPlayType != ClientServerBootstrap.PlayType.Server)
                {
                    DrawSimulator();
                    DrawSeparator();
                }

                DrawLoggingGroup();

                DrawDebugGizmosDrawer();

                if (EditorApplication.isPlaying)
                {
                    var numWorldsDisplayed = 0;

                    DrawAllServerWorlds(ref numWorldsDisplayed);

                    DrawAllPresentingClientWorlds(ref numWorldsDisplayed);

                    DrawAllThinClientWorlds(ref numWorldsDisplayed);

                    if (numWorldsDisplayed == 0)
                    {
                        GUI.color = Color.white;
                        DrawSeparator();
                        EditorGUILayout.HelpBox("No NetCode worlds exist yet.", MessageType.Info);
                    }
                    else if (numWorldsDisplayed > k_MaxWorldsToDisplay)
                    {
                        GUI.color = Color.white;
                        EditorGUILayout.HelpBox("Too many worlds to display!", MessageType.Warning);
                    }
                }
                else
                {
                    DrawSeparator();
                    GUI.color = Color.grey;
                    GUILayout.Label("\nEnter playmode to view data and controls for NetCode-related worlds.\n");
                    DrawSeparator();
                }
            }
            GUILayout.EndScrollView();
        }

        // Required due to bug: https://fogbugz.unity3d.com/f/cases/1398336/
        static void HackFixBoxStyle()
        {
            s_BoxStyleHack ??= new GUIStyle(GUI.skin.box);
            s_BoxStyleHack.normal.textColor = s_BoxStyleHack.onNormal.textColor = EditorGUIUtility.isProSkin ? new Color(0.824f, 0.824f, 0.824f, 1f) : Color.black;
        }

        void DrawAllServerWorlds(ref int numWorldsDisplayed)
        {
            if (ClientServerBootstrap.ServerWorlds.Count > 0)
            {
                DrawSeparator();
                foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
                {
                    if (++numWorldsDisplayed > k_MaxWorldsToDisplay)
                        break;

                    DrawServerWorld(serverWorld);
                }
            }
        }

        void DrawAllPresentingClientWorlds(ref int numWorldsDisplayed)
        {
            if (ClientServerBootstrap.ClientWorlds.Count > 0)
            {
                DrawSeparator();
                foreach (var clientWorld in ClientServerBootstrap.ClientWorlds)
                {
                    if (++numWorldsDisplayed > k_MaxWorldsToDisplay)
                        break;

                    DrawClientWorld(clientWorld);
                }
            }
        }

        void DrawAllThinClientWorlds(ref int numWorldsDisplayed)
        {
            if (ClientServerBootstrap.ThinClientWorlds.Count > 0)
            {
                DrawSeparator();
                foreach (var world in ClientServerBootstrap.ThinClientWorlds)
                {
                    if (++numWorldsDisplayed > k_MaxWorldsToDisplay)
                        break;

                    DrawClientWorld(world);
                }
            }
        }

        void HandleWindowProperties()
        {
            // Window:
            minSize = new Vector2(600, 210);
            maxSize = new Vector2(1200, maxSize.y);
        }

        static void DrawClientAutoConnect()
        {
            var isValidEditorAuthoredAddress = Prefs.IsEditorInputtedAddressValidForConnect(out var targetEp);
            if (Prefs.RequestedPlayType == ClientServerBootstrap.PlayType.Client)
            {
                EditorGUI.BeginChangeCheck();
                GUILayout.BeginHorizontal();
                {
                    GUI.color = isValidEditorAuthoredAddress ? Color.white : GhostAuthoringComponentEditor.brokenColor;
                    Prefs.AutoConnectionAddress = EditorGUILayout.TextField(s_AutoConnectionAddress, Prefs.AutoConnectionAddress);
                    Prefs.AutoConnectionPort = (ushort) EditorGUILayout.IntField(s_AutoConnectionPort, Prefs.AutoConnectionPort);
                }
                GUILayout.EndHorizontal();

                if (EditorGUI.EndChangeCheck())
                    Prefs.AutoConnectionAddress = new Regex(@"[^0-9.]+").Replace(Prefs.AutoConnectionAddress, "");

                GUI.enabled = isValidEditorAuthoredAddress;
                if (EditorApplication.isPlaying)
                {
                    var autoConnectionAddress = Prefs.AutoConnectionAddress;
                    if (string.IsNullOrWhiteSpace(autoConnectionAddress)) autoConnectionAddress = "??.??.??.??";
                    var autoConnectionPort = Prefs.AutoConnectionPort.ToString();
                    if (Prefs.AutoConnectionPort == 0) autoConnectionPort = "??";
                    s_ClientConnect.text = $"Connect to {autoConnectionAddress}:{autoConnectionPort}";
                    if (GUILayout.Button(s_ClientConnect))
                    {
                        foreach (var clientWorld in ClientServerBootstrap.AllClientWorldsEnumerator())
                        {
                            var connSystem = clientWorld.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>();
                            connSystem.ChangeStateImmediate(targetEp);
                        }
                    }
                }

                GUI.enabled = true;
                GUI.color = Color.white;
            }

            // Notifying of code vs editor overrides:
            if (EditorApplication.isPlaying)
            {
                if (!ClientServerBootstrap.DetermineIfBootstrappingEnabled())
                {
                    // TODO should be able to do this warning outside of playmode
                    EditorGUILayout.HelpBox("Bootstrapping is disabled for this project or scene. I.e. Waiting for you to create netcode worlds yourself, which will then appear here.", MessageType.Warning);
                }
                else if (!ClientServerBootstrap.WillServerAutoListen)
                {
                    static bool AnyServerListening()
                    {
                        foreach (var x in ClientServerBootstrap.ServerWorlds)
                            if (x.IsCreated && x.GetExistingSystemManaged<MultiplayerServerPlayModeConnectionSystem>().IsListening)
                                return true;
                        return false;
                    }
                    static bool AnyClientConnecting()
                    {
                        foreach (var x in ClientServerBootstrap.AllClientWorldsEnumerator())
                            if (x.IsCreated && x.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>().ClientConnectionState != ConnectionState.State.Disconnected)
                                return true;
                        return false;
                    }
                    var anyNetcodeActivity = AnyServerListening() || AnyClientConnecting();
                    if (!anyNetcodeActivity)
                    {
                        switch (Prefs.RequestedPlayType)
                        {
                            case ClientServerBootstrap.PlayType.ClientAndServer:
                                EditorGUILayout.HelpBox("Auto-connection is disabled via this Bootstrapper. Waiting for you to manually call `Connect` or `Listen` in the NetworkStreamReceiveSystem on the ClientWorld[s].", MessageType.Warning);
                                break;
                            case ClientServerBootstrap.PlayType.Client:
                                EditorGUILayout.HelpBox("Auto-connection is disabled via this Bootstrapper. Waiting for you to manually call `NetworkStreamReceiveSystem.Connect`. Alternatively, use the controls above.", MessageType.Warning);
                                break;
                            case ClientServerBootstrap.PlayType.Server:
                                EditorGUILayout.HelpBox("Auto-connection is disabled via this Bootstrapper. Waiting for you to manually call `NetworkStreamReceiveSystem.Listen` in the \"Server World\".", MessageType.Warning);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(Prefs.RequestedPlayType), Prefs.RequestedPlayType, nameof(DrawClientAutoConnect));
                        }
                    }
                }
            }
        }

        static void DrawThinClientSelector()
        {
            GUI.color = Color.white;
            GUILayout.BeginHorizontal();
            {
                // Thin clients are only enabled if the delegates are hooked up.
                // As there are two types (bootstap vs runtime), if we're not in Play Mode,
                // we can check if either are present.
                GUI.enabled = EditorApplication.isPlaying
                    ? AutomaticThinClientWorldsUtility.IsRuntimeInitializationEnabled
                    : AutomaticThinClientWorldsUtility.IsBootstrapInitializationEnabled || AutomaticThinClientWorldsUtility.IsRuntimeInitializationEnabled;
                var buttonLayout = GUILayout.ExpandWidth(false);
                EditorGUILayout.PrefixLabel(s_NumThinClients);
                if (GUILayout.Button("-", buttonLayout))
                    Prefs.RequestedNumThinClients--;
                Prefs.RequestedNumThinClients = EditorGUILayout.IntField(Prefs.RequestedNumThinClients, GUILayout.MaxWidth(200));
                if (GUILayout.Button("+", buttonLayout))
                    Prefs.RequestedNumThinClients++;
                Prefs.ThinClientCreationFrequency = EditorGUILayout.FloatField(s_InstantiationFrequency, Prefs.ThinClientCreationFrequency);
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
            var isRunningWithoutOptimizations = Prefs.RequestedNumThinClients > 4 && !BurstCompiler.IsEnabled;
            var isRunningHighCount = Prefs.RequestedNumThinClients > 16;
            if(isRunningWithoutOptimizations || isRunningHighCount)
                EditorGUILayout.HelpBox("Enabling many in-process thin clients will slow down enter-play-mode durations (as well as throttle the editor itself). It is therefore recommended to have Burst enabled, your Editor set to Release, and to use this feature sparingly.", MessageType.Warning);
        }

        static void DrawPlayType()
        {
#if UNITY_USE_MULTIPLAYER_ROLES
            if (Unity.Multiplayer.Editor.EditorMultiplayerRolesManager.EnableMultiplayerRoles)
            {
                EditorGUILayout.HelpBox($"When Multiplayer Content Selection is active, the PlayMode Type is overriden by the active Multiplayer Role.", MessageType.Info);
                EditorGUI.BeginDisabledGroup(true);
            }
#endif

            GUI.color = EditorApplication.isPlayingOrWillChangePlaymode ? Color.grey : Color.white;
            EditorGUI.BeginChangeCheck();
            var requestedPlayType = (int) Prefs.RequestedPlayType;
            var hostMode = NetCodeConfig.HostWorldMode.BinaryWorlds;
            var hasHostWorld = false;
            foreach (var world in World.All)
            {
                if (world.IsHost())
                {
                    hasHostWorld = true;
                    break;
                }
            }

            if (hasHostWorld)
            {
                hostMode = NetCodeConfig.HostWorldMode.SingleWorld;
            }
            else
            {
                NetCodeConfig.RuntimeTryFindSettings();
                if (NetCodeConfig.Global != null)
                    hostMode = NetCodeConfig.Global.HostWorldModeSelection;
            }
            EditorPopup(s_PlayModeType, hostMode == NetCodeConfig.HostWorldMode.BinaryWorlds ? k_PlayModeStrings : k_PlayModeStringsSingleWorld, ref requestedPlayType);

            if (EditorGUI.EndChangeCheck())
            {
                Prefs.RequestedPlayType = (ClientServerBootstrap.PlayType) requestedPlayType;
                EditorApplication.isPlaying = false;
            }

            if (hostMode == NetCodeConfig.HostWorldMode.BinaryWorlds)
            {
                if ((ClientServerBootstrap.PlayType)requestedPlayType != ClientServerBootstrap.PlayType.Client &&
                    NetCodeClientSettings.instance.ClientTarget == NetCodeClientTarget.ClientAndServer)
                {
                    EditorGUI.BeginChangeCheck();
                    var simulateDedicatedServer = Prefs.SimulateDedicatedServer ? 1 : 0;
                    EditorPopup(s_ServerEmulation, s_ServerEmulationContents, ref simulateDedicatedServer);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Prefs.SimulateDedicatedServer = simulateDedicatedServer > 0;
                        EditorApplication.isPlaying = false;
                    }
                }
            }
            else
            {
                Prefs.SimulateDedicatedServer = false;
            }

#if UNITY_USE_MULTIPLAYER_ROLES
            if (Unity.Multiplayer.Editor.EditorMultiplayerRolesManager.EnableMultiplayerRoles)
            {
                EditorGUI.EndDisabledGroup();
            }
#endif
        }

        void DrawSimulator()
        {
            GUILayout.BeginHorizontal();

            // Simulator Toggle:
            {
                EditorGUI.BeginChangeCheck();
                GUI.color = Prefs.SimulatorEnabled ? s_Blue : Color.white;
                var wasSimulatorEnabled = Prefs.SimulatorEnabled;
                Prefs.SimulatorEnabled = EditorGUILayout.Toggle(s_SimulatorTitle, wasSimulatorEnabled);
                if (EditorGUI.EndChangeCheck())
                {
                    if (wasSimulatorEnabled != Prefs.SimulatorEnabled)
                    {
                        EditorApplication.isPlaying = false;
                        HandleSimulatorValuesChanged(Prefs.IsCurrentNetworkSimulatorPresetCustom);
                    }
                }
                GUI.color = Color.white;
            }

            // Per-Packet View vs Ping View.
            if (Prefs.SimulatorEnabled)
            {
                GUILayout.FlexibleSpace();

                // HACK: Subtract and add 1 to resolve the issue with '[Obsolete] Disabled' being 0.
                // It's a breaking change to fix properly.
                var requestedSimulatorView = (int) Prefs.RequestedSimulatorView;
                requestedSimulatorView -= 1;
                EditorPopup(s_SimulatorView, s_SimulatorViewContents, ref requestedSimulatorView, s_SimulatorViewWidth);
                Prefs.RequestedSimulatorView = (SimulatorView) requestedSimulatorView + 1;
            }

            GUILayout.EndHorizontal();

            if (Prefs.SimulatorEnabled)
            {
                // Presets:
                {
                    EditorGUI.BeginChangeCheck();
                    Prefs.CurrentNetworkSimulatorPreset = EditorPopup(s_SimulatorPreset, s_InUseSimulatorPresetContents, Prefs.CurrentNetworkSimulatorPreset);
                    if (EditorGUI.EndChangeCheck())
                        HandleSimulatorValuesChanged(false);
                }

                // Manual simulator values:
                {
                    float perPacketMin = math.max(0, Prefs.PacketDelayMs - Prefs.PacketJitterMs);
                    float perPacketMax = Prefs.PacketDelayMs + Prefs.PacketJitterMs;
                    var totalDelayMin = perPacketMin * 2;
                    var totalDelayMax = perPacketMax * 2;
                    var totalRtt = Prefs.PacketDelayMs * 2;
                    var totalJitter = Prefs.PacketJitterMs * 2;

                    switch (Prefs.RequestedSimulatorView)
                    {
                        case SimulatorView.PerPacketView:
                        {
                            GUILayout.BeginHorizontal();
                            {
                                EditorGUI.BeginChangeCheck();
                                Prefs.PacketDelayMs = EditorGUILayout.IntField(s_PacketDelay, Prefs.PacketDelayMs);
                                Prefs.PacketJitterMs = EditorGUILayout.IntField(s_PacketJitter, Prefs.PacketJitterMs);
                                if (EditorGUI.EndChangeCheck())
                                    HandleSimulatorValuesChanged(true);
                            }
                            GUILayout.EndHorizontal();

                            var lastPerPacketMin = perPacketMin;
                            var lastPerPacketMax = perPacketMax;
                            EditorGUI.BeginChangeCheck();

                            s_PacketDelayRange.text = $"Range {perPacketMin} to {perPacketMax} (ms)";
                            EditorGUILayout.MinMaxSlider(s_PacketDelayRange, ref perPacketMin, ref perPacketMax, 0, 500);

                            if (EditorGUI.EndChangeCheck())
                            {
                                // Prevents int precision lost causing this value to change when it shouldn't.
                                if (math.abs(perPacketMin - lastPerPacketMin) - math.abs(perPacketMax - lastPerPacketMax) <= 0.001f)
                                    Prefs.PacketJitterMs = (int) math.round((perPacketMax - perPacketMin) / 2f);
                                Prefs.PacketDelayMs = (int) (perPacketMin + Prefs.PacketJitterMs);
                                HandleSimulatorValuesChanged(true);
                            }
                            break;
                        }
                        case SimulatorView.PingView:
                        {
                            GUILayout.BeginHorizontal();
                            {
                                EditorGUI.BeginChangeCheck();
                                totalRtt = EditorGUILayout.IntField(s_RttDelay, totalRtt);
                                totalJitter = EditorGUILayout.IntField(s_RttJitter, totalJitter);
                                if (EditorGUI.EndChangeCheck())
                                {
                                    Prefs.PacketJitterMs = (int) math.round(totalJitter / 2f);
                                    Prefs.PacketDelayMs = (int) math.round(totalRtt / 2f);

                                    HandleSimulatorValuesChanged(true);
                                }
                            }
                            GUILayout.EndHorizontal();

                            var lastTotalDelayMin = totalDelayMin;
                            var lastTotalDelayMax = totalDelayMax;
                            EditorGUI.BeginChangeCheck();

                            s_RttDelayRange.text = $"Range {(int) totalDelayMin} to {(int) totalDelayMax} (ms)";
                            EditorGUILayout.MinMaxSlider(s_RttDelayRange, ref totalDelayMin, ref totalDelayMax, 0, 800);

                            if (EditorGUI.EndChangeCheck())
                            {
                                // Prevents int precision lost causing this value to change when it shouldn't.
                                if (math.abs(totalDelayMin - lastTotalDelayMin) - math.abs(totalDelayMax - lastTotalDelayMax) <= 0.001f)
                                    Prefs.PacketJitterMs = (int) math.round((totalDelayMax - totalDelayMin) * .25f);
                                Prefs.PacketDelayMs = (int) (totalDelayMin * .5f + Prefs.PacketJitterMs);
                                HandleSimulatorValuesChanged(true);
                            }
                            break;
                        }
#pragma warning disable CS0618
                        case SimulatorView.Disabled:
#pragma warning restore CS0618
                            // Show nothing.
                            break;
                        default:
                            Debug.LogError("Unknown Prefs.SimulatorModeInEditor, using default!");
                            Prefs.RequestedSimulatorView = Prefs.DefaultSimulatorView;
                            HandleSimulatorValuesChanged(false);
                            break;
                    }



                    // Packet Loss %.
                    {
                        EditorGUI.BeginChangeCheck();
                        GUILayout.BeginHorizontal();
                        Prefs.PacketDropPercentage = EditorGUILayout.IntField(s_PacketDrop, Prefs.PacketDropPercentage);
                        Prefs.PacketFuzzPercentage = EditorGUILayout.IntField(s_FuzzyPacket, Prefs.PacketFuzzPercentage);
                        GUILayout.EndHorizontal();
                        if (EditorGUI.EndChangeCheck())
                            HandleSimulatorValuesChanged(true);
                    }

                    // Notify users if they're simulating something very bad.
                    {
                        if (Prefs.PacketFuzzPercentage > 0)
                            EditorGUILayout.HelpBox("This simulator is intentionally corrupting packets (sent by - and received by - the client). Expect errors and data corruptions.", MessageType.Error);
                        else
                        {
                            if (Prefs.PacketDropPercentage > 60 || Prefs.PacketDelayMs + Prefs.PacketJitterMs > 500)
                                EditorGUILayout.HelpBox("You are simulating a terrible connection. Expect transport connection issues and/or unplayability.", MessageType.Error);
                            else if (Prefs.PacketDropPercentage > 15 || Prefs.PacketDelayMs + Prefs.PacketJitterMs > 200)
                                EditorGUILayout.HelpBox("You are simulating a poor connection. Expect netcode instability and/or visible lag.", MessageType.Warning);
                        }
                    }

                    // Lag spike UI:
                    if (Prefs.RequestedPlayType != ClientServerBootstrap.PlayType.Server)
                    {
                        DrawSeparator();

                        var firstClient = ClientServerBootstrap.ClientWorld ?? (ClientServerBootstrap.ThinClientWorlds.Count != 0 ? ClientServerBootstrap.ThinClientWorlds[0] : null);
                        var connSystem = firstClient != null && firstClient.IsCreated ? firstClient.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>() : null;

                        GUILayout.BeginHorizontal();
                        {
                            var keyBinding = UnityEditor.ShortcutManagement.ShortcutManager.instance.GetShortcutBinding(k_ToggleLagSpikeSimulatorBindingKey).ToString();
                            if (string.IsNullOrWhiteSpace(keyBinding))
                                keyBinding = "no shortcut";
                            s_LagSpike.text = $"Lag Spike Simulator [{keyBinding}]";
                            var isSimulatingLagSpike = connSystem != null && connSystem.IsSimulatingLagSpike;
                            GUI.color = isSimulatingLagSpike ? GhostAuthoringComponentEditor.brokenColor : s_Blue;
                            GUILayout.Label(s_LagSpike);
                            GUILayout.FlexibleSpace();

                            GUI.enabled = Application.isPlaying && connSystem != null;
                            s_LagSpike.text = isSimulatingLagSpike ? $"Triggered [{Mathf.CeilToInt(connSystem.LagSpikeMillisecondsLeft)}ms]" : $"Trigger";
                            if (GUILayout.Button(s_LagSpike, s_RightButtonWidth))
                            {
                                connSystem?.ToggleLagSpikeSimulator();
                            }

                            GUI.enabled = true;
                        }
                        GUILayout.EndHorizontal();
                        GUI.color = Color.white;
                        Prefs.LagSpikeSelectionIndex = GUILayout.Toolbar(Prefs.LagSpikeSelectionIndex, k_LagSpikeDurationStrings);
                    }
                }
            }
        }

        void DrawClientWorld(World world)
        {
            if (world == default || !world.IsCreated || world.IsHost()) return;

            var conSystem = world.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>();
            if (conSystem == null)
            {
                // during playmode tests, only essential systems are included as part of the world. editor systems aren't which means conSystem is null
                // TODO should just query the world directly for that info, why is this cached in a system?
                return;
            }
            if (s_ShouldUpdateStatusTexts) conSystem.UpdateStatusText();
            var isConnected = conSystem.ClientConnectionState == ConnectionState.State.Connected;
            var isHandshakeOrApproval = conSystem.NetworkStreamConnection.IsHandshakeOrApproval;
            var connectionColor = GetConnectionStateColor(conSystem.ClientConnectionState);
            GUILayout.BeginHorizontal();
            {
                GUI.color = connectionColor;
                s_NetworkId.text = isConnected && !isHandshakeOrApproval ? conSystem.NetworkId.Value.ToString() : "-";
                GUILayout.Box(s_NetworkId, s_BoxStyleHack, s_NetworkIdWidth);

                s_WorldName.text = world.Name;
                GUILayout.Label(world.Name + $" [{(world.IsHost() ? "Host" : world.IsClient() ? "Client" : "Server")}]", s_WorldNameWidth);
                GUI.color = Color.white;
                DrawDriverDisplayInfo(ref conSystem.DriverInfos, conSystem.NetworkStreamConnection);

                if (conSystem.IsSimulatingLagSpike)
                {
                    GUI.color = GhostAuthoringComponentEditor.brokenColor;
                    GUILayout.Label(s_LagSpikeOccuring);
                }

                GUI.color = connectionColor;
                if (conSystem.ClientConnectionState == ConnectionState.State.Unknown)
                {
                    GUILayout.Label(s_NoNetworkConnectionEntity);
                }
                else
                {
                    s_ClientConnectionState.text = $"[{conSystem.ClientConnectionState.ToString()}]";
                    GUILayout.Label(s_ClientConnectionState);
                }

                if (world.IsThinClient() && AutomaticThinClientWorldsUtility.AutomaticallyManagedWorlds.Contains(world))
                {
                    GUI.color = s_Blue;
                    GUILayout.Label(s_Auto);
                }

                if (conSystem.DisconnectPending)
                {
                    GUI.color = GhostAuthoringComponentEditor.brokenColor;
                    GUILayout.Label(s_PendingDc);
                }
                else if (conSystem.TargetEp.HasValue)
                {
                    GUI.color = Color.yellow;
                    s_Awaiting.text = $"[Awaiting {conSystem.TargetEp.Value.Address}]";
                    GUILayout.Label(s_Awaiting);
                }

                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUI.color = Color.white;
            if ((conSystem.ClientConnectionState != ConnectionState.State.Disconnected && conSystem.ClientConnectionState != ConnectionState.State.Unknown) || conSystem.TargetEp.HasValue)
            {
                GUI.enabled = conSystem.ClientConnectionState != ConnectionState.State.Disconnected;
                if (GUILayout.Button(s_ClientReconnect))
                {
                    NetCodeAnalytics.SendAnalytic(new PlayModeConnectionChangedAnalytic(Operation.ClientReconnect, conSystem.World));
                    Prefs.IsEditorInputtedAddressValidForConnect(out var ep);
                    conSystem.ChangeStateImmediate(conSystem.LastEndpoint ?? ep);
                }

                GUI.enabled = true;
                if (GUILayout.Button(s_ClientDc))
                {
                    NetCodeAnalytics.SendAnalytic(new PlayModeConnectionChangedAnalytic(Operation.ClientDisconnect, conSystem.World));
                    conSystem.ChangeStateImmediate(null);
                }

                if (GUILayout.Button(s_ServerDc))
                {
                    NetCodeAnalytics.SendAnalytic(new PlayModeConnectionChangedAnalytic(Operation.ServerDisconnect, conSystem.World));
                    ServerDisconnectNetworkId(conSystem);
                }
            }
            else
            {
                if (GUILayout.Button("Connect"))
                {
                    NetCodeAnalytics.SendAnalytic(new PlayModeConnectionChangedAnalytic(Operation.Connect, conSystem.World));
                    Prefs.IsEditorInputtedAddressValidForConnect(out var ep);
                    conSystem.ChangeStateImmediate(conSystem.LastEndpoint ?? ep);
                }
            }

            // You can force a timeout even when disconnected, to allow testing reconnect attempts while timed out.
            var isTimingOut = conSystem.IsSimulatingTimeout;
            s_Timeout.text = isTimingOut ? $"Simulating Timeout\n[{Mathf.CeilToInt(conSystem.TimeoutSimulationDurationSeconds)}s]" : $"Timeout";
            GUI.color = isTimingOut ? GhostAuthoringComponentEditor.brokenColor :  Color.white;
            if (GUILayout.Button(s_Timeout))
            {
                NetCodeAnalytics.SendAnalytic(new PlayModeConnectionChangedAnalytic(Operation.Timeout, conSystem.World));
                conSystem.ToggleTimeoutSimulation();
            }

            GUI.color = connectionColor;
            GUILayout.Box(conSystem.PingText, s_BoxStyleHack, s_PingWidth);
            EditorGUILayout.EndHorizontal();

            DrawConnectionEvents(conSystem.ConnectionEventsForTick);

            EditorGUILayout.Separator();
        }

        private static Color GetConnectionStateColor(ConnectionState.State state)
        {
            switch (state)
            {
                case ConnectionState.State.Unknown:
                    return GhostAuthoringComponentEditor.brokenColor;
                case ConnectionState.State.Disconnected:
                    return s_Red;
                case ConnectionState.State.Connecting:
                    return Color.yellow;
                case ConnectionState.State.Handshake:
                    return s_Orange;
                case ConnectionState.State.Approval:
                    return s_Pink;
                case ConnectionState.State.Connected:
                    return s_Blue;
                default: throw new NotImplementedException(state.ToString());
            }
        }

        void DrawServerWorld(World serverWorld)
        {
            if (serverWorld == default || !serverWorld.IsCreated) return;
            var conSystem = serverWorld.GetExistingSystemManaged<MultiplayerServerPlayModeConnectionSystem>();
            if (s_ShouldUpdateStatusTexts) conSystem.UpdateStatusText();
            var connectingColor = conSystem.IsListening ? s_Green : GhostAuthoringComponentEditor.brokenColor;

            GUILayout.BeginHorizontal();
            {
                GUILayout.BeginVertical();
                GUILayout.BeginHorizontal();
                GUI.color = connectingColor;
                s_NetworkId.text = "-";
                GUILayout.Box(s_NetworkId, s_BoxStyleHack, s_NetworkIdWidth);

                s_WorldName.text = serverWorld.Name;
                EditorGUILayout.LabelField(s_WorldName + $" [{(serverWorld.IsHost() ? "host" : "server")}]", s_WorldNameWidth);
                if (conSystem == null)
                {
                    // during playmode tests, only essential systems are included as part of the world. editor systems aren't which means conSystem is null
                    // TODO should just query the world directly for that info, why is this cached in a system?
                    GUILayout.EndHorizontal();
                    return;
                }
                DrawDriverDisplayInfo(ref conSystem.DriverInfos, null);

                GUILayout.FlexibleSpace();

                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();

                GUI.color = Color.white;
                GUILayout.EndHorizontal();
            }

            // Server button panel:
            {
                GUILayout.BeginHorizontal();

                if (GUILayout.Button(s_ServerDcAllClients))
                {
                    NetCodeAnalytics.SendAnalytic(new PlayModeConnectionChangedAnalytic(Operation.ServerDisconnect, TargetWorld.AllClients));
                    DisconnectAllClients(serverWorld);
                }

                if (GUILayout.Button(s_ServerReconnectAllClients))
                {
                    NetCodeAnalytics.SendAnalytic(new PlayModeConnectionChangedAnalytic(Operation.ServerReconnect, TargetWorld.AllClients));
                    DisconnectAllClients(serverWorld);

                    foreach (var clientWorld in ClientServerBootstrap.AllClientWorldsEnumerator())
                    {
                        if (!clientWorld.IsCreated) continue;
                        var connSystem = clientWorld.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>();
                        Prefs.IsEditorInputtedAddressValidForConnect(out var ep);
                        connSystem.ChangeStateImmediate(connSystem.LastEndpoint ?? ep);
                    }
                }

                if (GUILayout.Button(s_ServerLogRelevancy))
                {
                    NetCodeAnalytics.SendAnalytic(new PlayModeLogRelevancyAnalytic());
                    LogRelevancy(serverWorld);
                }
                if (GUILayout.Button(s_ServerLogCommandStats))
                {
                    NetCodeAnalytics.SendAnalytic(new PlayModeLogCommandStatsAnalytic());
                    LogCommandStats(serverWorld);
                }

                GUILayout.EndVertical();
                GUILayout.EndHorizontal();

                GUI.color = connectingColor;
                GUILayout.Box(s_ServerStats, s_BoxStyleHack, s_PingWidth);
            }
            GUILayout.EndHorizontal();

            DrawConnectionEvents(conSystem.ConnectionEventsForTick);
        }

        private static void DrawConnectionEvents(List<NetCodeConnectionEvent> connectionEvents)
        {
            if (connectionEvents.Count == 0)
                return;

            GUI.color = s_Green;
            FixedString4096Bytes s = "";
            for (int i = 0; i < connectionEvents.Count; i++)
            {
                var evt = connectionEvents[i];
                s.Append(evt.ConnectionId.ToFixedString());
                s.Append('-');
                if (evt.Id.Value < 0)
                    s.Append('?');
                else s.Append(evt.Id.Value);
                s.Append('-');
                s.Append(evt.State.ToFixedString());
                if (evt.State == ConnectionState.State.Disconnected)
                {
                    s.Append('-');
                    s.Append(evt.DisconnectReason.ToFixedString());
                }
                if (i < connectionEvents.Count - 1)
                {
                    s.Append(' ');
                    s.Append('|');
                    s.Append(' ');
                }
            }
            GUILayout.Label(s.ToString(), EditorStyles.wordWrappedLabel);
        }

        private static void DrawDriverDisplayInfo(ref FixedList512Bytes<DriverDisplayInfo> displayInfos, NetworkStreamConnection? clientConnection)
        {
            for (int i = 0; i < displayInfos.Length; i++)
            {
                ref var ddi = ref displayInfos.ElementAt(i);
                const char separator = ':';
                FixedString32Bytes text = default;

                // Family & TransportType:
                var type = ddi.TransportType switch
                {
                    TransportType.IPC => "IPC",
                    TransportType.Socket => "UDP",  // We assume UDP!
                    TransportType.Invalid => "Invalid",
                    _ => throw new NotImplementedException(ddi.TransportType.ToString()),
                };
                var family = ddi.NetworkFamily switch
                {
                    // TODO - Transport does not reset the Bound field when disconnecting a client,
                    // so don't display that here as it's misleading.
                    NetworkFamily.Invalid => clientConnection.HasValue ? type : $"{type}{separator}{(ddi.Bound ? "BoundOnly" : "Closed")}",
                    NetworkFamily.Ipv4 => type,
                    NetworkFamily.Ipv6 => type,
                    NetworkFamily.Custom => ddi.IsWebSocket ? "WebSocket" : "Custom",
                    _ => throw new NotImplementedException(ddi.NetworkFamily.ToString()),
                };
                text += family;

                // Address:
                string address = null;
                if (ddi.Endpoint.IsValid) address += $"{separator}{ddi.Endpoint.Address}";

                GUI.color = (type: ddi.TransportType, family: ddi.NetworkFamily, isWebSocket: ddi.IsWebSocket) switch
                {
                    (TransportType.IPC, _, _) => s_Green,
                    (_, _, true) => Color.yellow,
                    (_, NetworkFamily.Custom, false) => s_Orange,
                    (_, NetworkFamily.Ipv4, _) => s_Blue,
                    (_, NetworkFamily.Ipv6, _) => s_Pink,
                    _ => GhostAuthoringComponentEditor.brokenColor,
                };
                s_DriverDisplayInfo.text = $"[{text}{address}]";
                GUILayout.Label(s_DriverDisplayInfo);

                // Emulation:
                if (clientConnection.HasValue)
                {
                    GUI.color = Color.white;
                    s_NetworkEmulation.text = ddi.SimulatorEnabled ? "[Emulation Enabled]" : "[No Emulation]";
                    GUILayout.Label(s_NetworkEmulation);
                }
            }
        }

        static string EditorPopup(GUIContent content, GUIContent[] list, string value)
        {
            var index = 0;
            for (var i = 0; i < list.Length; i++)
            {
                if (string.Equals(value, list[i].text, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            EditorPopup(content, list, ref index);
            value = list[index].text;
            return value;
        }

        static void EditorPopup(GUIContent content, GUIContent[] list, ref int index)
        {
            index = math.clamp(index, 0, list.Length);
            index = EditorGUILayout.Popup(content, index, list);
        }

        static void EditorPopup(GUIContent content, GUIContent[] list, ref int index, GUILayoutOption style)
        {
            index = math.clamp(index, 0, list.Length);
            index = EditorGUILayout.Popup(content, index, list, style);
        }

        static void HandleSimulatorValuesChanged(bool isUsingCustomValues)
        {
            if (!isUsingCustomValues && SimulatorPreset.TryGetPresetFromName(Prefs.CurrentNetworkSimulatorPreset, s_InUseSimulatorPresetsCache, out var preset, out _))
            {
                Prefs.ApplySimulatorPresetToPrefs(preset);
            }
            else
            {
                Prefs.CurrentNetworkSimulatorPreset = SimulatorPreset.k_CustomProfileKey;
                // Leave values as is, as they are user-defined.
            }

            if(Application.isPlaying)
                RefreshSimulationPipelineParametersLiveForAllWorlds();
        }

        void DrawLoggingGroup()
        {
            GUILayout.BeginHorizontal();
            GUI.color = Prefs.ApplyLoggerSettings ? s_Blue : Color.white;
            Prefs.ApplyLoggerSettings = EditorGUILayout.Toggle(s_ForceLogLevel, Prefs.ApplyLoggerSettings);
            if (!Prefs.ApplyLoggerSettings)
                DrawLogFileLocationButton();
            GUILayout.EndHorizontal();

            GUI.color = Color.white;
            if (Prefs.ApplyLoggerSettings)
            {
                Prefs.TargetLogLevel = (NetDebug.LogLevelType) EditorGUILayout.EnumPopup(s_LogLevel, Prefs.TargetLogLevel);

                GUILayout.BeginHorizontal();
#if NETCODE_NDEBUG
                GUI.enabled = false;
#else
                GUI.enabled = Prefs.ApplyLoggerSettings;
#endif
                Prefs.TargetShouldDumpPackets = EditorGUILayout.Toggle(s_DumpPacketLogs, Prefs.TargetShouldDumpPackets);
                GUI.enabled = true;
                DrawLogFileLocationButton();
                GUILayout.EndHorizontal();
            }
#if NETCODE_NDEBUG
            EditorGUILayout.HelpBox("`NETCODE_NDEBUG` is currently defined, so netcode packet dump functionality (and related CPU overhead) is removed.", MessageType.Info);
#endif

            DrawSeparator();

            GUILayout.BeginHorizontal();
            bool WarnBatchedTicksBeforeToggle = Prefs.WarnBatchedTicks;
            Prefs.WarnBatchedTicks = EditorGUILayout.Toggle(s_WarnBatchedTicks, Prefs.WarnBatchedTicks);
            GUILayout.EndHorizontal();

            if ( Highlighter.active && WarnBatchedTicksBeforeToggle != Prefs.WarnBatchedTicks && Highlighter.activeText == s_WarnBatchedTicks.text )
            {
                Highlighter.Stop();
            }

            if (Prefs.WarnBatchedTicks)
            {
                GUILayout.BeginHorizontal();
                Prefs.WarnBatchedTicksRollingWindow = EditorGUILayout.IntField(s_WarnBatchedTicksRollingWindow, Prefs.WarnBatchedTicksRollingWindow);
                Prefs.WarnAboveAverageBatchedTicksPerFrame = EditorGUILayout.FloatField(s_WarnAboveAverageBatchedTicksPerFrame, Prefs.WarnAboveAverageBatchedTicksPerFrame);
                GUILayout.EndHorizontal();
            }

            static void DrawLogFileLocationButton()
            {
                GUI.enabled = true;
                if (GUILayout.Button(s_LogFileLocation, s_RightButtonWidth))
                    EditorUtility.OpenWithDefaultApp(s_LogFileLocation.tooltip);
            }
        }

        void DrawDebugGizmosDrawer()
        {
            if (DebugGhostDrawer.CustomDrawers.Count <= 0) return;
            if (Prefs.RequestedPlayType != ClientServerBootstrap.PlayType.ClientAndServer) return;

            DrawSeparator();

            foreach (var visitor in DebugGhostDrawer.CustomDrawers)
            {
                EditorGUI.BeginChangeCheck();

                GUILayout.BeginHorizontal();
                {
                    GUI.enabled = visitor.Enabled;
                    GUI.color = new Color(0.84f, 0.84f, 0.84f);
                    visitor.DetailsVisible = EditorGUILayout.BeginFoldoutHeaderGroup(visitor.DetailsVisible, visitor.Name);

                    GUI.enabled = true;
                    GUI.color = visitor.Enabled ? s_Blue : Color.grey;
                    if (GUILayout.Button(visitor.Enabled ? "Drawing" : "Disabled", s_RightButtonWidth))
                        visitor.Enabled ^= true;
                }
                GUILayout.EndHorizontal();

                if (visitor.DetailsVisible)
                {
                    GUI.color = Color.white;
                    GUI.enabled = visitor.Enabled;
                    visitor.OnGuiAction?.Invoke();
                }
                EditorGUILayout.EndFoldoutHeaderGroup();
                GUI.enabled = true;

                if (EditorGUI.EndChangeCheck())
                    visitor.EditorSave();
            }
        }

        static ref NetDebug GetNetDbgForWorld(World world)
        {
            using var netDebugQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>());
            return ref netDebugQuery.GetSingletonRW<NetDebug>().ValueRW;
        }

        static void LogRelevancy(World serverWorld)
        {
            using var relevancyQuery = serverWorld.EntityManager.CreateEntityQuery(typeof(GhostRelevancy));
            var relevancy = relevancyQuery.GetSingleton<GhostRelevancy>();
            string relevantMode = (relevancy.GhostRelevancyMode == GhostRelevancyMode.SetIsRelevant ? "relevant" : "irrelevant");
            string message = $"Mode {relevancy.GhostRelevancyMode}. {relevantMode} count: {relevancy.GhostRelevancySet.Count()}\n";
            if (relevancy.GhostRelevancyMode != GhostRelevancyMode.Disabled)
            {
                foreach (var entry in relevancy.GhostRelevancySet)
                {
                    message += $"- ghostId {entry.Key.Ghost} {relevantMode} for\t {entry.Key.Connection}\n";
                }
            }

            var allChunks = relevancy.DefaultRelevancyQuery.ToArchetypeChunkArray(Allocator.Temp);
            NativeHashSet<EntityArchetype> archetypeSet = new NativeHashSet<EntityArchetype>(allChunks.Length, Allocator.Temp);
            foreach (var chunk in allChunks)
            {
                archetypeSet.Add(chunk.Archetype);
            }

            message += $"\nTotal matching archetype count for global relevancy query {archetypeSet.Count}\n";
            foreach (var entityArchetype in archetypeSet)
            {
                message += $"- archetype {entityArchetype.ToString()}\n";
            }

            Debug.Log(message);
        }

        static void LogCommandStats(World serverWorld)
        {
            using var query = serverWorld.EntityManager.CreateEntityQuery(typeof(NetworkSnapshotAck), typeof(NetworkId));
            var networkSnapshotAcks = query.ToComponentDataArray<NetworkSnapshotAck>(Allocator.Temp);
            var networkIds = query.ToComponentDataArray<NetworkId>(Allocator.Temp);
            var message = $"Stats for {networkSnapshotAcks.Length} players:";
            for (var i = 0; i < networkSnapshotAcks.Length; i++)
            {
                var ack = networkSnapshotAcks[i];
                message += $"\n- Client {networkIds[i].ToFixedString()} with ping {(int)ack.EstimatedRTT}±{(int)ack.DeviationRTT} has {ack.CommandArrivalStatistics.ToFixedString()} and {ack.SnapshotPacketLoss.ToFixedString()}";
            }
            Debug.Log(message);
        }

        static void DisconnectAllClients(World serverWorld)
        {
            serverWorld.EntityManager.CompleteAllTrackedJobs();
            using var activeConnectionsQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.ReadOnly<NetworkStreamConnection>());
            var networkIds = activeConnectionsQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
            serverWorld.GetExistingSystemManaged<MultiplayerServerPlayModeConnectionSystem>().TryDisconnectImmediate(networkIds.ToArray());
        }

        void DrawSeparator()
        {
            GUI.color = Color.white;
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        }

        static void RefreshSimulationPipelineParametersLiveForAllWorlds()
        {
            foreach (var clientWorld in ClientServerBootstrap.AllClientWorldsEnumerator())
            {
                clientWorld.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>().UpdateSimulator = true;
            }
        }

        /// <summary>Note: Will disconnect this NetworkId from all server worlds it is found in.</summary>
        static void ServerDisconnectNetworkId(MultiplayerClientPlayModeConnectionSystem connSystem)
        {
            foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
            {
                serverWorld.GetExistingSystemManaged<MultiplayerServerPlayModeConnectionSystem>().TryDisconnectImmediate(connSystem.NetworkId);
                GetNetDbgForWorld(serverWorld).DebugLog($"{serverWorld.Name} triggered `{nameof(ServerDisconnectNetworkId)}` on NetworkId `{connSystem.NetworkId.Value}` via `{nameof(MultiplayerPlayModeWindow)}`!");
                connSystem.DisconnectPending = true;
            }
        }

        private static void HandleHyperLinkClicked(EditorWindow window, HyperLinkClickedEventArgs args)
        {
            HandleHyperLinkArgs(args.hyperLinkData);
        }

        internal static void HandleHyperLinkArgs( Dictionary<string,string> hyperLinkData )
        {
            if ( hyperLinkData.TryGetValue("href", out var href ) && NetCodeHyperLinkArguments.s_OpenPlayModeTools.Equals(href) )
            {
                ShowWindow();
            }

            if ( hyperLinkData.TryGetValue( "highlight", out var highlight ) && NetCodeHyperLinkArguments.s_HighlightWarnBatchedTicks.Equals(highlight) )
            {
                s_HighlightWarnBatchedTicks = true;
            }
        }


        private void HighlightWarnBatchedTicks()
        {
            if (DateTime.UtcNow > s_HighlightWarnBatchedTicksTime)
            {
                Highlighter.Highlight(k_Title, s_WarnBatchedTicks.text );
                EditorApplication.update -= HighlightWarnBatchedTicks;
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    internal partial class MultiplayerClientPlayModeConnectionSystem : SystemBase
    {
        internal GUIContent PingText = new GUIContent();
        internal NetworkStreamConnection NetworkStreamConnection { get; private set; }
        internal ConnectionState.State ClientConnectionState => NetworkStreamConnection.CurrentState;
        internal GhostCount GhostCount { get; private set; }

        internal NetworkSnapshotAck ClientNetworkSnapshotAck;
        internal NetworkId NetworkId;

        public bool UpdateSimulator;
        public bool DisconnectPending;
        public FixedList512Bytes<DriverDisplayInfo> DriverInfos;

        public bool IsAnyUsingSimulator {get; private set;}
        public List<NetCodeConnectionEvent> ConnectionEventsForTick { get; } = new(4);
        public NetworkEndpoint? LastEndpoint {get; private set;}
        public NetworkEndpoint? TargetEp {get; private set;}

        internal TransportType SocketType { get; private set; }
        internal NetworkFamily SocketFamily { get; private set; }

        internal int LagSpikeMillisecondsLeft { get; private set; } = -1;
        internal float TimeoutSimulationDurationSeconds { get; private set; } = -1;

        internal bool IsSimulatingTimeout => TimeoutSimulationDurationSeconds >= 0;
        internal bool IsSimulatingLagSpike => LagSpikeMillisecondsLeft >= 0;

        EntityQuery m_PredictedGhostsQuery;

        protected override void OnCreate()
        {
            if (World.IsHost())
            {
                Enabled = false;
                return;
            }
            RequireForUpdate<UnscaledClientTime>();
            m_PredictedGhostsQuery = GetEntityQuery(ComponentType.ReadOnly<PredictedGhost>());
            UpdateStatusText();
        }

        protected override void OnUpdate()
        {
            Dependency.Complete();

            var netDebug = SystemAPI.GetSingleton<NetDebug>();

            var unscaledClientTime = SystemAPI.GetSingleton<UnscaledClientTime>();
            if (IsSimulatingTimeout)
            {
                TimeoutSimulationDurationSeconds += unscaledClientTime.UnscaleDeltaTime;
            }

            if (IsSimulatingLagSpike)
            {
                LagSpikeMillisecondsLeft -= Mathf.CeilToInt(unscaledClientTime.UnscaleDeltaTime * 1000);
                if (!IsSimulatingLagSpike || ClientConnectionState == ConnectionState.State.Disconnected)
                {
                    LagSpikeMillisecondsLeft = -1;
                    UpdateSimulator = true;
                    netDebug.DebugLog("Lag Spike Simulator: Finished dropping packets!");
                    MultiplayerPlayModeWindow.s_ForceRepaint = true;
                }
            }

            var lastState = ClientConnectionState;
            NetworkStreamConnection = SystemAPI.TryGetSingleton(out NetworkStreamConnection conn) ? conn : default;
            GhostCount = SystemAPI.TryGetSingleton(out GhostCount ghostCount) ? ghostCount : default;

            DriverInfos.Length = 0;
            var hasNetworkStreamDriver = SystemAPI.TryGetSingletonRW<NetworkStreamDriver>(out var netStream);
            if (hasNetworkStreamDriver)
            {
                ref var driverStore = ref netStream.ValueRO.DriverStore;
                DriverDisplayInfo.Read(ref driverStore, ref DriverInfos, NetworkStreamConnection.Value);

                LastEndpoint = netStream.ValueRO.LastEndPoint;
                IsAnyUsingSimulator = driverStore.IsAnyUsingSimulator;
                ConnectionEventsForTick.Clear();
                if (EditorApplication.isPaused) // Can't see one frame events when unpaused anyway.
                {
                    if (netStream.ValueRO.ConnectionEventsForTick.Length > 0)
                    {
                        ConnectionEventsForTick.AddRange(netStream.ValueRO.ConnectionEventsForTick);
                        MultiplayerPlayModeWindow.s_ForceRepaint = true;
                    }
                }
            }

            if (ClientConnectionState != lastState)
                MultiplayerPlayModeWindow.s_ForceRepaint = true;

            if (ClientConnectionState != ConnectionState.State.Disconnected && SystemAPI.TryGetSingletonEntity<NetworkStreamConnection>(out var singletonEntity) && EntityManager.HasComponent<NetworkId>(singletonEntity))
            {
                NetworkId = EntityManager.GetComponentData<NetworkId>(singletonEntity);
                ClientNetworkSnapshotAck = EntityManager.GetComponentData<NetworkSnapshotAck>(singletonEntity);
            }
            else
            {
                NetworkId = default;
                ClientNetworkSnapshotAck = default;
                DisconnectPending = false;
            }

            if (UpdateSimulator && hasNetworkStreamDriver)
            {
                UpdateSimulator = false;

                var clientSimulatorParameters = Prefs.ClientSimulatorParameters;
                if (IsSimulatingTimeout || IsSimulatingLagSpike)
                {
                    clientSimulatorParameters.PacketDropPercentage = 100;
                }

                NetworkSimulatorSettings.RefreshSimulationPipelineParametersLive(in clientSimulatorParameters, ref netStream.ValueRW.DriverStore);
            }
            if (TargetEp.HasValue)
                ChangeStateImmediate(TargetEp);
        }

        internal void UpdateStatusText()
        {
            if (ClientConnectionState == ConnectionState.State.Connected)
            {
                var estimatedRTT = (int) ClientNetworkSnapshotAck.EstimatedRTT;
                var deviationRTT = (int) ClientNetworkSnapshotAck.DeviationRTT;
                PingText.text = estimatedRTT < 1000 ? $"{estimatedRTT}±{deviationRTT}ms" : $"~{estimatedRTT + deviationRTT}ms";

            var snapshotPacketLoss = ClientNetworkSnapshotAck.SnapshotPacketLoss.ToFixedString().ToString();
            var predictedGhostCount = m_PredictedGhostsQuery.CalculateEntityCount();
            var interpolatedGhostCount = GhostCount.IsCreated ? GhostCount.GhostCountInstantiatedOnClient - predictedGhostCount : 0;

            var ghostCount = World.IsThinClient() ? "n/a" : $"{GhostCount}\n{predictedGhostCount} Predicted, {interpolatedGhostCount} Interpolated";
            PingText.tooltip =
$@"<b>GhostCount</b> Singleton
{ghostCount}
 • <i>Note1: Received % can be greater than 100%, as the server can despawn many ghosts at once.</i>
 • <i>Note2: Thin clients do not fully process received snapshots (to remain lightweight), and therefore don't spawn any ghosts.</i>

<b>SnapshotPacketLossStatistics</b> Singleton
{snapshotPacketLoss}
 • <i>Note3: Packet clobbering can be mitigated. See Manual.</i>
";
            }
            else
            {
                PingText.text = "-";
                PingText.tooltip = "Not connected.";
            }
        }

        public void ToggleLagSpikeSimulator()
        {
            if (!IsAnyUsingSimulator)
            {
                SystemAPI.GetSingletonRW<NetDebug>().ValueRW.LogError($"Cannot enable LagSpike simulator as Client Network Emulation is disabled!");
                return;
            }

            if(IsSimulatingTimeout)
                ToggleTimeoutSimulation();

            LagSpikeMillisecondsLeft = IsSimulatingLagSpike ? -1 : MultiplayerPlayModeWindow.k_LagSpikeDurationsSeconds[Prefs.LagSpikeSelectionIndex];
            UpdateSimulator = true;
            SystemAPI.GetSingletonRW<NetDebug>().ValueRW.DebugLog($"Lag Spike Simulator: Toggled! Dropping packets for {Mathf.CeilToInt(LagSpikeMillisecondsLeft)}ms!");
            MultiplayerPlayModeWindow.s_ForceRepaint = true;
            NetCodeAnalytics.SendAnalytic(new PlayModeLagSpikeTriggeredAnalytic(LagSpikeMillisecondsLeft));
        }

        public void ToggleTimeoutSimulation()
        {
            if (!IsAnyUsingSimulator)
            {
                SystemAPI.GetSingletonRW<NetDebug>().ValueRW.LogError($"Cannot enable Timeout Simulation as Client Network Emulation is disabled!");
                return;
            }

            if(LagSpikeMillisecondsLeft > 0)
                ToggleLagSpikeSimulator();

            var isSimulatingTimeout = IsSimulatingTimeout;
            SystemAPI.GetSingletonRW<NetDebug>().ValueRW.DebugLog($"Timeout Simulation: Toggled {(isSimulatingTimeout ? $"OFF after {TimeoutSimulationDurationSeconds}s!" : "ON")}!");

            UpdateSimulator = true;
            if (isSimulatingTimeout)
                TimeoutSimulationDurationSeconds = -1;
            else TimeoutSimulationDurationSeconds = 0;

            MultiplayerPlayModeWindow.s_ForceRepaint = true;
        }

        public void ChangeStateImmediate(NetworkEndpoint? targetEp)
        {
            if (!SystemAPI.TryGetSingletonRW<NetworkStreamDriver>(out var netStream))
            {
                UnityEngine.Debug.LogError($"{World.Name} does not have a NetworkStreamDriver, unable to perform actions via {nameof(MultiplayerPlayModeWindow)}!");
                return;
            }
            TargetEp = targetEp;

            // Disconnect first:
            // - F0: Disconnect is invoked somewhere on Frame0.
            // - F1: NetworkStreamReceiveSystem will poll the connection entity, and create a BeginSimulation ECB destroying the existing entity.
            // - F2: ECB is invoked, so now there is no NetworkStreamConnection. Connect can safely be called.
            if (SystemAPI.TryGetSingletonEntity<NetworkStreamConnection>(out var connectedEntity))
            {
                var existingConn = EntityManager.GetComponentData<NetworkStreamConnection>(connectedEntity);
                if (existingConn.Value.IsCreated)
                {
                    NetworkStreamConnection = SystemAPI.GetSingleton<NetworkStreamConnection>();
                    if (ClientConnectionState != ConnectionState.State.Disconnected)
                    {
                        UnityEngine.Debug.Log($"[{World.Name}] You triggered a disconnection of {existingConn.Value.ToFixedString()} (on {connectedEntity.ToFixedString()}) via {nameof(MultiplayerPlayModeWindow)}!");
                        MultiplayerPlayModeWindow.s_ForceRepaint = true;
                        netStream.ValueRW.DriverStore.Disconnect(existingConn);
                        DisconnectPending = true;
                        UpdateStatusText();
                    }
                }
                // Wait 1 frame before reconnecting:
                return;
            }

            // Connect:
            UpdateSimulator = true;
            if (targetEp != default)
            {
                if (targetEp.Value.IsValid)
                {
                    LagSpikeMillisecondsLeft = -1;
                    UpdateSimulator = true;
                    UnityEngine.Debug.Log($"[{World.Name}] You triggered a reconnection to {targetEp.Value.Address} via {nameof(MultiplayerPlayModeWindow)}!");
                    MultiplayerPlayModeWindow.s_ForceRepaint = true;
                    var connEntity = netStream.ValueRW.Connect(EntityManager, targetEp.Value);
                    NetworkStreamConnection = EntityManager.GetComponentData<NetworkStreamConnection>(connEntity);
                }
                else
                {
                    UnityEngine.Debug.LogError($"[{World.Name}] You triggered a reconnection, but targetEp:{targetEp.Value.Address} is not valid!");
                }
            }
            TargetEp = null;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    internal partial class MultiplayerServerPlayModeConnectionSystem : SystemBase
    {
        public bool IsListening { get; private set; }

        public FixedList512Bytes<DriverDisplayInfo> DriverInfos;

        public List<NetCodeConnectionEvent> ConnectionEventsForTick { get; } = new(4);

        private EntityQuery m_ActiveConnectionsQuery;
        private EntityQuery m_NotInGameQuery;
        private EntityQuery m_GhostsQuery;
        private EntityQuery m_GhostPrefabsQuery;

        protected override void OnCreate()
        {
            m_ActiveConnectionsQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.Exclude<NetworkStreamRequestDisconnect>());
            m_NotInGameQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.Exclude<NetworkStreamRequestDisconnect>(), ComponentType.Exclude<NetworkStreamInGame>());
            m_GhostsQuery = GetEntityQuery(ComponentType.ReadOnly<GhostInstance>());
            m_GhostPrefabsQuery = GetEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<Prefab>());
            UpdateStatusText();
        }

        internal void TryDisconnectImmediate(params NetworkId[] networkIdsToDisconnect)
        {
            Dependency.Complete();
            m_ActiveConnectionsQuery.CompleteDependency();
            var networkIdEntities = m_ActiveConnectionsQuery.ToEntityArray(WorldUpdateAllocator);
            var networkIdValues = m_ActiveConnectionsQuery.ToComponentDataArray<NetworkId>(WorldUpdateAllocator);
            ref readonly var netStream = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            var connectionLookup = SystemAPI.GetComponentLookup<NetworkStreamConnection>(true);

            foreach (var networkIdToDc in networkIdsToDisconnect)
            {
                for (int i = 0; i < networkIdValues.Length; i++)
                {
                    var networkId = networkIdValues[i];
                    if (networkId.Value == networkIdToDc.Value)
                    {
                        var entity = networkIdEntities[i];
                        if (!connectionLookup.TryGetComponent(entity, out var conn))
                        {
                            Debug.LogError($"Unable to disconnect NetworkId[{networkId.Value}] (found on Entity {entity.ToFixedString()} on {World.Name}) as no NetworkStreamConnection component found!");
                            continue;
                        }
                        netStream.DriverStore.Disconnect(conn);
                        goto found;
                    }
                }

                Debug.LogError($"Unable to disconnect NetworkId[{networkIdToDc.Value}] from {World.Name} as unable to find connection with this NetworkId!");
                found: ;
            }
        }

        protected override void OnUpdate()
        {
            ref readonly var netStream = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            ref var driverStore = ref netStream.DriverStore;
            IsListening = netStream.DriverStore.GetDriverInstanceRO(netStream.DriverStore.FirstDriver).driver.Listening;
            ConnectionEventsForTick.Clear();
            if (EditorApplication.isPaused) // Can't see one frame events when unpaused anyway.
                ConnectionEventsForTick.AddRange(netStream.ConnectionEventsForTick);
            Editor.DriverDisplayInfo.Read(ref driverStore, ref DriverInfos, null);
        }

        public void UpdateStatusText()
        {
            var ghostChunkCount = m_GhostsQuery.CalculateChunkCount();
            var ghostCount = m_GhostsQuery.CalculateEntityCount();
            var ghostPrefabCount = m_GhostPrefabsQuery.CalculateEntityCount();
            var numConnections = m_ActiveConnectionsQuery.CalculateEntityCount();
            var numInGame = numConnections - m_NotInGameQuery.CalculateEntityCount();
            MultiplayerPlayModeWindow.s_ServerStats.text = $"{numConnections} Clients\n{ghostCount} Ghosts";
            var ghostsPerChunk = ghostChunkCount > 0 ? $"\n~{(int)(ghostCount / (float)ghostChunkCount)} Ghosts Per Chunk" : "";
            MultiplayerPlayModeWindow.s_ServerStats.tooltip = $@"<b>Client Connections</b>
{numConnections} Connected
{numInGame} In-Game

<b>Ghosts</b>
{ghostCount} Ghost Instances
Across {ghostChunkCount} Chunks{ghostsPerChunk}
{ghostPrefabCount} Ghost Types";
        }

        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.Editor)]
        [CreateAfter(typeof(SceneSystem))]
        internal partial struct ConfigureClientGUIDSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                bool canChangeSettings = (!UnityEditor.EditorApplication.isPlaying || state.WorldUnmanaged.IsClient());
                if (canChangeSettings)
                {
                    ref var sceneSystemGuid = ref state.EntityManager.GetComponentDataRW<SceneSystemData>(state.World.GetExistingSystem<SceneSystem>()).ValueRW;
                    sceneSystemGuid.BuildConfigurationGUID = DotsGlobalSettings.Instance.GetClientGUID();
                }

                state.Enabled = false;
            }

        }

        [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
        [CreateAfter(typeof(SceneSystem))]
        internal partial struct ConfigureServerGUIDSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                ref var sceneSystemGuid = ref state.EntityManager.GetComponentDataRW<SceneSystemData>(state.World.GetExistingSystem<SceneSystem>()).ValueRW;
                // If client type is client-only, the server must use dedicated server data:
                if (NetCodeClientSettings.instance.ClientTarget == NetCodeClientTarget.Client)
                    sceneSystemGuid.BuildConfigurationGUID = DotsGlobalSettings.Instance.GetServerGUID();
                // If playmode is simulating dedicated server, we must also use server data:
                else if (Prefs.SimulateDedicatedServer)
                    sceneSystemGuid.BuildConfigurationGUID = DotsGlobalSettings.Instance.GetServerGUID();
                // Otherwise we use client & server data, as we know that 'client hosted' is possible in the editor at this point:
                else
                    sceneSystemGuid.BuildConfigurationGUID = DotsGlobalSettings.Instance.GetClientGUID();

                state.Enabled = false;
            }
        }
    }

    internal struct DriverDisplayInfo
    {
        public TransportType TransportType;
        public NetworkFamily NetworkFamily;
        public byte DriverIndex;
        public bool IsWebSocket;
        public bool Listening;
        public bool Bound;
        public bool SimulatorEnabled;
        public NetworkEndpoint Endpoint;

        public static void Read(ref NetworkDriverStore driverStore, ref FixedList512Bytes<DriverDisplayInfo> list, NetworkConnection? clientConnection)
        {
            UnityEngine.Assertions.Assert.IsTrue(list.Capacity >= NetworkDriverStore.Capacity);
            list.Length = math.min(driverStore.DriversCount, list.Capacity);
            for (int entryIdx = 0; entryIdx < list.Length; entryIdx++)
            {
                ref var entry = ref list.ElementAt(entryIdx);
                var driverIdx = entryIdx + driverStore.FirstDriver;
                entry.DriverIndex = (byte)driverIdx;
                entry.TransportType = driverStore.GetDriverType(driverIdx);
                ref var driver = ref driverStore.GetDriverRW(driverIdx); // RW as calling non-readonly method!
                entry.NetworkFamily = driver.GetLocalEndpoint().Family;
                entry.IsWebSocket = driver.CurrentSettings.TryGet<WebSocketParameter>(out _);
                entry.SimulatorEnabled = driverStore.GetDriverInstanceRO(driverIdx).simulatorEnabled;
                entry.Listening = driver.Listening;
                entry.Bound = driver.Bound;
                entry.Endpoint = clientConnection.HasValue
                    ? driver.GetRemoteEndpoint(clientConnection.Value)
                    : driver.GetLocalEndpoint();
            }
        }
    }
}

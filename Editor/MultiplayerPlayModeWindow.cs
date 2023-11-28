using System;
using System.Collections.Generic;
using System.Linq;
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
using Unity.NetCode.Hybrid;
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
        const int k_MaxWorldsToDisplay = 8;
        const int k_InitialThinClientWorldCreationInterval = 1;
        const int k_ThinClientWorldCreationFailureRetryInterval = 5;
        const string k_ToggleLagSpikeSimulatorBindingKey = "Main Menu/Multiplayer/Toggle Lag Spike Simulation";
        const string k_SimulatorPresetCaveat = "\n\n<i>Note: The simulator can only <b>add</b> additional latency to a given connection, and it does so naively. Therefore, poor editor performance will exacerbate the delay (and is not compensated for).</i>";
        const string k_ProjectSettingsConfigPath = "<i>ProjectSettings > Entities > Build</i>";

        static Color ActiveColor => new Color(0.5f, 0.84f, 0.99f); // TODO: netCode color into this view. GhostAuthoringComponentEditor.netcodeColor;
        static GUILayoutOption s_PingWidth = GUILayout.Width(100);
        static GUILayoutOption s_NetworkIdWidth = GUILayout.Width(30);
        static GUILayoutOption s_SimulatorViewWidth = GUILayout.Width(120);
        static GUILayoutOption s_WorldNameWidth = GUILayout.Width(130);

        static GUIContent s_PlayModeType = new GUIContent("PlayMode Type", "During multiplayer development, it's useful to modify and run the client and server at the same time, in the same process (i.e. \"in-proc\"). DOTS Multiplayer supports this out of the box via the DOTS Entities \"Worlds\" feature.\n\nUse this toggle to determine which mode of operation is used for this playmode session.\n\n\"Client & Server\" is recommended for most workflows.");
        static GUIContent s_ServerEmulation = new GUIContent("Server Emulation", $"Denotes how the ServerWorld should load data when in PlayMode in the Editor. This setting does not affect builds (see {k_ProjectSettingsConfigPath} for build configuration).");
        static GUIContent[] s_ServerEmulationContents;
        static GUIContent s_NumThinClients = new GUIContent("Num Thin Clients", "Thin clients are clients that receive snapshots, but do not attempt to process game logic. They can send arbitrary inputs though, and are useful to simulate opponents (to test connection & game logic).\n\nThin clients are instantiated on boot and at runtime. I.e. This value can be tweaked during playmode.");
        static GUIContent s_InstantiationFrequency = new GUIContent("Instantiation Frequency", "How many thin client worlds to instantiate per second. Runtime thin client instantiation can be disabled by setting `RuntimeThinClientWorldInitialization` to null. Does not affect thin clients created during boot.");
        static GUIContent s_RuntimeInstantiationDisabled = new GUIContent("Runtime Instantiation Disabled", "Enable it by setting `MultiplayerPlayModeWindow.RuntimeThinClientWorldInitialization`.");

        static GUIContent s_AutoConnectionAddress = new GUIContent("Auto Connect Address", "The ClientServerBootstrapper will attempt to automatically connect the created client world to this address on boot.");
        static GUIContent s_AutoConnectionPort = new GUIContent("Auto Connect Port", "The ClientServerBootstrapper will attempt to automatically connect the created client world to this port on boot.");

        static GUIContent s_SimulatorTitle = new GUIContent("Network Emulation", "Enabling this allows you to emulate various realistic network conditions.\n\nIn practice, this toggle denotes whether or not all Client Worlds will pass Unity Transport's SimulatorPipelineStage into the NetworkDriver, during construction.\n\nFor this reason, toggling Network Emulation requires a PlayMode restart.");
        static GUIContent s_SimulatorPreset = new GUIContent("?? Presets", "Simulate a variety of connection types & server locations.\n\nThese presets have been created by Multiplayer devs.\n\n<b>We strongly recommend that you test every new multiplayer feature with this simulator enabled.</b>\n\nBy default, switching platform will change which presets are available to you. To toggle showing all presets, use the context menu. Alternatively, you can inject your own presets by modifying the `InUseSimulatorPresets` delegate.");
        static GUIContent s_ShowAllSimulatorPresets = new GUIContent("Show All Simulator Presets", "Toggle to view all simulator presets, or only your platform specific ones?");

        static GUIContent s_WebSocket = new GUIContent("[WebSocket]", "<b>WebSocket</b>\nThis World is using Unity's WebSocket NetworkInterface to communicate with the server.");
        static GUIContent s_UdpSocket = new GUIContent("[UDP]", "<b>UDP | User Datagram Protocol</b>\nThis World is using Unity's UDP socket NetworkInterface (formerly 'baselib') to communicate with the server.");
        static GUIContent s_Ipc = new GUIContent("[IPC]", "<b>IPC | Intra-Process Communication</b>\nThis World is using an IPC NetworkInterface to communicate with the server. IPC is an in-memory, socket-like wrapper, emulating the Transport API but without any OS overhead and unreliability.\n\nTherefore, IPC operations will be instantaneous, but can only be used to communicate with other NetworkDriver instances inside the same process (which is why IPC really means intra-process and not inter-process here). Useful for testing, or to implement a single player mode in a multiplayer game.");
        static GUIContent s_NetworkEmulation = new GUIContent(string.Empty, "Denotes whether or not this world uses Network Emulation with the above settings.");

        static GUIContent s_SimulatorView = new GUIContent(string.Empty, string.Empty);
        private const string s_SimulatorExplination = "The simulator works by adding a delay before processing all packets sent from - and received by - the ClientWorld's Socket Driver.\n\nIn this view, you can observe and modify ";
        static GUIContent[] s_SimulatorViewContents = {
            new GUIContent("Ping View",s_SimulatorExplination + "the sum of both the sent and received delays, which therefore becomes an estimation of the \"ping\" (i.e. \"RTT\") value. Thus, per-packet values will be roughly half these values.  Switch to the \"Per-Packet View\" to observe this."),
            new GUIContent("Per-Packet View",s_SimulatorExplination + "the emulator values applied to each packet (i.e. each way). Note that the effect on \"ping\" (i.e. \"RTT\") is therefore at least doubled. Switch to the \"Ping View\" to observe this."),
        };

        static GUIContent s_PacketDelay = new GUIContent("Packet Delay (ms)", "Fixed delay applied to each packet before it is processed. Simulates real network delay.");
        static GUIContent s_PacketJitter = new GUIContent("Packet Jitter (±ms)", "Random delay 'added to' or 'subtracted from' each packets delay (min 0). Simulates network jitter (where packets sent in order \"A > B > C\" can arrive \"A > C > B\".");
        static GUIContent s_PacketDelayRange = new GUIContent("", "Denotes the min and max delay for each packet, calculated as \"Delay ± Jitter\". Your ping will be roughly double, plus an additional delay incurred during frame processing, as well as any real packet delay.");

        static GUIContent s_RttDelay = new GUIContent("RTT Delay (+ms)", "A fixed delay is calculated and applied to each packet so that the sum of the delay (each way) adds up to this value, thus simulating your \"RTT\" or \"Ping\". Simulates real network delay.");
        static GUIContent s_RttJitter = new GUIContent("RTT Jitter (±ms)", "A random delay calculated and 'added to' or 'subtracted from' from each packets delay (min 0) so that the max jitter (i.e. variance) equals this value.\n\nSimulates network jitter (where packets sent in order \"A > B > C\" can arrive \"A > C > B\".");
        static GUIContent s_RttDelayRange = new GUIContent("", "Denotes your clients min and max simulated ping, calculated as \"Delay ± Jitter\".\n\nNote that your actual ping will be higher due to the delay incurred during frame processing, and any real packet delay.");

        static GUIContent s_PacketDrop = new GUIContent("Packet Drop (%)", "Denotes the percentage of packets - sent or received - that will be dropped. Simulates interruptions in UDP packet flow.");
        static GUIContent s_FuzzyPacket = new GUIContent("Packet Fuzz (%)", "Denotes the percentage of packets - sent or received - that will have random bits flipped (i.e. \"fuzzed\" / \"corrupted\"). Fuzzed packets trigger (often catastrophic) errors in deserialization code (both yours, and ours).\n\nI.e. This tool is used for security testing, and simulates malicious MitM attacks, and thus, error recovery.\n\nNote: These packets will PASS packet CRC validation checks, so cannot be easily discarded.");

        static GUIContent[] s_InUseSimulatorPresetContents;
        static List<SimulatorPreset> s_InUseSimulatorPresetsCache = new List<SimulatorPreset>(32);

        static readonly GUIContent[] k_PlayModeStrings = { new GUIContent("Client & Server", "Instantiates a server instance alongside a single \"full\" client, with a configurable number of thin clients."), new GUIContent("Client", "Only instantiate a client (with a configurable number of thin clients) that'll automatically attempt to connect to the listed address and port."), new GUIContent("Server", "Only instantiate a server. Expects that clients will be instantiated in another process.")};
        static GUILayoutOption s_ExpandWidth = GUILayout.ExpandWidth(true);
        static GUILayoutOption s_DontExpandWidth = GUILayout.ExpandWidth(false);
        static GUIContent s_ServerName = new GUIContent("", "Name of server world.");
        static GUIContent s_ServerPort = new GUIContent("", "Listening Port");
        static GUIContent s_ServerPlayers = new GUIContent("", "Count of connected players. | Count of players who have registered as 'in-game' via the `NetworkStreamInGame` component, on the Server.");
        static GUIContent s_ClientConnect = new GUIContent("", "Trigger all clients to disconnect from the server they're connected to and [re]connect to the specified address and port.");
        static GUIContent s_ServerDcAllClients = new GUIContent("DC All", "Trigger the server to attempt to gracefully disconnect all clients. Useful to batch-test a bunch of client disconnect scenarios (e.g. mid-game).");
        static GUIContent s_ServerReconnectAllClients = new GUIContent("Reconnect All", "Trigger the server to attempt to gracefully disconnect all clients, then have them automatically reconnect. Useful to batch-test player rejoining scenarios (e.g. people dropping out mid-match).\n\nNote that clients will also disconnect themselves from the server in the same frame as they're attempting to reconnect, so you can test same frame DCing.");
        static GUIContent s_ClientReconnect = new GUIContent("Client Reconnect", "Attempt to gracefully disconnect from the server, followed by an immediate reconnect attempt.");
        static GUIContent s_ClientDc = new GUIContent("Client DC", "Attempt to gracefully disconnect from the server. Triggered by the client (e.g. a player closing the application).");
        static GUIContent s_ServerDc = new GUIContent("Server DC", "Trigger the server to attempt to gracefully disconnect this client, identified by their 'NetworkId'. Server-authored (e.g. like a server kicking a client when the match has ended).");
        static GUIContent s_Timeout = new GUIContent("Force Timeout", "Simulate a timeout (i.e. the client and server stop communicating instantly, and critically, <b>without</b> either being able to send graceful disconnect control messages). A.k.a. An \"ungraceful\" disconnection or \"Server unreachable\".\n\n- Clients should notify the player of the internet issue, and provide automatic (or triggerable) reconnect or quit flows.\n\n - Servers should ensure they handle clients timing out as a valid form of disconnection, and (if supported) ensure that 'same client reconnections' are properly handled.\n\n - Transport settings will inform how quickly all parties detect a lost connection.");

        static GUIContent s_LogFileLocation = new GUIContent("Open Log Folder", string.Empty);
        static GUIContent s_ForceLogLevel = new GUIContent("Force Log Settings", "Force all `NetDebug` loggers to a specified setting, clobbering any `NetCodeDebugConfig` singleton.");
        static GUIContent s_LogLevel = new GUIContent("Log Level", "Every NetDbg log is raised with a specific severity. Use this to discard logs below this level.");
        static GUIContent s_DumpPacketLogs = new GUIContent("Dump Packet Logs", "Should we dump packet logs to `NetDebug.LogFolderForPlatform`?\n\nNote: Modify this value to enable editor override, otherwise the editor will use whatever logging configuration values are already set.");
        static GUIContent s_LagSpike = new GUIContent("", "In playmode, press the shortcut key to toggle 'total packet loss' for the specified duration.\n\nUseful when testing short periods of lost connection (e.g. while in a tunnel) and to see how well your client and server handle an \"ungraceful\" disconnect (e.g. internet going down).\n\n- This window must be open for this tool to work.\n- Will only be applied to the \"full\" (i.e.: rendering) clients.\n- Depending on timeouts specified, this may cause the actual driver to timeout. Ensure you handle reconnections.");

        static readonly string[] k_LagSpikeDurationStrings = { "10ms", "100ms", "200ms", "500ms", "1s", "2s", "5s", "10s", "30s", "1m", "2m"};
        internal static readonly int[] k_LagSpikeDurationsSeconds = { 10, 100, 200, 500, 1_000, 2_000, 5_000, 10_000, 30_000, 60_000, 120_000 };

        static ulong s_LastNextSequenceNumber;
        static float s_SecondsTillCanCreateThinClient;
        static bool s_UserIsInteractingWithMenu;
        static TimeSpan s_RepaintDelayTimeSpan = TimeSpan.FromSeconds(1);

        static GUIStyle s_BoxStyleHack;
        static DateTime s_LastRepaintedUtc;
        Vector2 m_WorldScrollPosition;
        bool m_DidRepaint;

        /// <inheritdoc cref="ClientServerBootstrap.RuntimeThinClientWorldInitialization"/>
        public delegate bool RuntimeThinClientWorldInitializationDelegate(World world);
        public delegate void SimulatorPresetsSelectionDelegate(out string presetGroupName, List<SimulatorPreset> appendPresets);

        /// <summary>
        ///     If your thin clients need custom initialization due to scene management settings, modify this delegate.
        ///     Set to null to disable the runtime ThinClient feature.
        /// </summary>
        public static RuntimeThinClientWorldInitializationDelegate RuntimeThinClientWorldInitialization = DefaultRuntimeThinClientWorldInitialization;

        /// <summary>If your team would prefer to use other Simulator Presets, override this.
        /// Defaults to: <see cref="SimulatorPreset.DefaultInUseSimulatorPresets"/></summary>
        public static SimulatorPresetsSelectionDelegate InUseSimulatorPresets = SimulatorPreset.DefaultInUseSimulatorPresets;

        static GUILayoutOption s_RightButtonWidth = GUILayout.Width(120);

        [MenuItem("Multiplayer/Window: PlayMode Tools", priority = 50)]
        private static void ShowWindow()
        {
            GetWindow<MultiplayerPlayModeWindow>(false, "PlayMode Tools", true);
        }

        void OnEnable()
        {
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
            s_InUseSimulatorPresetContents = s_InUseSimulatorPresetsCache.Select(x => new GUIContent(x.Name, x.Tooltip + k_SimulatorPresetCaveat)).ToArray();
        }

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= PlayModeStateChanged;
        }

        void PlayModeStateChanged(PlayModeStateChange playModeStateChange)
        {
            EditorApplication.update -= PlayModeUpdate;
            if (playModeStateChange == PlayModeStateChange.EnteredPlayMode)
                EditorApplication.update += PlayModeUpdate;

            s_SecondsTillCanCreateThinClient = k_InitialThinClientWorldCreationInterval;

            PlayModeUpdate();
            Repaint();
        }

        void PlayModeUpdate()
        {
            UpdateNumThinClientWorlds();

            var utcNow = DateTime.UtcNow;
            m_DidRepaint = utcNow - s_LastRepaintedUtc >= s_RepaintDelayTimeSpan;
            if (m_DidRepaint)
            {
                s_LastRepaintedUtc = utcNow;
                s_UserIsInteractingWithMenu = false;
                Repaint();
            }
        }

        [MenuItem("Multiplayer/Toggle Lag Spike Simulation _F12", priority = 51)]
        static void ToggleLagSpikeSimulatorShortcut()
        {
            if (ClientServerBootstrap.ClientWorld != null)
            {
                var system = ClientServerBootstrap.ClientWorld.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>();
                system.ToggleLagSpikeSimulator();
                ForceRepaint();
            }
        }

        /// <summary>By default, thin clients will attempt to copy the scenes loaded on the server, or the presenting client.</summary>
        static bool DefaultRuntimeThinClientWorldInitialization(World newThinClientWorld)
        {
            var worldToCopyFrom = ClientServerBootstrap.ClientWorld ?? ClientServerBootstrap.ServerWorld;
            if (worldToCopyFrom?.IsCreated != true)
            {
                Debug.LogError("Cannot properly initialize ThinClientWorld as no Client or Server world found, so no idea which scenes to load.");
                return false;
            }

            using var serverWorldScenesQuery = worldToCopyFrom.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<RequestSceneLoaded>(), ComponentType.ReadOnly<SceneReference>());
            var serverWorldScenes = serverWorldScenesQuery.ToComponentDataArray<SceneReference>(Allocator.Temp);
            for (int i = 0; i < serverWorldScenes.Length; i++)
            {
                var desiredGoSceneReferenceGuid = serverWorldScenes[i];
                SceneSystem.LoadSceneAsync(newThinClientWorld.Unmanaged,
                    desiredGoSceneReferenceGuid.SceneGUID,
                    new SceneSystem.LoadParameters
                    {
                        Flags = SceneLoadFlags.BlockOnImport | SceneLoadFlags.BlockOnStreamIn,
                        AutoLoad = true,
                    });
            }

            return true;
        }


        /// <summary>Will Create or Dispose thin client worlds until the final count is equal to <see cref="Prefs.NumThinClients"/>.</summary>
        void UpdateNumThinClientWorlds()
        {
            if (Prefs.RequestedPlayType == ClientServerBootstrap.PlayType.Server || !EditorApplication.isPlaying || EditorApplication.isCompiling || EditorApplication.isPaused) return;

            s_SecondsTillCanCreateThinClient -= Time.deltaTime;

            var requestedNumThinClients = MultiplayerPlayModePreferences.RequestedNumThinClients;

            // Dispose if too many:
            while(ClientServerBootstrap.ThinClientWorlds.Count > requestedNumThinClients)
            {
                var index = ClientServerBootstrap.ThinClientWorlds.Count - 1;
                var world = ClientServerBootstrap.ThinClientWorlds[index];
                if (world.IsCreated)
                    world.Dispose();
                ForceRepaint();
            }

            // Create new:
            var hasServerOrClient = ClientServerBootstrap.ServerWorld != null || ClientServerBootstrap.ClientWorld != null;
            if (hasServerOrClient && !s_UserIsInteractingWithMenu)
            {
                for(var i = ClientServerBootstrap.ThinClientWorlds.Count; i < requestedNumThinClients && s_SecondsTillCanCreateThinClient <= 0; i++)
                {
                    var thinClientWorld = ClientServerBootstrap.CreateThinClientWorld();
                    ForceRepaint();

                    var success = RuntimeThinClientWorldInitialization(thinClientWorld);

                    if (MultiplayerPlayModePreferences.ThinClientCreationFrequency > 0)
                        s_SecondsTillCanCreateThinClient = 1f / MultiplayerPlayModePreferences.ThinClientCreationFrequency;

                    if (!success)
                    {
                        s_SecondsTillCanCreateThinClient = math.max(s_SecondsTillCanCreateThinClient, k_ThinClientWorldCreationFailureRetryInterval);
                        return;
                    }
                }
            }
        }

        internal static void ForceRepaint()
        {
            s_LastRepaintedUtc = default;
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
                ForceRepaint();
            }
        }

        void OnGUI()
        {
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
            maxSize = new Vector2(600, maxSize.y);

            // Avoid creating new thin clients while the user is interacting with the menu.
            var e = Event.current;
            s_UserIsInteractingWithMenu |= e.type == EventType.MouseDrag || e.type == EventType.MouseDown;
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
                        foreach (var clientWorld in ClientServerBootstrap.ClientWorlds.Concat(ClientServerBootstrap.ThinClientWorlds))
                        {
                            var connSystem = clientWorld.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>();
                            connSystem.ClientConnectionState = MultiplayerClientPlayModeConnectionSystem.ConnectionState.TriggerConnect;
                            connSystem.OverrideEndpoint = targetEp;
                        }
                    }
                }

                GUI.enabled = true;
                GUI.color = Color.white;
            }

            // Notifying of code vs editor overrides:
            if (EditorApplication.isPlaying)
            {
                if (!ClientServerBootstrap.WillServerAutoListen)
                {
                    var anyConnected = ClientServerBootstrap.ServerWorlds.Any(x => x.IsCreated && x.GetExistingSystemManaged<MultiplayerServerPlayModeConnectionSystem>().IsListening)
                        || ClientServerBootstrap.ClientWorlds.Concat(ClientServerBootstrap.ThinClientWorlds).Any(x => x.IsCreated && x.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>().ClientConnectionState != MultiplayerClientPlayModeConnectionSystem.ConnectionState.NotConnected);
                    if (!anyConnected)
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
            if (Prefs.RequestedPlayType == ClientServerBootstrap.PlayType.Server)
                return;

            GUI.color = Color.white;
            GUILayout.BeginHorizontal();
            {
                GUI.enabled = !EditorApplication.isPlaying || RuntimeThinClientWorldInitialization != null;
                Prefs.RequestedNumThinClients = EditorGUILayout.IntField(s_NumThinClients, Prefs.RequestedNumThinClients);

                GUI.enabled = true;

                if(RuntimeThinClientWorldInitialization != null)
                    Prefs.ThinClientCreationFrequency = EditorGUILayout.FloatField(s_InstantiationFrequency, Prefs.ThinClientCreationFrequency);
                else
                {
                    GUI.enabled = false;
                    GUILayout.Box(s_RuntimeInstantiationDisabled, s_BoxStyleHack);
                    GUI.enabled = true;
                }
            }
            GUILayout.EndHorizontal();

            var isRunningWithoutOptimizations = Prefs.RequestedNumThinClients > 4 && !BurstCompiler.IsEnabled;
            var isRunningHighCount = Prefs.RequestedNumThinClients > 16;
            if(isRunningWithoutOptimizations || isRunningHighCount)
                EditorGUILayout.HelpBox("Enabling many in-process thin clients will slowdown enter-play-mode durations (as well as throttle the editor itself). It is therefore recommended to have Burst enabled, your Editor set to Release, and to use this feature sparingly.", MessageType.Warning);
        }

        static void DrawPlayType()
        {
#if UNITY_USE_MULTIPLAYER_ROLES
            if (Unity.Multiplayer.Editor.EditorMultiplayerManager.enableMultiplayerRoles)
            {
                EditorGUILayout.HelpBox($"When Multiplayer Content Selection is active, the PlayMode Type is overriden by the active Multiplayer Role.", MessageType.Info);
                EditorGUI.BeginDisabledGroup(true);
            }
#endif

            GUI.color = EditorApplication.isPlayingOrWillChangePlaymode ? Color.grey : Color.white;
            EditorGUI.BeginChangeCheck();
            var requestedPlayType = (int) Prefs.RequestedPlayType;
            EditorPopup(s_PlayModeType, k_PlayModeStrings, ref requestedPlayType);
            if (EditorGUI.EndChangeCheck())
            {
                Prefs.RequestedPlayType = (ClientServerBootstrap.PlayType) requestedPlayType;
                EditorApplication.isPlaying = false;
            }

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

#if UNITY_USE_MULTIPLAYER_ROLES
            if (Unity.Multiplayer.Editor.EditorMultiplayerManager.enableMultiplayerRoles)
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
                GUI.color = Prefs.SimulatorEnabled ? ActiveColor : Color.white;
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

                var requestedSimulatorView = (int) Prefs.RequestedSimulatorView;
                EditorPopup(s_SimulatorView, s_SimulatorViewContents, ref requestedSimulatorView, s_SimulatorViewWidth);
                Prefs.RequestedSimulatorView = (SimulatorView) requestedSimulatorView;
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

                        var firstClient = ClientServerBootstrap.ClientWorld ?? ClientServerBootstrap.ThinClientWorlds?.FirstOrDefault();
                        var connSystem = firstClient?.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>();

                        GUILayout.BeginHorizontal();
                        {
                            var keyBinding = UnityEditor.ShortcutManagement.ShortcutManager.instance.GetShortcutBinding(k_ToggleLagSpikeSimulatorBindingKey);

                            s_LagSpike.text = $"Lag Spike Simulator [{keyBinding.ToString()}]";
                            var isSimulatingLagSpike = connSystem != null && connSystem.IsSimulatingLagSpike;
                            GUI.color = isSimulatingLagSpike ? GhostAuthoringComponentEditor.brokenColor : ActiveColor;
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
            if (world == default || !world.IsCreated) return;

            var conSystem = world.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>();

            var isConnected = conSystem.ClientConnectionState == MultiplayerClientPlayModeConnectionSystem.ConnectionState.Connected;
            var isConnecting = conSystem.ClientConnectionState == MultiplayerClientPlayModeConnectionSystem.ConnectionState.Connecting;
            var connectionColor = isConnected ? ActiveColor  : (isConnecting ? Color.yellow : GhostAuthoringComponentEditor.brokenColor);
            GUILayout.BeginHorizontal();
            {
                GUI.color = connectionColor;
                GUILayout.Box(isConnected ? conSystem.NetworkId.Value.ToString() : "-", s_BoxStyleHack, s_NetworkIdWidth);

                GUILayout.Label(world.Name, s_WorldNameWidth);
                GUI.color = Color.white;
                if(conSystem.IsUsingIpc)
                    GUILayout.Label(s_Ipc);
                if (conSystem.IsUsingSocket)
                {
                    GUILayout.Label(conSystem.IsUsingWebSocket ? s_WebSocket : s_UdpSocket);
                }

                switch (conSystem.SocketFamily)
                {
                    case NetworkFamily.Invalid:
                        break;
                    case NetworkFamily.Ipv4:
                        GUILayout.Label("[IPv4]");
                        break;
                    case NetworkFamily.Ipv6:
                        GUILayout.Label("[IPv6]");
                        break;
                    case NetworkFamily.Custom:
                        GUILayout.Label("[Custom]");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (conSystem.IsSimulatingLagSpike)
                {
                    GUI.color = GhostAuthoringComponentEditor.brokenColor;
                    GUILayout.Label("[Lag Spike]");
                }

                GUI.color = connectionColor;
                s_NetworkEmulation.text = conSystem.IsAnyUsingSimulator ? "[Using Network Emulation]" : "[No Emulation]";
                GUILayout.Label(s_NetworkEmulation);

                GUI.color = connectionColor;
                if(conSystem.LastEndpoint != default)
                    GUILayout.Label($"[{conSystem.LastEndpoint}]");
                GUILayout.Label($"[{conSystem.ClientConnectionState.ToString()}]");
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUI.color = Color.white;
            switch (conSystem.ClientConnectionState)
            {
                case MultiplayerClientPlayModeConnectionSystem.ConnectionState.Connected:
                case MultiplayerClientPlayModeConnectionSystem.ConnectionState.Connecting:
                {

                    if (GUILayout.Button(s_ClientReconnect))
                        conSystem.ClientConnectionState = MultiplayerClientPlayModeConnectionSystem.ConnectionState.TriggerConnect;

                    if (GUILayout.Button(s_ClientDc))
                        conSystem.ClientConnectionState = MultiplayerClientPlayModeConnectionSystem.ConnectionState.TriggerDisconnect;

                    if (GUILayout.Button(s_ServerDc))
                        ServerDisconnectNetworkId(conSystem.NetworkId, NetworkStreamDisconnectReason.ConnectionClose);

                    break;
                }
                case MultiplayerClientPlayModeConnectionSystem.ConnectionState.NotConnected:
                {
                    if (GUILayout.Button("Connect"))
                        conSystem.ClientConnectionState = MultiplayerClientPlayModeConnectionSystem.ConnectionState.TriggerConnect;
                    break;
                }
                default:
                {
                    GUILayout.Box("-", s_BoxStyleHack, s_ExpandWidth);
                    break;
                }
            }

            // You can force a timeout even when disconnected, to allow testing reconnect attempts while timed out.
            var isTimingOut = conSystem.IsSimulatingTimeout;
            s_Timeout.text = isTimingOut ? $"Simulating Timeout\n[{Mathf.CeilToInt(conSystem.TimeoutSimulationDurationSeconds)}s]" : $"Timeout";
            GUI.color = isTimingOut ? GhostAuthoringComponentEditor.brokenColor :  Color.white;
            if (GUILayout.Button(s_Timeout))
                conSystem.ToggleTimeoutSimulation();

            GUI.color = connectionColor;
            if (m_DidRepaint)
                conSystem.UpdatePingText();
            GUILayout.Box(conSystem.PingText, s_BoxStyleHack, s_PingWidth);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Separator();
        }

        static void DrawServerWorld(World serverWorld)
        {
            GUILayout.BeginHorizontal();
            {
                GUI.color = Color.white;
                s_ServerName.text = serverWorld.Name;
                EditorGUILayout.LabelField(s_ServerName, s_WorldNameWidth);
                var conSystem = serverWorld.GetExistingSystemManaged<MultiplayerServerPlayModeConnectionSystem>();

                if (conSystem.IsListening)
                {
                    s_ServerPort.text = $"[{conSystem.LastEndpoint.Address}]";
                    GUILayout.Label(s_ServerPort, s_DontExpandWidth);

                    GUILayout.Label("[Listening]");
                }
                else GUILayout.Label("[Not Listening]");

                var numConnections = conSystem.NumActiveConnections;
                var numInGame = conSystem.NumActiveConnectionsInGame;
                GUI.color = numConnections > 0 ? ActiveColor : Color.white;
                s_ServerPlayers.text = $"[{numConnections} Connected | {numInGame} In Game]";
                GUILayout.Label(s_ServerPlayers, s_DontExpandWidth);

                GUI.color = Color.white;

                if (GUILayout.Button(s_ServerDcAllClients))
                {
                    DisconnectAllClients(serverWorld.EntityManager, NetworkStreamDisconnectReason.ConnectionClose);
                }

                if (GUILayout.Button(s_ServerReconnectAllClients))
                {
                    DisconnectAllClients(serverWorld.EntityManager, NetworkStreamDisconnectReason.ConnectionClose);

                    foreach (var clientWorld in ClientServerBootstrap.ClientWorlds.Concat(ClientServerBootstrap.ThinClientWorlds))
                    {
                        var connSystem = clientWorld.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>();
                        connSystem.ClientConnectionState = MultiplayerClientPlayModeConnectionSystem.ConnectionState.TriggerConnect;
                    }
                }

                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();
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
            GUI.color = Prefs.ApplyLoggerSettings ? ActiveColor : Color.white;
            Prefs.ApplyLoggerSettings = EditorGUILayout.Toggle(s_ForceLogLevel, Prefs.ApplyLoggerSettings);
            if (!Prefs.ApplyLoggerSettings)
                DrawLogFileLocationButton();
            GUILayout.EndHorizontal();

            GUI.enabled = Prefs.ApplyLoggerSettings;
            GUI.color = Color.white;
            if (Prefs.ApplyLoggerSettings)
            {
                Prefs.TargetLogLevel = (NetDebug.LogLevelType) EditorGUILayout.EnumPopup(s_LogLevel, Prefs.TargetLogLevel);

                GUILayout.BeginHorizontal();
                Prefs.TargetShouldDumpPackets = EditorGUILayout.Toggle(s_DumpPacketLogs, Prefs.TargetShouldDumpPackets);
                DrawLogFileLocationButton();
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
                    GUI.color = visitor.Enabled ? ActiveColor : Color.grey;
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

        static void DisconnectSpecificClient(EntityManager entityManager, NetworkId networkId, NetworkStreamDisconnectReason reason = NetworkStreamDisconnectReason.ConnectionClose)
        {
            entityManager.CompleteAllTrackedJobs();
            using var activeConnectionsQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.Exclude<NetworkStreamRequestDisconnect>());
            var entities = activeConnectionsQuery.ToEntityArray(Allocator.Temp);
            var networkIds = activeConnectionsQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                if (networkIds[i].Value == networkId.Value)
                {
                    entityManager.AddComponentData(entities[i], new NetworkStreamRequestDisconnect { Reason = reason });
                    break;
                }
            }
        }

        static void DisconnectAllClients(EntityManager entityManager, NetworkStreamDisconnectReason reason)
        {
            entityManager.CompleteAllTrackedJobs();
            using var activeConnectionsQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.Exclude<NetworkStreamRequestDisconnect>());
            var entities = activeConnectionsQuery.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                // TODO - Convert to batch when API supports 1 NetworkStreamRequestDisconnect for n entities.
                entityManager.AddComponentData(entities[i], new NetworkStreamRequestDisconnect { Reason = reason });
            }
        }

        void DrawSeparator()
        {
            GUI.color = Color.white;
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        }

        static void RefreshSimulationPipelineParametersLiveForAllWorlds()
        {
            foreach (var clientWorld in ClientServerBootstrap.ClientWorlds.Concat(ClientServerBootstrap.ThinClientWorlds))
            {
                clientWorld.GetExistingSystemManaged<MultiplayerClientPlayModeConnectionSystem>().UpdateSimulator = true;
            }
        }

        /// <summary>Note: Will disconnect this NetworkId from all server worlds it is found in.</summary>
        static void ServerDisconnectNetworkId(NetworkId networkId, NetworkStreamDisconnectReason reason)
        {
            foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
            {
                DisconnectSpecificClient(serverWorld.EntityManager, networkId, reason);

                GetNetDbgForWorld(serverWorld).DebugLog($"{serverWorld.Name} triggered '{nameof(ServerDisconnectNetworkId)}' on NetworkId '{networkId.Value}' via {nameof(MultiplayerPlayModeWindow)}!");
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    internal partial class MultiplayerClientPlayModeConnectionSystem : SystemBase
    {
        internal enum ConnectionState
        {
            NotConnected,
            Connecting,
            Connected,
            TriggerDisconnect,
            TriggerConnect,
        }

        internal string PingText;
        internal ConnectionState ClientConnectionState;
        internal NetworkSnapshotAck ClientNetworkSnapshotAck;
        internal NetworkId NetworkId;
        internal NetworkEndpoint OverrideEndpoint;
        EndSimulationEntityCommandBufferSystem m_EndSimulationEcbSystem;
        ConnectionState m_LastConnectionState;

        public bool UpdateSimulator;

        public bool IsAnyUsingSimulator {get; private set;}

        public NetworkEndpoint LastEndpoint {get; private set;}

        internal bool IsUsingIpc { get; private set; }
        internal bool IsUsingWebSocket { get; private set; }
        internal bool IsUsingSocket { get; private set; }
        internal NetworkFamily SocketFamily { get; private set; }

        internal int LagSpikeMillisecondsLeft { get; private set; } = -1;
        internal float TimeoutSimulationDurationSeconds { get; private set; } = -1;

        internal bool IsSimulatingTimeout => TimeoutSimulationDurationSeconds >= 0;
        internal bool IsSimulatingLagSpike => LagSpikeMillisecondsLeft >= 0;

        protected override void OnCreate()
        {
            m_EndSimulationEcbSystem = World.GetExistingSystemManaged<EndSimulationEntityCommandBufferSystem>();
            UpdatePingText();
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
                if (!IsSimulatingLagSpike || ClientConnectionState == ConnectionState.NotConnected)
                {
                    LagSpikeMillisecondsLeft = -1;
                    UpdateSimulator = true;
                    netDebug.DebugLog("Lag Spike Simulator: Finished dropping packets!");
                    MultiplayerPlayModeWindow.ForceRepaint();
                }
            }

            var hasNetworkStreamDriver = SystemAPI.TryGetSingletonRW<NetworkStreamDriver>(out var netStream);
            if (hasNetworkStreamDriver)
            {
                ref var driverStore = ref netStream.ValueRO.DriverStore;
                LastEndpoint = netStream.ValueRO.LastEndPoint;
                IsAnyUsingSimulator = driverStore.IsAnyUsingSimulator;
                for (int i = driverStore.FirstDriver; i < driverStore.LastDriver; i++)
                {
                    switch (driverStore.GetDriverType(i))
                    {
                        case TransportType.IPC:
                            IsUsingIpc = true;
                            break;
                        case TransportType.Socket:
                            IsUsingSocket = true;
                            var driverInstance = driverStore.GetDriverInstance(i);
                            SocketFamily = driverInstance.driver.GetLocalEndpoint().Family;

                            // todo: Fetch the NetworkInterface from the driver directly, by Type name, to future proof this.
#if UNITY_WEBGL
                        IsUsingWebSocket = true;
#else
                            IsUsingWebSocket = false;
#endif
                            break;
                        default:
                            netDebug.LogError($"{World.Name} has unknown or invalid driver type passed into DriverStore!");
                            break;
                    }
                }
            }

            var isConnected = false;
            var isConnecting = false;
            var isDisconnecting = false;
            if (SystemAPI.TryGetSingletonEntity<NetworkStreamConnection>(out var singletonEntity))
            {
                if (EntityManager.HasComponent<NetworkId>(singletonEntity))
                {
                    NetworkId = EntityManager.GetComponentData<NetworkId>(singletonEntity);
                    ClientNetworkSnapshotAck = EntityManager.GetComponentData<NetworkSnapshotAck>(singletonEntity);
                    isConnected = true;
                    isDisconnecting = EntityManager.HasComponent<NetworkStreamRequestDisconnect>(singletonEntity);
                }
                else isConnecting = true;
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

            var refreshConnectionStatus = true;
            var ecb = m_EndSimulationEcbSystem.CreateCommandBuffer();

            switch (ClientConnectionState)
            {
                case ConnectionState.TriggerDisconnect when (isConnected || isConnecting) && !isDisconnecting:
                    ecb.AddComponent(singletonEntity, new NetworkStreamRequestDisconnect
                    {
                        Reason = NetworkStreamDisconnectReason.ConnectionClose,
                    });
                    netDebug.DebugLog($"{World.Name} triggered a connection close via {nameof(MultiplayerPlayModeWindow)}!");
                    break;
                case ConnectionState.TriggerConnect when (isConnected || isConnecting) && !isDisconnecting:
                    ecb.AddComponent(singletonEntity, new NetworkStreamRequestDisconnect
                    {
                        Reason = NetworkStreamDisconnectReason.ConnectionClose,
                    });
                    refreshConnectionStatus = false;
                    break;
                case ConnectionState.TriggerConnect when isDisconnecting:
                    refreshConnectionStatus = false;
                    break;
                case ConnectionState.TriggerConnect when hasNetworkStreamDriver:
                    var clientSimulatorParameters = Prefs.ClientSimulatorParameters;
                    ref var driverStore = ref netStream.ValueRW.DriverStore;
                    NetworkSimulatorSettings.RefreshSimulationPipelineParametersLive(in clientSimulatorParameters, ref driverStore);

                    var ep = OverrideEndpoint != default ? OverrideEndpoint : netStream.ValueRO.LastEndPoint;
                    if (ep != default || Prefs.IsEditorInputtedAddressValidForConnect(out ep))
                    {
                        OverrideEndpoint = default;
                        LagSpikeMillisecondsLeft = -1;
                        UpdateSimulator = true;
                        netStream.ValueRW.Connect(EntityManager, ep);
                        netDebug.DebugLog($"{World.Name} triggered a reconnection to {ep.Address} via {nameof(MultiplayerPlayModeWindow)}!");
                    }
                    else
                        netDebug.LogError($"{World.Name} triggered a reconnection, but cannot find a suitable endpoint!");
                    break;
            }
            m_EndSimulationEcbSystem.AddJobHandleForProducer(Dependency);

            if (refreshConnectionStatus)
            {
                if (isConnected)
                    ClientConnectionState = ConnectionState.Connected;
                else if (isConnecting)
                    ClientConnectionState = ConnectionState.Connecting;
                else
                    ClientConnectionState = ConnectionState.NotConnected;
            }

            if (ClientConnectionState != m_LastConnectionState)
            {
                MultiplayerPlayModeWindow.ForceRepaint();
                m_LastConnectionState = ClientConnectionState;
            }
        }

        internal void UpdatePingText()
        {
            if (ClientConnectionState == ConnectionState.Connected)
            {
                var estimatedRTT = (int) ClientNetworkSnapshotAck.EstimatedRTT;
                var deviationRTT = (int) ClientNetworkSnapshotAck.DeviationRTT;
                PingText = estimatedRTT < 1000 ? $"{estimatedRTT}±{deviationRTT}ms" : $"~{estimatedRTT + deviationRTT / 2:0}ms";
            }
            else
                PingText = "-";
        }

        public void ToggleLagSpikeSimulator()
        {
            if (!IsAnyUsingSimulator)
            {
                SystemAPI.GetSingletonRW<NetDebug>().ValueRW.LogError($"Cannot enable LagSpike simulator as Simulator disabled!");
                return;
            }

            if(IsSimulatingTimeout)
                ToggleTimeoutSimulation();

            LagSpikeMillisecondsLeft = IsSimulatingLagSpike ? -1 : MultiplayerPlayModeWindow.k_LagSpikeDurationsSeconds[Prefs.LagSpikeSelectionIndex];
            UpdateSimulator = true;
            SystemAPI.GetSingletonRW<NetDebug>().ValueRW.DebugLog($"Lag Spike Simulator: Toggled! Dropping packets for {Mathf.CeilToInt(LagSpikeMillisecondsLeft)}ms!");
            MultiplayerPlayModeWindow.ForceRepaint();
        }

        public void ToggleTimeoutSimulation()
        {
            if (!IsAnyUsingSimulator)
            {
                SystemAPI.GetSingletonRW<NetDebug>().ValueRW.LogError($"Cannot enable Timeout Simulation as Simulator disabled!");
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

            MultiplayerPlayModeWindow.ForceRepaint();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    internal partial class MultiplayerServerPlayModeConnectionSystem : SystemBase
    {
        public bool IsListening{get; private set;}
        public NetworkEndpoint LastEndpoint{get; private set;}

        public int NumActiveConnections => m_activeConnectionsQuery.CalculateEntityCount();

        public int NumActiveConnectionsInGame => NumActiveConnections - m_notInGameQuery.CalculateEntityCount();

        private EntityQuery m_activeConnectionsQuery;
        private EntityQuery m_notInGameQuery;
        protected override void OnCreate()
        {
            m_activeConnectionsQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.Exclude<NetworkStreamRequestDisconnect>());
            m_notInGameQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.Exclude<NetworkStreamRequestDisconnect>(), ComponentType.Exclude<NetworkStreamInGame>());
        }
        protected override void OnUpdate()
        {
            ref readonly var netStream = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRO;
            IsListening = netStream.DriverStore.GetDriverInstance(netStream.DriverStore.FirstDriver).driver.Listening;
            LastEndpoint = netStream.LastEndPoint;
        }
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

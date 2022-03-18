using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using UnityEditor;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    public class MultiplayerPlayModeWindow : EditorWindow
    {
        const string k_PrefsKeyPrefix = "MultiplayerPlayMode";

        [MenuItem("Multiplayer/PlayMode Tools")]
        public static void ShowWindow()
        {
            GetWindow<MultiplayerPlayModeWindow>(false, "Multiplayer PlayMode Tools", true);
        }

        void OnEnable()
        {
            EditorApplication.playModeStateChanged += PlayModeStateChanged;
        }

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= PlayModeStateChanged;
        }

        void PlayModeStateChanged(PlayModeStateChange playModeStateChange)
        {
            Repaint();
        }

        private void OnGUI()
        {
            var playModeType = EditorPopup("PlayMode Type", new[] {"Client & Server", "Client", "Server"}, "Type");
            if (playModeType != 2)
            {
                EditorInt("Num Thin Clients", "NumThinClients", 0, ClientServerBootstrap.k_MaxNumThinClients);
                EditorInt("Client send/recv delay (ms)", "ClientDelay", 0, 2000);
                EditorInt("Client send/recv jitter (ms)", "ClientJitter", 0, 200);
                EditorInt("Client packet drop (percentage)", "ClientDropRate", 0, 100);
            }

            if (playModeType == 1)
            {
                EditorString("Client auto connect address", "AutoConnectAddress");
            }

            if (EditorApplication.isPlaying)
            {
                DrawLoggingGroup();
                DrawSeparator();
                EditorGUILayout.LabelField("Controls", EditorStyles.boldLabel);

                foreach (var world in World.All)
                {
                    var simulationGroup = world.GetExistingSystem<ClientSimulationSystemGroup>();
                    if (simulationGroup == null)
                        continue;
                    EditorGUILayout.LabelField(world.Name);
                    if (world.EntityManager.CreateEntityQuery(typeof(ThinClientComponent))
                            .CalculateEntityCount() != 1)
                    {
                        if (EditorGUILayout.Toggle("Present",
                            MultiplayerPlayModeControllerSystem.PresentedClient == simulationGroup))
                            MultiplayerPlayModeControllerSystem.PresentedClient = simulationGroup;
                    }

                    var conSystem = world
                        .GetExistingSystem<MultiplayerPlayModeConnectionSystem>();
                    if (conSystem != null)
                    {
                        if (conSystem.ClientConnectionState ==
                            MultiplayerPlayModeConnectionSystem.ConnectionState.Connected)
                        {
                            EditorGUILayout.BeginHorizontal();
                            if (GUILayout.Button("Disconnect"))
                                conSystem.ClientConnectionState =
                                    MultiplayerPlayModeConnectionSystem.ConnectionState.TriggerDisconnect;
                            //if (GUILayout.Button("Timeout"))
                            //    conSystem.ClientConnectionState =
                            //        MultiplayerPlayModeConnectionSystem.ConnectionState.TriggerTimeout;
                            EditorGUILayout.EndHorizontal();
                        }
                        else if (conSystem.ClientConnectionState ==
                                 MultiplayerPlayModeConnectionSystem.ConnectionState.NotConnected)
                        {
                            if (GUILayout.Button("Connect"))
                                conSystem.ClientConnectionState =
                                    MultiplayerPlayModeConnectionSystem.ConnectionState.TriggerConnect;
                        }
                    }
                }
            }
        }

        static string GetKey(string subKey)
        {
            return k_PrefsKeyPrefix + "_" + Application.productName + "_" + subKey;
        }

        int EditorPopup(string label, string[] list, string key = null)
        {
            string prefsKey = (string.IsNullOrEmpty(key) ? GetKey(label) : GetKey(key));
            int index = EditorPrefs.GetInt(prefsKey);
            index = EditorGUILayout.Popup(label, index, list);
            EditorPrefs.SetInt(prefsKey, index);
            return index;
        }

        int EditorInt(string label, string key = null, int minValue = Int32.MinValue, int maxValue = Int32.MaxValue)
        {
            string prefsKey = (string.IsNullOrEmpty(key) ? GetKey(label) : GetKey(key));
            int value;
            value = EditorPrefs.GetInt(prefsKey);

            if (value < minValue)
                value = minValue;
            if (value > maxValue)
                value = maxValue;
            value = EditorGUILayout.IntField(label, value);
            if (value < minValue)
                value = minValue;
            if (value > maxValue)
                value = maxValue;
            EditorPrefs.SetInt(prefsKey, value);

            return value;
        }

        string EditorString(string label, string key = null)
        {
            string prefsKey = (string.IsNullOrEmpty(key) ? GetKey(label) : GetKey(key));
            string value;
            value = EditorPrefs.GetString(prefsKey);

            value = EditorGUILayout.TextField(label, value);
            EditorPrefs.SetString(prefsKey, value);

            return value;
        }

        void DrawLoggingGroup()
        {
            // Same log level or dump packet toggle is applied to all worlds, so just find the first
            // case when reading and apply to all worlds/connections when writing
            var logLevel = NetDebug.LogLevelType.Notify;
            foreach (var world in World.All)
            {
                var debugSystem = world.GetExistingSystem<NetDebugSystem>();
                if (debugSystem == null || debugSystem.LogLevel == 0) continue;
                logLevel = debugSystem.LogLevel;
                break;
            }

            DrawSeparator();

            EditorGUILayout.BeginHorizontal();
            var newLogLevel = (NetDebug.LogLevelType)EditorGUILayout.EnumPopup("Log Level", logLevel);
            EditorGUILayout.EndHorizontal();
            if (newLogLevel != logLevel)
            {
                foreach (var world in World.All)
                {
                    var debugConfigQuery = world.EntityManager.CreateEntityQuery(typeof(NetCodeDebugConfig));
                    if (debugConfigQuery.CalculateEntityCount() > 0)
                    {
                        var debugConfigs = debugConfigQuery.ToComponentDataArray<NetCodeDebugConfig>(Allocator.Temp);
                        var debugEntity = debugConfigQuery.ToEntityArray(Allocator.Temp);
                        var config = debugConfigs[0];
                        config.LogLevel = newLogLevel;
                        world.EntityManager.SetComponentData(debugEntity[0], config);
                    }
                    else
                    {
                        var debugSystem = world.GetExistingSystem<NetDebugSystem>();
                        if (debugSystem != null)
                            debugSystem.LogLevel = newLogLevel;
                    }
                }
            }

            bool dumpPackets = false;
            foreach (var world in World.All)
            {
                var logSys = world.GetExistingSystem<MultiplayerPlaymodeLoggingSystem>();
                if (logSys != null)
                    dumpPackets |= logSys.IsDumpingPackets;
            }

            EditorGUILayout.BeginHorizontal();
            var newDumpPackets = EditorGUILayout.Toggle("Dump packet logs",
                dumpPackets);
            EditorGUILayout.EndHorizontal();
            if (newDumpPackets != dumpPackets)
            {
                foreach (var world in World.All)
                {
                    var logSys = world.GetExistingSystem<MultiplayerPlaymodeLoggingSystem>();
                    if (logSys != null)
                    {
                        logSys.ShouldDumpPackets = newDumpPackets;
                    }
                }
            }
        }

        void DrawSeparator()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        }
    }

    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    [AlwaysUpdateSystem]
    public partial class MultiplayerPlayModeConnectionSystem : SystemBase
    {
        public enum ConnectionState
        {
            Uninitialized,
            NotConnected,
            Connected,
            TriggerDisconnect,
            TriggerTimeout,
            TriggerConnect
        }

        public ConnectionState ClientConnectionState;
        private EntityQuery m_clientConnectionGroup;
        private NetworkEndPoint m_prevEndPoint;

        protected override void OnCreate()
        {
            m_clientConnectionGroup = GetEntityQuery(
                ComponentType.ReadWrite<NetworkStreamConnection>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());
            ClientConnectionState = ConnectionState.Uninitialized;
        }

        protected override void OnUpdate()
        {
            bool isConnected = !m_clientConnectionGroup.IsEmptyIgnoreFilter;
            // Trigger connect / disconnect events
            if (ClientConnectionState == ConnectionState.TriggerDisconnect && isConnected)
            {
                var con = m_clientConnectionGroup.ToComponentDataArray<NetworkStreamConnection>(Allocator.TempJob);
                var recvSys = World.GetExistingSystem<NetworkStreamReceiveSystem>();
                recvSys.LastDriverWriter.Complete();
                m_prevEndPoint = recvSys.Driver.RemoteEndPoint(con[0].Value);
                for (int i = 0; i < con.Length; ++i)
                {
                    recvSys.Driver.Disconnect(con[i].Value);
                }

                con.Dispose();
                EntityManager.AddComponent(m_clientConnectionGroup,
                    ComponentType.ReadWrite<NetworkStreamDisconnected>());
            }
            /*else if (ClientConnectionState == ConnectionState.TriggerTimeout && isConnected)
            {
                EntityManager.AddComponent(m_clientConnectionGroup, ComponentType.ReadWrite<NetworkStreamDisconnected>());
            }*/
            else if (ClientConnectionState == ConnectionState.TriggerConnect && !isConnected && m_prevEndPoint.IsValid)
            {
                World.GetExistingSystem<NetworkStreamReceiveSystem>().Connect(m_prevEndPoint);
            }

            // Update connection status
            ClientConnectionState = isConnected
                ? ConnectionState.Connected
                : (m_prevEndPoint.IsValid ? ConnectionState.NotConnected : ConnectionState.Uninitialized);
        }
    }

    [UpdateInWorld(TargetWorld.Default)]
    public partial class MultiplayerPlayModeControllerSystem : SystemBase
    {
        public static ClientSimulationSystemGroup PresentedClient;
        private ClientSimulationSystemGroup m_currentPresentedClient;

        protected override void OnCreate()
        {
            PresentedClient = null;
            m_currentPresentedClient = null;
        }

        protected override void OnUpdate()
        {
            if (PresentedClient == null)
            {
                foreach (var world in World.All)
                {
                    var simulationGroup = world.GetExistingSystem<ClientSimulationSystemGroup>();
                    if (simulationGroup != null && PresentedClient == null)
                        m_currentPresentedClient = PresentedClient = simulationGroup;
                    else if (simulationGroup != null)
                        world.GetExistingSystem<ClientPresentationSystemGroup>().Enabled = false;
                }
            }
            if (PresentedClient != m_currentPresentedClient)
            {
                // Change active client for presentation
                foreach (var world in World.All)
                {
                    var simulationGroup = world.GetExistingSystem<ClientSimulationSystemGroup>();
                    if (simulationGroup != null && simulationGroup == m_currentPresentedClient)
                        world.GetExistingSystem<ClientPresentationSystemGroup>().Enabled = false;
                    if (simulationGroup != null && simulationGroup == PresentedClient)
                        world.GetExistingSystem<ClientPresentationSystemGroup>().Enabled = true;
                }

                m_currentPresentedClient = PresentedClient;
            }
        }
    }

    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    public partial class MultiplayerPlaymodeLoggingSystem : SystemBase
    {
        public bool ShouldDumpPackets { get; set; }
        public bool IsDumpingPackets { get; private set; }

        private BeginSimulationEntityCommandBufferSystem m_CmdBuffer;
        EntityQuery m_logQuery;

        protected override void OnCreate()
        {
            ShouldDumpPackets = false;
            IsDumpingPackets = false;
            m_CmdBuffer = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_logQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<EnablePacketLogging>());
        }

        protected override void OnUpdate()
        {
            if (HasSingleton<ThinClientComponent>())
                return;

            if (TryGetSingleton<NetCodeDebugConfig>(out var cfg))
            {
                if (ShouldDumpPackets != IsDumpingPackets)
                {
                    cfg.DumpPackets = ShouldDumpPackets;
                    SetSingleton(cfg);
                }
                else
                    ShouldDumpPackets = cfg.DumpPackets;
                IsDumpingPackets = cfg.DumpPackets;
            }
            else
            {
                if (ShouldDumpPackets != IsDumpingPackets)
                {
                    if (ShouldDumpPackets)
                    {
                        var cmdBuffer = m_CmdBuffer.CreateCommandBuffer();
                        Entities.WithNone<EnablePacketLogging>()
                            .ForEach((Entity entity, in NetworkStreamConnection conn) =>
                            {
                                cmdBuffer.AddComponent<EnablePacketLogging>(entity);
                            }).Schedule();
                    }
                    else
                    {
                        var cmdBuffer = m_CmdBuffer.CreateCommandBuffer();
                        Entities.WithAll<EnablePacketLogging>().ForEach((Entity entity, in NetworkStreamConnection conn) =>
                        {
                            cmdBuffer.RemoveComponent<EnablePacketLogging>(entity);
                        }).Schedule();
                    }
                    m_CmdBuffer.AddJobHandleForProducer(Dependency);
                    IsDumpingPackets = ShouldDumpPackets;
                }
                else
                {
                    IsDumpingPackets = m_logQuery.CalculateEntityCount()>0;
                    ShouldDumpPackets = IsDumpingPackets;
                }
            }
        }
    }
}

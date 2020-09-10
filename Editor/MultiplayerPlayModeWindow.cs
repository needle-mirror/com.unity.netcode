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
    }

    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    [AlwaysUpdateSystem]
    public class MultiplayerPlayModeConnectionSystem : SystemBase
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
                m_prevEndPoint = World.GetExistingSystem<NetworkStreamReceiveSystem>().Driver
                    .RemoteEndPoint(con[0].Value);
                for (int i = 0; i < con.Length; ++i)
                {
                    World.GetExistingSystem<NetworkStreamReceiveSystem>().Driver.Disconnect(con[i].Value);
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

#if !UNITY_SERVER
    [UpdateBefore(typeof(TickClientSimulationSystem))]
#endif
#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
    [UpdateBefore(typeof(TickServerSimulationSystem))]
#endif
    [UpdateInWorld(UpdateInWorld.TargetWorld.Default)]
    public class MultiplayerPlayModeControllerSystem : SystemBase
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
}

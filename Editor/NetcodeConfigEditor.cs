using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.NetCode.Hybrid;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode.Editor
{
    /// <summary>Editor script managing the creation and registration of <see cref="NetCodeConfig"/> Global ScriptableObject.</summary>
    [CustomEditor(typeof(NetCodeConfig), true, isFallback = false)]
    internal class NetcodeConfigEditor : UnityEditor.Editor
    {
        private const string k_LiveEditingWarning = " Therefore, be aware that the Global config is applied project-wide automatically:\n - In the Editor; this config is set every frame, enabling live editing. Note that this invalidates (by replacing) any C# code of yours that modifies these NetCode configuration singleton components manually.\n - In a build; this config is applied once (during Server & Client World system creation).";
        private static readonly GUILayoutOption s_ButtonWidth = GUILayout.Width(90);

        private static NetCodeConfig SavedConfig
        {
            get => NetCodeClientAndServerSettings.instance.GlobalNetCodeConfig;
            set
            {
                if (SavedConfig == value) return;
                NetCodeClientAndServerSettings.instance.GlobalNetCodeConfig = value;
                EditorUtility.SetDirty(NetCodeClientAndServerSettings.instance);
                LoadAllNetCodeConfigsAndSetGlobalFlags();
                NetCodeClientAndServerSettings.instance.Save();
            }
        }

        internal static void CreateNetcodeSettingsAsset()
        {
            var assetPath = AssetDatabase.GenerateUniqueAssetPath("Assets/NetcodeConfig.asset");
            var netCodeConfig = CreateInstance<NetCodeConfig>();
            netCodeConfig.IsGlobalConfig = true; // Prevent warning when first creating it.
            AssetDatabase.CreateAsset(netCodeConfig, assetPath);
            Selection.activeObject = SavedConfig = AssetDatabase.LoadAssetAtPath<NetCodeConfig>(assetPath);
        }

        /// <summary>
        /// Fixes an issue where any config added to the preloaded assets is not automatically initialized.
        /// We (netcode) used the preloaded assets as our previous storage location for the global config.
        /// Thus, users reported issues where the NetCodeConfig.Global would not reliably set when entering playmode.
        /// https://forum.unity.com/threads/occasionally-netcodeconfig-fails-to-load.1535359/
        /// </summary>
        [InitializeOnLoadMethod]
        private static void InitializeNetCodeConfigEditorBugFix()
        {
            if (SavedConfig)
            {
                // Here we force the loading of the Global NetcodeConfig, thus fixing the Resources.Load boot issue in the editor.
                ValidateConfig(SavedConfig);
            }
            else
            {
                // Remove after Netcode 1.x.
                // For a couple of minor netcode package versions, we saved this config into the preloaded assets.
                // Now that we use our custom ProjectSettings, we don't need this anymore, but we do need to support auto-upgrading.
               // UNFORTUNATE SIDE EFFECT: If you don't have a global config, this loads ALL Preloaded assets in the Editor, on first boot!
               var found = PlayerSettings.GetPreloadedAssets().OfType<NetCodeConfig>().FirstOrDefault(x => x.IsGlobalConfig);
               if (found)
               {
                   SavedConfig = found;
                   Debug.LogWarning($"The Global NetCodeConfig ('{found.name}') is now saved into the {nameof(NetCodeClientAndServerSettings)} ProjectAsset! Please ensure you save that file to source control (if applicable). It is now safe to remove this asset from the Preloaded Assets list, if you'd like to. It'll get added automatically during builds. This corrective logic will be removed after Netcode 1.x.");
               }
            }
        }

        /// <summary>Internal method to register the provider (with IMGUI for drawing).</summary>
        /// <returns></returns>
        [SettingsProvider]
        public static SettingsProvider CreateNetcodeConfigSettingsProvider()
        {
            // First parameter is the path in the Settings window.
            // Second parameter is the scope of this setting: it only appears in the Project Settings window.
            var provider = new SettingsProvider("Project/Multiplayer", SettingsScope.Project)
            {
                // By default the last token of the path is used as display name if no label is provided.
                label = "Multiplayer",
                // Create the SettingsProvider and initialize its drawing (IMGUI) function in place:
                guiHandler = (searchContext) =>
                {
                    Links();

                    GUILayout.BeginHorizontal();
                    var inst = NetCodeClientAndServerSettings.instance;
                    {
                        EditorGUI.BeginChangeCheck();
                        GUI.enabled = !Application.isPlaying;
                        inst.GlobalNetCodeConfig = EditorGUILayout.ObjectField(new GUIContent(string.Empty, "Select the asset that NetCode will use, by default."), inst.GlobalNetCodeConfig, typeof(NetCodeConfig), allowSceneObjects: false) as NetCodeConfig;

                        if (GUILayout.Button("Find & Set", s_ButtonWidth))
                        {
                            if (SavedConfig == null)
                            {
                                var configs = AssetDatabase.FindAssets($"t:{nameof(NetCodeConfig)}")
                                    .Select(AssetDatabase.GUIDToAssetPath)
                                    .Select(AssetDatabase.LoadAssetAtPath<NetCodeConfig>)
                                    .ToArray();
                                Array.Sort(configs);
                                SavedConfig = configs.FirstOrDefault();
                                EditorGUIUtility.PingObject(SavedConfig);
                            }
                        }

                        if (GUILayout.Button("Create & Set", s_ButtonWidth))
                        {
                            CreateNetcodeSettingsAsset();
                        }

                        if (EditorGUI.EndChangeCheck())
                        {
                            LoadAllNetCodeConfigsAndSetGlobalFlags();
                        }
                    }
                    GUILayout.EndHorizontal();

                    if (!SavedConfig)
                    {
                        EditorGUILayout.HelpBox("No Global NetCodeConfig is set. This is valid, but note that the NetCode package will therefore be configured with default settings, unless otherwise specified (e.g. by modifying the Netcode singleton component values directly in C#).", MessageType.Info);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("You have now set a Global NetCodeConfig asset." + k_LiveEditingWarning, MessageType.Warning);
                    }

                    EditorGUILayout.Separator();

                    // CurrentImportanceSuggestions:
                    var prevFlags = inst.hideFlags;
                    inst.hideFlags = HideFlags.None; // Allow editing of it.
                    var clientAndServerSettingsSO = new SerializedObject(inst, inst);
                    clientAndServerSettingsSO.Update();
                    var CurrentImportanceSuggestionsProperty = clientAndServerSettingsSO.FindProperty(nameof(inst.CurrentImportanceSuggestions));
                    EditorGUILayout.PropertyField(CurrentImportanceSuggestionsProperty);
                    if (clientAndServerSettingsSO.ApplyModifiedProperties())
                    {
                        inst.Save();
                    }
                    inst.hideFlags = prevFlags;
                },

                // Populate the search keywords to enable smart search filtering and label highlighting:
                keywords = new HashSet<string>(new[] {"NetCode", "NetCodeConfig", "TickRate", "SimulationTickRate", "NetworkTickRate", "NetworkSendRate"}),
            };
            return provider;
        }

        /// <summary>
        /// Slow, so we only do it when we actually set a new config.
        /// </summary>
        private static void LoadAllNetCodeConfigsAndSetGlobalFlags()
        {
            foreach (var config in AssetDatabase.FindAssets($"t:{nameof(NetCodeConfig)}")
                         .Select(AssetDatabase.GUIDToAssetPath)
                         .Select(AssetDatabase.LoadAssetAtPath<NetCodeConfig>))
            {
                if(config)
                    ValidateConfig(config);
            }
        }

        private static void ValidateConfig(NetCodeConfig config)
        {
            var isActuallyGlobalConfig = (config == SavedConfig);
            if (isActuallyGlobalConfig != config.IsGlobalConfig)
            {
                Debug.LogWarning($"Detected individual NetCodeConfig asset ('{AssetDatabase.GetAssetPath(config) ?? config.name}') with incorrect `IsGlobalConfig` flag! Was '{config.IsGlobalConfig}', updated to '{isActuallyGlobalConfig}'. Check for modifications to the {nameof(NetCodeClientAndServerSettings)}.asset, and commit all changed netcode files. These warnings are expected when modifying the Global NetCodeConfig, and are harmless.", config);
                config.IsGlobalConfig = isActuallyGlobalConfig;
                EditorUtility.SetDirty(config);
            }
        }

        private static readonly GUIContent s_ClientServerTickRate = new GUIContent("ClientServerTickRate", "General multiplayer settings.\n\nServer Authoritative - Thus, when a client connects, the server will send an RPC clobbering any existing client values.");
        private static readonly GUIContent s_ClientTickRate = new GUIContent("ClientTickRate", "General multiplayer settings for the client.\n\nCan be configured on a per-client basis (via use of multiple configs, or direct C# component manipulation).");
        private static readonly GUIContent s_GhostSendSystemData = new GUIContent("GhostSendSystemData", "Specific optimization (and debug) settings for the GhostSendSystem to reduce bandwidth and CPU consumption.");
        private static readonly GUIContent s_TransportSettings = new GUIContent("NetworkConfigParameter (Unity Transport)", "Configures various UTP <b>NetworkConfigParameter</b> configuration values, but only when user-code uses one of the built-in <b>INetworkStreamDriverConstructor</b>'s.\n\nTo read this config in your own driver constructors, call <b>DefaultDriverBuilder.AddNetcodePackageDefaultNetworkConfigParameters</b>.");
        private static bool s_TransportSettingsFoldedOut = true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var config = (NetCodeConfig)target;

            ValidateConfig(config);

            if (config.IsGlobalConfig)
                EditorGUILayout.HelpBox("You have selected this as your Global config." + k_LiveEditingWarning, MessageType.Info);
            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Live tweaking is not supported for disabled values.", MessageType.Warning);

            //.
            GUI.enabled = !Application.isPlaying;
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.EnableClientServerBootstrap)));
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.HostWorldModeSelection)));
#endif
            //.
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ClientServerTickRate)), s_ClientServerTickRate);
            GUI.enabled = true;
            ValidateClientServerTickRate(config.ClientServerTickRate);
            GUILayout.Space(15);

            //.
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ClientTickRate)), s_ClientTickRate);
            GUILayout.Space(15);

            //.
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.GhostSendSystemData)), s_GhostSendSystemData);
            ValidateGhostSendSystemData(config.GhostSendSystemData);
            GUILayout.Space(15);

            //.
            GUI.enabled = !Application.isPlaying;
            s_TransportSettingsFoldedOut = EditorGUILayout.Foldout(s_TransportSettingsFoldedOut, s_TransportSettings, toggleOnLabelClick: true);
            if (s_TransportSettingsFoldedOut)
            {
                EditorGUI.indentLevel += 2;
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ConnectTimeoutMS)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.MaxConnectAttempts)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.DisconnectTimeoutMS)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.HeartbeatTimeoutMS)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ReconnectionTimeoutMS)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ClientSendQueueCapacity)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ClientReceiveQueueCapacity)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ServerSendQueueCapacity)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ServerReceiveQueueCapacity)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.MaxMessageSize)));
                GUI.enabled = true;
                EditorGUI.indentLevel -= 2;
            }

            GUILayout.Space(15);

            //.
            Links();
            serializedObject.ApplyModifiedProperties();
        }

        private static void Links()
        {
            GUILayout.BeginHorizontal();
            {
                if (EditorGUILayout.LinkButton("Manual"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest");
                if (EditorGUILayout.LinkButton("RPCs"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/rpcs.html");
                if (EditorGUILayout.LinkButton("Input"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/command-stream.html");
                if (EditorGUILayout.LinkButton("Snapshot Synchronization"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/ghost-snapshots.html");
                if (EditorGUILayout.LinkButton("Client Prediction"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/prediction.html");
                if (EditorGUILayout.LinkButton("Optimizations"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/optimizations.html");
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(15);
        }

        /// <summary>Validation.</summary>
        /// <param name="config">A copy, so that we don't clobber the config ScriptableObject.</param>
        private static void ValidateClientServerTickRate(ClientServerTickRate config)
        {
            var previous = config;
            config.ResolveDefaults(); // Call this here (before validate) because this is what the netcode package does at runtime.

            var s = "Each client will be sent a snapshot on ";
            var networkSendRateInterval = config.CalculateNetworkSendRateInterval();
            var actualEstimatedRate = ((float)config.SimulationTickRate / networkSendRateInterval);
            switch (networkSendRateInterval)
            {
                case 1:
                    s += $"every server tick (i.e. ~{actualEstimatedRate:0} times per second).";
                    break;
                case 2:
                    s += $"every other server tick (i.e. ~{actualEstimatedRate:0.0} times per second), which is approximately a 50% CPU and bandwidth reduction compared to sending every frame.";
                    break;
                case 3:
                    s += $"every third server tick (i.e. ~{actualEstimatedRate:0.0} times per second), which is approximately a 66% CPU and bandwidth reduction compared to sending every frame.";
                    break;
                default:
                    s += $"every {networkSendRateInterval}th server tick (i.e. ~{actualEstimatedRate:0.0} times per second), which is approximately a {100-((int)(100f/networkSendRateInterval))}% CPU and bandwidth reduction compared to sending every frame.";
                    break;
            }
            EditorGUILayout.HelpBox(s, MessageType.Info);
            if (networkSendRateInterval > 1)
            {
                EditorGUILayout.HelpBox($"The server can (and will) now distribute these packet sends across the send interval (i.e. a round-robin approach), distributing the GhostSendSystem CPU cost more evenly across frames, reducing CPU spikes. E.g. If you have 50 clients connected, we'll send ~{Math.Max(1, 50/networkSendRateInterval)} of them a snapshot every tick.", MessageType.Info);
            }

            // Manual exceptions: We want to validate these RAW fields.
            {
                if(previous.SimulationTickRate != 0) config.SimulationTickRate = previous.SimulationTickRate;
                if(previous.NetworkTickRate != 0) config.NetworkTickRate = previous.NetworkTickRate;
            }
            // Validate:
            {
                FixedList4096Bytes<FixedString64Bytes> errors = default;
                config.ValidateAll(ref errors);
                foreach (var error in errors)
                {
                    EditorGUILayout.HelpBox($"{error}!", MessageType.Error);
                }
            }
        }

        /// <summary>Validation.</summary>
        /// <param name="config">A copy, so that we don't clobber the config ScriptableObject.</param>
        private void ValidateGhostSendSystemData(GhostSendSystemData config)
        {
            if (config.EnablePerComponentProfiling) EditorGUILayout.HelpBox("You've enabled EnablePerComponentProfiling, which will adversely impact performance.", MessageType.Warning);
            if (config.ForcePreSerialize) EditorGUILayout.HelpBox("You've enabled ForcePreSerialize (a debug setting), which may adversely impact performance.", MessageType.Warning);
            if (config.ForceSingleBaseline) EditorGUILayout.HelpBox("You've enabled ForceSingleBaseline, which will adversely impact bandwidth (often significantly), but improve CPU performance.", MessageType.Warning);
        }


        /// <summary>
        /// Adding the Global config to the build using the same logic as the Localization package,
        /// com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs.
        /// </summary>
        internal class NetcodeConfigEditorBuildProcess : IPreprocessBuildWithReport, IPostprocessBuildWithReport
        {
            bool m_RemoveFromPreloadedAssets;
            public int callbackOrder => 0;

           /// <summary>Copied almost verbatim from com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs.</summary>
            public void OnPreprocessBuild(BuildReport report)
            {
                m_RemoveFromPreloadedAssets = false;
                if (SavedConfig == null)
                    return;

                // Add the NETCODE settings to the preloaded assets.
                var preloadedAssets = PlayerSettings.GetPreloadedAssets();
                bool wasDirty = IsPlayerSettingsDirty();

                if (!preloadedAssets.Contains(SavedConfig))
                {
                    ArrayUtility.Add(ref preloadedAssets, SavedConfig);
                    PlayerSettings.SetPreloadedAssets(preloadedAssets);

                    // If we have to add the settings then we should also remove them.
                    m_RemoveFromPreloadedAssets = true;

                    // Clear the dirty flag so we dont flush the modified file (case 1254502)
                    if (!wasDirty)
                        ClearPlayerSettingsDirtyFlag();
                }
            }

            /// <summary>Copied almost verbatim from com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs.</summary>
            public void OnPostprocessBuild(BuildReport report)
            {
                if (SavedConfig == null || !m_RemoveFromPreloadedAssets)
                    return;

                bool wasDirty = IsPlayerSettingsDirty();

                var preloadedAssets = PlayerSettings.GetPreloadedAssets();
                ArrayUtility.Remove(ref preloadedAssets, SavedConfig);
                PlayerSettings.SetPreloadedAssets(preloadedAssets);

                // Clear the dirty flag so we dont flush the modified file (case 1254502)
                if (!wasDirty)
                    ClearPlayerSettingsDirtyFlag();
            }

            /// <summary>Copied almost verbatim from com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs.</summary>
            static bool IsPlayerSettingsDirty()
            {
                var settings = Resources.FindObjectsOfTypeAll<PlayerSettings>();
                if (settings != null && settings.Length > 0)
                    return EditorUtility.IsDirty(settings[0]);
                return false;
            }

            /// <summary>Copied almost verbatim from com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs.</summary>
            static void ClearPlayerSettingsDirtyFlag()
            {
                var settings = Resources.FindObjectsOfTypeAll<PlayerSettings>();
                if (settings != null && settings.Length > 0)
                    EditorUtility.ClearDirty(settings[0]);
            }
        }
    }
}

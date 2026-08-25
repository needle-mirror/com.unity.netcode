using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.NetCode.Hybrid;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Assertions;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode.Editor
{
    /// <summary>Editor script managing the creation and registration of <see cref="NetCodeConfig"/> Global ScriptableObject.</summary>
    [CustomEditor(typeof(NetCodeConfig), true, isFallback = false)]
    internal class NetcodeConfigEditor : UnityEditor.Editor
    {
        private const string k_LiveEditingWarning = " Therefore, be aware that the Global config is applied project-wide automatically:\n - Note that this can be overridden by any C# code that modifies these NetCode configuration singleton components manually. "+nameof(NetCodeConfig)+" only serves as a seed to the in world settings and is set at world creation time before all systems OnCreate.\n";
        private static readonly GUILayoutOption s_ButtonWidth = GUILayout.Width(90);
        const string k_DefaultConfigPath = "Assets/NetcodeConfig.asset";

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
            var assetPath = AssetDatabase.GenerateUniqueAssetPath(k_DefaultConfigPath);
            var netCodeConfig = CreateInstance<NetCodeConfig>();
#pragma warning disable 618 // IsGlobalConfig is obsolete (RemovedAfter 6.8), but still used to resolve the build config deterministically.
            netCodeConfig.IsGlobalConfig = true; // Prevent warning when first creating it.
#pragma warning restore 618
            AssetDatabase.CreateAsset(netCodeConfig, assetPath);
            Selection.activeObject = SavedConfig = netCodeConfig; // can't use LoadAsset as that doesn't work when starting the engine for the first time
            Assert.IsNotNull(SavedConfig);
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
               if (PlayerSettings.GetPreloadedAssets() is NetCodeConfig[] { Length: > 0 } netCodeConfigs)
               {
                   var found = netCodeConfigs[0];
                   SavedConfig = found;
                   Debug.LogWarning($"The Global NetCodeConfig ('{found.name}') is now saved into the {nameof(NetCodeClientAndServerSettings)} ProjectAsset! Please ensure you save that file to source control (if applicable). It is now safe to remove this asset from the Preloaded Assets list, if you'd like to. It'll get added automatically during builds. This corrective logic will be removed after Netcode 1.x.");
               }

               var foundConfigs = GetNetcodeConfigs();
               if (foundConfigs.Count == 0)
               {
                   CreateNetcodeSettingsAsset();
               }
               else
               {
                   SavedConfig = foundConfigs[0];
               }
            }

            NetCodeConfig.Global = SavedConfig;
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
                        if (inst.GlobalNetCodeConfig == null)
                        {
                            var configs = GetNetcodeConfigs();
                            if (configs.Count == 0)
                            {
                                CreateNetcodeSettingsAsset();
                            }
                            else
                            {
                                var netCodeConfigs = configs.ToArray();
                                Array.Sort(netCodeConfigs);
                                SavedConfig = netCodeConfigs[0];
                            }
                        }
                        inst.GlobalNetCodeConfig = EditorGUILayout.ObjectField(new GUIContent($"Required {nameof(NetCodeConfig)}", "Select the asset that NetCode will use, by default."), inst.GlobalNetCodeConfig, typeof(NetCodeConfig), allowSceneObjects: false) as NetCodeConfig;

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
                        EditorGUILayout.HelpBox($"No Global NetCodeConfig is set. This is invalid. Note that even with a {nameof(NetCodeConfig)} you can still override ECS component based settings once the world is created. This config only seeds the initial settings component. Please raise a bug with the netcode team as you shouldn't be in this state.", MessageType.Error);
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
        static List<NetCodeConfig> GetNetcodeConfigs()
        {
            var netcodeConfigs = AssetDatabase.FindAssets($"t:{nameof(NetCodeConfig)}");
            var configs = new List<NetCodeConfig>();
            foreach (var netcodeConfig in netcodeConfigs)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(netcodeConfig);
                configs.Add(AssetDatabase.LoadAssetAtPath<NetCodeConfig>(assetPath));
            }
            return configs;
        }

        /// <summary>
        /// Slow, so we only do it when we actually set a new config.
        /// </summary>
        static void LoadAllNetCodeConfigsAndSetGlobalFlags()
        {
            foreach (var config in GetNetcodeConfigs())
            {
                if (config) { ValidateConfig(config); }
            }
        }

        // TODO (6.8): Once multiple NetCodeConfigs support are fully removed and non-global configs are stripped from builds via
        // HideFlags.DontSaveInBuild, IsGlobalConfig (and therefore this whole method) can be deleted.
        private static void ValidateConfig(NetCodeConfig config)
        {
            var isActuallyGlobalConfig = (config == SavedConfig);
#pragma warning disable 618 // IsGlobalConfig is obsolete (RemovedAfter 6.8), but still used to resolve the build config deterministically.
            if (isActuallyGlobalConfig != config.IsGlobalConfig)
            {
                Debug.LogWarning($"Detected individual NetCodeConfig asset ('{AssetDatabase.GetAssetPath(config) ?? config.name}') with incorrect `IsGlobalConfig` flag! Was '{config.IsGlobalConfig}', updated to '{isActuallyGlobalConfig}'. Check for modifications to the {nameof(NetCodeClientAndServerSettings)}.asset, and commit all changed netcode files. These warnings are expected when modifying the Global NetCodeConfig, and are harmless.", config);
                config.IsGlobalConfig = isActuallyGlobalConfig;
                EditorUtility.SetDirty(config);
            }
#pragma warning restore 618
        }

        private static readonly GUIContent s_ClientServerTickRate = new GUIContent("ClientServerTickRate", "General multiplayer settings.\n\nServer Authoritative - Thus, when a client connects, the server will send an RPC clobbering any existing client values.");
        private static readonly GUIContent s_ClientTickRate = new GUIContent("ClientTickRate", "General multiplayer settings for the client.\n\nCan be configured on a per-client basis (via use of multiple configs, or direct C# component manipulation).");
        private static readonly GUIContent s_GhostSendSystemData = new GUIContent("GhostSendSystemData", "Specific optimization (and debug) settings for the GhostSendSystem to reduce bandwidth and CPU consumption.");
        private static readonly GUIContent s_TransportSettings = new GUIContent("NetworkConfigParameter (Unity Transport)", "Configures various UTP <b>NetworkConfigParameter</b> configuration values, but only when user-code uses one of the built-in <b>INetworkStreamDriverConstructor</b>'s.\n\nTo read this config in your own driver constructors, call <b>DefaultDriverBuilder.AddNetcodePackageDefaultNetworkConfigParameters</b>.");
        private static bool s_TransportSettingsFoldedOut = true;
        const int k_SpaceBetweenConfigs = 15;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var config = (NetCodeConfig)target;

            ValidateConfig(config);

#pragma warning disable 618 // IsGlobalConfig is obsolete (RemovedAfter 6.8), but still used to resolve the build config deterministically.
            if (config.IsGlobalConfig)
                EditorGUILayout.HelpBox("You have selected this as your Global config." + k_LiveEditingWarning, MessageType.Info);
#pragma warning restore 618
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
            GUILayout.Space(k_SpaceBetweenConfigs);

            //.
            GUI.enabled = true; // we can always edit a ClientTickRate config.
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ClientTickRate)), s_ClientTickRate);
            GUI.enabled = ClientServerBootstrap.ClientWorld != null || !Application.isPlaying; // But the "apply" button is only applicable if there's a world to apply to while in playmode
            if (!GUI.enabled)
                EditorGUILayout.HelpBox("No client world available to live tweak.", MessageType.Info);
            if (Application.isPlaying && GUILayout.Button("Apply to All Client Worlds"))
            {
                foreach (var world in ClientServerBootstrap.ClientWorlds)
                {
                    var em = world.EntityManager;
                    var ent = em.CreateEntityQuery(typeof(ClientTickRate)).GetSingletonEntity();
                    em.SetComponentData(ent, NetCodeConfig.Global.ClientTickRate);
                }
            }

            GUILayout.Space(k_SpaceBetweenConfigs);

            //.
            GUI.enabled = true; // we can always edit a GhostSendSystemData config.
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.GhostSendSystemData)), s_GhostSendSystemData);
            GUI.enabled = ClientServerBootstrap.ServerWorld != null || !Application.isPlaying; // But the "apply" button is only applicable if there's a world to apply to while in playmode
            if (!GUI.enabled)
                EditorGUILayout.HelpBox("No server world available to live tweak.", MessageType.Info);
            ValidateGhostSendSystemData(config.GhostSendSystemData);
            if (Application.isPlaying && GUILayout.Button("Apply to All Server Worlds"))
            {
                foreach (var world in ClientServerBootstrap.ServerWorlds)
                {
                    var em = world.EntityManager;
                    var ent = em.CreateEntityQuery(typeof(GhostSendSystemData)).GetSingletonEntity();
                    em.SetComponentData(ent, NetCodeConfig.Global.GhostSendSystemData);
                }
            }
            GUILayout.Space(k_SpaceBetweenConfigs);

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
            GUI.enabled = true;
            GUILayout.Space(k_SpaceBetweenConfigs); // space between various configs
#if NETCODE_TRACING_TOOL
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.TracingConfig)));
#endif
            GUILayout.Space(k_SpaceBetweenConfigs);

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
            GUILayout.Space(k_SpaceBetweenConfigs);
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
            const string k_DuplicateMessageDoneEditorPrefKey = "NetcodeDuplicateConfigMessageDone";

            bool DuplicateConfigMessageDone
            {
                get => EditorPrefs.GetBool(k_DuplicateMessageDoneEditorPrefKey);
                set => EditorPrefs.SetBool(k_DuplicateMessageDoneEditorPrefKey, value);
            }

            /// <summary>Copied almost verbatim from com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs.</summary>
            public void OnPreprocessBuild(BuildReport report)
            {
                m_RemoveFromPreloadedAssets = false;
                if (SavedConfig == null)
                {
                    // This can happen if users delete their single netcodeconfig after the editor was launched and before making a build.
                    // In this case, we log an error and generate a default config.
                    Debug.LogError($"No {nameof(NetCodeConfig)} asset found, generating a new one. This will not fail this build.");
                    InitializeNetCodeConfigEditorBugFix();
                }

                if (!DuplicateConfigMessageDone)
                {
                    // Step: Decide on a single global config for this build, and report on multiple-config usage.
                    // Support for multiple NetCodeConfigs is deprecated (RemovedAfter 6.8). For now we keep building such
                    // projects, but we (a) deterministically pick the Project Settings config as the global one,
                    // (b) warn the user, and (c) emit analytics so we can track how many projects still ship multiple configs.
                    MessageAndResolveMultipleConfigs();
                    DuplicateConfigMessageDone = true;
                }

                // Add the NETCODE settings to the preloaded assets.
                var preloadedAssets = PlayerSettings.GetPreloadedAssets();

                foreach (var asset in preloadedAssets)
                {
                    if (asset == SavedConfig)
                    {
                        return;
                    }
                }

                ArrayUtility.Add(ref preloadedAssets, SavedConfig);
                PlayerSettings.SetPreloadedAssets(preloadedAssets);

                // If we have to add the settings then we should also remove them.
                m_RemoveFromPreloadedAssets = true;

                // Clear the dirty flag so we dont flush the modified file (case 1254502)
                bool wasDirty = IsPlayerSettingsDirty();
                if (!wasDirty)
                    ClearPlayerSettingsDirtyFlag();
            }

            /// <summary>
            /// Decides on the single global <see cref="NetCodeConfig"/> for the build (the one selected in Project Settings),
            /// fixes up the deprecated <c>IsGlobalConfig</c> flag on every config so the runtime can resolve deterministically,
            /// warns the user if more than one config exists, and emits analytics tracking the config count.
            /// </summary>
            /// <remarks>
            /// TODO (6.8): Once users have stopped shipping multiple configs, this should additionally mark every non-global
            /// config with <c>HideFlags.DontSaveInBuild</c> so they are excluded from the build entirely (leaving only the
            /// single global config loaded at runtime). At that point the <c>IsGlobalConfig</c> field and the deterministic
            /// sort in <see cref="NetCodeConfig.CompareTo"/> can be removed.
            /// </remarks>
            static void MessageAndResolveMultipleConfigs()
            {
                var configs = GetNetcodeConfigs();

                // Decide on a singular global config and set IsGlobalConfig correctly on all existing configs.
                LoadAllNetCodeConfigsAndSetGlobalFlags();

                if (configs.Count > 1)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[NetCodeConfig] Found {configs.Count} NetCodeConfig assets in this project. Support for multiple NetCodeConfigs is deprecated (RemovedAfter 6.8); only a single global config is supported. ");
                    sb.Append($"'{SavedConfig.name}' (selected in Project Settings > Multiplayer) will be used as the global config for this build. ");
                    sb.Append("This commonly happens when a NetCodeConfig asset is duplicated (copy/paste). This check is a migration check and won't be called again. Please delete the extra asset(s). The following configs were found:");
                    foreach (var config in configs)
                    {
                        if (!config)
                            continue;
                        var isGlobal = config == SavedConfig;
                        sb.Append($"\n - '{AssetDatabase.GetAssetPath(config)}'{(isGlobal ? " (global)" : string.Empty)}");
                    }
                    // Non-fatal: we still build, picking the global config deterministically.
                    Debug.LogError(sb.ToString(), SavedConfig);
                }

                // Note: the project's NetCodeConfig count is reported via analytics through
                // GhostScaleAnalyticsData.NetCodeConfigCount (gathered on play-mode exit), so we don't send anything here.
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

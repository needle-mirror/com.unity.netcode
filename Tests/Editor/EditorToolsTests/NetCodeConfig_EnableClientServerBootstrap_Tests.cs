using System;
using System.Collections;
using NUnit.Framework;
using Unity.Entities;
using Unity.Netcode;
using Unity.Netcode.Hybrid;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.Netcode.Tests
{
    [TestFixture]
    [Serializable]
    internal class NetCodeConfig_EnableClientServerBootstrap_Tests
    {
        public NetcodeConfig m_OldNetcodeConfigGlobal;
        public bool m_OldEnterPlayModeOptionsEnabled;
        public EnterPlayModeOptions m_OldEnterPlayModeOptions;
        public NetcodeConfig m_TempNetcodeConfig;
        public string m_TempNetCodeConfigGlobalProjectAsset;
        public bool m_ExpectDomainReload;
        public bool m_WarnWhenTicksBatch;

        [UnityTest, Timeout(60_000), Description("Tests all permutations of the NetCodeConfig.EnableClientServerBootstrap option against all permutations of the EnterPlayModeOptions option. As a secondary test: It ensures no runtime errors when domain reloads is either on or off."),]
        [Ignore("Unstable test that times out and not properly cleaning NetCodeConfig upon such event which leads to more failures. Tracked in MTT-13034")]
        public IEnumerator Test([Values] NetcodeConfig.AutomaticBootstrapSetting value,
            [Values(EnterPlayModeOptions.None,
                EnterPlayModeOptions.DisableDomainReload,
                EnterPlayModeOptions.DisableSceneReload,
                EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload)]
            EnterPlayModeOptions enterPlayModeOptions)
        {
            // Setup:
            LogAssert.ignoreFailingMessages = false;
            m_OldNetcodeConfigGlobal = NetcodeClientAndServerSettings.instance.GlobalNetcodeConfig;
            m_OldEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            m_OldEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;
            m_TempNetcodeConfig = ScriptableObject.CreateInstance<NetcodeConfig>();
            m_TempNetcodeConfig.m_IsGlobalConfig = true;
            m_TempNetCodeConfigGlobalProjectAsset = AssetDatabase.GenerateUniqueAssetPath("Assets/NetCodeConfigGlobal_Test.asset");
#if UNITY_EDITOR
            m_WarnWhenTicksBatch = MultiplayerPlayModePreferences.WarnBatchedTicks;
            MultiplayerPlayModePreferences.WarnBatchedTicks = false;
#endif
            NetcodeConfig.Global = m_TempNetcodeConfig;
            AssetDatabase.CreateAsset(NetcodeConfig.Global, m_TempNetCodeConfigGlobalProjectAsset);
            NetcodeClientAndServerSettings.instance.GlobalNetcodeConfig = NetcodeConfig.Global;

            // Test:
            EditorSettings.enterPlayModeOptionsEnabled = enterPlayModeOptions != EnterPlayModeOptions.None;
            EditorSettings.enterPlayModeOptions = enterPlayModeOptions;
            NetcodeClientAndServerSettings.instance.GlobalNetcodeConfig.EnableClientServerBootstrap = value;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            m_ExpectDomainReload = (enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
            yield return new EnterPlayMode(m_ExpectDomainReload); // This step destroys all private or static field data,
                                                                  // and variables (except arguments),
                                                                  // as it triggers a domain reload!
            var hasClientWorlds = ClientServerBootstrap.HasClientWorlds;
            var hasServerWorld = ClientServerBootstrap.HasServerWorld;

            // Teardown:
            yield return new ExitPlayMode();
            World.DisposeAllWorlds();
            EditorSettings.enterPlayModeOptions = m_OldEnterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = m_OldEnterPlayModeOptionsEnabled;
            NetcodeClientAndServerSettings.instance.GlobalNetcodeConfig = m_OldNetcodeConfigGlobal;
            AssetDatabase.DeleteAsset(m_TempNetCodeConfigGlobalProjectAsset);
#if UNITY_EDITOR
            MultiplayerPlayModePreferences.WarnBatchedTicks = m_WarnWhenTicksBatch;
#endif
            // Run Assertions:
            var expectNetCodeWorlds = value == NetcodeConfig.AutomaticBootstrapSetting.EnableAutomaticBootstrap;
            Assert.AreEqual(expectNetCodeWorlds, hasClientWorlds, nameof(hasClientWorlds));
            Assert.AreEqual(expectNetCodeWorlds, hasServerWorld, nameof(hasServerWorld));
            LogAssert.NoUnexpectedReceived();
        }
    }
}

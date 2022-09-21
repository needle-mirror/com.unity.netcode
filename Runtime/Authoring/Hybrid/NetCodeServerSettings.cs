#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEngine;
using Unity.Entities.Build;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using Hash128 = Unity.Entities.Hash128;

namespace Authoring.Hybrid
{
    internal class NetCodeServerSettings : DotsPlayerSettings
    {
        [SerializeField]
        public NetcodeConversionTarget NetcodeTarget = NetcodeConversionTarget.Server;

        [SerializeField]
        public BakingSystemFilterSettings FilterSettings;

        [SerializeField] public string[] AdditionalScriptingDefines = Array.Empty<string>();

        public override BakingSystemFilterSettings GetFilterSettings()
        {
            return FilterSettings;
        }

        public override string[] GetAdditionalScriptingDefines()
        {
            return AdditionalScriptingDefines;
        }
    }

    internal class ServerSettings : DotsPlayerSettingsProvider
    {
        private NetCodeServerSettings m_NetCodeServerSettings;

        private VisualElement m_rootElement;

        private Hash128 m_ServerGUID;

        public override int Importance
        {
            get { return 1; }
        }

        public override DotsGlobalSettings.PlayerType GetPlayerType()
        {
            return DotsGlobalSettings.PlayerType.Server;
        }

        public override Hash128 GetPlayerSettingGUID()
        {
            if(!m_ServerGUID.IsValid)
                LoadOrCreateServerAsset();
            return m_ServerGUID;
        }

        public override DotsPlayerSettings GetSettingAsset()
        {
            if (m_NetCodeServerSettings == null)
                LoadOrCreateServerAsset();
            return m_NetCodeServerSettings;
        }

        void LoadOrCreateServerAsset()
        {
            var  path = k_DefaultAssetPath + k_DefaultAssetName + "ServerSettings" + k_DefaultAssetExtension;
            if(File.Exists(path))
                m_NetCodeServerSettings = AssetDatabase.LoadAssetAtPath<NetCodeServerSettings>(path);
            else
            {
                m_NetCodeServerSettings = (NetCodeServerSettings)ScriptableObject.CreateInstance(typeof(NetCodeServerSettings));
                m_NetCodeServerSettings.name = k_DefaultAssetName + nameof(ServerSettings);

                AssetDatabase.CreateAsset(m_NetCodeServerSettings, path);
            }
            m_ServerGUID = new Hash128(AssetDatabase.AssetPathToGUID(path));
        }

        public override void Enable(int value)
        {
            m_rootElement.SetEnabled((value == (int)DotsGlobalSettings.PlayerType.Server));
        }

        public override void OnActivate(DotsGlobalSettings.PlayerType type, VisualElement rootElement)
        {
            m_rootElement = new VisualElement();
            m_rootElement.AddToClassList("target");
            m_rootElement.SetEnabled(type == DotsGlobalSettings.PlayerType.Server);

            var so = new SerializedObject(GetSettingAsset());
            m_rootElement.Bind(so);
            so.Update();

            var label = new Label("Server");
            m_rootElement.Add(label);

            var targetS = new VisualElement();
            targetS.AddToClassList("target-Settings");
            var propServerSettings = so.FindProperty("FilterSettings");
            var propServerField = new PropertyField(propServerSettings.FindPropertyRelative("ExcludedBakingSystemAssemblies"));

            targetS.Add(propServerField);

            var propExtraDefines = so.FindProperty("AdditionalScriptingDefines");
            var propExtraDefinesField = new PropertyField(propExtraDefines);
            propExtraDefinesField.name = "Extra Defines";
            targetS.Add(propExtraDefinesField);

            m_rootElement.Add(targetS);
            rootElement.Add(m_rootElement);

            so.ApplyModifiedProperties();
        }

        public override string[] GetExtraScriptingDefines()
        {
            return new []{"UNITY_SERVER"}.Concat(GetSettingAsset().GetAdditionalScriptingDefines()).ToArray();
        }

        public override BuildOptions GetExtraBuildOptions()
        { // DOTS-5792
#pragma warning disable 618
            return BuildOptions.EnableHeadlessMode;
#pragma warning restore 618
        }
    }
}
#endif

#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Unity.Entities.Build;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Hash128 = Unity.Entities.Hash128;

namespace Authoring.Hybrid
{
    public enum NetCodeClientTarget
    {
        Client = 0,
        ClientAndServer = 1
    }

    internal class NetCodeClientSettings : DotsPlayerSettings
    {
        [SerializeField]
        public NetcodeConversionTarget NetcodeTarget = NetcodeConversionTarget.Client;

        [SerializeField]
        public BakingSystemFilterSettings FilterSettings;

        [SerializeField]
        public string[] AdditionalScriptingDefines = Array.Empty<string>();

        public override BakingSystemFilterSettings GetFilterSettings()
        {
            return FilterSettings;
        }

        public override string[] GetAdditionalScriptingDefines()
        {
            return AdditionalScriptingDefines;
        }
    }

    public class ClientSettings : DotsPlayerSettingsProvider
    {
        private const string m_EditorPrefsNetCodeClientTarget = "com.unity.entities.netcodeclient.target";

        public NetCodeClientTarget NetCodeClientTarget
        {
            get => (NetCodeClientTarget) EditorPrefs.GetInt(m_EditorPrefsNetCodeClientTarget, 0);
            set => EditorPrefs.SetInt(m_EditorPrefsNetCodeClientTarget, (int)value);
        }

        private VisualElement m_rootElement;

        private NetCodeClientSettings m_NetCodeClientSettings;
        private NetCodeClientAndServerSettings m_NetCodeClientAndServerSettings;

        private Hash128 m_ClientGUID;
        private Hash128 m_ClientAndServerGUID;

        public override int Importance
        {
            get { return 1; }
        }

        public override DotsGlobalSettings.PlayerType GetPlayerType()
        {
            return DotsGlobalSettings.PlayerType.Client;
        }

        public override Hash128 GetPlayerSettingGUID()
        {
            return GetSettingGUID(NetCodeClientTarget);
        }

        public Hash128 GetSettingGUID(NetCodeClientTarget target)
        {
            if (target == NetCodeClientTarget.Client)
            {
                if(!m_ClientGUID.IsValid)
                    LoadOrCreateClientAsset();
                return m_ClientGUID;
            }

            if (target == NetCodeClientTarget.ClientAndServer)
            {
                if(!m_ClientAndServerGUID.IsValid)
                    LoadOrCreateClientAndServerAsset();
                return m_ClientAndServerGUID;
            }
            return new Hash128();
        }

        public override void Enable(int value)
        {
            m_rootElement.SetEnabled((value == (int)DotsGlobalSettings.PlayerType.Client));
        }

        public override void OnActivate(DotsGlobalSettings.PlayerType type, VisualElement rootElement)
        {
            m_rootElement = new VisualElement();
            m_rootElement.SetEnabled(type == DotsGlobalSettings.PlayerType.Client);

            var targetElement = UpdateUI();

            m_rootElement.Add(targetElement);
            rootElement.Add(m_rootElement);
        }

        VisualElement UpdateUI()
        {
            var targetElement = new VisualElement();
            targetElement.name = "target";
            targetElement.AddToClassList("target");

            var so = new SerializedObject(GetSettingAsset());
            targetElement.Bind(so);
            so.Update();

            var label = new Label("Client");
            targetElement.Add(label);

            var targetS = new VisualElement();
            targetS.AddToClassList("target-Settings");

            var field = new EnumField("NetCode client target:",  NetCodeClientTarget);
            targetS.Add(field);

            if (NetCodeClientTarget == NetCodeClientTarget.Client)
            {
                var propClientSettings = so.FindProperty("FilterSettings");
                var propClientField = new PropertyField(propClientSettings.FindPropertyRelative("ExcludedBakingSystemAssemblies"));
                propClientField.name = "ClientFilterSettings";
                targetS.Add(propClientField);
            }
            else
            {
                var propClientField = targetS.Q<PropertyField>("ClientFilterSettings");
                if (propClientField != null)
                    targetS.Remove(propClientField);
            }

            var propExtraDefines = so.FindProperty("AdditionalScriptingDefines");
            var propExtraDefinesField = new PropertyField(propExtraDefines);
            propExtraDefinesField.name = "Client Extra Defines";
            targetS.Add(propExtraDefinesField);

            field.RegisterCallback<ChangeEvent<Enum>>(evt =>
            {
                m_rootElement.Remove(targetElement);
                NetCodeClientTarget = (NetCodeClientTarget)evt.newValue;
                var newTargetElement = UpdateUI();
                m_rootElement.Add(newTargetElement);
            });

            targetElement.Add(targetS);
            so.ApplyModifiedProperties();

            return targetElement;
        }

        public override string[] GetExtraScriptingDefines()
        {
            var extraScriptingDefines = GetSettingAsset().GetAdditionalScriptingDefines();
            if(NetCodeClientTarget == NetCodeClientTarget.Client)
                return extraScriptingDefines.Append("UNITY_CLIENT").ToArray();
            if (NetCodeClientTarget == NetCodeClientTarget.ClientAndServer)
                return extraScriptingDefines;
            return Array.Empty<string>();
        }

        public override DotsPlayerSettings GetSettingAsset()
        {
            if (NetCodeClientTarget == NetCodeClientTarget.Client)
            {
                if (m_NetCodeClientSettings == null)
                    LoadOrCreateClientAsset();
                return m_NetCodeClientSettings;
            }

            if (NetCodeClientTarget == NetCodeClientTarget.ClientAndServer)
            {
                if(m_NetCodeClientAndServerSettings == null)
                    LoadOrCreateClientAndServerAsset();
                return m_NetCodeClientAndServerSettings;
            }
            return null;
        }

        void LoadOrCreateClientAsset()
        {
            var path = k_DefaultAssetPath + k_DefaultAssetName + "ClientSettings" + k_DefaultAssetExtension;
            if(File.Exists(path))
                m_NetCodeClientSettings = AssetDatabase.LoadAssetAtPath<NetCodeClientSettings>(path);
            else
            {
                //Create the Client asset
                m_NetCodeClientSettings = (NetCodeClientSettings)ScriptableObject.CreateInstance(typeof(NetCodeClientSettings));
                m_NetCodeClientSettings.NetcodeTarget = NetcodeConversionTarget.Client;
                m_NetCodeClientSettings.name = k_DefaultAssetName + nameof(NetCodeClientSettings);

                AssetDatabase.CreateAsset(m_NetCodeClientSettings, path);
            }
            m_ClientGUID = new Hash128(AssetDatabase.AssetPathToGUID(path));
        }

        void LoadOrCreateClientAndServerAsset()
        {
            if (m_NetCodeClientAndServerSettings == null)
            {
                var path = k_DefaultAssetPath + k_DefaultAssetName + "ClientAndServerSettings" + k_DefaultAssetExtension;
                if(File.Exists(path))
                    m_NetCodeClientAndServerSettings = AssetDatabase.LoadAssetAtPath<NetCodeClientAndServerSettings>(path);
                else
                {
                    //Create the ClientAndServer asset
                    m_NetCodeClientAndServerSettings = (NetCodeClientAndServerSettings)ScriptableObject.CreateInstance(typeof(NetCodeClientAndServerSettings));
                    m_NetCodeClientAndServerSettings.NetcodeTarget = NetcodeConversionTarget.ClientAndServer;
                    m_NetCodeClientAndServerSettings.name = k_DefaultAssetName + nameof(NetCodeClientAndServerSettings);

                    AssetDatabase.CreateAsset(m_NetCodeClientAndServerSettings, path);
                }
                m_ClientAndServerGUID = new Hash128(AssetDatabase.AssetPathToGUID(path));
            }
        }
    }
}
#endif

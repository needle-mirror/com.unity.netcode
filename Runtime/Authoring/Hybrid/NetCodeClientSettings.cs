﻿#if UNITY_EDITOR
using System;
using System.Linq;
using Unity.Entities.Build;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode.Hybrid
{
    public enum NetCodeClientTarget
    {
        [Tooltip("Build a client-only player.")]
        Client = 0,
        [Tooltip("Build a client-server player.")]
        ClientAndServer = 1
    }

    [FilePath("ProjectSettings/NetCodeClientSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal class NetCodeClientSettings : ScriptableSingleton<NetCodeClientSettings>, IEntitiesPlayerSettings, INetCodeConversionTarget
    {
        NetcodeConversionTarget INetCodeConversionTarget.NetcodeTarget => NetcodeConversionTarget.Client;

        [SerializeField]
        public BakingSystemFilterSettings FilterSettings;

        [SerializeField]
        public string[] AdditionalScriptingDefines = Array.Empty<string>();

        [SerializeField]
        public NetCodeClientTarget ClientTarget = NetCodeClientTarget.ClientAndServer;

        static Entities.Hash128 s_Guid;
        public Entities.Hash128 GUID
        {
            get
            {
                if (!s_Guid.IsValid)
                    s_Guid = UnityEngine.Hash128.Compute(GetFilePath());
                return s_Guid;
            }
        }
        public string CustomDependency => GetFilePath();
        void IEntitiesPlayerSettings.RegisterCustomDependency()
        {
            var hash = GetHash();
            AssetDatabase.RegisterCustomDependency(CustomDependency, hash);
        }

        public UnityEngine.Hash128 GetHash()
        {
            var hash = (UnityEngine.Hash128)GUID;
            if (FilterSettings?.ExcludedBakingSystemAssemblies != null)
                foreach (var assembly in FilterSettings.ExcludedBakingSystemAssemblies)
                {
                    var guid = AssetDatabase.GUIDFromAssetPath(AssetDatabase.GetAssetPath(assembly.asset));
                    hash.Append(ref guid);
                }
            foreach (var define in AdditionalScriptingDefines)
                hash.Append(define);
            return hash;
        }

        public BakingSystemFilterSettings GetFilterSettings()
        {
            return FilterSettings;
        }

        public string[] GetAdditionalScriptingDefines()
        {
            return AdditionalScriptingDefines;
        }

        ScriptableObject IEntitiesPlayerSettings.AsScriptableObject() => instance;

        internal void Save()
        {
            Save(true);
            ((IEntitiesPlayerSettings)this).RegisterCustomDependency();
            if (!AssetDatabase.IsAssetImportWorkerProcess())
                AssetDatabase.Refresh();
        }
        private void OnDisable() { Save(); }
    }

    internal class ClientSettings : DotsPlayerSettingsProvider
    {
        private const string m_EditorPrefsNetCodeClientTarget = "com.unity.entities.netcodeclient.target";

        [Obsolete("Use NetCodeClientSettings.instance.ClientTarget instead. Note that this EditorPref has been clobbered by the default field value now, too. (RemovedAfter NetCode 1.0)")]
        public NetCodeClientTarget NetCodeClientTarget
        {
            get => NetCodeClientSettings.instance.ClientTarget;
            set => NetCodeClientSettings.instance.ClientTarget = value;
        }

        private VisualElement m_rootElement;

        public override int Importance
        {
            get { return 1; }
        }

        public override DotsGlobalSettings.PlayerType GetPlayerType()
        {
            return DotsGlobalSettings.PlayerType.Client;
        }

        protected override Hash128 DoGetPlayerSettingGUID()
        {
            return GetSettingGUID(NetCodeClientSettings.instance.ClientTarget);
        }

        public Hash128 GetSettingGUID(NetCodeClientTarget target)
        {
            if (target == NetCodeClientTarget.Client)
            {
                return NetCodeClientSettings.instance.GUID;
            }

            if (target == NetCodeClientTarget.ClientAndServer)
            {
                return NetCodeClientAndServerSettings.instance.GUID;
            }
            return default;
        }

        public override void OnActivate(DotsGlobalSettings.PlayerType type, VisualElement rootElement)
        {
            rootElement.RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            rootElement.RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);

            m_rootElement = new VisualElement();
            m_rootElement.SetEnabled(type == DotsGlobalSettings.PlayerType.Client);

            var targetElement = UpdateUI();

            m_rootElement.Add(targetElement);
            rootElement.Add(m_rootElement);
        }

        static void OnAttachToPanel(AttachToPanelEvent evt)
        {
            // The ScriptableSingleton<T> is not directly editable by default.
            // Change the hideFlags to make the SerializedObject editable.
            NetCodeClientSettings.instance.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            NetCodeClientAndServerSettings.instance.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
        }

        static void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            NetCodeClientSettings.instance.hideFlags = HideFlags.HideAndDontSave;
            NetCodeClientAndServerSettings.instance.hideFlags = HideFlags.HideAndDontSave;
            NetCodeClientSettings.instance.Save();
            NetCodeClientAndServerSettings.instance.Save();
        }

        VisualElement UpdateUI()
        {
            var targetElement = new VisualElement();
            targetElement.name = "target";
            targetElement.AddToClassList("target");

            var so = new SerializedObject(GetSettingAsset().AsScriptableObject());
            targetElement.Bind(so);
            so.Update();

            var label = new Label("Client");
            targetElement.Add(label);

            var targetS = new VisualElement();
            targetS.AddToClassList("target-Settings");

            // PropertyField didn't seem to work here.
            var field = new EnumField("NetCode Client Target", NetCodeClientSettings.instance.ClientTarget);
            field.tooltip = "Denotes whether or not Server data and logic is included in a client build (when making a client build). Doing so allows the client executable to self-host (i.e. \"Client Host\") a multiplayer game.";
            targetS.Add(field);

            var prop = so.FindProperty("FilterSettings.ExcludedBakingSystemAssemblies");
            var propField = new PropertyField(prop);
            propField.BindProperty(prop);
            propField.RegisterCallback<ChangeEvent<string>>(
                evt =>
                {
                    NetCodeClientSettings.instance.FilterSettings.SetDirty();
                });
            targetS.Add(propField);

            var propExtraDefines = so.FindProperty("AdditionalScriptingDefines");
            var propExtraDefinesField = new PropertyField(propExtraDefines);
            propExtraDefinesField.name = "Client Extra Defines";
            targetS.Add(propExtraDefinesField);

            field.RegisterCallback<ChangeEvent<Enum>>(evt =>
            {
                m_rootElement.Remove(targetElement);

                var serializedObject = new SerializedObject(NetCodeClientSettings.instance);
                var serializedProperty = serializedObject.FindProperty("ClientTarget");
                serializedProperty.enumValueIndex = (int)(NetCodeClientTarget)evt.newValue;
                serializedObject.ApplyModifiedProperties();

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
            var netCodeClientTarget = NetCodeClientSettings.instance.ClientTarget;
            if(netCodeClientTarget == NetCodeClientTarget.Client)
                return extraScriptingDefines.Append("UNITY_CLIENT").ToArray();
            if (netCodeClientTarget == NetCodeClientTarget.ClientAndServer)
                return extraScriptingDefines;
            return Array.Empty<string>();
        }

        protected override IEntitiesPlayerSettings DoGetSettingAsset()
        {
            var netCodeClientSettings = NetCodeClientSettings.instance;
            var netCodeClientTarget = netCodeClientSettings.ClientTarget;
            if (netCodeClientTarget == NetCodeClientTarget.Client)
            {
                return netCodeClientSettings;
            }

            if (netCodeClientTarget == NetCodeClientTarget.ClientAndServer)
            {
                return NetCodeClientAndServerSettings.instance;
            }
            return null;
        }
    }
}
#endif

﻿#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEngine;
using Unity.Entities.Build;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode.Hybrid
{
    [FilePath("ProjectSettings/NetCodeServerSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal class NetCodeServerSettings : ScriptableSingleton<NetCodeServerSettings>, IEntitiesPlayerSettings, INetCodeConversionTarget
    {
        NetcodeConversionTarget INetCodeConversionTarget.NetcodeTarget => NetcodeConversionTarget.Server;

        [SerializeField]
        public BakingSystemFilterSettings FilterSettings;

        [SerializeField] public string[] AdditionalScriptingDefines = Array.Empty<string>();

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

        public ScriptableObject AsScriptableObject()
        {
            return instance;
        }

        internal void Save()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            Save(true);
            ((IEntitiesPlayerSettings)this).RegisterCustomDependency();
        }
        private void OnDisable() { Save(); }
    }

    internal class ServerSettings : DotsPlayerSettingsProvider
    {
        VisualElement m_BuildSettingsContainer;

        public override int Importance
        {
            get { return 1; }
        }

        public override DotsGlobalSettings.PlayerType GetPlayerType()
        {
            return DotsGlobalSettings.PlayerType.Server;
        }

        protected override Hash128 DoGetPlayerSettingGUID()
        {
            return NetCodeServerSettings.instance.GUID;
        }

        protected override IEntitiesPlayerSettings DoGetSettingAsset()
        {
            return NetCodeServerSettings.instance;
        }

        public override void OnActivate(DotsGlobalSettings.PlayerType type, VisualElement rootElement)
        {
            rootElement.RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            rootElement.RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);

            m_BuildSettingsContainer = new VisualElement();
            m_BuildSettingsContainer.AddToClassList("target");

            var so = new SerializedObject(NetCodeServerSettings.instance);
            m_BuildSettingsContainer.Bind(so);
            so.Update();

            var label = new Label("Server");
            m_BuildSettingsContainer.Add(label);

            var targetS = new VisualElement();
            targetS.AddToClassList("target-Settings");
            var propServerSettings = so.FindProperty("FilterSettings.ExcludedBakingSystemAssemblies");
            var propServerField = new PropertyField(propServerSettings);
            propServerField.BindProperty(propServerSettings);
            propServerField.RegisterCallback<ChangeEvent<string>>(
                evt =>
                {
                    NetCodeServerSettings.instance.FilterSettings.SetDirty();
                });
            targetS.Add(propServerField);

            var propExtraDefines = so.FindProperty("AdditionalScriptingDefines");
            var propExtraDefinesField = new PropertyField(propExtraDefines);
            propExtraDefinesField.name = "Extra Defines";
            targetS.Add(propExtraDefinesField);

            m_BuildSettingsContainer.Add(targetS);
            rootElement.Add(m_BuildSettingsContainer);

            so.ApplyModifiedProperties();
        }

        static void OnAttachToPanel(AttachToPanelEvent evt)
        {
            // The ScriptableSingleton<T> is not directly editable by default.
            // Change the hideFlags to make the SerializedObject editable.
            NetCodeServerSettings.instance.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
        }

        static void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            // Restore the original flags
            NetCodeServerSettings.instance.hideFlags = HideFlags.HideAndDontSave;
            NetCodeServerSettings.instance.Save();
        }

        public override string[] GetExtraScriptingDefines()
        {
            var extraDefines = GetSettingAsset().GetAdditionalScriptingDefines().Append("UNITY_SERVER");
#if !NETCODE_NDEBUG
            if (EditorUserBuildSettings.development)
                extraDefines = extraDefines.Append("NETCODE_DEBUG");
#endif
            return extraDefines.ToArray();
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

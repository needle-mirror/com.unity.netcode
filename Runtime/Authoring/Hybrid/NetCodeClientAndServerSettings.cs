#if UNITY_EDITOR
using System;
using Unity.Entities.Build;
using UnityEditor;
using UnityEngine;

namespace Unity.NetCode.Hybrid
{
    [FilePath("ProjectSettings/NetCodeClientAndServerSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal class NetCodeClientAndServerSettings : ScriptableSingleton<NetCodeClientAndServerSettings>, IEntitiesPlayerSettings, INetCodeConversionTarget
    {
        NetcodeConversionTarget INetCodeConversionTarget.NetcodeTarget => NetcodeConversionTarget.ClientAndServer;

        [SerializeField]
        public BakingSystemFilterSettings FilterSettings;

        [SerializeField]
        public string[] AdditionalScriptingDefines = Array.Empty<string>();

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
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            Save(true);
            ((IEntitiesPlayerSettings)this).RegisterCustomDependency();
        }

        private void OnDisable() => Save();
    }
}
#endif

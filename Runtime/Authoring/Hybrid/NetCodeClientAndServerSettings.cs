#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities.Build;
using UnityEditor;
using UnityEngine;
using UnityEngine.PlayerLoop;
using UnityEngine.UIElements;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// The <see cref="IEntitiesPlayerSettings"/> baking settings to use for server builds. You can assign the <see cref="GUID"/>
    /// to the <see cref="Unity.Scenes.SceneSystemData.BuildConfigurationGUID"/> to instrument the asset import worker to bake the
    /// scene using this setting.
    /// </summary>
    [FilePath("ProjectSettings/NetCodeClientAndServerSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class NetCodeClientAndServerSettings : ScriptableSingleton<NetCodeClientAndServerSettings>, IEntitiesPlayerSettings, INetCodeConversionTarget
    {
        NetcodeConversionTarget INetCodeConversionTarget.NetcodeTarget => NetcodeConversionTarget.ClientAndServer;

        [SerializeField] private BakingSystemFilterSettings FilterSettings;

        [SerializeField] private string[] AdditionalScriptingDefines = Array.Empty<string>();

        /// <summary>
        ///     The <see cref="NetCodeConfig"/> automatically added to the build, accessed via user-code via <see cref="NetCodeConfig.Global"/>.
        /// </summary>
        [SerializeField] public NetCodeConfig GlobalNetCodeConfig;

        /// <inheritdoc cref="EditorImportanceSuggestion"/>
        [SerializeField] public List<EditorImportanceSuggestion> CurrentImportanceSuggestions = new List<EditorImportanceSuggestion>
        {
            new () { MinValue = 1, MaxValue = 4, Name = "Low Importance", Tooltip = "For cosmetic (i.e. visual-only) ghosts like glass bottles, signs, beach-balls, and cones etc. Typically <b>Static</b>.", },
            new () { MinValue = 5, MaxValue = 40, Name = "Medium Importance", Tooltip = "For common gameplay-affecting ghosts like trees, doors, explosive barrels, dropped loot etc. Typically <b>Static</b>.", },
            new () { MinValue = 50, MaxValue = 250, Name = "High Importance", Tooltip = "For per-player and objective-critical ghosts like Player Character Controllers and CTF flags etc. Typically for <b>Dynamic</b> i.e. <b>Predicted</b> ghosts. <b>UsePreSerialization</b> is likely a good fit.", },
            new () { MinValue = 1000, MaxValue = 0, Name = "Critical Importance", Tooltip = "For gameplay critical singletons like the one keeping the current score, or the one denoting whether or not the current round has started etc. Choose <b>UsePreSerialization</b>, and use sparingly.", },
        };

        static Entities.Hash128 s_Guid;
        /// <inheritdoc/>
        public Entities.Hash128 GUID
        {
            get
            {
                if (!s_Guid.IsValid)
                    s_Guid = UnityEngine.Hash128.Compute(GetFilePath());
                return s_Guid;
            }
        }

        /// <inheritdoc/>
        public string CustomDependency => GetFilePath();

        /// <inheritdoc/>
        void IEntitiesPlayerSettings.RegisterCustomDependency()
        {
            if (!AssetDatabase.IsAssetImportWorkerProcess())
            {
                var hash = GetHash();
                AssetDatabase.RegisterCustomDependency(CustomDependency, hash);
            }
        }
        /// <inheritdoc/>
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
        /// <inheritdoc/>
        public BakingSystemFilterSettings GetFilterSettings()
        {
            return FilterSettings;
        }
        /// <inheritdoc/>
        public string[] GetAdditionalScriptingDefines()
        {
            return AdditionalScriptingDefines;
        }
        /// <inheritdoc/>
        ScriptableObject IEntitiesPlayerSettings.AsScriptableObject() => instance;

        internal void Save()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            ((IEntitiesPlayerSettings)this).RegisterCustomDependency();
            Save(true);
            AssetDatabase.Refresh();
        }

#if UNITY_2023_2_OR_NEWER
        private void OnEnable()
        {
            if (!AssetDatabase.IsAssetImportWorkerProcess())
            {
                ((IEntitiesPlayerSettings)this).RegisterCustomDependency();
            }
        }
#endif
        private void OnDisable()
        {
#if !UNITY_2023_2_OR_NEWER
            Save();
#else
            //But the depedency is going to be update when the scriptable is re-enabled.
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            Save(true);
            //This safeguard is necessary because the RegisterCustomDependency throw exceptions
            //if this is called when the editor is refreshing the database.
            if(!EditorApplication.isUpdating)
            {
                ((IEntitiesPlayerSettings)this).RegisterCustomDependency();
                AssetDatabase.Refresh();
            }
#endif
        }
    }

    /// <summary>
    /// Editor-only helper - allows you to configure the value-specific suggested ranges on the <see cref="GhostAuthoringComponent.Importance"/>
    /// tooltip.
    /// </summary>
    [Serializable]
    public struct EditorImportanceSuggestion
    {
        /// <summary>Loose minimum value.</summary>
        public float MinValue;
        /// <summary>Loose maximum value.</summary>
        public float MaxValue;
        /// <summary>Short, inline name for this importance category/range.</summary>
        public string Name;
        /// <summary>Single-line example for when you'd want to use this.</summary>
        public string Tooltip;
        /// <summary>Helper.</summary>
        /// <returns>Formatted string.</returns>
        public override string ToString() => $"{MinValue} ~ {MaxValue} for {Name} - {Tooltip}";
    }
}
#endif

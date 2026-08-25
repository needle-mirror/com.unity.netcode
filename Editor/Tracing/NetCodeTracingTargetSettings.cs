using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
#if UNITY_USE_SETTINGS_MANAGER
using UnityEditor.SettingsManagement;
#endif
namespace Unity.NetCode.Editor.Tracing
{
    /// <summary>
    /// Package-scoped storage for ECS tracing target selection (systems and components).
    /// With <c>com.unity.settings-manager</c>, values are stored under
    /// <c>ProjectSettings/Packages/com.unity.netcode/Tracing.json</c>. Without it, the same selection is stored in
    /// <c>ProjectSettings/Packages/com.unity.netcode/TracingTargetSelection.json</c>.
    /// </summary>
    static class NetCodeTracingTargetSettings
    {
        internal const string k_PackageId = "com.unity.netcode";
        const string k_SettingsFileName = "Tracing";
        const string k_SelectionKey = "tracing-target-selection";
        const string k_FallbackSelectionFileName = "TracingTargetSelection.json";
        internal static Action OnSelectionChanged;

#if UNITY_USE_SETTINGS_MANAGER
        static readonly Settings s_Settings = new Settings(k_PackageId, k_SettingsFileName);
        static readonly UserSetting<TracingTargetSelectionFile> s_Selection =
            new UserSetting<TracingTargetSelectionFile>(s_Settings, k_SelectionKey, new TracingTargetSelectionFile(),
                SettingsScope.Project);
#endif

        internal static TracingTargetSelectionFile GetSelection()
        {
#if UNITY_USE_SETTINGS_MANAGER
            return s_Selection.value;
#else
            return LoadSelectionFallback();
#endif
        }

        internal static void SaveSelection(TracingTargetSelectionFile data)
        {
#if UNITY_USE_SETTINGS_MANAGER
            s_Selection.SetValue(data ?? new TracingTargetSelectionFile(), saveProjectSettingsImmediately: true);
#else
            SaveSelectionFallback(data);
#endif
            OnSelectionChanged?.Invoke();
        }

#if !UNITY_USE_SETTINGS_MANAGER
        static string GetFallbackSelectionPath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProjectSettings", "Packages", k_PackageId,
                k_FallbackSelectionFileName));

        static TracingTargetSelectionFile LoadSelectionFallback()
        {
            try
            {
                var path = GetFallbackSelectionPath();
                if (!File.Exists(path))
                    return new TracingTargetSelectionFile();

                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new TracingTargetSelectionFile();

                var result = JsonUtility.FromJson<TracingTargetSelectionFile>(json);
                if (result == null)
                    return new TracingTargetSelectionFile();
                result.entries ??= new List<TracingTargetSelectionEntry>();
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"NetCode tracing targets: could not load saved selection. {ex.Message}");
                return new TracingTargetSelectionFile();
            }
        }

        static void SaveSelectionFallback(TracingTargetSelectionFile data)
        {
            data ??= new TracingTargetSelectionFile();
            data.entries ??= new List<TracingTargetSelectionEntry>();

            try
            {
                var path = GetFallbackSelectionPath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(path, JsonUtility.ToJson(data, prettyPrint: true));
            }
            catch (Exception ex)
            {
                Debug.LogError($"NetCode tracing targets: could not save selection. {ex.Message}");
            }
        }
#endif
    }

    [Serializable]
    internal class TracingTargetSelectionFile
    {
        public List<TracingTargetSelectionEntry> entries = new();
    }

    [Serializable]
    internal class TracingTargetSelectionEntry
    {
        public string assemblyQualifiedName;
        public int kind;
        /// <summary>
        /// For component targets only; <c>false</c> means tracing filter "Optional", <c>true</c> means "Required". Ignored for systems.
        /// </summary>
        public bool required;
    }
}

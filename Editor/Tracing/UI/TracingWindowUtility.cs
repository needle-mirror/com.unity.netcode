using System;
using System.IO;
using System.Text.RegularExpressions;
using Unity.Entities;
using Unity.NetCode.Tracing;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI
{
    internal class TracingWindowUtility
    {
        // Opens the source declaring the type (system or component); the fallback regex matches class and struct.
        internal static void OpenScriptInIde(Type systemType)
        {
            if (systemType == null)
            {
                UnityEngine.Debug.LogWarning("Could not open script: Type is null.");
                return;
            }

            // Generic types' Type.Name includes a backtick arity suffix (e.g. "MySystem`1") that
            // never appears in source. Strip it so AssetDatabase / regex search find the file.
            var searchName = StripGenericArity(systemType.Name);

            // 1. Try the fast approach first (works if the file name matches the type name).
            // MonoScript.GetClass returns the open generic definition for generic types, so a closed
            // generic systemType won't equal it directly — compare against the generic type definition too.
            var guids = AssetDatabase.FindAssets($"t:MonoScript {searchName}");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null)
                    continue;
                var scriptClass = script.GetClass();
                if (scriptClass == null)
                    continue;
                if (scriptClass == systemType
                    || (systemType.IsGenericType && systemType.GetGenericTypeDefinition() == scriptClass))
                {
                    AssetDatabase.OpenAsset(script);
                    return;
                }
            }

            // 2. Fallback: the system is likely a secondary type inside another file.
            var assemblyName = systemType.Assembly.GetName().Name;
            var regex = new Regex($@"\b(?:class|struct)\s+{Regex.Escape(searchName)}\b");

            foreach (var unityAsm in CompilationPipeline.GetAssemblies())
            {
                if (unityAsm.name != assemblyName)
                    continue;

                foreach (var sourceFilePath in unityAsm.sourceFiles)
                {
                    string text;
                    try
                    {
                        text = File.ReadAllText(sourceFilePath);
                    }
                    catch (IOException)
                    {
                        // File temporarily locked or otherwise unreadable — skip it rather than
                        // aborting the whole scan.
                        continue;
                    }
                    // Cheap pre-filter: skip files that don't mention the system name at all
                    // before paying the regex cost.
                    if (!text.Contains(searchName))
                        continue;
                    var match = regex.Match(text);
                    if (!match.Success)
                        continue;

                    var scriptAsset = AssetDatabase.LoadAssetAtPath<MonoScript>(sourceFilePath);
                    if (scriptAsset == null)
                        continue;

                    AssetDatabase.OpenAsset(scriptAsset, CountLines(text, match.Index));
                    return;
                }
            }

            UnityEngine.Debug.LogWarning($"Could not find the script file containing type: {systemType.FullName}");
        }

        // Best-effort jump to a ghost's authoring source: there's no public Baker<T> lookup, so resolve the
        // prefab and open one of its authoring MonoBehaviour scripts (preferring user code over framework).
        internal static void OpenGhostAuthoringScriptInIde(string ghostName)
        {
            if (string.IsNullOrEmpty(ghostName))
            {
                UnityEngine.Debug.LogWarning("Could not open ghost authoring script: ghost name is empty.");
                return;
            }

            var prefab = FindGhostPrefab(ghostName);
            if (prefab == null)
            {
                UnityEngine.Debug.LogWarning($"Could not find a ghost prefab named '{ghostName}' to open its authoring script.");
                return;
            }

            // Prefer a non-Unity.* MonoBehaviour so we land on game code, not a framework authoring component.
            var behaviours = prefab.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true);
            UnityEngine.MonoBehaviour fallback = null;
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null)
                    continue;
                fallback ??= behaviour;
                var ns = behaviour.GetType().Namespace;
                if (ns == null || !ns.StartsWith("Unity.", StringComparison.Ordinal))
                {
                    var userScript = UnityEditor.MonoScript.FromMonoBehaviour(behaviour);
                    if (userScript != null)
                    {
                        AssetDatabase.OpenAsset(userScript);
                        return;
                    }
                }
            }

            if (fallback != null)
            {
                var script = UnityEditor.MonoScript.FromMonoBehaviour(fallback);
                if (script != null)
                {
                    AssetDatabase.OpenAsset(script);
                    return;
                }
            }

            // Last resort: surface the prefab itself so the click still does something useful.
            UnityEditor.EditorGUIUtility.PingObject(prefab);
        }

        static UnityEngine.GameObject FindGhostPrefab(string ghostName)
        {
            var guids = AssetDatabase.FindAssets($"{ghostName} t:Prefab");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                // Reject by file name before loading — FindAssets substring-matches and can return many prefabs.
                if (System.IO.Path.GetFileNameWithoutExtension(path) != ghostName)
                    continue;
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(path);
                if (asset != null && asset.name == ghostName)
                    return asset;
            }
            return null;
        }

        static string StripGenericArity(string name)
        {
            var backtick = name.IndexOf('`');
            return backtick >= 0 ? name.Substring(0, backtick) : name;
        }

        static int CountLines(string text, int upTo)
        {
            var line = 1;
            for (var i = 0; i < upTo; i++)
            {
                if (text[i] == '\n')
                    line++;
            }
            return line;
        }

        /// <summary>
        /// Sets the types to trace in the TracingDataAccess.Config.Data based on the selection in NetCodeTracingTargetSettings.
        /// </summary>
        internal static void SetTypesToTrace()
        {
            var list = NetCodeTracingTargetSettings.GetSelection()?.entries;
            if (list == null)
            {
                return;
            }

            if (TracingDataAccess.Config.Data.Initialized)
            {
                TracingDataAccess.Config.Data.ResetTracingTargets();
            }

            foreach (var item in list)
            {
                var type = Type.GetType(item.assemblyQualifiedName);

                if (type == null)
                {
                    Debug.Log($"Unknown assembly qualified name '{item.assemblyQualifiedName}'");
                    continue;
                }

                // The backend already traces and diffs GhostInstance on every ghost, don't re-add it
                if (type == typeof(GhostInstance))
                    continue;

                if (item.kind == (int)TracingTargetKind.System)
                {
                    TracingDataAccess.Config.Data.AddSystemTypeToTrace(TypeManager.GetSystemTypeIndex(type));
                }
                else if (item.required)
                {
                    TracingDataAccess.Config.Data.AddRequiredTypeToTrace(GetComponentTypeFromEntry(item));
                }
                else
                {
                    TracingDataAccess.Config.Data.AddOptionalTypeToTrace(GetComponentTypeFromEntry(item));
                }
            }

            if (list.Count == 0)
            {
                TracingDataAccess.OnTypeTracesUpdated?.Invoke();
            }
        }

        internal static bool IsGhostInstanceSelected()
        {
            var list = NetCodeTracingTargetSettings.GetSelection()?.entries;
            if (list == null)
                return false;
            foreach (var item in list)
            {
                if (item.kind != (int)TracingTargetKind.System
                    && Type.GetType(item.assemblyQualifiedName) == typeof(GhostInstance))
                    return true;
            }
            return false;
        }

        static ComponentType GetComponentTypeFromEntry(TracingTargetSelectionEntry entry)
        {
            var type = Type.GetType(entry.assemblyQualifiedName);
            if (type == null)
                throw new System.Exception($"Could not load type: {entry.assemblyQualifiedName}");

            return ComponentType.ReadWrite(type);
        }

        public static void SetHidden(VisualElement element, bool shouldHide)
        {
            element?.EnableInClassList(TracingWindowUssClasses.Hidden, shouldHide);
        }
    }
}

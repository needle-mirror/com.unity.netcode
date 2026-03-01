#if !UNITY_DISABLE_MANAGED_COMPONENTS
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Assertions;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using System.IO;
using UnityEditor.Build.Reporting;
using System.Linq;
using UnityEditor.Compilation;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
#endif


namespace Unity.NetCode
{
    /// <summary>
    /// Asset used to store the script execution order for all the GhostBehaviour. The asset is necessary
    /// because at runtime this information is not available (even though the player know it, it is not exposed).
    /// The asset is also used in the Editor, where the value of the update order is cached when entering
    /// play-mode.
    /// Consequently, changing the Script Execution Order in the ProjectSettings or via MonoImporter.SetExecutionOrder
    /// will be not immediately reflected at runtime in the Editor. You need to exit and re-entering play mode in order
    /// to see the value updated.
    /// </summary>
    internal class GhostBehaviourSortOrder : ScriptableObject
    {
        [HideInInspector][SerializeField]private GhostBehaviourTypeInfo[] m_BehaviourSortOrders;
        [HideInInspector][SerializeField]private int m_NumScriptSortOrder;

        /// <summary>
        /// The total number of unique script sort order .
        /// </summary>
        public int NumScriptSortOrder => m_NumScriptSortOrder;
        public GhostBehaviourTypeInfo[] Behaviours => m_BehaviourSortOrders;

#if UNITY_EDITOR
        private static ProfilerMarker s_IniProfiletMarker = new ProfilerMarker("Netcode-InitializeSortOrderFromScriptSortOrder");
#endif
        //This must be loaded before scene load, otherwise the runtime stuff aren't ready yet. Netcode initialize this lazily at
        //runtime in player build
        [Conditional("UNITY_EDITOR")]
        public void InitializeSortOrderFromScriptSortOrder(bool withExportLog=false, bool withoutTestAssemblies=false)
        {
#if UNITY_EDITOR
            using var marker = s_IniProfiletMarker.Auto();
            var buckets = new NativeHashSet<int>(256, Allocator.Temp);
            var ghostBehaviourTypes = new Dictionary<Type, GhostBehaviourTypeInfo>(256);
            var builder = new StringBuilder();
            if(withExportLog)
                builder.Append("GhostBehaviourSortOrder exported types:\n");
            //The DefaultExecutionOrder don't show in the ScriptOrderExecution inspector.
            //If you manually set the order via MonoImporter.SetExecutionOrder it will instead show up
            //If you manually add the class using the inspector, it does not get the default value you put but
            //one depending on the position.
            //So the correct way to handle this is :
            // - Check current order using the MonoImporter
            // - if 0, check for the custom attribute
            var assemblies = new HashSet<string>(CompilationPipeline
                .GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Select(a => a.name));
            foreach (var monoScript in MonoImporter.GetAllRuntimeMonoScripts())
            {
                var @class = monoScript.GetClass();
                if (@class == null || @class.IsAbstract || @class.IsGenericType || !@class.IsSubclassOf(typeof(GhostBehaviour)))
                    continue;
                if (withoutTestAssemblies && !assemblies.Contains(@class.Assembly.GetName().Name))
                    continue;

                var hash = Netcode.Instance.GhostBehaviourTypeManager.GetGhostBehaviourStableTypeHash(@class);
                var sortOrder = MonoImporter.GetExecutionOrder(monoScript);
                if (sortOrder == 0)
                {
                    var defaultExecutionOrder = @class.GetCustomAttribute<DefaultExecutionOrder>();
                    if (defaultExecutionOrder != null)
                        sortOrder = defaultExecutionOrder.order;
                }
                if(withExportLog)
                    builder.Append($"mono-script type:{@class.FullName} hash: {hash} sortOrder: {sortOrder}\n");
                buckets.Add(sortOrder);
                ghostBehaviourTypes.Add(@class, new GhostBehaviourTypeInfo
                {
                    TypeHash = hash,
                    ScriptSortOrder = sortOrder,
                });
            }
            //Need to patch here because certain types are not added at runtime in the editor and for
            //some tests (i.e test types). Not great logic though but I didn't find reliable alternatives,
            //apart returning for this missing types the default sort order (0).
            var behaviourTypes = TypeCache.GetTypesDerivedFrom<GhostBehaviour>();
            foreach (var b in behaviourTypes)
            {
                if (withoutTestAssemblies && !assemblies.Contains(b.Assembly.GetName().Name))
                    continue;
                if (!b.IsAbstract && !b.IsGenericType && !ghostBehaviourTypes.ContainsKey(b))
                {
                    var defaultExecutionOrder = b.GetCustomAttribute<DefaultExecutionOrder>();
                    var sortOrder =  defaultExecutionOrder?.order ?? 0;
                    var hash = Netcode.Instance.GhostBehaviourTypeManager.GetGhostBehaviourStableTypeHash(b);
                    if(withExportLog)
                        builder.Append($"patching missing mono-script type:{b.FullName} hash: {hash} sortOrder: {sortOrder}\n");
                    buckets.Add(sortOrder);
                    ghostBehaviourTypes.Add(b, new GhostBehaviourTypeInfo
                    {
                        TypeHash = hash,
                        ScriptSortOrder = sortOrder,
                    });
                }
            }
            var bucketIndices = buckets.ToNativeArray(Allocator.Temp);
            bucketIndices.Sort();

            if(withExportLog)
                builder.Append("GhostBehaviourSortOrder types and buckets:\n");
            m_BehaviourSortOrders = new GhostBehaviourTypeInfo[ghostBehaviourTypes.Count];
            m_NumScriptSortOrder = bucketIndices.Length;
            int index = 0;
            foreach(var kv in ghostBehaviourTypes)
            {
                var sortOrderIndex = bucketIndices.BinarySearch(kv.Value.ScriptSortOrder);
                var sortOrder = kv.Value;
                Assert.IsFalse(sortOrderIndex < 0);

                bool HasMethod(string name, Type[] methodParameterTypes, bool includeNonPublic = false)
                {
                    var bindingFilter = BindingFlags.Instance | BindingFlags.Public;
                    if (includeNonPublic)
                        bindingFilter |= BindingFlags.NonPublic;
                    var method = kv.Key.GetMethod(name, bindingFilter,
                        null,
                        CallingConventions.Any,
                        methodParameterTypes, null);
                    return method != null && (method.DeclaringType != typeof(GhostBehaviour));
                }

                // Optim note: It'd be better to check that the user declared method is empty, but for now, checking if the method still has the parent empty implementation is good enough for most cases.
                {
                    sortOrder.HasPredictionUpdate = HasMethod(nameof(GhostBehaviour.PredictionUpdate), new Type[] { typeof(float) });
                }
                {
                    sortOrder.HasInputUpdate = HasMethod(nameof(GhostBehaviour.GatherInput), new Type[] { typeof(float) });
                }
                {
                    sortOrder.HasNetworkedFixedUpdate = HasMethod("TODO_NetworkedFixedUpdate", new Type[] { typeof(float) });
                }
                {
                    sortOrder.HasStart = HasMethod("Start", new Type[] { }, includeNonPublic: true);
                }

                sortOrder.UpdateBucket = (short)sortOrderIndex;
                m_BehaviourSortOrders[index] = sortOrder;
                ++index;
                if(withExportLog)
                    builder.Append($"type: {kv.Key.FullName} type:{sortOrder.ToString()}\n");
            }

            EditorUtility.SetDirty(this);
            if(withExportLog)
                Debug.Log(builder.ToString());
#endif
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Build Processor to include script order metadata in builds and make it a "preloaded asset" so it loads automatically.
    /// </summary>
    internal class GhostBehaviourBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        /// <summary>
        /// Run as almost one of the last exporter.
        /// </summary>
        public int callbackOrder => 1000;

        static bool IsPlayerSettingsDirty()
        {
            var settings = Resources.FindObjectsOfTypeAll<PlayerSettings>();
            if (settings != null && settings.Length > 0)
                return EditorUtility.IsDirty(settings[0]);
            return false;
        }

        static void ClearPlayerSettingsDirtyFlag()
        {
            var settings = Resources.FindObjectsOfTypeAll<PlayerSettings>();
            if (settings != null && settings.Length > 0)
                EditorUtility.ClearDirty(settings[0]);
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.buildType == BuildType.AssetBundle)
                return;

            GhostBehaviourSortOrder sortOrder;
            var withoutTestAssemblies = !report.summary.options.HasFlag(BuildOptions.IncludeTestAssemblies);
            if (!AssetDatabase.AssetPathExists(GhostBehaviourTypeManager.m_AssetPath))
            {
                if (!AssetDatabase.IsValidFolder($"Assets/{Netcode.kTempBuildFolder}"))
                {
                    AssetDatabase.CreateFolder("Assets", Netcode.kTempBuildFolder);
                }
                sortOrder = ScriptableObject.CreateInstance<GhostBehaviourSortOrder>();
                sortOrder.InitializeSortOrderFromScriptSortOrder(withExportLog:true, withoutTestAssemblies:withoutTestAssemblies);
                AssetDatabase.CreateAsset(sortOrder, GhostBehaviourTypeManager.m_AssetPath);
            }
            else
            {
                sortOrder = AssetDatabase.LoadAssetAtPath<GhostBehaviourSortOrder>(GhostBehaviourTypeManager.m_AssetPath);
                sortOrder.InitializeSortOrderFromScriptSortOrder(withExportLog:true, withoutTestAssemblies:withoutTestAssemblies);
                AssetDatabase.SaveAssetIfDirty(sortOrder);
            }

            AssetDatabase.Refresh();

            var preloadedAssets = PlayerSettings.GetPreloadedAssets();
            bool wasDirty = IsPlayerSettingsDirty();

            if (sortOrder != null && ArrayUtility.IndexOf(preloadedAssets, sortOrder) < 0)
            {
                ArrayUtility.Add(ref preloadedAssets, sortOrder);
            }

            PlayerSettings.SetPreloadedAssets(preloadedAssets);
            // Clear the dirty flag so we dont flush the modified file (case 1254502)
            if (!wasDirty)
                ClearPlayerSettingsDirtyFlag();
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            AssetDatabase.DeleteAsset(GhostBehaviourTypeManager.m_AssetPath);
            var preloadedAssets = new List<UnityEngine.Object>(UnityEditor.PlayerSettings.GetPreloadedAssets());
            preloadedAssets.RemoveAll(x => x is GhostBehaviourSortOrder);
            preloadedAssets.RemoveAll(x => x is NetCodeConfig);
            bool wasDirty = IsPlayerSettingsDirty();
            PlayerSettings.SetPreloadedAssets(preloadedAssets.ToArray());
            FileUtil.DeleteFileOrDirectory($"Assets/{Netcode.kTempBuildFolder}");
            FileUtil.DeleteFileOrDirectory($"Assets/{Netcode.kTempBuildFolder}.meta");

            // Clear the dirty flag so we dont flush the modified file (case 1254502)
            if (!wasDirty)
                ClearPlayerSettingsDirtyFlag();
        }
    }
#endif
}
#endif
#endif

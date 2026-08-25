using System;
using Unity.Entities;
using Unity.Entities.Editor;
using Unity.NetCode.LowLevel.StateSave;
using UnityEditor;

namespace Unity.NetCode.Editor
{
    [InitializeOnLoad]
    static class NetcodeEditorUtility
    {
        static readonly Type k_InspectorWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow");

        // Persisted across button clicks so the SystemProxy we hand to the Inspector keeps receiving
        // live data via its WorldProxyUpdater. Disposing this on every call (the previous `using` block)
        // tore down the updater immediately, freezing the inspector and poisoning SystemScheduleWindow's
        // global selection with a dead proxy.
        static WorldProxyManager s_WorldProxyManager;

        static NetcodeEditorUtility()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DisposeWorldProxyManager;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Drop the manager around both Play mode boundaries so the next session always starts with
            // fresh proxies. ExitingEditMode matters when "Disable Domain Reload" is enabled — without
            // it, static fields persist across the Edit→Play transition and would leak Edit-mode
            // world proxies into the new Play session.
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.ExitingEditMode)
                DisposeWorldProxyManager();
        }

        static void DisposeWorldProxyManager()
        {
            s_WorldProxyManager?.Dispose();
            s_WorldProxyManager = null;
        }

        static WorldProxyManager GetOrCreateWorldProxyManager()
        {
            s_WorldProxyManager ??= new WorldProxyManager();
            // CreateWorldProxiesForAllWorlds is idempotent — it skips worlds already known and prunes dead ones.
            s_WorldProxyManager.CreateWorldProxiesForAllWorlds();
            return s_WorldProxyManager;
        }

        internal static void ShowGhostComponentInspectorContent(Type type)
        {
            ContentUtilities.ShowComponentInspectorContent(type);
            FocusInspectorWindow();
        }

        // Selects the live ghost entity in Play mode (via SpawnedGhostEntityMap); otherwise pings the prefab.
        internal static void SelectGhost(SavedEntityID ghost, string ghostName, bool fromClientWorld)
        {
            if (EditorApplication.isPlaying && TrySelectLiveGhostEntity(ghost, fromClientWorld))
            {
                FocusInspectorWindow();
                return;
            }

            if (PingGhostPrefab(ghostName))
                FocusInspectorWindow();
        }

        static bool PingGhostPrefab(string ghostName)
        {
            if (string.IsNullOrEmpty(ghostName))
                return false;

            // Ping/select the ghost prefab in the Project window by name.
            var guids = AssetDatabase.FindAssets(ghostName);
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                // Reject by file name before loading — FindAssets substring-matches and can return many assets.
                if (System.IO.Path.GetFileNameWithoutExtension(path) != ghostName)
                    continue;
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(path);
                if (asset != null && asset.name == ghostName)
                {
                    EditorGUIUtility.PingObject(asset);
                    Selection.activeObject = asset;
                    return true;
                }
            }

            return false;
        }

        static bool TrySelectLiveGhostEntity(SavedEntityID ghost, bool fromClientWorld)
        {
            foreach (var world in World.All)
            {
                // Match the world the row came from (the inspector shows client data).
                if (fromClientWorld ? !world.IsClient() : !world.IsServer())
                    continue;

                using var query = world.EntityManager.CreateEntityQuery(typeof(SpawnedGhostEntityMap));
                if (!query.HasSingleton<SpawnedGhostEntityMap>())
                    continue;

                var map = query.GetSingleton<SpawnedGhostEntityMap>();
                if (!map.Value.ContainsKey(ghost.value))
                    continue;

                EntitySelectionProxy.SelectEntity(world, map.Value[ghost.value]);
                return true;
            }

            return false;
        }

        internal static void ShowSystemInspectorContent(Type systemType, bool fromClientWorld)
        {
            if (systemType == null)
                return;

            // ScheduledSystemData.FullName is built from Type.FullName, so compare against systemType.FullName
            // (reflection format) to avoid Cecil-vs-reflection mismatches that bite generics.
            var systemFullName = systemType.FullName;

            var worldProxyManager = GetOrCreateWorldProxyManager();

            foreach (var world in World.All)
            {
                // The tick inspector currently only displays client-world data, so only opening client-world
                // systems makes sense. Same system type usually exists in both worlds, so picking the first
                // match without this filter would frequently land on the wrong world (typically Server).
                if (fromClientWorld ? !world.IsClient() : !world.IsServer())
                    continue;

                if (!worldProxyManager.TryGetWorldProxy(world, out var worldProxy))
                    continue;

                for (var i = 0; i < worldProxy.AllSystemData.Count; i++)
                {
                    if (worldProxy.AllSystemData[i].FullName != systemFullName)
                        continue;

                    var systemProxy = worldProxy.AllSystems[i];
                    SystemScheduleWindow.HighlightSystem(systemProxy);
                    ContentUtilities.ShowSystemInspectorContent(systemProxy);
                    FocusInspectorWindow();
                    return;
                }
            }
        }

        static void FocusInspectorWindow()
        {
            if (k_InspectorWindowType == null)
                return;

            // GetWindow won't reliably re-focus an already-open docked tab, so Focus() explicitly.
            var window = EditorWindow.GetWindow(k_InspectorWindowType);
            if (window != null)
                window.Focus();
        }
    }
}

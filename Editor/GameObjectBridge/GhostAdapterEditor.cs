#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID

using UnityEditor;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    /*
     GhostAuthoring is meant as a baking interface. You setup stuff and then it's baked. It's not meant to exist at runtime
     Reusing it for GhostAdapter would be diverging its main use.
     We DO want a common set of editor time GUI scripts to edit a ghost and configure it. But even then, some configurations wouldn't apply for runtime GameObjects.
     Having separate Monobehaviours for the two seem to make sense.
     Plus it allows more flexibility if we want to have a special GUI for the GO, it could be for the whole GO.
     */

    [CustomEditor(typeof(GhostAdapter))]
    [CanEditMultipleObjects]
    class GhostAdapterEditor : BaseGhostAuthoringComponentEditor<GhostAdapterEditor, GhostAdapter>
    {

        [InitializeOnLoadMethod]
        private static void Init()
        {
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
            UnityEditor.Editor.finishedDefaultHeaderGUI -= OnGameObjectHeader;
            UnityEditor.Editor.finishedDefaultHeaderGUI += OnGameObjectHeader;
#endif
        }

        static void OnGameObjectHeader(UnityEditor.Editor editor)
        {
            if (!(editor.target is GameObject target))
                return;

            // WIP notes:
            // To determine whether we're in the prefab view (prefab stage, happens when you click the prefab to edit it) or in the scene view, we can use the following
            // var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(target);
            // if (prefabStage != null && prefabStage.prefabContentsRoot == target)
            //     return;
            // And to know if this is a prefab or not, we can use the following
            // if (PrefabUtility.IsPartOfPrefabAsset(target))
            //     return;
            var ghostAdapter = target.GetComponent<GhostAdapter>();
            bool isNetworked = ghostAdapter != null;
            GUILayout.BeginHorizontal();
            GUILayout.Label(LegacyHierarchyDrawer.GhostIcon, GUILayout.MaxHeight(16));
            isNetworked = GUILayout.Toggle(isNetworked, "Network");
            GUILayout.EndHorizontal();
            if (isNetworked && ghostAdapter == null)
                target.AddComponent<GhostAdapter>();
            else if (!isNetworked && ghostAdapter != null)
                DestroyImmediate(ghostAdapter, allowDestroyingAssets: true);

            if (isNetworked)
            {
                // ghostAdapter.hideFlags |= HideFlags.HideInInspector; // TODO-release can hide the actual component and have the UI in the GO header directly
                // TODO-release we can expand on this here and add a nice UI for configuring your GameObject's replication. We should make sure to allow multi editing.
            }

            // TODO-release, move GhostAdapter's configuration UI out of the per monobehaviour logic and into the header, as a dropdown
        }
    }
}
#endif

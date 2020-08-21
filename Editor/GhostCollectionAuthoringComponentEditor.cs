using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    [CustomEditor(typeof(GhostCollectionAuthoringComponent))]
    public class GhostCollectionAuthoringComponentEditor : UnityEditor.Editor
    {
        private UnityEditorInternal.ReorderableList m_GhostList;

        void OnEnable()
        {
            var collectionTarget = target as GhostCollectionAuthoringComponent;
            m_GhostList =
                new ReorderableList(collectionTarget.Ghosts, typeof(GhostCollectionAuthoringComponent.Ghost), true,
                    true, true, true);
            m_GhostList.drawHeaderCallback += DrawHeader;
            m_GhostList.drawElementCallback += DrawGhost;
            m_GhostList.onAddCallback += AddGhost;
            m_GhostList.onRemoveCallback += RemoveGhost;
        }

        void OnDisable()
        {
            m_GhostList.drawHeaderCallback -= DrawHeader;
            m_GhostList.drawElementCallback -= DrawGhost;
            m_GhostList.onAddCallback -= AddGhost;
            m_GhostList.onRemoveCallback -= RemoveGhost;
        }

        void DrawHeader(Rect rect)
        {
            EditorGUI.LabelField(rect, "Ghosts");
        }

        void DrawGhost(Rect rect, int index, bool isActive, bool isFocused)
        {
            var collectionTarget = target as GhostCollectionAuthoringComponent;
            var ghost = collectionTarget.Ghosts[index];
            EditorGUI.BeginChangeCheck();
            rect.width -= rect.height + 3;
            ghost.prefab =
                EditorGUI.ObjectField(rect, ghost.prefab, typeof(GhostAuthoringComponent), true) as
                    GhostAuthoringComponent;
            rect.x += rect.width + 3;
            rect.width = rect.height;
            ghost.enabled = EditorGUI.Toggle(rect, ghost.enabled);
            collectionTarget.Ghosts[index] = ghost;
            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(target);
        }

        void AddGhost(ReorderableList list)
        {
            var collectionTarget = target as GhostCollectionAuthoringComponent;
            collectionTarget.Ghosts.Add(new GhostCollectionAuthoringComponent.Ghost {enabled = true});
            EditorUtility.SetDirty(target);
        }

        void RemoveGhost(ReorderableList list)
        {
            var collectionTarget = target as GhostCollectionAuthoringComponent;
            collectionTarget.Ghosts.RemoveAt(list.index);
            EditorUtility.SetDirty(target);
        }

        public override void OnInspectorGUI()
        {
            m_GhostList.DoLayoutList();

            if (GUILayout.Button("Update ghost list"))
            {
                AddAllNewGhosts(target as GhostCollectionAuthoringComponent);
            }
        }

        public static void AddAllNewGhosts(GhostCollectionAuthoringComponent collectionTarget)
        {
            var list = collectionTarget.Ghosts;
            var alreadyAdded = new HashSet<GhostAuthoringComponent>();
            bool hasEmpty = false;
            foreach (var ghost in list)
            {
                if (ghost.prefab != null)
                    alreadyAdded.Add(ghost.prefab);
                else
                    hasEmpty = true;
            }

            if (hasEmpty)
            {
                for (int i = 0; i < list.Count; ++i)
                {
                    if (list[i].prefab == null)
                    {
                        list.RemoveAt(i);
                        --i;
                        EditorUtility.SetDirty(collectionTarget);
                    }
                }
            }

            var prefabGuids = AssetDatabase.FindAssets("t:" + typeof(GameObject).Name);
            foreach (var guid in prefabGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var ghost = go.GetComponent<GhostAuthoringComponent>();
                if (ghost != null && !alreadyAdded.Contains(ghost))
                {
                    list.Add(new GhostCollectionAuthoringComponent.Ghost {prefab = ghost, enabled = true});
                    EditorUtility.SetDirty(collectionTarget);
                }
            }
        }
    }
}

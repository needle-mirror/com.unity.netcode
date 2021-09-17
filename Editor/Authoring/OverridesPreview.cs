using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    static class OverridesPreview
    {
        static public void OnInspectorGUI(GhostAuthoringComponent authoringComponent, GhostComponentVariantLookup variantLookup)
        {
            foreach (var prefabOverride in authoringComponent.ComponentOverrides)
            {
                var fullTypeName = prefabOverride.fullTypeName;
                prefabOverride.isExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(prefabOverride.isExpanded, new GUIContent(fullTypeName));
                var rect = GUILayoutUtility.GetLastRect();
                if (prefabOverride.isExpanded)
                {
                    // We don't support more that 1 level of object hierarchy so this check is sufficient to retrieve the
                    // entity / child index.
                    var transform = prefabOverride.gameObject.transform;
                    var childIndex = transform.parent == null ? 0 : transform.GetSiblingIndex() + 1;
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.LabelField("Entity Index", childIndex.ToString());
                    EditorGUILayout.ObjectField("gameObject",prefabOverride.gameObject, typeof(GameObject), true);
                    if(prefabOverride.PrefabType != GhostAuthoringComponent.ComponentOverride.UseDefaultValue)
                        EditorGUILayout.LabelField("PrefabType", ((GhostPrefabType)prefabOverride.PrefabType).ToString());
                    if(prefabOverride.OwnerPredictedSendType != GhostAuthoringComponent.ComponentOverride.UseDefaultValue)
                        EditorGUILayout.LabelField("OwnerPredictedSendType", ((GhostSendType)prefabOverride.OwnerPredictedSendType).ToString());
                    if (prefabOverride.ComponentVariant != 0)
                    {
                        var variantName = variantLookup.GetVariantName(fullTypeName, prefabOverride.ComponentVariant);
                        EditorGUILayout.LabelField("Variant", variantName);
                    }
                    EditorGUI.EndDisabledGroup();
                }
                EditorGUILayout.EndFoldoutHeaderGroup();
                if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
                {
                    if (rect.Contains(Event.current.mousePosition))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Remove Modifier"), false, () =>
                        {
                            //Add modifier if not present
                            authoringComponent.RemovePrefabOverride(prefabOverride);
                            EditorUtility.SetDirty(authoringComponent.gameObject);
                        });
                        menu.ShowAsContext();
                    }
                }
            }
        }
    }
}

using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    [CustomEditor(typeof(GhostCollectionAuthoringComponent))]
    public class GhostCollectionAuthoringComponentEditor : UnityEditor.Editor
    {
        SerializedProperty SerializerCollectionPath;
        SerializedProperty DeserializerCollectionPath;
        SerializedProperty NamePrefix;
        SerializedProperty RootPath;
        private UnityEditorInternal.ReorderableList m_GhostList;

        void OnEnable()
        {
            SerializerCollectionPath = serializedObject.FindProperty("SerializerCollectionPath");
            DeserializerCollectionPath = serializedObject.FindProperty("DeserializerCollectionPath");
            NamePrefix = serializedObject.FindProperty("NamePrefix");
            RootPath = serializedObject.FindProperty("RootPath");

            if (NamePrefix.stringValue == "")
                NamePrefix.stringValue = Application.productName.Replace("-", "");
            serializedObject.ApplyModifiedProperties();

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
            EditorGUILayout.PropertyField(RootPath);
            EditorGUILayout.PropertyField(SerializerCollectionPath);
            EditorGUILayout.PropertyField(DeserializerCollectionPath);
            EditorGUILayout.PropertyField(NamePrefix);
            serializedObject.ApplyModifiedProperties();

            m_GhostList.DoLayoutList();

            if (GUILayout.Button("Update ghost list"))
            {
                AddAllNewGhosts(target as GhostCollectionAuthoringComponent);
            }

            if (GUILayout.Button("Regenerate all ghosts"))
            {
                RegenerateAllGhosts(target as GhostCollectionAuthoringComponent);
            }

            if (GUILayout.Button("Generate collection code"))
            {
                GenerateCollection(target as GhostCollectionAuthoringComponent);
            }
        }

        public static void RegenerateAllGhosts(GhostCollectionAuthoringComponent collectionTarget)
        {
            foreach (var ghost in collectionTarget.Ghosts)
            {
                if (ghost.prefab == null || !ghost.enabled)
                    continue;
                GhostAuthoringComponentEditor.SyncComponentList(ghost.prefab);
                GhostAuthoringComponentEditor.GenerateGhost(ghost.prefab);
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

        public static GhostCodeGen.Status GenerateCollection(GhostCollectionAuthoringComponent collectionTarget, bool testOnly = false)
        {
            var serializerCodeGen = new GhostCodeGen("Packages/com.unity.netcode/Editor/CodeGenTemplates/GhostSerializerCollection.cs");
            var deserializerCodeGen = new GhostCodeGen("Packages/com.unity.netcode/Editor/CodeGenTemplates/GhostDeserializerCollection.cs");

            var assetPath = GhostCodeGen.GetPrefabAssetPath(collectionTarget.gameObject);

            int ghostCount = 0;
            var namePrefix = collectionTarget.NamePrefix;

            var localReplacements = new Dictionary<string, string>();
            for (int i = 0; i < collectionTarget.Ghosts.Count; ++i)
            {
                var ghost = collectionTarget.Ghosts[i];
                if (ghost.prefab != null && ghost.enabled)
                {
                    ++ghostCount;
                    var serializerTypeName = ghost.prefab.Name + "GhostSerializer";
                    var snapshotTypeName = ghost.prefab.Name + "SnapshotData";
                    var spawnerTypeName = ghost.prefab.Name + "GhostSpawnSystem";

                    localReplacements.Clear();
                    localReplacements.Add("GHOST_SERIALIZER_TYPE", serializerTypeName);
                    localReplacements.Add("GHOST_SNAPSHOT_TYPE", snapshotTypeName);
                    localReplacements.Add("GHOST_SPAWNER_TYPE", spawnerTypeName);
                    localReplacements.Add("GHOST_SERIALIZER_INDEX", i.ToString());
                    localReplacements.Add("GHOST_COLLECTION_PREFIX", namePrefix);

                    serializerCodeGen.GenerateFragment("GHOST_SERIALIZER_INSTANCE", localReplacements);
                    deserializerCodeGen.GenerateFragment("GHOST_DESERIALIZER_INSTANCE", localReplacements);

                    serializerCodeGen.GenerateFragment("GHOST_SERIALIZER_NAME", localReplacements);
                    serializerCodeGen.GenerateFragment("GHOST_FIND_TYPE", localReplacements);
                    serializerCodeGen.GenerateFragment("GHOST_BEGIN_SERIALIZE", localReplacements);
                    serializerCodeGen.GenerateFragment("GHOST_CALCULATE_IMPORTANCE", localReplacements);
                    serializerCodeGen.GenerateFragment("GHOST_SNAPSHOT_SIZE", localReplacements);
                    serializerCodeGen.GenerateFragment("GHOST_INVOKE_SERIALIZE", localReplacements);

                    deserializerCodeGen.GenerateFragment("GHOST_SERIALIZER_NAME", localReplacements);
                    deserializerCodeGen.GenerateFragment("GHOST_INITIALIZE_DESERIALIZE", localReplacements);
                    deserializerCodeGen.GenerateFragment("GHOST_BEGIN_DESERIALIZE", localReplacements);
                    deserializerCodeGen.GenerateFragment("GHOST_INVOKE_DESERIALIZE", localReplacements);
                    deserializerCodeGen.GenerateFragment("GHOST_INVOKE_SPAWN", localReplacements);
                }
            }

            var replacements = new Dictionary<string, string>();
            replacements.Add("GHOST_COLLECTION_PREFIX", namePrefix);
            replacements.Add("GHOST_SYSTEM_PREFIX", namePrefix);
            replacements.Add("GHOST_SERIALIZER_COUNT", ghostCount.ToString());
            var batch = new GhostCodeGen.Batch();
            serializerCodeGen.GenerateFile(assetPath, collectionTarget.RootPath, collectionTarget.SerializerCollectionPath, replacements, batch);
            deserializerCodeGen.GenerateFile(assetPath, collectionTarget.RootPath, collectionTarget.DeserializerCollectionPath, replacements, batch);
            var didWrite = batch.Flush(testOnly);
            AssetDatabase.Refresh();
            return didWrite ? GhostCodeGen.Status.Ok : GhostCodeGen.Status.NotModified;
        }
    }
}

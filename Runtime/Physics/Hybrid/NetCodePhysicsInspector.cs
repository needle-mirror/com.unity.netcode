#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    [CustomEditor(typeof(NetCodePhysicsConfig))]
    public sealed class NetCodePhysicsInspector : UnityEditor.Editor
    {
        private SerializedProperty EnableLagCompensation;
        private SerializedProperty ServerHistorySize;
        private SerializedProperty ClientHistorySize;
        private SerializedProperty ClientNonGhostWorldIndex;
        private SerializedProperty DeepCopyDynamicColliders;
        private SerializedProperty DeepCopyStaticColliders;
        private SerializedProperty PhysicGroupRunMode;
        private static readonly GUIContent s_LagCompensationTitle = new GUIContent("Lag Compensation", "Configure how the Lag Compensation ring buffers function.");
        private static readonly GUIContent s_PhysicsRunMode = new GUIContent("PhysicsGroup Run Mode");

        private void OnEnable()
        {
            EnableLagCompensation = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.EnableLagCompensation));
            ServerHistorySize = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.ServerHistorySize));
            ClientHistorySize = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.ClientHistorySize));
            ClientNonGhostWorldIndex = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.ClientNonGhostWorldIndex));
            DeepCopyDynamicColliders = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.DeepCopyDynamicColliders));
            DeepCopyStaticColliders = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.DeepCopyStaticColliders));
            PhysicGroupRunMode = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.PhysicGroupRunMode));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"), true);
            EditorGUILayout.PropertyField(EnableLagCompensation, s_LagCompensationTitle);
            EditorGUILayout.PropertyField(PhysicGroupRunMode, s_PhysicsRunMode);
            if (EnableLagCompensation.boolValue)
            {
                EditorGUI.indentLevel += 1;
                EditorGUILayout.PropertyField(ServerHistorySize);
                EditorGUILayout.PropertyField(ClientHistorySize);
                EditorGUILayout.PropertyField(DeepCopyDynamicColliders);
                EditorGUILayout.PropertyField(DeepCopyStaticColliders);
                EditorGUI.indentLevel -= 1;
            }

            EditorGUILayout.PropertyField(ClientNonGhostWorldIndex);

            if (serializedObject.hasModifiedProperties)
                serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif

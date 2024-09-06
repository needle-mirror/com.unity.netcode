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
        private SerializedProperty DeepCopyDynamicColliders;
        private SerializedProperty DeepCopyStaticColliders;
        private static readonly GUIContent s_LagCompensationTitle = new GUIContent("Lag Compensation", "Configure how the Lag Compensation ring buffers function.");

        private void OnEnable()
        {
            EnableLagCompensation = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.EnableLagCompensation));
            ServerHistorySize = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.ServerHistorySize));
            ClientHistorySize = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.ClientHistorySize));
            DeepCopyDynamicColliders = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.DeepCopyDynamicColliders));
            DeepCopyStaticColliders = serializedObject.FindProperty(nameof(NetCodePhysicsConfig.DeepCopyStaticColliders));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"), true);
            EditorGUILayout.PropertyField(EnableLagCompensation, s_LagCompensationTitle);

            if (EnableLagCompensation.boolValue)
            {
                EditorGUI.indentLevel += 1;
                EditorGUILayout.PropertyField(ServerHistorySize);
                EditorGUILayout.PropertyField(ClientHistorySize);
                EditorGUILayout.PropertyField(DeepCopyDynamicColliders);
                EditorGUILayout.PropertyField(DeepCopyStaticColliders);
                EditorGUI.indentLevel -= 1;
            }

            if (serializedObject.hasModifiedProperties)
                serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif

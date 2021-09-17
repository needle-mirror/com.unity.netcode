using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Unity.NetCode.Generators;

namespace Unity.NetCode.Editor
{
    [CustomEditor(typeof(GhostAuthoringComponent))]
    public class GhostAuthoringComponentEditor : UnityEditor.Editor
    {
        internal class ComponentField
        {
            public string name;
            public SmoothingAction smoothing = SmoothingAction.Clamp;
            public int quantization = -1;
        }

        internal class SerializedComponentData
        {
            public string name;
            public Type managedType;
            public GhostComponentAttribute attribute;
            public ComponentField[] fields;
        }
        SerializedProperty DefaultGhostMode;
        SerializedProperty SupportedGhostModes;
        SerializedProperty OptimizationMode;
        SerializedProperty HasOwner;
        SerializedProperty SupportAutoCommandTarget;
        SerializedProperty UsePreSerialization;
        SerializedProperty Importance;
        SerializedProperty Name;
        private GhostComponentInspector componentInspector;
        private GhostComponentVariantLookup variantLookup;
        static Color s_BrokenColor = new Color(1f, 0.56f, 0.54f);

        bool IsInvalidName() => string.IsNullOrWhiteSpace(Name.stringValue);

        static string ValidateGhostName(string ghostName) => ghostName.Replace(" ", string.Empty);

        void OnEnable()
        {
            DefaultGhostMode = serializedObject.FindProperty("DefaultGhostMode");
            SupportedGhostModes = serializedObject.FindProperty("SupportedGhostModes");
            OptimizationMode = serializedObject.FindProperty("OptimizationMode");
            HasOwner = serializedObject.FindProperty("HasOwner");
            SupportAutoCommandTarget = serializedObject.FindProperty("SupportAutoCommandTarget");
            UsePreSerialization = serializedObject.FindProperty("UsePreSerialization");
            Importance = serializedObject.FindProperty("Importance");
            Name = serializedObject.FindProperty("Name");
            variantLookup = new GhostComponentVariantLookup();
            componentInspector = new GhostComponentInspector((GhostAuthoringComponent)target, variantLookup);

            if (IsInvalidName())
            {
                Name.stringValue = ValidateGhostName(target.name);
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log($"'{target.name}' has a `GhostAuthoringComponent` with a null or empty `Name` property. It must be assigned, so was auto-assigned to '{Name.stringValue}'.");
            }
        }

        public override void OnInspectorGUI()
        {
            var authoringComponent = (GhostAuthoringComponent) target;
            if (serializedObject.UpdateIfRequiredOrScript())
            {
                componentInspector.Refresh();
            }

            var originalColor = GUI.color;

            EditorGUI.BeginDisabledGroup(PrefabUtility.IsPartOfNonAssetPrefabInstance(target));
            GUI.color = IsInvalidName() ? s_BrokenColor : originalColor;
            EditorGUILayout.PropertyField(Name);
            Name.stringValue = ValidateGhostName(Name.stringValue);
            GUI.color = originalColor;
            if(IsInvalidName()) EditorGUILayout.HelpBox($"Ghosts must have a `Name`. We recommend it matches the prefab name.", MessageType.Error);

            EditorGUILayout.PropertyField(Importance);
            EditorGUILayout.PropertyField(SupportedGhostModes);

            var self = (GhostAuthoringComponent)target;
            bool hasGhostOwnerAuthoring = self.gameObject.GetComponent<GhostOwnerComponentAuthoring>() != null;
            bool hasGhostOwner = hasGhostOwnerAuthoring || self.HasOwner;
            var isOwnerPredictedError = DefaultGhostMode.enumValueIndex == (int)GhostAuthoringComponent.GhostMode.OwnerPredicted && !hasGhostOwner;

            if (SupportedGhostModes.intValue == (int)GhostAuthoringComponent.GhostModeMask.All)
            {
                EditorGUILayout.PropertyField(DefaultGhostMode);
                // Selecting OwnerPredicted on a ghost without a GhostOwnerComponent will cause an exception during conversion - display an error for that case in the inspector
                if (isOwnerPredictedError)
                {
                    EditorGUILayout.HelpBox("Setting `Default Ghost Mode` to `Owner Predicted` requires the ghost to have a `Ghost Owner Component`. You must also ensure your code sets the `NetworkId` of that component correctly. The solutions are:", MessageType.Error);
                    GUI.color = s_BrokenColor;
                    if (GUILayout.Button("Enable `Has Owner` now (and ensure code hooks up `GhostOwnerComponent.NetworkId` myself)?")) HasOwner.boolValue = true;
                    if (GUILayout.Button("Set to `GhostMode.Interpolated`?")) DefaultGhostMode.enumValueIndex = (int)GhostAuthoringComponent.GhostMode.Interpolated;
                    if (GUILayout.Button("Set to `GhostMode.Predicted`?")) DefaultGhostMode.enumValueIndex = (int)GhostAuthoringComponent.GhostMode.Predicted;
                    GUI.color = originalColor;
                }
            }
            EditorGUILayout.PropertyField(OptimizationMode);
            if (hasGhostOwnerAuthoring)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Toggle("Has Owner", true);
                EditorGUI.EndDisabledGroup();
            }
            else
                EditorGUILayout.PropertyField(HasOwner);
            if (hasGhostOwner)
                EditorGUILayout.PropertyField(SupportAutoCommandTarget);
            EditorGUILayout.PropertyField(UsePreSerialization);
            EditorGUI.EndDisabledGroup();

            GUILayout.Label("Prefab Overrides", "box", GUILayout.ExpandWidth(true));
            OverridesPreview.OnInspectorGUI(authoringComponent, variantLookup);

            //Show components
            GUILayout.Label("Components", "box", GUILayout.ExpandWidth(true));
            componentInspector.OnInspectorGUI();
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.Separator();
            GUI.enabled = ! isOwnerPredictedError;
            GUI.color = isOwnerPredictedError ? s_BrokenColor : originalColor;
            if (GUILayout.Button(isOwnerPredictedError ? "Update component list is disabled due to errors" : "Update component list"))
            {
                componentInspector.UpdateComponentList();
            }
        }
    }
}

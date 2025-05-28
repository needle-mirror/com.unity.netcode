using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities.Conversion;
using Unity.Mathematics;
using Unity.NetCode.Hybrid;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    [CustomEditor(typeof(GhostAuthoringComponent))]
    [CanEditMultipleObjects]
    internal class GhostAuthoringComponentEditor : UnityEditor.Editor
    {
        SerializedProperty DefaultGhostMode;
        SerializedProperty SupportedGhostModes;
        SerializedProperty OptimizationMode;
        SerializedProperty HasOwner;
        SerializedProperty SupportAutoCommandTarget;
        SerializedProperty TrackInterpolationDelay;
        SerializedProperty GhostGroup;
        SerializedProperty UsePreSerialization;
        SerializedProperty Importance;
        SerializedProperty MaxSendRate;
        SerializedProperty PredictedSpawnedGhostRollbackToSpawnTick;
        SerializedProperty RollbackPredictionOnStructuralChanges;
        SerializedProperty UseSingleBaseline;

        internal static Color brokenColor = new Color(1f, 0.56f, 0.54f);
        internal static Color brokenColorUIToolkit = new Color(0.35f, 0.19f, 0.19f);
        internal static Color brokenColorUIToolkitText = new Color(0.9f, 0.64f, 0.61f);
        private static readonly GUILayoutOption s_HelperWidth = GUILayout.Width(180);

        /// <summary>Aligned with NetCode for GameObjects.</summary>
        public static Color netcodeColor => new Color(0.91f, 0.55f, 0.86f, 1f);

        void OnEnable()
        {
            DefaultGhostMode = serializedObject.FindProperty(nameof(GhostAuthoringComponent.DefaultGhostMode));
            SupportedGhostModes = serializedObject.FindProperty(nameof(GhostAuthoringComponent.SupportedGhostModes));
            OptimizationMode = serializedObject.FindProperty(nameof(GhostAuthoringComponent.OptimizationMode));
            HasOwner = serializedObject.FindProperty(nameof(GhostAuthoringComponent.HasOwner));
            SupportAutoCommandTarget = serializedObject.FindProperty(nameof(GhostAuthoringComponent.SupportAutoCommandTarget));
            TrackInterpolationDelay = serializedObject.FindProperty(nameof(GhostAuthoringComponent.TrackInterpolationDelay));
            GhostGroup = serializedObject.FindProperty(nameof(GhostAuthoringComponent.GhostGroup));
            UsePreSerialization = serializedObject.FindProperty(nameof(GhostAuthoringComponent.UsePreSerialization));
            Importance = serializedObject.FindProperty(nameof(GhostAuthoringComponent.Importance));
            MaxSendRate = serializedObject.FindProperty(nameof(GhostAuthoringComponent.MaxSendRate));
            PredictedSpawnedGhostRollbackToSpawnTick = serializedObject.FindProperty(nameof(GhostAuthoringComponent.RollbackPredictedSpawnedGhostState));
            RollbackPredictionOnStructuralChanges = serializedObject.FindProperty(nameof(GhostAuthoringComponent.RollbackPredictionOnStructuralChanges));
            UseSingleBaseline = serializedObject.FindProperty(nameof(GhostAuthoringComponent.UseSingleBaseline));
        }

        public override void OnInspectorGUI()
        {
            var authoringComponent = (GhostAuthoringComponent)target;
            var go = authoringComponent.gameObject;
            var isPrefabEditable = IsPrefabEditable(go);
            GUI.enabled = isPrefabEditable;

            var currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            var isViewingPrefab = PrefabUtility.IsPartOfPrefabAsset(go) || PrefabUtility.IsPartOfPrefabInstance(go) || (currentPrefabStage != null && currentPrefabStage.IsPartOfPrefabContents(go));
            if (isPrefabEditable)
            {
                if (authoringComponent.transform != authoringComponent.transform.root)
                {
                    EditorGUILayout.HelpBox("The `GhostAuthoringComponent` must only be added to the root GameObject of a prefab. This is invalid, please remove or correct this authoring.", MessageType.Error);
                    GUI.enabled = false;
                }

                if (!isViewingPrefab)
                {
                    EditorGUILayout.HelpBox($"'{authoringComponent}' is not a recognised Prefab, so the `GhostAuthoringComponent` is not valid. Please ensure that this GameObject is an unmodified Prefab instance mapped to a known project asset.", MessageType.Error);
                }
            }

            var originalColor = GUI.color;
            GUI.color = originalColor;
            // Importance:
            {
                EditorGUILayout.BeginHorizontal();
                var importanceContent = new GUIContent(nameof(Importance), GetImportanceFieldTooltip());
                EditorGUILayout.PropertyField(Importance, importanceContent);
                var editorImportanceSuggestion = ImportanceInlineTooltip(authoringComponent.Importance);
                importanceContent.text = editorImportanceSuggestion.Name;
                GUILayout.Box(importanceContent, s_HelperWidth);
                EditorGUILayout.EndHorizontal();
            }
            // MaxSendRate:
            {
                var hasMaxSendRate = authoringComponent.MaxSendRate != 0;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(MaxSendRate);
                var globalConfig = NetCodeClientAndServerSettings.instance?.GlobalNetCodeConfig;
                var tickRate = globalConfig != null ? globalConfig.ClientServerTickRate : new ClientServerTickRate();
                tickRate.ResolveDefaults();
                var clientTickRate = globalConfig != null ? NetCodeClientAndServerSettings.instance.GlobalNetCodeConfig.ClientTickRate : NetworkTimeSystem.DefaultClientTickRate;
                var sendInterval = tickRate.CalculateNetworkSendIntervalOfGhostInTicks(authoringComponent.MaxSendRate);
                var label = new GUIContent(SendRateInlineTooltip(), MaxSendRate.tooltip);
                GUILayout.Box(label, s_HelperWidth);

                string SendRateInlineTooltip() =>
                    (sendInterval, hasMaxSendRate) switch
                    {
                        (_, false) => "OFF | Every Snapshot",
                        (1, true) => "Every Snapshot",
                        (2, true) => "Every Other Snapshot",
                        (_, true) => $"Every {WithOrdinalSuffix(sendInterval)} Snapshot",
                    } + $" @ {tickRate.NetworkTickRate}Hz";

                EditorGUILayout.EndHorizontal();

                // MaxSendRate warning:
                if (authoringComponent.SupportedGhostModes != GhostModeMask.Predicted)
                {
                    var interpolationBufferWindowInTicks = clientTickRate.CalculateInterpolationBufferTimeInTicks(in tickRate);
                    var delta = sendInterval - interpolationBufferWindowInTicks;
                    if (delta > 0)
                    {
                        EditorGUILayout.HelpBox($"This ghost prefab is using a MaxSendRate value of {authoringComponent.MaxSendRate}, which leads to a maximum send interval of '{label.text}' i.e. every {sendInterval}ms, which is {delta} ticks longer than your maximum interpolation buffer window of {interpolationBufferWindowInTicks} ticks. You are therefore not replicating this ghost often enough to allow it to smoothly interpolate. To fix; either increase MaxSendRate, or increase the size of the interpolation buffer window globally.", MessageType.Warning);
                    }
                }
            }

            EditorGUILayout.PropertyField(SupportedGhostModes);

            var self = (GhostAuthoringComponent) target;
            var isOwnerPredictedError = DefaultGhostMode.enumValueIndex == (int) GhostMode.OwnerPredicted && !self.HasOwner;

            if (SupportedGhostModes.intValue == (int) GhostModeMask.All)
            {
                EditorGUILayout.PropertyField(DefaultGhostMode);

                // Selecting OwnerPredicted on a ghost without a GhostOwner will cause an exception during conversion - display an error for that case in the inspector
                if (isOwnerPredictedError)
                {
                    EditorGUILayout.HelpBox("Setting `Default Ghost Mode` to `Owner Predicted` is not valid unless the Ghost also supports being Owned by a player (via the `Ghost Owner Component`). Please resolve it one of the following ways.", MessageType.Error);
                    GUI.color = brokenColor;
                    if (GUILayout.Button("Enable Ownership via 'Has Owner'?")) HasOwner.boolValue = true;
                    if (GUILayout.Button("Set to `GhostMode.Interpolated`?")) DefaultGhostMode.enumValueIndex = (int) GhostMode.Interpolated;
                    if (GUILayout.Button("Set to `GhostMode.Predicted`?")) DefaultGhostMode.enumValueIndex = (int) GhostMode.Predicted;
                    GUI.color = originalColor;
                }
            }

            var canBeStaticOptimized = !self.GhostGroup; // TODO - Disable if any child ghost components exist.
            if (canBeStaticOptimized)
            {
                EditorGUILayout.PropertyField(OptimizationMode);
            }
            else
            {
                EditorGUILayout.HelpBox("This ghost prefab has enabled GhostGroup usage, therefore it cannot be static-optimized. Forcing `OptimizationMode.Dynamic` - the user-specified value will be ignored.", MessageType.Info);
                GUI.enabled = false;
                EditorGUILayout.PropertyField(OptimizationMode);
                GUI.enabled = true;
            }

            if (self.OptimizationMode == GhostOptimizationMode.Static)
            {
                EditorGUILayout.HelpBox("The ghost prefab is using `Static` optimization mode, therefore forcibly serialized by server using a single baseline. Forcing `UseSingleBaseline:true` - the user-specified value will be ignored.", MessageType.Info);
                GUI.enabled = false;
                EditorGUILayout.Toggle(new GUIContent("Use Single Baseline", UseSingleBaseline.tooltip), true);
                GUI.enabled = true;
            }
            else
            {
                UseSingleBaseline.boolValue = EditorGUILayout.Toggle(new GUIContent("Use Single Baseline", UseSingleBaseline.tooltip), UseSingleBaseline.boolValue);
            }

            EditorGUILayout.PropertyField(HasOwner);

            if (self.HasOwner)
            {
                EditorGUILayout.PropertyField(SupportAutoCommandTarget);
                EditorGUILayout.PropertyField(TrackInterpolationDelay);
            }
            EditorGUILayout.PropertyField(GhostGroup);
            EditorGUILayout.PropertyField(UsePreSerialization);

            if(self.SupportedGhostModes != GhostModeMask.Interpolated)
            {
                EditorGUILayout.PropertyField(PredictedSpawnedGhostRollbackToSpawnTick);
                EditorGUILayout.PropertyField(RollbackPredictionOnStructuralChanges);
            }

            if (serializedObject.ApplyModifiedProperties())
            {
                GhostAuthoringInspectionComponent.forceBake = true;
                var allComponentOverridesForGhost = GhostAuthoringInspectionComponent.CollectAllComponentOverridesInInspectionComponents(authoringComponent, false);
                GhostComponentAnalytics.BufferConfigurationData(authoringComponent, allComponentOverridesForGhost.Count);
            }

            if (isViewingPrefab && !go.GetComponent<GhostAuthoringInspectionComponent>())
            {
                EditorGUILayout.HelpBox("To modify this ghost's per-entity component meta-data, add a `Ghost Authoring Inspection Component` (a MonoBehaviour) to the relevant authoring GameObject. Inspecting children is supported by adding the Inspection component to the relevant child.", MessageType.Info);
            }
        }

        // TODO - Add guard against nested Ghost prefabs as they're invalid (although a non-ghost prefab containing ghost nested prefabs is valid AFAIK).
        /// <summary>
        /// <para>Lots of valid and invalid ways to view a prefab. These API calls check to ensure we're either:</para>
        /// <para>- IN the prefabs own scene (thus it's editable).</para>
        /// <para>- Selecting the prefab in the PROJECT.</para>
        /// <para>- NOT selecting this prefab in a SCENE.</para>
        /// </summary>
        /// <remarks>Note that it is valid to add this Inspection onto a nested-prefab!</remarks>
        internal static bool IsPrefabEditable(GameObject go)
        {
            if (PrefabUtility.IsPartOfImmutablePrefab(go))
                return false;
            if (PrefabUtility.IsPartOfPrefabAsset(go))
                return true;
            return !PrefabUtility.IsPartOfPrefabInstance(go);
        }

        internal string GetImportanceFieldTooltip()
        {
            var suggestions = NetCodeClientAndServerSettings.instance.CurrentImportanceSuggestions;
            var s = Importance.tooltip;
            foreach (var eis in suggestions)
            {
                var value = eis.MaxValue == uint.MaxValue || eis.MaxValue == eis.MinValue || eis.MaxValue == 0
                    ? $"~{eis.MinValue}" :  $"{eis.MinValue} ~ {eis.MaxValue}";
                s += $"\n\n <b>{value}</b> for <b>{eis.Name}</b>\n<i>{eis.Tooltip}</i>";
            }
            return s;
        }

        internal static EditorImportanceSuggestion ImportanceInlineTooltip(long importance)
        {
            var suggestions = NetCodeClientAndServerSettings.instance.CurrentImportanceSuggestions;
            foreach (var eis in suggestions)
            {
                if (importance <= eis.MaxValue)
                {
                    return eis;
                }
            }
            return suggestions.LastOrDefault();
        }

        /// <summary>Adds the ordinal indicator/suffix to an integer.</summary>
        internal static string WithOrdinalSuffix(long number)
        {
            // Numbers in the teens always end with "th".
            if((number % 100 > 10 && number % 100 < 20))
                return number + "th";
            return (number % 10) switch
            {
                1 => number + "st",
                2 => number + "nd",
                3 => number + "rd",
                _ => number + "th",
            };
        }
    }
}

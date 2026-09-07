using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor
{
    /*
     GhostAuthoring is meant as a baking interface. You setup stuff and then it's baked. It's not meant to exist at runtime
     Reusing it for GhostObject would be diverging its main use.
     We DO want a common set of editor time GUI scripts to edit a ghost and configure it. But even then, some configurations wouldn't apply for runtime GameObjects.
     Having separate Monobehaviours for the two seem to make sense.
     Plus it allows more flexibility if we want to have a special GUI for the GO, it could be for the whole GO.
     */

    [CustomEditor(typeof(GhostObject))]
    [CanEditMultipleObjects]
    internal class GhostObjectEditor : BaseGhostAuthoringComponentEditor<GhostObjectEditor, GhostObject>
    {

        [InitializeOnLoadMethod]
        private static void Init()
        {
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
            UnityEditor.Editor.finishedDefaultHeaderGUI -= OnGameObjectHeader;
            UnityEditor.Editor.finishedDefaultHeaderGUI += OnGameObjectHeader;
#endif
        }

        internal delegate void OnGhostObjectPreRemovalDelegateHandler(GameObject gameObject);
        /// <summary>
        /// Invoked when the GhostObject component is removed.
        /// (Keep this INTERNAL only)
        /// </summary>
        /// <remarks>
        /// This callback is used by NGO to handle removing the GhostBehaviours from a prefab
        /// prior to removing the GhostObject.
        /// </remarks>
        internal static OnGhostObjectPreRemovalDelegateHandler OnGhostObjectPreRemoval;

        internal static bool TryAddRemoveTargetMonoBehaviour<T>(bool shouldHave, GameObject go, int targetIndex, out T created) where T : MonoBehaviour
        {
            var existingMonoBehaviour = go.GetComponent<T>();
            bool alreadyThere = existingMonoBehaviour != null;
            if (shouldHave && !alreadyThere)
            {
                var newComp = go.AddComponent<T>();
                var currentIndex = newComp.GetComponentIndex(); // creates the component always under the target index. This way if the user prefab already has a bajillion existing monobehaviours, the ghost and inspection components are not too far away from each other.
                int moveCount = currentIndex - targetIndex - 1;
                Assert.IsTrue(currentIndex > targetIndex);
                for (int i = 0; i < moveCount; i++)
                {
                    UnityEditorInternal.ComponentUtility.MoveComponentUp(newComp);
                }

                created = newComp;
                return true;
            }
            else if (!shouldHave && alreadyThere)
            {
                OnGhostObjectPreRemoval?.Invoke(existingMonoBehaviour.gameObject);

                DestroyImmediate(existingMonoBehaviour, allowDestroyingAssets: true);
                created = null;
                return false;
            }

            created = null;
            return false;
        }

        // With UseUniformScale there is no PostTransformMatrix on the entity anymore, so a saved override targeting it
        // can no longer map to a baked component and would surface as an invalid-override error in the inspection UI.
        static void RemovePostTransformMatrixOverrides(GameObject go)
        {
            var inspection = go.GetComponent<GhostAuthoringInspectionComponent>();
            if (inspection == null)
                return;
            var removedAny = false;
            for (var i = 0; i < inspection.OverrideCount; i++)
            {
                if (!string.Equals(inspection.ComponentOverrides[i].FullTypeName, typeof(Unity.Transforms.PostTransformMatrix).FullName, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                inspection.RemoveComponentOverrideByIndex(i);
                removedAny = true;
                i--;
            }
            if (removedAny)
            {
                GhostAuthoringInspectionComponent.forceSave = true;
                Debug.Log($"Removed the 'PostTransformMatrix' component override(s) on '{go.name}' because 'Use Uniform Scale' was enabled and the component no longer exists on this ghost. Disabling 'Use Uniform Scale' restores the default (3D scale replication); re-add an override if you had customized it.");
            }
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
            var ghostObject = target.GetComponent<GhostObject>();
            bool isNetworked = ghostObject != null;
            GUILayout.BeginHorizontal();
            GUILayout.Label(LegacyHierarchyDrawer.GhostIcon, GUILayout.MaxHeight(16));
            isNetworked = GUILayout.Toggle(isNetworked, "Network");
            GUILayout.EndHorizontal();

            TryAddRemoveTargetMonoBehaviour<GhostObject>(isNetworked, target, 0, out _);

            if (isNetworked)
            {
                // ghostObject.hideFlags |= HideFlags.HideInInspector; // TODO-release can hide the actual component and have the UI in the GO header directly
                // TODO-release we can expand on this here and add a nice UI for configuring your GameObject's replication. We should make sure to allow multi editing.
            }

            // TODO-release, move GhostObject's configuration UI out of the per monobehaviour logic and into the header, as a dropdown
        }

        public override VisualElement CreateInspectorGUI()
        {
            var ghost = ((GhostObject)target);
            var go = ghost.gameObject;
            IMGUIContainer oldContainer = new IMGUIContainer(() =>
            {
                base.OnInspectorGUI();
            });

            var baseInspector = new VisualElement();
            baseInspector.Add(oldContainer);

            // GhostObject-specific settings: the base editor only draws the shared BaseGhostSettings fields.
            // The returned root is auto-bound to serializedObject, so the PropertyField handles apply/undo/multi-edit.
            var useUniformScaleProperty = serializedObject.FindProperty(nameof(GhostObject.UseUniformScale));
            var useUniformScale = new PropertyField(useUniformScaleProperty);
            var lastUseUniformScale = useUniformScaleProperty.boolValue;
            useUniformScale.RegisterValueChangeCallback(evt =>
            {
                // The change event also fires on (re)bind: only react to actual value changes.
                var newValue = evt.changedProperty.boolValue;
                if (newValue == lastUseUniformScale)
                    return;
                lastUseUniformScale = newValue;

                // Changes the entity archetype (PostTransformMatrix presence), so the inspection preview must rebake.
                GhostAuthoringInspectionComponent.forceBake = true;
                foreach (var t in targets)
                {
                    var ghostObject = (GhostObject)t;
                    if (ghostObject.UseUniformScale)
                        RemovePostTransformMatrixOverrides(ghostObject.gameObject);
                }
            });
            baseInspector.Add(useUniformScale);

            VisualElement addRemoveInspectionComponent = new VisualElement();
            Toggle checkbox = new Toggle();
            checkbox.label = "Customize Send/Serialization Rules";
            addRemoveInspectionComponent.Add(checkbox);
            checkbox.RegisterCallback<ChangeEvent<bool>>(evt =>
            {
                TryAddRemoveTargetMonoBehaviour<GhostAuthoringInspectionComponent>(evt.newValue, go, ghost.GetComponentIndex(), out _);
            });
            checkbox.value = go.GetComponent<GhostAuthoringInspectionComponent>() != null;
            baseInspector.Add(addRemoveInspectionComponent);

            return baseInspector;
        }
    }
}

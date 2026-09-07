using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor
{
    // Inspector used for discoverability for users. This way they don't need to know they need to add a GhostRigidbody monobehaviour to replicate a Rigidbody, they just have a "replicate" checkbox.
    [CustomEditor(typeof(Rigidbody))]
    internal class GhostRigidbodyEditor : GhostMonoBehaviourEditor<Rigidbody, GhostRigidbody>
    {

    }

    /// <summary>
    /// Base class for different kinds of monobehaviour editors, where we add a check box to an already existing monobehaviour (e.g. Rigidbody).
    /// Useful for PhysicsCharacterController, Colliders, etc
    /// </summary>
    /// <typeparam name="T">The existing type to add the checkbox to.</typeparam>
    /// <typeparam name="GhostT">The behaviour type to add when that checkbox is checked.</typeparam>
    internal abstract class GhostMonoBehaviourEditor<T, GhostT> : UnityEditor.Editor where T : Component where GhostT : GhostBehaviour
    {
        public override VisualElement CreateInspectorGUI()
        {
            var baseElement = new VisualElement();
            InspectorElement.FillDefaultInspector(baseElement, this.serializedObject, this);
            T monobehaviourTarget = (T)target;

            // TODO-release@physicsUI better UI style for this
            var toggleParent = new PropertyField();
            toggleParent.tooltip = $"Adds Netcode replication/prediction support to this component.";
            toggleParent.style.flexDirection = FlexDirection.Row;
            var iconElement = new UnityEngine.UIElements.Image();
            iconElement.image = LegacyHierarchyDrawer.GhostIcon;
            iconElement.style.height = 15;
            iconElement.style.width = 15;
            var toggle = new Toggle("");
            var existingMonoBehaviour = monobehaviourTarget.GetComponent<GhostT>();
            toggle.value = existingMonoBehaviour != null;
            if (existingMonoBehaviour != null)
                existingMonoBehaviour.hideFlags = HideFlags.HideInInspector;
            var label = new Label("Netcode Replication");
            label.style.marginLeft = 2;

            toggle.RegisterCallback<ChangeEvent<bool>>(evt =>
            {
                if (GhostObjectEditor.TryAddRemoveTargetMonoBehaviour<GhostT>(evt.newValue, monobehaviourTarget.gameObject, monobehaviourTarget.GetComponentIndex(), out var created))
                {
                    // after conversation with Danni, the idea is that adding network logic to existing engine components could simply mean
                    // adding UI config under that specific component instead of adding an extra monobehaviour on the GameObject which would add
                    // noise for users.
                    created.hideFlags = HideFlags.HideInInspector;
                    EditorUtility.SetDirty(created.gameObject);
                }
            });

            toggleParent.Add(toggle);
            toggleParent.Add(iconElement);
            toggleParent.Add(label);
            baseElement.Insert(0, toggleParent);
            return baseElement;
        }
    }
}

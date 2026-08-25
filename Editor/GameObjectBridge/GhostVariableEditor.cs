using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{

    /// <summary>
    /// Custom drawer so that we can see the component based value in the inspector.
    /// </summary>
    [CustomPropertyDrawer(typeof(GhostField<>))]
    internal class GhostFieldPropertyDrawer : PropertyDrawer
    {
        Label m_GhostValueLabel;
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var ghostBehaviour = property.serializedObject.targetObject as GhostBehaviour;
            var ret = new VisualElement();
            ret.Add(new PropertyField(property));
            var field = ghostBehaviour.GetType().GetField(property.name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            IGhostFieldInspectorDrawerUtilities ghostField = (IGhostFieldInspectorDrawerUtilities)field.GetValue(ghostBehaviour);

            if (!ghostField.Initialized())
            {
                return ret;
            }
            var previousField = ghostField;
            ret.schedule.Execute(() =>
            {
                var ghostField = (IGhostFieldInspectorDrawerUtilities)field.GetValue(ghostBehaviour);
                if (ghostField.Initialized())
                {
                    if (previousField.Initialized() && ghostField.ShouldApplyInspectorValue(previousField))
                    {
                        ghostField.CopyInspectorValueToEntityValue();
                    }
                    else
                    {
                        ghostField.CopyEntityValueToInspectorValue(); // We refresh the field that unity is aware of (with SerializeField) so that the inspector can just draw it. This is great since if users have their own custom logic for certain internal types, this can still work. We're letting the engine do its thing
                    }

                    previousField = ghostField;
                    field.SetValue(ghostBehaviour, ghostField);
                }
            }).Every(1); // effectively tells unity to draw this on every frame --> which is pretty much what we want.

            return ret;
        }
    }
}

using System;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class LabelWithIcon : VisualElement
    {
        Label m_Label;
        VisualElement m_Icon;

        const string k_OverheadIconSuffix = "__overhead-icon";
        const string k_WarningIconSuffix = "__warning-icon";
        const string k_GhostPrefabIconSuffix = "__ghost-prefab-icon";
        const string k_GhostComponentIconSuffix = "__ghost-component-icon";


        internal LabelWithIcon(IconPosition iconPosition)
        {
            name = "tree-view-item";
            m_Label = new Label { name = "tree-view-item"};
            AddToClassList(MultiColumnController.cellLabelUssClassName);
            m_Icon = new VisualElement { name = "tree-view-item"};
            style.flexDirection = FlexDirection.Row;
            var iconInsertionIndex = iconPosition == IconPosition.BeforeLabel ? 0 : 1;

            Add(m_Label);
            Insert(iconInsertionIndex, m_Icon);
        }

        internal void SetIcon(IconType iconType)
        {
            m_Icon.ClearClassList();

            if (iconType == IconType.None)
            {
                SetIconEnabled(false);
                return;
            }

            var iconStyleClassSuffix = iconType switch
            {
                IconType.Overhead => k_OverheadIconSuffix,
                IconType.Warning => k_WarningIconSuffix,
                IconType.GhostPrefab => k_GhostPrefabIconSuffix,
                IconType.GhostComponent => k_GhostComponentIconSuffix,
                _ => throw new ArgumentOutOfRangeException(nameof(iconType), iconType, null)
            };

            m_Icon.AddToClassList($"{MultiColumnController.cellUssClassName}{iconStyleClassSuffix}");
            SetIconEnabled(true);
        }

        internal void SetText(string t)
        {
            m_Label.text = t;
        }

        internal void SetTooltip(string tooltipText)
        {
            tooltip = tooltipText;
        }

        void SetIconEnabled(bool enabled)
        {
            m_Icon.style.display = new StyleEnum<DisplayStyle>(enabled ? DisplayStyle.Flex : DisplayStyle.None);
        }

        internal void ResetIcon()
        {
            m_Icon.ClearClassList();
        }
    }
}

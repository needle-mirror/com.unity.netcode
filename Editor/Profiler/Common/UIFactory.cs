using System;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    static class UIFactory
    {
        const string k_HeaderColumnUssClass = "unity-multi-column-header__column";
        internal static Func<VisualElement> MakeTreeViewColumnHeader(string text)
        {
            var container = new VisualElement();
            container.AddToClassList($"{k_HeaderColumnUssClass}__default-content");
            container.AddToClassList($"{k_HeaderColumnUssClass}__content");
            container.AddToClassList($"{k_HeaderColumnUssClass}__content--has-title");
            var icon = new VisualElement();
            icon.AddToClassList(Image.ussClassName);
            icon.AddToClassList($"{k_HeaderColumnUssClass}__icon");
            var label = new Label();
            label.AddToClassList($"{k_HeaderColumnUssClass}-title");
            label.text = text;
            container.Add(label);
            return () => container;
        }

        internal static Label CreateTreeViewLabel()
        {
            var label = new Label();
            label.AddToClassList(MultiColumnController.cellLabelUssClassName);
            return label;
        }

        internal static VisualElement CreateNoDataInfoLabel(string packetDirection, string url)
        {
            // There is a bug where an href link does not work if a line break happened before.
            // This is why we create two separate labels here.
            var ve = new VisualElement();
            var label1 = new Label();
            var label2 = new Label();
            label1.text = $"No ghost snapshots were {packetDirection} this frame.";
            label2.text = $"This is expected behavior for most network scenarios. For more information, refer to the <a href=\"{url}\">Network Profiler documentation</a>.";
            ve.Add(label1);
            ve.Add(label2);
            // Hide by default
            ve.style.display = DisplayStyle.None;
            ve.style.marginBottom = 10;
            return ve;
        }

        internal static VisualElement CreateFilterOptionsForSnapshots(Action<string> treeViewFilter, bool showOverheadFilter = false, Action<bool> toggleOverhead = null)
        {
            var container = new VisualElement();
            container.AddToClassList("ghost-snapshot__filter");
            var horizontalContainer = new VisualElement();
            horizontalContainer.AddToClassList("ghost-snapshot__filter--horizontal");

            // var averagesToggle = new Toggle("Display averages over last second");
            // horizontalContainer.Add(averagesToggle);

            if (showOverheadFilter)
            {
                var overheadToggle = new Toggle("Display Overhead/Metadata")
                {
                    name = "overhead-toggle",
                    value = true
                };
                overheadToggle.RegisterValueChangedCallback(evt => toggleOverhead?.Invoke(evt.newValue));
                horizontalContainer.Add(overheadToggle);
            }

            container.Add(horizontalContainer);

            var searchBar = new ToolbarSearchField();
            searchBar.RegisterValueChangedCallback(evt =>
            {
                treeViewFilter?.Invoke(evt.newValue);
            });

            // TODO: Add search bar
            // container.Add(searchBar);
            return container;
        }

        internal class ColorIndicatorWithText : VisualElement
        {
            VisualElement m_ColorIndicator;
            VisualElement m_ColorIndicatorSub;

            const string k_UssClassNameColorIndicator = "category-legend__color-indicator";
            const string k_UssClassNameColorIndicatorSub = "category-legend__color-indicator__sub";

            internal void SetUssCategory(string name)
            {
                m_ColorIndicator.ClearClassList();
                m_ColorIndicatorSub.ClearClassList();

                m_ColorIndicator.AddToClassList(k_UssClassNameColorIndicator);
                m_ColorIndicatorSub.AddToClassList(k_UssClassNameColorIndicatorSub);
                m_ColorIndicator.AddToClassList(k_UssClassNameColorIndicator + "--" + name);
                m_ColorIndicatorSub.AddToClassList(k_UssClassNameColorIndicator + "--" + name + "--sub");
            }

            internal void SetView(bool showColorBox, bool showSubColor)
            {
                m_ColorIndicator.style.display = new StyleEnum<DisplayStyle>(showColorBox ? DisplayStyle.Flex : DisplayStyle.None);
                m_ColorIndicatorSub.style.display = new StyleEnum<DisplayStyle>(showSubColor ? DisplayStyle.Flex : DisplayStyle.None);
            }

            internal void SetText(string t)
            {
                this.Q<Label>().text = t;
            }

            internal ColorIndicatorWithText()
            {
                AddToClassList("category-legend__name-container");
                m_ColorIndicator = new VisualElement { name = "color-indicator" };
                m_ColorIndicator.AddToClassList("category-legend__color-indicator");
                Label nameLabel = new Label("Name");
                Add(m_ColorIndicator);
                Add(nameLabel);
                m_ColorIndicatorSub = new VisualElement { name = "color-indicator-overlay" };
                m_ColorIndicatorSub.AddToClassList("category-legend__color-indicator__sub");
                m_ColorIndicator.Add(m_ColorIndicatorSub);
            }
        }

        internal static VisualElement CreateTreeViewLabelWithIcon(IconType iconType, IconPosition iconPosition)
        {
            return new LabelWithIcon(iconType, iconPosition);
        }
    }

    class MetricsHeader : VisualElement
    {
        Label m_WorldNameLabel;
        Label m_ServerTickLabel;
        Label m_JitterLabel;
        Label m_RttLabel;

        const string k_MetricsHeaderUssClass = "metrics-header";
        const string k_MetricsHeaderLeftAlignedUssClass = k_MetricsHeaderUssClass + "__left-aligned";
        const string k_MetricsHeaderRightAlignedUssClass = k_MetricsHeaderUssClass + "__right-aligned";
        const string k_MetricsHeaderLabelUssClass = k_MetricsHeaderUssClass + "__label";

        internal MetricsHeader(NetworkRole role)
        {
            AddToClassList(k_MetricsHeaderUssClass);

            // Left-aligned label for the world name and server tick
            var leftAlignedContainer = new VisualElement();
            leftAlignedContainer.AddToClassList(k_MetricsHeaderLeftAlignedUssClass);
            m_WorldNameLabel = new Label("World Name");
            m_WorldNameLabel.AddToClassList(k_MetricsHeaderLabelUssClass);
            m_ServerTickLabel = new Label("Servertick: N/A");
            m_ServerTickLabel.AddToClassList(k_MetricsHeaderLabelUssClass);
            leftAlignedContainer.Add(m_WorldNameLabel);
            leftAlignedContainer.Add(m_ServerTickLabel);
            Add(leftAlignedContainer);

            // Right-aligned labels for jitter and rtt
            if (role == NetworkRole.Client)
            {
                var rightAlignedContainer = new VisualElement();
                rightAlignedContainer.AddToClassList(k_MetricsHeaderRightAlignedUssClass);
                m_JitterLabel = new Label("Jitter: N/A");
                m_RttLabel = new Label("RTT: N/A");
                rightAlignedContainer.Add(m_JitterLabel);
                rightAlignedContainer.Add(m_RttLabel);
                Add(rightAlignedContainer);
            }

            // Background highlight
            AddToClassList(BaseVerticalCollectionView.itemAlternativeBackgroundUssClassName);
        }

        internal void SetWorldName(string worldName)
        {
            m_WorldNameLabel.text = worldName;
        }

        internal void SetServerTick(NetworkTick serverTick)
        {
            m_ServerTickLabel.text = $"Servertick: {serverTick}";
        }

        internal void SetJitter(float jitter)
        {
            m_JitterLabel.text = $"Jitter: {jitter:F2}ms";
        }

        internal void SetRtt(float rtt)
        {
            m_RttLabel.text = $"RTT: {rtt:F2}ms";
        }
    }

    class LabelWithIcon : VisualElement
    {
        Label m_Label;
        VisualElement m_Icon;

        internal Label Label => m_Label;

        const string k_OverheadIconSuffix = "__overhead-icon";
        const string k_WarningIconSuffix = "__warning-icon";

        internal LabelWithIcon(IconType iconType, IconPosition iconPosition)
        {
            var iconStyleClassSuffix = iconType switch
            {
                IconType.Overhead => k_OverheadIconSuffix,
                IconType.Warning => k_WarningIconSuffix,
                _ => throw new ArgumentOutOfRangeException(nameof(iconType), iconType, null)
            };
            m_Label = new Label();
            AddToClassList(MultiColumnController.cellLabelUssClassName);
            m_Icon = new VisualElement();
            m_Icon.AddToClassList($"{MultiColumnController.cellUssClassName}{iconStyleClassSuffix}");

            style.flexDirection = FlexDirection.Row;

            var iconInsertionIndex = iconPosition == IconPosition.BeforeLabel ? 0 : 1;

            Add(m_Label);
            Insert(iconInsertionIndex, m_Icon);
        }

        internal void SetText(string t)
        {
            m_Label.text = t;
        }

        internal void SetTooltip(string tooltipText)
        {
            tooltip = tooltipText;
        }

        internal void SetIconEnabled(bool enabled)
        {
            m_Icon.style.display = new StyleEnum<DisplayStyle>(enabled ? DisplayStyle.Flex : DisplayStyle.None);
        }
    }
}

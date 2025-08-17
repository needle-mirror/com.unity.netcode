using System;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    static class UIFactory
    {
        internal static Label CreateTreeViewLabel()
        {
            var label = new Label();
            label.AddToClassList("unity-multi-column-view__cell__label");
            return label;
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

        internal static VisualElement CreateTreeViewLabelWithIcon()
        {
            return new LabelWithIcon();
        }
    }

    class MetricsHeader : VisualElement
    {
        Label m_WorldNameLabel;
        Label m_ServerTickLabel;
        Label m_JitterLabel;
        Label m_RttLabel;

        internal MetricsHeader(NetworkRole role)
        {
            AddToClassList("metrics-header");

            // Left-aligned label for the world name and server tick
            var leftAlignedContainer = new VisualElement();
            leftAlignedContainer.AddToClassList("metrics-header__left-aligned");
            m_WorldNameLabel = new Label("World Name");
            m_WorldNameLabel.AddToClassList("metrics-header__label");
            m_ServerTickLabel = new Label("Servertick: N/A");
            m_ServerTickLabel.AddToClassList("metrics-header__label");
            leftAlignedContainer.Add(m_WorldNameLabel);
            leftAlignedContainer.Add(m_ServerTickLabel);
            Add(leftAlignedContainer);

            // Right-aligned labels for jitter and rtt
            if (role == NetworkRole.Client)
            {
                var rightAlignedContainer = new VisualElement();
                rightAlignedContainer.AddToClassList("metrics-header__right-aligned");
                m_JitterLabel = new Label("Jitter: N/A");
                m_RttLabel = new Label("RTT: N/A");
                rightAlignedContainer.Add(m_JitterLabel);
                rightAlignedContainer.Add(m_RttLabel);
                Add(rightAlignedContainer);
            }

            // background highlight
            AddToClassList("unity-collection-view__item--alternative-background");
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

        internal LabelWithIcon()
        {
            m_Label = new Label();
            AddToClassList("unity-multi-column-view__cell__label");
            m_Icon = new VisualElement();
            m_Icon.AddToClassList("unity-multi-column-view__cell__icon");

            style.flexDirection = FlexDirection.Row;
            Add(m_Icon);
            Add(m_Label);
        }

        internal void SetText(string t)
        {
            m_Label.text = t;
        }

        internal void SetIconEnabled(bool enabled)
        {
            m_Icon.style.display = new StyleEnum<DisplayStyle>(enabled ? DisplayStyle.Flex : DisplayStyle.None);
        }
    }
}

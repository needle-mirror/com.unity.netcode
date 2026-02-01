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
            label.name = "tree-view-item";
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

        internal static VisualElement CreateFilterOptionsForSnapshots(Action<string> filterTreeViewAction, bool showOverheadFilter = false, Action<bool> toggleOverheadAction = null)
        {
            var container = new VisualElement();
            container.AddToClassList("ghost-snapshot__filter");
            var horizontalContainer = new VisualElement();
            horizontalContainer.AddToClassList("ghost-snapshot__filter--horizontal");

            // var averagesToggle = new Toggle("Display averages over last second");
            // horizontalContainer.Add(averagesToggle);

            var searchField = new ToolbarSearchField();
            var innerInputField = searchField.Q(className: "unity-text-element--inner-input-field-component");
            if (innerInputField != null) innerInputField.name = "search-field";
            searchField.RegisterValueChangedCallback(evt =>
            {
                filterTreeViewAction?.Invoke(evt.newValue);
            });

            if (showOverheadFilter)
            {
                var overheadToggle = new Toggle("Display Overhead/Metadata")
                {
                    name = "overhead-toggle",
                    value = true
                };
                overheadToggle.RegisterValueChangedCallback(evt =>
                {
                    toggleOverheadAction?.Invoke(evt.newValue);
                    filterTreeViewAction?.Invoke(searchField.value);
                });
                horizontalContainer.Add(overheadToggle);
            }

            container.Add(horizontalContainer);
            container.Add(searchField);

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
    }
}

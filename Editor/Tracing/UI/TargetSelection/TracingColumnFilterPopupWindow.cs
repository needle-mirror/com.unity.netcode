using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static Unity.Netcode.Editor.Tracing.TracingColumnFilters;

namespace Unity.Netcode.Editor.Tracing
{
    /// <summary>
    /// Dropdown popup opened by the arrow of a column's header filter button in <see cref="SelectTracingTargetWindow"/>.
    /// Lists every distinct value of that column with a toggle per value, writing straight to the
    /// <see cref="TracingColumnFilters"/> it was opened with.
    /// </summary>
    internal class TracingColumnFilterPopupWindow : EditorWindow
    {
        internal const string k_UssClassPopupRoot = "tracing-column-filter-popup";
        const string k_UssClassPopupTitle = "tracing-column-filter-popup__title";
        const string k_UssClassPopupScroll = "tracing-column-filter-popup__scroll";
        internal const string k_UssClassPopupFooter = "tracing-column-filter-popup__footer";
        internal const string k_UssClassPopupAllToggle = "tracing-column-filter-popup__all-toggle";
        internal const string k_UssClassPopupValueToggle = "tracing-column-filter-popup__value-toggle";
        const string k_UssClassPopupEmptyLabel = "tracing-column-filter-popup__empty-label";

        const string k_ToggleAllLabel = "All";

        const float k_ValueRowHeight = 19f;
        const float k_FooterPaddingHeight = 8f;
        const float k_HeaderAndPaddingHeight = 34f;
        const float k_MaxHeight = 380f;
        const float k_Width = 250f;

        TracingColumnFilters m_Filters;
        VisualElement m_StyleSource;
        string m_ColumnName;
        string m_ColumnTitle;
        readonly List<string> m_Values = new();
        readonly List<Toggle> m_ValueToggles = new();
        Toggle m_ToggleAll;

        internal static void Open(TracingColumnFilters filters, string columnName, string columnTitle, List<string> values, VisualElement styleSource, Rect activatorScreenRect)
        {
            if (filters == null)
            {
                return;
            }

            var window = CreateInstance<TracingColumnFilterPopupWindow>();
            window.m_Filters = filters;
            window.m_StyleSource = styleSource;
            window.m_ColumnName = columnName;
            window.m_ColumnTitle = columnTitle;
            window.m_Values.AddRange(values);

            // The footer (special entries + All) never scrolls, so it is sized separately from the value list.
            var specialCount = 0;
            foreach (var value in window.m_Values)
            {
                if (IsSpecialColumnFilterValue(value))
                {
                    specialCount++;
                }
            }

            var footerHeight = window.m_Values.Count > 0 ? (specialCount + 1) * k_ValueRowHeight + k_FooterPaddingHeight : 0f;
            var height = Mathf.Min(k_MaxHeight,
                Mathf.Max(1, window.m_Values.Count - specialCount) * k_ValueRowHeight
                + footerHeight
                + k_HeaderAndPaddingHeight);
            window.ShowAsDropDown(activatorScreenRect, new Vector2(k_Width, height));
        }

        public void CreateGUI()
        {
            // Survives a domain reload as an empty shell (filters and values are not serialized); just close.
            if (m_Filters == null)
            {
                rootVisualElement.schedule.Execute(Close);
                return;
            }

            var root = rootVisualElement;
            if (m_StyleSource != null)
            {
                var sourceSheets = m_StyleSource.styleSheets;
                for (var i = 0; i < sourceSheets.count; i++)
                {
                    root.styleSheets.Add(sourceSheets[i]);
                }
            }

            root.AddToClassList(k_UssClassPopupRoot);
            root.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape)
                {
                    Close();
                }
            });

            var label = new Label($"Show {m_ColumnTitle} values");
            label.AddToClassList(k_UssClassPopupTitle);
            root.Add(label);

            if (m_Values.Count == 0)
            {
                var empty = new Label("No values to filter");
                empty.AddToClassList(k_UssClassPopupEmptyLabel);
                root.Add(empty);
                return;
            }

            var scroll = new ScrollView();
            scroll.AddToClassList(k_UssClassPopupScroll);
            var footer = new VisualElement();
            footer.AddToClassList(k_UssClassPopupFooter);

            m_ValueToggles.Clear();
            foreach (var value in m_Values)
            {
                var toggle = new Toggle
                {
                    text = FormatValueToggleLabel(value),
                    userData = value
                };
                toggle.SetValueWithoutNotify(!m_Filters.IsColumnFilterValueHidden(m_ColumnName, value));
                toggle.AddToClassList(k_UssClassPopupValueToggle);
                toggle.RegisterValueChangedCallback(OnValueToggleChanged);
                m_ValueToggles.Add(toggle);
                // Special entries (pattern filters, "None") live in the non-scrolling footer with the All toggle.
                if (IsSpecialColumnFilterValue(value))
                {
                    footer.Add(toggle);
                }
                else
                {
                    scroll.Add(toggle);
                }
            }

            root.Add(scroll);

            m_ToggleAll = new Toggle { text = k_ToggleAllLabel };
            m_ToggleAll.AddToClassList(k_UssClassPopupAllToggle);
            m_ToggleAll.RegisterValueChangedCallback(OnToggleAllChanged);
            footer.Add(m_ToggleAll);
            root.Add(footer);

            RefreshToggleAllState();
        }

        // Rows with no value for the column render the placeholder cell text; list them as "None".
        static string FormatValueToggleLabel(string value) =>
            value == k_ColumnFilterNoValue ? $"None ({value})" : value;

        void OnValueToggleChanged(ChangeEvent<bool> evt)
        {
            if (m_Filters == null)
            {
                Close();
                return;
            }

            if (evt.target is Toggle { userData: string value })
            {
                m_Filters.SetColumnFilterValueHidden(m_ColumnName, value, hidden: !evt.newValue);
            }

            RefreshToggleAllState();
        }

        void OnToggleAllChanged(ChangeEvent<bool> evt)
        {
            if (m_Filters == null)
            {
                Close();
                return;
            }

            var show = evt.newValue;
            foreach (var toggle in m_ValueToggles)
            {
                toggle.SetValueWithoutNotify(show);
            }

            m_Filters.SetColumnFilterValuesHidden(m_ColumnName, m_Values, hidden: !show);
            RefreshToggleAllState();
        }

        void RefreshToggleAllState()
        {
            if (m_ToggleAll == null)
            {
                return;
            }

            var shownCount = 0;
            foreach (var toggle in m_ValueToggles)
            {
                if (toggle.value)
                {
                    shownCount++;
                }
            }

            m_ToggleAll.showMixedValue = shownCount > 0 && shownCount < m_ValueToggles.Count;
            m_ToggleAll.SetValueWithoutNotify(shownCount > 0 && shownCount == m_ValueToggles.Count);
        }
    }
}

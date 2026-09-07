using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Netcode.Editor.Tracing.UI.FilterPanel;
using Unity.Netcode.Tracing;
using UnityEditor;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor.Tracing.UI.TracingToolbar
{
    enum TracingFilterTab { TracingTarget, DiffTags }

    // Persistent state of the tracing filter popup
    internal class TracingFilterState
    {
        public readonly HashSet<string> HiddenSystems = new();
        public readonly HashSet<string> HiddenComponents = new();
        public DiffInfo.DiffReasons SelectedDiffReasons = DiffInfo.AllDiffReasons;
        public bool ChangedValuesOnly;
        public float FuzzyDiffThreshold;
        public TracingFilterTab ActiveTab = TracingFilterTab.TracingTarget;
    }

    // One row of the Tracing Target tab
    struct TracingItemData
    {
        public string Name;
        public bool IsSystem;
        // In the selection but not in the current recording (added after it stopped)
        public bool IsUntraced;
        // In the current recording but no longer in the selection: its data is still browsable
        public bool IsRemoved;
        // Managed component: its values are never captured, so there is nothing to show or hide
        public bool IsManagedComponent;
    }

    internal class TracingFilterDropdown
    {
        const string k_UssPath = Constants.Stylesheets + "tracing-toolbar.uss";
        const string k_VariablesDarkPath = Constants.Stylesheets + "tracing-vars-dark.uss";
        const string k_VariablesLightPath = Constants.Stylesheets + "tracing-vars-light.uss";
        const string k_FilterRowTemplatePath = Constants.Templates + "tracing-filter-row.uxml";
        const string k_Toggle = "toggle";
        const string k_Name = "name";
        const string k_TracingType = "tracing-type";
        const string k_Info = "info";

        const string k_EmptyTracingTargets =
            "No system or component\n is selected. Currently tracing default\n targets once per tick.";
        internal const string k_TextTracingTarget = "Targets";
        const string k_TextDiffTags = "Diff Tags";
        const string k_ChangeTracingTargetButtonLabel = "Set prediction tracing target";
        const string k_TextChangedValuesOnly = "Changed values only";
        const string k_TooltipChangedValuesOnly =
            "Only show systems, ghosts and components whose values actually changed during the tick.";
        internal const string k_TextFuzzyThreshold = "Fuzzy diff threshold";
        internal const string k_TooltipFuzzyThreshold =
            "Hide component data diffs whose largest per-field difference does not exceed this value. "
            + "Non-numeric mismatches (for example, booleans) always show. 0 disables the fuzzy diff filter; "
            + "the field accepts any value ≥ 0, beyond the slider's 0.01–10 range.";
        internal const string k_TooltipUntracedTarget =
            "This target has not been traced yet. Resume or restart the tracing to trace it.";
        internal const string k_TooltipRemovedTarget =
            "This target will not be traced anymore, but its recorded traces stay viewable. Add it back to the tracing targets to trace it again.";
        internal const string k_TooltipToggleVisibility = "Temporarily hide this from the filtering view";
        internal const string k_TooltipManagedComponent = "Managed component values are not captured or diffed.";
        const float k_ListViewPadding = 15f;
        const float k_MinDropdownWidth = 240f;
        const float k_RowRightPadding = 8f;

        internal static readonly string k_GhostInstanceTypeName = typeof(GhostInstance).FullName;

        readonly TracingFilterState m_State;
        readonly StyleSheet m_StyleSheet;
        readonly StyleSheet m_UssVariables;
        readonly VisualTreeAsset m_FilterRowTemplate;

        GenericDropdownMenu m_Menu;
        Button m_TracingTargetTab;
        Button m_DiffTagsTab;
        ListView m_TargetListView;
        ListView m_DiffTagListView;
        Toggle m_ChangedValuesOnlyToggle;
        Slider m_ThresholdSlider;
        FloatField m_ThresholdField;
        Button m_SelectAllToggle;
        float m_ListViewWidth;
        ListView m_ListView;
        List<TracingItemData> m_TargetItems;

        // Raised after the hidden sets in the state changed. Arguments are the state's sets.
        public event Action<HashSet<string>, HashSet<string>> VisibilityTargetsChanged;

        // Raised after the selected diff reasons in the state changed.
        public event Action<DiffInfo.DiffReasons> SelectedDiffReasonsChanged;

        // Raised after "Changed values only" in the state changed.
        public event Action<bool> ChangedValuesOnlyChanged;

        // Raised after the fuzzy diff threshold in the state changed.
        public event Action<float> FuzzyDiffThresholdChanged;

        // Raised when the popup closes; the dropdown must be discarded afterwards.
        public event Action Closed;

        public TracingFilterDropdown(TracingFilterState state)
        {
            m_State = state;
            m_StyleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(k_UssPath);
            m_UssVariables = EditorGUIUtility.isProSkin
                ? AssetDatabase.LoadAssetAtPath<StyleSheet>(k_VariablesDarkPath)
                : AssetDatabase.LoadAssetAtPath<StyleSheet>(k_VariablesLightPath);
            m_FilterRowTemplate = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(k_FilterRowTemplatePath);
        }

        public void Show(UnityEngine.Rect anchorBound, VisualElement anchor)
        {
            m_Menu = new GenericDropdownMenu();
            m_Menu.contentContainer.styleSheets.Add(m_UssVariables);
            m_Menu.contentContainer.styleSheets.Add(m_StyleSheet);
            m_Menu.contentContainer.AddToClassList(TracingToolbarUssClasses.TracingFilterContentContainer);
            var tabHeader = CreateTabHeader();
            var thresholdRow = CreateThresholdRow();
            var optionsRow = CreateOptionsRow();

            m_Menu.contentContainer.Add(CreateTargetListView());
            m_Menu.contentContainer.Add(CreateDiffTagsListView());
            ApplyActiveTab();

            var changeTracingButton = new Button { text = k_ChangeTracingTargetButtonLabel, tooltip = "Changing tracing sources will reset the tracing data"};
            changeTracingButton.clicked += SelectTracingTargetWindow.ShowWindow;
            changeTracingButton.AddToClassList(TracingToolbarUssClasses.TracingFilterChangeButton);

            m_Menu.DropDown(anchorBound, anchor, DropdownMenuSizeMode.Auto);

            // The tab strip and options row pin above the scrolling lists; the footer pins below them.
            var scrollView = m_Menu.contentContainer.GetFirstAncestorOfType<ScrollView>();
            if (scrollView != null)
            {
                scrollView.hierarchy.Insert(0, tabHeader);
                scrollView.hierarchy.Insert(1, thresholdRow);
                scrollView.hierarchy.Insert(2, optionsRow);
                var footerSeparator = new VisualElement();
                footerSeparator.AddToClassList(TracingToolbarUssClasses.TracingFilterFooterSeparator);
                scrollView.hierarchy.Add(footerSeparator);
                scrollView.hierarchy.Add(changeTracingButton);

                tabHeader.styleSheets.Add(m_UssVariables);
                tabHeader.styleSheets.Add(m_StyleSheet);
                thresholdRow.styleSheets.Add(m_UssVariables);
                thresholdRow.styleSheets.Add(m_StyleSheet);
                optionsRow.styleSheets.Add(m_UssVariables);
                optionsRow.styleSheets.Add(m_StyleSheet);
                footerSeparator.styleSheets.Add(m_StyleSheet);
                changeTracingButton.styleSheets.Add(m_StyleSheet);
            }
            else
            {
                m_Menu.contentContainer.Insert(0, tabHeader);
                m_Menu.contentContainer.Insert(1, thresholdRow);
                m_Menu.contentContainer.Insert(2, optionsRow);
                m_Menu.AddSeparator("");
                m_Menu.contentContainer.Add(changeTracingButton);
            }

            m_Menu.onClose += OnClose;
        }

        public void RefreshTargetList()
        {
            if (m_TargetListView == null || m_TargetItems == null)
                return;
            m_TargetItems.Clear();
            m_TargetItems.AddRange(CreateListItems());
            m_TargetListView.RefreshItems();
        }

        public void SyncFromState()
        {
            RefreshSelectAllToggle();
            m_ChangedValuesOnlyToggle?.SetValueWithoutNotify(m_State.ChangedValuesOnly);
            m_ThresholdField?.SetValueWithoutNotify(m_State.FuzzyDiffThreshold);
            m_ThresholdSlider?.SetValueWithoutNotify(FuzzyThresholdSlider.ThresholdToPosition(m_State.FuzzyDiffThreshold));
            m_DiffTagListView?.RefreshItems();
        }

        public void Close()
        {
            var content = m_Menu?.contentContainer;
            if (content?.panel == null)
                return;
            using (var cancel = NavigationCancelEvent.GetPooled())
            {
                cancel.target = content;
                content.SendEvent(cancel);
            }
        }

        internal static List<TracingItemData> CreateListItems()
        {
            var items = new List<TracingItemData>();
            var untracedItems = new List<TracingItemData>();
            var config = TracingDataAccess.Config.Data;

            if (!config.Initialized) return items;

            var showingTraces = TracingDisplayState.IsShowingTraces;

            var selectedSystems = new HashSet<string>();
            var selectedComponents = new HashSet<string>();

            foreach (var sysIndex in config.SystemTypesToTrace)
            {
                AddListItem(items, untracedItems, selectedSystems, TracingTargetNames.System(sysIndex), isSystem: true, showingTraces);
            }

            void AddComponentItem(ComponentType ct)
            {
                AddListItem(items, untracedItems, selectedComponents, TracingTargetNames.Component(ct.TypeIndex), isSystem: false, showingTraces,
                    isManagedComponent: !TracingTargetTypes.IsUnmanagedComponentType(ct.GetManagedType()));
            }

            foreach (var ct in config.RequiredTypesToTrace)
                AddComponentItem(ct);
            foreach (var ct in config.OptionalTypesToTrace)
                AddComponentItem(ct);

            // GhostInstance never joins the trace-request sets.
            // It is a filterable target only while it is in the user's tracing target selection.
            if (TracingWindowUtility.IsGhostInstanceSelected())
            {
                selectedComponents.Add(k_GhostInstanceTypeName);
                items.Add(new TracingItemData
                {
                    Name = k_GhostInstanceTypeName,
                    IsSystem = false
                });
            }

            if (showingTraces)
            {
                // Recorded targets that are no longer selected still have data to show/hide.
                foreach (var name in TracingRecordingTargets.RecordedSystems)
                {
                    if (!selectedSystems.Contains(name))
                        items.Add(new TracingItemData { Name = name, IsSystem = true, IsRemoved = true });
                }

                foreach (var name in TracingRecordingTargets.RecordedComponents)
                {
                    if (!selectedComponents.Contains(name))
                        items.Add(new TracingItemData { Name = name, IsSystem = false, IsRemoved = true });
                }
            }

            items.AddRange(untracedItems);
            return items;
        }

        static void AddListItem(List<TracingItemData> items, List<TracingItemData> untracedItems, HashSet<string> selectedNames, string name, bool isSystem, bool showingTraces, bool isManagedComponent = false)
        {
            selectedNames.Add(name);
            var item = new TracingItemData
            {
                Name = name,
                IsSystem = isSystem,
                IsUntraced = showingTraces && TracingRecordingTargets.IsUntracedTarget(name, isSystem),
                IsManagedComponent = isManagedComponent,
            };
            (item.IsUntraced ? untracedItems : items).Add(item);
        }

        VisualElement CreateThresholdRow()
        {
            var row = new VisualElement { tooltip = k_TooltipFuzzyThreshold };
            row.AddToClassList(TracingToolbarUssClasses.TracingFilterThresholdRow);

            var label = new Label(k_TextFuzzyThreshold);
            label.AddToClassList(TracingToolbarUssClasses.TracingFilterThresholdLabel);
            row.Add(label);

            m_ThresholdSlider = new Slider(0f, 1f);
            m_ThresholdSlider.AddToClassList(TracingToolbarUssClasses.TracingFilterThresholdSlider);
            m_ThresholdSlider.SetValueWithoutNotify(FuzzyThresholdSlider.ThresholdToPosition(m_State.FuzzyDiffThreshold));
            m_ThresholdSlider.RegisterValueChangedCallback(evt =>
                SetThreshold(FuzzyThresholdSlider.PositionToThreshold(evt.newValue), fromSlider: true));
            row.Add(m_ThresholdSlider);

            m_ThresholdField = new FloatField();
            m_ThresholdField.AddToClassList(TracingToolbarUssClasses.TracingFilterThresholdField);
            m_ThresholdField.SetValueWithoutNotify(m_State.FuzzyDiffThreshold);
            m_ThresholdField.RegisterValueChangedCallback(evt =>
            {
                var threshold = float.IsFinite(evt.newValue) ? Math.Max(0f, evt.newValue) : 0f;
                if (threshold != evt.newValue)
                    m_ThresholdField.SetValueWithoutNotify(threshold);
                SetThreshold(threshold, fromSlider: false);
            });
            row.Add(m_ThresholdField);
            return row;
        }

        void SetThreshold(float threshold, bool fromSlider)
        {
            if (fromSlider)
                m_ThresholdField.SetValueWithoutNotify(threshold);
            else
                m_ThresholdSlider.SetValueWithoutNotify(FuzzyThresholdSlider.ThresholdToPosition(threshold));

            if (m_State.FuzzyDiffThreshold == threshold)
                return;
            m_State.FuzzyDiffThreshold = threshold;
            FuzzyDiffThresholdChanged?.Invoke(threshold);
        }

        VisualElement CreateOptionsRow()
        {
            var row = new VisualElement();
            row.AddToClassList(TracingToolbarUssClasses.TracingFilterOptionsRow);

            m_ChangedValuesOnlyToggle = new Toggle
            {
                text = k_TextChangedValuesOnly,
                value = m_State.ChangedValuesOnly,
                tooltip = k_TooltipChangedValuesOnly,
            };
            m_ChangedValuesOnlyToggle.AddToClassList(TracingToolbarUssClasses.ChangedValuesToggle);
            m_ChangedValuesOnlyToggle.RegisterValueChangedCallback(evt =>
            {
                m_State.ChangedValuesOnly = evt.newValue;
                ChangedValuesOnlyChanged?.Invoke(evt.newValue);
            });
            row.Add(m_ChangedValuesOnlyToggle);

            m_SelectAllToggle = new Button();
            m_SelectAllToggle.AddToClassList(TracingToolbarUssClasses.Base);
            m_SelectAllToggle.AddToClassList(TracingToolbarUssClasses.TracingFilterRowToggle);
            m_SelectAllToggle.AddToClassList(TracingToolbarUssClasses.TracingFilterSelectAll);
            m_SelectAllToggle.clicked += OnSelectAllClicked;
            row.Add(m_SelectAllToggle);
            return row;
        }

        void OnSelectAllClicked()
        {
            if (m_State.ActiveTab == TracingFilterTab.DiffTags)
            {
                var allSelected = (m_State.SelectedDiffReasons & DiffReasonCatalog.SelectableMask) == DiffReasonCatalog.SelectableMask;
                m_State.SelectedDiffReasons = allSelected
                    ? m_State.SelectedDiffReasons & ~DiffReasonCatalog.SelectableMask
                    : m_State.SelectedDiffReasons | DiffReasonCatalog.SelectableMask;
                m_DiffTagListView?.RefreshItems();
                SelectedDiffReasonsChanged?.Invoke(m_State.SelectedDiffReasons);
            }
            else
            {
                if (m_State.HiddenSystems.Count + m_State.HiddenComponents.Count > 0)
                {
                    m_State.HiddenSystems.Clear();
                    m_State.HiddenComponents.Clear();
                }
                else if (m_TargetItems != null)
                {
                    foreach (var item in m_TargetItems)
                    {
                        // Untraced targets have no data yet and managed components never will: neither has
                        // anything to hide.
                        if (item.IsUntraced || item.IsManagedComponent)
                            continue;
                        var hiddenTypes = item.IsSystem ? m_State.HiddenSystems : m_State.HiddenComponents;
                        hiddenTypes.Add(item.Name);
                    }
                }

                m_TargetListView?.RefreshItems();
                VisibilityTargetsChanged?.Invoke(m_State.HiddenSystems, m_State.HiddenComponents);
            }

            RefreshSelectAllToggle();
        }

        void RefreshSelectAllToggle()
        {
            if (m_SelectAllToggle == null)
                return;

            bool allOn;
            if (m_State.ActiveTab == TracingFilterTab.DiffTags)
            {
                allOn = (m_State.SelectedDiffReasons & DiffReasonCatalog.SelectableMask) == DiffReasonCatalog.SelectableMask;
                m_SelectAllToggle.tooltip = allOn ? "Deselect all diff tags" : "Select all diff tags";
            }
            else
            {
                allOn = m_State.HiddenSystems.Count == 0 && m_State.HiddenComponents.Count == 0;
                m_SelectAllToggle.tooltip = allOn ? "Hide all tracing targets" : "Show all tracing targets";
            }

            SetEyeIcon(m_SelectAllToggle, allOn);
        }

        VisualElement CreateTabHeader()
        {
            var header = new VisualElement();
            header.AddToClassList(TracingToolbarUssClasses.TracingFilterTabHeader);
            m_TracingTargetTab = CreateTabButton(k_TextTracingTarget, TracingFilterTab.TracingTarget);
            m_DiffTagsTab = CreateTabButton(k_TextDiffTags, TracingFilterTab.DiffTags);
            header.Add(m_TracingTargetTab);
            header.Add(m_DiffTagsTab);
            return header;
        }

        Button CreateTabButton(string text, TracingFilterTab tab)
        {
            var button = new Button { text = text };
            button.AddToClassList(TracingToolbarUssClasses.TracingFilterTab);
            button.clicked += () =>
            {
                if (m_State.ActiveTab == tab)
                    return;
                m_State.ActiveTab = tab;
                ApplyActiveTab();
            };
            return button;
        }

        void ApplyActiveTab()
        {
            var targetActive = m_State.ActiveTab == TracingFilterTab.TracingTarget;
            m_TracingTargetTab?.EnableInClassList(TracingToolbarUssClasses.TracingFilterTabActive, targetActive);
            m_DiffTagsTab?.EnableInClassList(TracingToolbarUssClasses.TracingFilterTabActive, !targetActive);
            m_TargetListView?.EnableInClassList(TracingToolbarUssClasses.Hidden, !targetActive);
            m_DiffTagListView?.EnableInClassList(TracingToolbarUssClasses.Hidden, targetActive);

            m_ListView = targetActive ? m_TargetListView : m_DiffTagListView;
            RefreshSelectAllToggle();
        }

        ListView CreateDiffTagsListView()
        {
            var items = DiffReasonCatalog.All;
            var list = new ListView
            {
                itemsSource = items,
                makeItem = MakeItemRow,
                bindItem = (row, i) =>
                {
                    var entry = items[i];
                    row.Q<VisualElement>(k_TracingType).EnableInClassList(TracingToolbarUssClasses.Hidden, true);
                    row.Q<VisualElement>(k_Info).EnableInClassList(TracingToolbarUssClasses.Hidden, true);

                    row.Q<Label>(k_Name).text = entry.Label;

                    // On the row, so hovering the name explains the reason as well as hovering the eye does.
                    row.tooltip = entry.Tooltip;

                    row.EnableInClassList(TracingToolbarUssClasses.TracingFilterRowEven, i % 2 == 0);

                    var toggle = row.Q<Button>(k_Toggle);
                    toggle.AddToClassList(TracingToolbarUssClasses.Base);
                    RefreshDiffTagToggle(toggle, entry);
                    SetToggleAction(toggle, () =>
                    {
                        m_State.SelectedDiffReasons ^= entry.Reason;
                        RefreshDiffTagToggle(toggle, entry);
                        RefreshSelectAllToggle();
                        SelectedDiffReasonsChanged?.Invoke(m_State.SelectedDiffReasons);
                    });
                },
                unbindItem = (row, i) => SetToggleAction(row.Q<Button>(k_Toggle), null),
            };

            list.AddToClassList(TracingToolbarUssClasses.TracingFilterList);
            list.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
            list.selectionType = SelectionType.None;
            list.RegisterCallback<GeometryChangedEvent>(OnListViewResize);
            // A scrollbar appearing does not resize the list itself, and the select-all eye alignment depends on it.
            list.Q<ScrollView>()?.verticalScroller.RegisterCallback<GeometryChangedEvent>(OnListViewResize);
            m_DiffTagListView = list;
            return list;
        }

        void RefreshDiffTagToggle(Button toggle, DiffReasonCatalog.Entry entry)
        {
            var selected = (m_State.SelectedDiffReasons & entry.Reason) != 0;
            SetEyeIcon(toggle, selected);
            toggle.tooltip = DiffReasonCatalog.SelectionTooltip(entry, selected);
        }

        static void SetEyeIcon(Button toggle, bool visible)
        {
            toggle.EnableInClassList(TracingToolbarUssClasses.IconVisible, visible);
            toggle.EnableInClassList(TracingToolbarUssClasses.IconHidden, !visible);
        }

        static void SetToggleAction(Button toggle, Action onClicked)
        {
            if (toggle.userData is Action oldAction)
                toggle.clicked -= oldAction;
            toggle.userData = onClicked;
            if (onClicked != null)
                toggle.clicked += onClicked;
        }

        void OnClose()
        {
            m_ListView = null;
            Closed?.Invoke();
        }

        internal ListView CreateTargetListView()
        {
            m_TargetItems = CreateListItems();
            var list = new ListView
            {
                itemsSource = m_TargetItems,
                makeItem = MakeItemRow,
                bindItem = (row, i) =>
                {
                    var item = m_TargetItems[i];
                    var icon = row.Q<VisualElement>(k_TracingType);

                    icon.EnableInClassList(TracingToolbarUssClasses.IconSystem, item.IsSystem);
                    icon.EnableInClassList(TracingToolbarUssClasses.IconComponent, !item.IsSystem);

                    var lastDotIndex = item.Name.LastIndexOf('.');
                    row.Q<Label>(k_Name).text = item.Name.Substring(lastDotIndex + 1);

                    row.EnableInClassList(TracingToolbarUssClasses.TracingFilterRowEven, i % 2 == 0);

                    var toggle = row.Q<Button>(k_Toggle);
                    var info = row.Q<VisualElement>(k_Info);
                    toggle.AddToClassList(TracingToolbarUssClasses.Base);

                    // Managed component values are never captured, so there is nothing to show or hide in the diff view
                    if (item.IsManagedComponent)
                    {
                        info.EnableInClassList(TracingToolbarUssClasses.Hidden, true);
                        toggle.EnableInClassList(TracingToolbarUssClasses.Invisible, false);
                        toggle.EnableInClassList(TracingToolbarUssClasses.IconVisible, false);
                        toggle.EnableInClassList(TracingToolbarUssClasses.IconHidden, false);
                        toggle.EnableInClassList(TracingToolbarUssClasses.IconInfo, true);
                        toggle.pickingMode = PickingMode.Ignore;
                        toggle.focusable = false;
                        toggle.tooltip = string.Empty;
                        row.tooltip = k_TooltipManagedComponent;
                        SetToggleAction(toggle, null);
                        return;
                    }

                    // Rows recycle: undo the managed-badge state before applying the eye states below.
                    toggle.EnableInClassList(TracingToolbarUssClasses.IconInfo, false);
                    toggle.pickingMode = PickingMode.Position;
                    toggle.focusable = true;

                    info.EnableInClassList(TracingToolbarUssClasses.Hidden, !(item.IsUntraced || item.IsRemoved));
                    info.tooltip = item.IsUntraced ? k_TooltipUntracedTarget
                        : item.IsRemoved ? k_TooltipRemovedTarget : string.Empty;

                    if (item.IsUntraced)
                    {
                        // Invisible, not hidden: the row keeps its height and the eye column its width.
                        toggle.EnableInClassList(TracingToolbarUssClasses.Invisible, true);
                        row.tooltip = k_TooltipUntracedTarget;
                        SetToggleAction(toggle, null);
                        return;
                    }

                    // Removed targets keep a functional eye: their recorded data is still browsable.
                    toggle.EnableInClassList(TracingToolbarUssClasses.Invisible, false);
                    row.tooltip = item.IsRemoved ? k_TooltipRemovedTarget : string.Empty;
                    toggle.tooltip = k_TooltipToggleVisibility;
                    var hiddenTypes = item.IsSystem ? m_State.HiddenSystems : m_State.HiddenComponents;
                    SetEyeIcon(toggle, !hiddenTypes.Contains(item.Name));
                    SetToggleAction(toggle, () =>
                    {
                        if (!hiddenTypes.Add(item.Name))
                            hiddenTypes.Remove(item.Name);
                        SetEyeIcon(toggle, !hiddenTypes.Contains(item.Name));
                        RefreshSelectAllToggle();
                        VisibilityTargetsChanged?.Invoke(m_State.HiddenSystems, m_State.HiddenComponents);
                    });
                },
                unbindItem = (row, i) => SetToggleAction(row.Q<Button>(k_Toggle), null),
            };


            list.makeNoneElement = () =>
            {
                var row = new VisualElement();
                row.AddToClassList(TracingToolbarUssClasses.TracingFilterNoResultsContainer);
                var iconColumn = new VisualElement();
                iconColumn.AddToClassList(TracingToolbarUssClasses.TracingFilterNoResultsIcon);

                var label = new Label(k_EmptyTracingTargets);
                label.AddToClassList(TracingToolbarUssClasses.TracingFilterNoResultsLabel);
                row.Add(iconColumn);
                row.Add(label);
                return row;
            };

            list.AddToClassList(TracingToolbarUssClasses.TracingFilterList);
            list.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
            list.selectionType = SelectionType.None;
            list.RegisterCallback<GeometryChangedEvent>(OnListViewResize);
            // A scrollbar appearing does not resize the list itself, and the select-all eye alignment depends on it.
            list.Q<ScrollView>()?.verticalScroller.RegisterCallback<GeometryChangedEvent>(OnListViewResize);
            m_TargetListView = list;
            m_ListView = list;
            return list;
        }

        // Handles the resize event of the ListView to adjust the width of the dropdown menu based on the widest item and padding.
        void OnListViewResize(GeometryChangedEvent evt)
        {
            // Scroller geometry events can trail the popup closing.
            if (m_ListView == null)
                return;

            if (m_ListView.resolvedStyle.width >= m_ListViewWidth + k_ListViewPadding)
            {
                m_ListViewWidth = m_ListView.resolvedStyle.width;
            }

            var width = Math.Max(m_ListViewWidth + k_ListViewPadding, k_MinDropdownWidth);

            // The menu's ScrollView is the element the dropdown visually renders at.
            var scrollView = m_Menu?.contentContainer.GetFirstAncestorOfType<ScrollView>();
            if (scrollView == null)
                return;

            var panelWidth = scrollView.panel?.visualTree.resolvedStyle.width ?? float.NaN;
            var left = scrollView.worldBound.x;
            if (!float.IsNaN(panelWidth) && !float.IsNaN(left) && panelWidth - left > k_ListViewPadding)
                width = Math.Min(width, panelWidth - left - k_ListViewPadding);

            if (scrollView.style.width != width)
                scrollView.style.width = width;

            // Keep the select-all eye lined up with the rows' eye column: the rows' right padding, plus
            // the vertical scrollbar's actual width when the active list overflows.
            if (m_SelectAllToggle != null)
            {
                var scroller = m_ListView.Q<ScrollView>()?.verticalScroller;
                var scrollbarVisible = scroller != null && scroller.resolvedStyle.display != DisplayStyle.None;
                var scrollerWidth = scrollbarVisible && !float.IsNaN(scroller.resolvedStyle.width) ? scroller.resolvedStyle.width : 0f;
                m_SelectAllToggle.style.marginRight = k_RowRightPadding + scrollerWidth;
            }
        }

        internal VisualElement MakeItemRow()
        {
            var row = m_FilterRowTemplate.Instantiate();
            row.AddToClassList(TracingToolbarUssClasses.TracingFilterRow);
            return row;
        }
    }
}

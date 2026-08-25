using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode.Editor.Tracing.UI;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static Unity.NetCode.Editor.Tracing.TracingColumnFilters;

namespace Unity.NetCode.Editor.Tracing
{
    /// <summary>
    /// Editor window to pick ECS systems and components for tracing. The selection lives in
    /// <see cref="TracingTargetSelection"/>, the column value filters in <see cref="TracingColumnFilters"/>;
    /// this class owns the widgets and the displayed-row pipeline (tab, search, column filters, sort).
    /// </summary>
    internal class SelectTracingTargetWindow : EditorWindow
    {
        internal enum TabKind : byte
        {
            All = 0,
            Systems = 1,
            Components = 2,
            Selected = 3
        }

        internal readonly struct ListEntry
        {
            public readonly Type Type;
            public readonly TracingTargetKind Kind;
            public readonly ComponentDataShapeKind ComponentShape;

            public ListEntry(Type type, TracingTargetKind kind, ComponentDataShapeKind componentShape = ComponentDataShapeKind.NotApplicable)
            {
                Type = type;
                Kind = kind;
                ComponentShape = componentShape;
            }
        }

        internal const string k_WindowTitle = "Select Tracing Target";

        const string k_UssClassTracingSelectTargetWindowRoot = "tracing-select-target-window-root";
        const string k_UssClassTracingTabHeaderSpacer = "tracing-tab-header-spacer";
        const string k_UssClassTracingTargetTabView = "tracing-target-tab-view";
        const string k_UssClassTracingTabKindIcon = "tracing-tab-kind-icon";
        const string k_UssClassTracingTabKindIconSystem = "tracing-tab-kind-icon--system";
        const string k_UssClassTracingTabKindIconComponent = "tracing-tab-kind-icon--component";
        const string k_UssClassTracingRowKindIconSystem = "tracing-row-kind-icon--system";
        const string k_UssClassTracingRowKindIconComponent = "tracing-row-kind-icon--component";
        const string k_UssClassTracingListRowModeAll = "tracing-list-row--mode-all";
        const string k_UssClassTracingListRowModeSystems = "tracing-list-row--mode-systems";
        const string k_UssClassTracingListRowModeComponents = "tracing-list-row--mode-components";
        const string k_UssClassTracingListRowModeSelected = "tracing-list-row--mode-selected";
        const string k_UssClassTracingMctvNameCell = "tracing-mctv-name-cell";
        const string k_UssClassTracingMctvSelectColumnHeaderCell = "tracing-mctv-select-column-header-cell";
        const string k_UssClassTracingMctvFilterHeaderContent = "tracing-mctv-filter-header-content";
        const string k_UssClassTracingMctvFilterHeaderTitle = "tracing-mctv-filter-header-title";
        internal const string k_UssClassTracingHeaderFilterButton = "tracing-header-filter-button";
        const string k_UssClassUnityIconArrow = "unity-icon-arrow";
        const string k_UssClassUnityPopupFieldArrow = "unity-base-popup-field__arrow";

        const string k_UssNameTracingTabView = "tracing-tab-view";
        const string k_UssNameTracingTabKindIcon = "tracing-tab-kind-icon";
        const string k_UssNameTracingTabPrefix = "tracing-tab-";
        const string k_UssNameTracingSearchContainer = "tracing-search-container";
        const string k_UssNameTracingHeaderSelectAllToggle = "tracing-header-select-all-toggle";
        const string k_UssNameTracingMainListView = "tracing-main-list-view";
        const string k_UssNameTracingStatusLabel = "tracing-status-label";
        const string k_UssNameTracingRowToggle = "tracing-row-toggle";
        const string k_UssNameTracingRowKindIcon = "tracing-row-kind-icon";
        internal const string k_UssNameTracingRowSceneVisIcon = "tracing-row-scene-vis-icon";
        const string k_UssNameTracingRowLabel = "tracing-row-label";
        const string k_UssClassTracingFlagCell = "tracing-mctv-flag-cell";
        const string k_UssNameTracingFlagToggle = "tracing-flag-toggle";

        const string k_PredictedSupportTooltip =
            "Tracing only supports systems running in the prediction group. "
            + "You can still select systems outside of the prediction group (disable the Predicted filter to show them) "
            + "if you will be moving them into it at runtime, like netcode does for some physics systems.";
        const string k_UnmanagedSupportTooltip =
            "Tracing only captures unmanaged component values. "
            + "You can still select managed components (disable the Unmanaged filter to show them) "
            + "to filter which ghosts are traced when marked as required; their values are not captured or diffed.";
        const string k_SceneVisIconTooltip =
            "Trace this component to visualize the ghosts in the Scene view. "
            + "Click to open the Netcode Tracing overlay in the Scene view.";
        const int k_ComponentFilterToggleRequiredIndex = 0;

        [SerializeField]
        StyleSheet m_StyleSheet;
        [SerializeField]
        StyleSheet m_VariablesDarkSheet;
        [SerializeField]
        StyleSheet m_VariablesLightSheet;

        static readonly TabKind[] k_TabOrder =
        {
            TabKind.All,
            TabKind.Systems,
            TabKind.Components,
            TabKind.Selected
        };

        static readonly string[] k_UssTracingListRowModeClasses =
        {
            k_UssClassTracingListRowModeAll,
            k_UssClassTracingListRowModeSystems,
            k_UssClassTracingListRowModeComponents,
            k_UssClassTracingListRowModeSelected
        };

        TabView m_TabView;
        readonly List<Tab> m_Tabs = new();
        ToolbarSearchField m_SearchField;
        VisualElement m_HeaderSelectAllCell;
        Toggle m_HeaderSelectAllToggle;
        MultiColumnTreeView m_TreeView;
        readonly List<TreeViewItemData<ListEntry>> m_TreeRootItems = new();
        Label m_StatusLabel;

        readonly List<Type> m_SystemTypes = new();
        readonly List<Type> m_ComponentTypes = new();
        TabKind m_ActiveTab = TabKind.All;
        readonly Vector2[] m_TracingTargetTabScrollOffsets = new Vector2[k_TabOrder.Length];
        string m_Search = string.Empty;
        readonly HashSet<Type> m_SystemTypeSet = new();
        readonly Dictionary<string, Type> m_TypeByKey = new(StringComparer.Ordinal);
        readonly List<ListEntry> m_DisplayEntries = new();
        /// <summary>Distinct, FullName-sorted union of <see cref="m_SystemTypes"/> and <see cref="m_ComponentTypes"/>. Rebuilt by <see cref="RebuildCaches"/>.</summary>
        readonly List<Type> m_AllTypes = new();
        readonly List<Type> m_ScratchFilterList = new();

        readonly TracingTargetSelection m_Selection = new();
        readonly TracingColumnFilters m_ColumnFilters = new();
        readonly Dictionary<Type, ComponentDataShapeKind> m_ComponentShapeByType = new();

        // Complete entry list pre filters
        readonly List<ListEntry> m_DisplayEntriesUnsortedCache = new();

        readonly Dictionary<string, EditorToolbarDropdownToggle> m_HeaderFilterButtonsByColumn = new(StringComparer.Ordinal);

        readonly List<SortColumnDescription> m_SortedDescriptionScratchpad = new();
        bool m_ApplyingTracingTreeColumnSortReorder;
        bool m_ApplyingTracingTabColumnVisibility;

        internal IReadOnlyList<ListEntry> DisplayEntries => m_DisplayEntries;
        internal MultiColumnTreeView TracingTreeView => m_TreeView;
        internal TracingTargetSelection Selection => m_Selection;
        internal TracingColumnFilters ColumnFilters => m_ColumnFilters;

        internal static void ShowWindow()
        {
            var window = GetWindow<SelectTracingTargetWindow>();
            window.titleContent = new GUIContent(k_WindowTitle);
            window.minSize = new Vector2(480, 360);
            window.Show();
        }

        void OnDestroy()
        {
            if (m_TabView != null)
            {
                m_TabView.activeTabChanged -= OnTracingActiveTabChanged;
            }

            if (m_TreeView != null)
            {
                m_TreeView.columnSortingChanged -= OnTracingTreeColumnSortingChanged;
            }
        }

        public void CreateGUI()
        {
            LoadStylesheets();

            rootVisualElement.AddToClassList(k_UssClassTracingSelectTargetWindowRoot);

            RebuildCaches();
            m_Selection.Load(TryResolveType);
            // CreateGUI re-runs on the same instance when the backing panel is recreated (dock/maximize).
            m_ColumnFilters.Changed -= OnColumnFilterChanged;
            m_ColumnFilters.Changed += OnColumnFilterChanged;

            BuildSearchRow();
            BuildTabView();
            BuildTracingMultiColumnTreeView();
            BuildStatusLabel();

            RefreshDisplay();
        }

        void LoadStylesheets()
        {
            rootVisualElement.styleSheets.Add(m_StyleSheet);
            var ussVariables = EditorGUIUtility.isProSkin ? m_VariablesDarkSheet : m_VariablesLightSheet;
            rootVisualElement.styleSheets.Add(ussVariables);
        }

        void BuildTabView()
        {
            m_TabView = new TabView { name = k_UssNameTracingTabView };
            m_TabView.AddToClassList(k_UssClassTracingTargetTabView);
            m_Tabs.Clear();
            foreach (var tabKind in k_TabOrder)
            {
                var tab = new Tab(string.Empty)
                {
                    name = $"{k_UssNameTracingTabPrefix}{tabKind}",
                    userData = tabKind
                };
                var tabLabelSpacer = new VisualElement { pickingMode = PickingMode.Ignore };
                tabLabelSpacer.AddToClassList(k_UssClassTracingTabHeaderSpacer);
                tab.Add(tabLabelSpacer);
                m_TabView.Add(tab);
                m_Tabs.Add(tab);
                InsertTracingTabHeaderKindIcon(tab, tabKind);
            }

            m_TabView.activeTabChanged += OnTracingActiveTabChanged;
            rootVisualElement.Add(m_TabView);
            RefreshTabLabels();
        }

        static void InsertTracingTabHeaderKindIcon(Tab tab, TabKind tabKind)
        {
            if (tabKind != TabKind.Systems && tabKind != TabKind.Components)
            {
                return;
            }

            var header = tab.tabHeader;
            if (header == null || header.Q<VisualElement>(k_UssNameTracingTabKindIcon) != null)
            {
                return;
            }

            var icon = new VisualElement { name = k_UssNameTracingTabKindIcon, pickingMode = PickingMode.Ignore };
            icon.AddToClassList(k_UssClassTracingTabKindIcon);
            icon.AddToClassList(tabKind == TabKind.Systems ? k_UssClassTracingTabKindIconSystem : k_UssClassTracingTabKindIconComponent);
            header.Insert(0, icon);
        }

        void OnTracingActiveTabChanged(Tab oldTab, Tab newTab)
        {
            if (oldTab?.userData is TabKind previousKind)
            {
                SaveTracingTargetTabScrollOffset(previousKind);
            }

            if (newTab?.userData is TabKind k)
            {
                m_ActiveTab = k;
                RefreshDisplay();
                ScheduleRestoreTracingTargetTabScrollOffset(k);
            }
        }

        ScrollView GetTracingTargetTreeListScrollView() => m_TreeView.Q<ScrollView>(className: BaseVerticalCollectionView.listScrollViewUssClassName);

        void SaveTracingTargetTabScrollOffset(TabKind tab)
        {
            var scrollView = GetTracingTargetTreeListScrollView();
            if (scrollView == null)
            {
                return;
            }

            m_TracingTargetTabScrollOffsets[(int)tab] = scrollView.scrollOffset;
        }

        void ScheduleRestoreTracingTargetTabScrollOffset(TabKind tab)
        {
            var offset = m_TracingTargetTabScrollOffsets[(int)tab];
            m_TreeView.schedule.Execute(() =>
            {
                if (m_ActiveTab != tab)
                {
                    return;
                }

                var scrollView = GetTracingTargetTreeListScrollView();
                if (scrollView == null)
                {
                    return;
                }

                scrollView.scrollOffset = offset;
            });
        }

        void BuildSearchRow()
        {
            var searchWrap = new VisualElement { name = k_UssNameTracingSearchContainer };
            m_SearchField = new ToolbarSearchField();
            m_SearchField.SetValueWithoutNotify(m_Search);
            m_SearchField.RegisterValueChangedCallback(evt =>
            {
                m_Search = evt.newValue ?? string.Empty;
                RefreshDisplay();
            });
            searchWrap.Add(m_SearchField);
            rootVisualElement.Add(searchWrap);
        }

        void OnHeaderSelectAllPointerDown(PointerDownEvent evt)
        {
            if (evt.currentTarget != m_HeaderSelectAllToggle)
            {
                return;
            }
            if (m_DisplayEntries.Count == 0)
            {
                evt.StopImmediatePropagation();
                return;
            }

            var allDisplayedSelected = true;
            foreach (var e in m_DisplayEntries)
            {
                if (!m_Selection.IsSelected(e.Type))
                {
                    allDisplayedSelected = false;
                    break;
                }
            }

            ApplySelectAllDisplayed(!allDisplayedSelected);
            RefreshAfterSelectionChange();
            evt.StopImmediatePropagation();
        }

        void RefreshHeaderSelectAllToggleState()
        {
            var selectedCount = 0;
            foreach (var e in m_DisplayEntries)
            {
                if (m_Selection.IsSelected(e.Type))
                {
                    selectedCount++;
                }
            }

            m_HeaderSelectAllToggle.SetEnabled(m_DisplayEntries.Count > 0);
            m_HeaderSelectAllToggle.showMixedValue = selectedCount > 0 && selectedCount < m_DisplayEntries.Count;
            m_HeaderSelectAllToggle.SetValueWithoutNotify(selectedCount > 0 && selectedCount == m_DisplayEntries.Count);
        }

        void ApplySelectAllDisplayed(bool select)
        {
            if (m_DisplayEntries.Count == 0)
            {
                return;
            }

            foreach (var entry in m_DisplayEntries)
            {
                m_Selection.Update(entry.Type, entry.Kind, select);
            }

            m_Selection.Save();
        }

        void BuildTracingMultiColumnTreeView()
        {
            m_TreeView = new MultiColumnTreeView
            {
                name = k_UssNameTracingMainListView,
                selectionType = SelectionType.None,
                showAlternatingRowBackgrounds = AlternatingRowBackground.All,
                style = { flexGrow = 1, minHeight = 120 }
            };

            m_HeaderSelectAllCell = new VisualElement();
            m_HeaderSelectAllCell.AddToClassList(k_UssClassTracingMctvSelectColumnHeaderCell);
            m_HeaderSelectAllToggle = new Toggle { name = k_UssNameTracingHeaderSelectAllToggle, text = string.Empty };
            m_HeaderSelectAllToggle.RegisterCallback<PointerDownEvent>(OnHeaderSelectAllPointerDown, TrickleDown.TrickleDown);
            m_HeaderSelectAllCell.Add(m_HeaderSelectAllToggle);

            SetupTracingTreeColumns();

            m_TreeView.columnSortingChanged += OnTracingTreeColumnSortingChanged;
            m_TreeView.SetRootItems(m_TreeRootItems);
            // Custom + explicit reorder: Unity's MultiColumn TreeView/Default sort relies on interactive headers, and won't work here.
            m_TreeView.sortingMode = ColumnSortingMode.Custom;
            rootVisualElement.Add(m_TreeView);
        }

        void SetupTracingTreeColumns()
        {
            Column selectColumn = new()
            {
                name = k_ColumnTracingSelectAll,
                width = 40,
                resizable = false,
                sortable = false,
                makeHeader = () => m_HeaderSelectAllCell,
                bindHeader = _ => RefreshHeaderSelectAllToggleState(),
                makeCell = () =>
                {
                    var toggle = new Toggle { name = k_UssNameTracingRowToggle };
                    toggle.RegisterValueChangedCallback(OnRowToggleChanged);
                    return toggle;
                },
                bindCell = BindTracingSelectTreeCell,
                stretchable = false
            };

            Column nameColumn = new()
            {
                name = k_ColumnTracingName,
                title = "Name",
                bindHeader = headerRoot => headerRoot.style.flexGrow = 1f,
                makeCell = MakeTracingNameTreeCell,
                bindCell = BindTracingNameTreeCell,
                resizable = true,
                stretchable = true,
                width = 200,
                minWidth = 120
            };

            Column worldColumn = new()
            {
                name = k_ColumnTracingWorld,
                title = "World",
                width = 132,
                resizable = true,
                makeCell = () => MakeTracingTypedCellLabel(k_ColumnTracingWorld),
                bindCell = (element, index) => BindTracingTextCell(element, k_ColumnTracingWorld, index)
            };

            Column predictedColumn = new()
            {
                name = k_ColumnTracingPredicted,
                title = "Predicted",
                width = 112,
                resizable = true,
                makeCell = MakeTracingFlagTreeCell,
                bindCell = BindTracingPredictedTreeCell
            };

            Column unmanagedColumn = new()
            {
                name = k_ColumnTracingUnmanaged,
                title = "Unmanaged",
                width = 112,
                resizable = true,
                makeCell = MakeTracingFlagTreeCell,
                bindCell = BindTracingUnmanagedTreeCell
            };

            Column namespaceColumn = new()
            {
                name = k_ColumnTracingNamespace,
                title = "Namespace",
                width = 200,
                resizable = true,
                makeCell = () => MakeTracingTypedCellLabel(k_ColumnTracingNamespace),
                bindCell = (element, index) => BindTracingTextCell(element, k_ColumnTracingNamespace, index)
            };

            Column typeShapeColumn = new()
            {
                name = k_ColumnTracingType,
                title = "Type",
                width = 56,
                resizable = true,
                makeCell = () => MakeTracingTypedCellLabel(k_ColumnTracingType),
                bindCell = (element, index) => BindTracingTextCell(element, k_ColumnTracingType, index)
            };

            Column filteringOptionsColumn = new()
            {
                name = k_ColumnTracingFilteringOptions,
                title = "Filtering Options",
                width = 168,
                resizable = false,
                stretchable = false,
                makeCell = MakeTracingFilteringOptionsTreeCell,
                bindCell = BindTracingFilteringOptionsTreeCell
            };

            // Filterable columns replace the default header content with a title + filter dropdown toggle;
            // click-to-sort and the sort indicator live on the surrounding built-in header and are unaffected.
            foreach (var column in new[] { worldColumn, predictedColumn, unmanagedColumn, namespaceColumn })
            {
                var columnName = column.name;
                var columnTitle = column.title;
                column.makeHeader = () => MakeTracingFilterableColumnHeaderContent(columnName, columnTitle);
                // A hidden column suspends its value filter
                column.propertyChanged += (_, args) =>
                {
                    if (args.propertyName == nameof(Column.visible))
                    {
                        OnTracingColumnVisibilityChanged(columnName);
                    }
                };
            }

            // Allow every column to still be resizable despite the changing amount
            m_TreeView.columns.stretchMode = Columns.StretchMode.Grow;

            m_TreeView.columns.Add(selectColumn);
            m_TreeView.columns.Add(nameColumn);
            m_TreeView.columns.Add(worldColumn);
            m_TreeView.columns.Add(namespaceColumn);
            m_TreeView.columns.Add(typeShapeColumn);
            m_TreeView.columns.Add(filteringOptionsColumn);
            m_TreeView.columns.Add(predictedColumn);
            m_TreeView.columns.Add(unmanagedColumn);
        }

        /// <summary>
        /// Header content for filterable columns: the column title plus a filter button built from
        /// <see cref="EditorToolbarDropdownToggle"/> — the same control as the scene-view toolbar dropdowns. The
        /// toggle half enables/disables the column's value filter without discarding its configuration; the arrow
        /// half opens <see cref="TracingColumnFilterPopupWindow"/> to pick which values stay visible.
        /// </summary>
        VisualElement MakeTracingFilterableColumnHeaderContent(string columnName, string columnTitle)
        {
            var content = new VisualElement();
            content.AddToClassList(k_UssClassTracingMctvFilterHeaderContent);
            if (columnName == k_ColumnTracingPredicted)
            {
                content.tooltip = k_PredictedSupportTooltip;
            }
            else if (columnName == k_ColumnTracingUnmanaged)
            {
                content.tooltip = k_UnmanagedSupportTooltip;
            }

            var title = new Label(columnTitle) { pickingMode = PickingMode.Ignore };
            title.AddToClassList(k_UssClassTracingMctvFilterHeaderTitle);
            content.Add(title);

            var filterButton = new EditorToolbarDropdownToggle
            {
                tooltip = $"Filter by {columnTitle}. Click the icon to toggle the filter, the arrow to choose the shown values."
            };
            filterButton.AddToClassList(k_UssClassTracingHeaderFilterButton);
            filterButton.SetValueWithoutNotify(m_ColumnFilters.IsColumnFilterEnabled(columnName));
            filterButton.Q(className: k_UssClassUnityIconArrow)?.AddToClassList(k_UssClassUnityPopupFieldArrow);
            // Clicking the header sorts the column; the filter control must swallow its own pointer events.
            filterButton.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            filterButton.RegisterValueChangedCallback(evt => m_ColumnFilters.SetColumnFilterEnabled(columnName, evt.newValue));
            filterButton.dropdownClicked += () => OpenTracingColumnFilterPopup(columnName, columnTitle, filterButton);
            m_HeaderFilterButtonsByColumn[columnName] = filterButton;
            content.Add(filterButton);
            return content;
        }

        void RefreshTracingHeaderFilterButton(string columnName)
        {
            if (m_HeaderFilterButtonsByColumn.TryGetValue(columnName, out var filterButton))
            {
                filterButton.SetValueWithoutNotify(m_ColumnFilters.IsColumnFilterEnabled(columnName));
            }
        }

        void OpenTracingColumnFilterPopup(string columnName, string columnTitle, VisualElement activator)
        {
            var activatorScreenRect = new Rect(position.position + activator.worldBound.position, activator.worldBound.size);
            var values = new List<string>();
            CollectUniqueColumnFilterValues(columnName, values);
            TracingColumnFilterPopupWindow.Open(m_ColumnFilters, columnName, columnTitle, values, rootVisualElement, activatorScreenRect);
        }

        /// <summary>
        /// Uses <see cref="ToggleButtonGroup"/> (segmented mutually exclusive toggles), matching Unity Editor
        /// patterns such as Package Manager Library filtering.
        /// </summary>
        VisualElement MakeTracingFilteringOptionsTreeCell()
        {
            var wrap = new VisualElement();
            wrap.AddToClassList("tracing-component-filter-cell");

            var group = new ToggleButtonGroup(string.Empty)
            {
                isMultipleSelection = false,
                allowEmptySelection = false
            };
            group.AddToClassList("tracing-component-filter-toggle-group");
            group.Add(new Button { text = "Required" });
            group.Add(new Button { text = "Optional" });
            group.RegisterValueChangedCallback(OnComponentFilteringToggleGroupChanged);
            wrap.Add(group);
            return wrap;
        }

        void BindTracingFilteringOptionsTreeCell(VisualElement element, int index)
        {
            var group = element.Q<ToggleButtonGroup>();
            group.userData = index;

            var isComponent = TryGetDisplayedEntry(index, out var entry) && entry.Kind == TracingTargetKind.Component;
            TracingWindowUtility.SetHidden(group, !isComponent);
            if (!isComponent)
            {
                return;
            }

            var required = m_Selection.GetComponentRequired(entry.Type);
            var state = ToggleButtonGroupState.CreateFromOptions(new List<bool> { required, !required });
            group.SetValueWithoutNotify(state);
        }

        void OnComponentFilteringToggleGroupChanged(ChangeEvent<ToggleButtonGroupState> evt)
        {
            if (evt.target is not ToggleButtonGroup group || group.userData is not int index)
            {
                return;
            }

            if (!TryGetDisplayedEntry(index, out var entry) || entry.Kind != TracingTargetKind.Component)
            {
                return;
            }

            var state = evt.newValue;
            Span<int> activeScratch = stackalloc int[state.length];
            var active = state.GetActiveOptions(activeScratch);
            if (active.Length == 0)
            {
                return;
            }

            var required = active[0] == k_ComponentFilterToggleRequiredIndex;
            m_Selection.SetComponentRequired(entry.Type, required);
        }

        /// <summary>For Edit Mode tests: updates the persisted component requirement when the type is selected for tracing.</summary>
        internal void SetComponentTracingRequirement(Type componentType, bool required)
        {
            if (componentType == null || !m_Selection.IsSelected(componentType))
            {
                return;
            }

            // Selected system types are rejected by SetComponentRequired itself.
            m_Selection.SetComponentRequired(componentType, required);
            m_TreeView?.RefreshItems();
        }

        static Label MakeTracingTypedCellLabel(string elementName)
        {
            var label = new Label { name = elementName };
            label.AddToClassList("tracing-mctv-cell-label");
            label.AddToClassList($"tracing-mctv-cell-label--{elementName}");
            return label;
        }

        internal VisualElement MakeTracingNameTreeCell()
        {
            var wrap = new VisualElement { name = "tracing-name-cell-root" };
            wrap.AddToClassList(k_UssClassTracingMctvNameCell);

            wrap.Add(new VisualElement
            {
                name = k_UssNameTracingRowKindIcon,
                pickingMode = PickingMode.Ignore
            });

            var sceneVisIcon = new Button(FocusSceneViewWithTracingOverlay)
            {
                name = k_UssNameTracingRowSceneVisIcon,
                tooltip = k_SceneVisIconTooltip
            };
            sceneVisIcon.RemoveFromClassList(Button.ussClassName);
            wrap.Add(sceneVisIcon);

            wrap.Add(new Label { name = k_UssNameTracingRowLabel });
            return wrap;
        }

        internal static void FocusSceneViewWithTracingOverlay()
        {
            var sceneView = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView : GetWindow<SceneView>();
            sceneView.Focus();
            if (sceneView.TryGetOverlay(SceneVisualizationOverlay.k_OverlayId, out var overlay))
            {
                overlay.displayed = true;
            }
        }

        void BindTracingSelectTreeCell(VisualElement element, int index)
        {
            var toggle = (Toggle)element;
            toggle.userData = index;
            if (TryGetDisplayedEntry(index, out var entry))
            {
                toggle.SetValueWithoutNotify(m_Selection.IsSelected(entry.Type));
            }
        }

        internal void BindTracingNameTreeCell(VisualElement element, int index)
        {
            if (!TryGetDisplayedEntry(index, out var entry))
            {
                return;
            }

            ApplyNameTreeCellTabModeClass(element);
            var kindIcon = element.Q<VisualElement>(k_UssNameTracingRowKindIcon);
            var sceneVisIcon = element.Q<VisualElement>(k_UssNameTracingRowSceneVisIcon);
            var label = element.Q<Label>(k_UssNameTracingRowLabel);
            TracingWindowUtility.SetHidden(sceneVisIcon, entry.Type != typeof(LocalTransform));
            kindIcon.RemoveFromClassList(k_UssClassTracingRowKindIconSystem);
            kindIcon.RemoveFromClassList(k_UssClassTracingRowKindIconComponent);

            if (m_ActiveTab == TabKind.All || m_ActiveTab == TabKind.Selected)
            {
                kindIcon.AddToClassList(entry.Kind == TracingTargetKind.System ? k_UssClassTracingRowKindIconSystem : k_UssClassTracingRowKindIconComponent);
                kindIcon.tooltip = entry.Kind == TracingTargetKind.System ? "System" : "Component";
            }
            else
            {
                kindIcon.tooltip = string.Empty;
            }

            label.text = entry.Type.Name;
        }

        void BindTracingTextCell(VisualElement element, string columnName, int index)
        {
            if (TryGetDisplayedEntry(index, out var entry))
            {
                ((Label)element).text = FormatColumnText(columnName, entry);
            }
        }

        /// <summary>
        /// Read-only informative checkbox cell shared by the Predicted and Unmanaged columns; the tooltip lives
        /// on the wrapping cell so it shows over the disabled toggle.
        /// </summary>
        static VisualElement MakeTracingFlagTreeCell()
        {
            var wrap = new VisualElement();
            wrap.AddToClassList(k_UssClassTracingFlagCell);
            var toggle = new Toggle { name = k_UssNameTracingFlagToggle };
            toggle.SetEnabled(false);
            wrap.Add(toggle);
            return wrap;
        }

        void BindTracingPredictedTreeCell(VisualElement element, int index)
        {
            var isSystem = TryGetDisplayedEntry(index, out var entry) && entry.Kind == TracingTargetKind.System;
            BindTracingFlagTreeCell(element, isSystem, isSystem && TracingTargetTypes.IsPredictionSystem(entry.Type), k_PredictedSupportTooltip);
        }

        void BindTracingUnmanagedTreeCell(VisualElement element, int index)
        {
            var isComponent = TryGetDisplayedEntry(index, out var entry) && entry.Kind == TracingTargetKind.Component;
            BindTracingFlagTreeCell(element, isComponent, isComponent && TracingTargetTypes.IsUnmanagedComponentType(entry.Type), k_UnmanagedSupportTooltip);
        }

        static void BindTracingFlagTreeCell(VisualElement element, bool hasFlagValue, bool flagValue, string tooltip)
        {
            var toggle = element.Q<Toggle>(k_UssNameTracingFlagToggle);
            if (!hasFlagValue)
            {
                TracingWindowUtility.SetHidden(toggle, true);
                element.tooltip = string.Empty;
                return;
            }

            TracingWindowUtility.SetHidden(toggle, false);
            toggle.SetValueWithoutNotify(flagValue);
            element.tooltip = tooltip;
        }

        bool TryGetDisplayedEntry(int index, out ListEntry entry)
        {
            if (index >= 0 && index < m_DisplayEntries.Count)
            {
                entry = m_DisplayEntries[index];
                return true;
            }

            entry = default;
            return false;
        }

        void ApplyNameTreeCellTabModeClass(VisualElement wrap)
        {
            for (var i = 0; i < k_UssTracingListRowModeClasses.Length; i++)
            {
                wrap.RemoveFromClassList(k_UssTracingListRowModeClasses[i]);
            }

            wrap.AddToClassList(m_ActiveTab switch
            {
                TabKind.All => k_UssClassTracingListRowModeAll,
                TabKind.Systems => k_UssClassTracingListRowModeSystems,
                TabKind.Components => k_UssClassTracingListRowModeComponents,
                TabKind.Selected => k_UssClassTracingListRowModeSelected,
                _ => k_UssClassTracingListRowModeAll
            });
        }

        void RebuildTracingTreeRootItems()
        {
            m_TreeRootItems.Clear();
            for (var i = 0; i < m_DisplayEntries.Count; i++)
            {
                m_TreeRootItems.Add(new TreeViewItemData<ListEntry>(i, m_DisplayEntries[i]));
            }

            m_TreeView.SetRootItems(m_TreeRootItems);
        }

        void OnTracingTreeColumnSortingChanged()
        {
            if (m_ApplyingTracingTreeColumnSortReorder)
            {
                return;
            }

            m_ApplyingTracingTreeColumnSortReorder = true;
            try
            {
                m_SortedDescriptionScratchpad.Clear();
                if (m_TreeView.sortedColumns != null)
                {
                    foreach (var item in m_TreeView.sortedColumns)
                    {
                        if (item is SortColumnDescription descriptor)
                        {
                            m_SortedDescriptionScratchpad.Add(descriptor);
                        }
                    }
                }

                m_DisplayEntries.Clear();
                foreach (var entry in m_DisplayEntriesUnsortedCache)
                {
                    if (!IsEntryHiddenByColumnValueFilters(entry))
                    {
                        m_DisplayEntries.Add(entry);
                    }
                }

                // If no column is sorted, pin the localTransformRow as the first Row for scene visualization discoverability
                if (m_SortedDescriptionScratchpad.Count > 0)
                {
                    m_DisplayEntries.Sort(CompareSortedTracingDisplayEntries);
                }
                else
                {
                    PinLocalTransformEntryFirst();
                }

                RebuildTracingTreeRootItems();
                m_TreeView.Rebuild();
                RefreshHeaderSelectAllToggleState();
            }
            finally
            {
                m_ApplyingTracingTreeColumnSortReorder = false;
            }
        }

        void PinLocalTransformEntryFirst()
        {
            for (var i = 1; i < m_DisplayEntries.Count; i++)
            {
                if (m_DisplayEntries[i].Type != typeof(LocalTransform))
                {
                    continue;
                }

                var entry = m_DisplayEntries[i];
                m_DisplayEntries.RemoveAt(i);
                m_DisplayEntries.Insert(0, entry);
                return;
            }
        }

        string ResolveSortColumnKeyFromDescription(SortColumnDescription descriptor)
        {
            if (!string.IsNullOrEmpty(descriptor.columnName))
            {
                return descriptor.columnName;
            }

            var columnReference = descriptor.column;
            if (columnReference != null && !string.IsNullOrEmpty(columnReference.name))
            {
                return columnReference.name;
            }

            var index = descriptor.columnIndex;
            if (index >= 0 && index < m_TreeView.columns.Count)
            {
                return m_TreeView.columns[index].name;
            }

            return string.Empty;
        }

        int CompareSortedTracingDisplayEntries(ListEntry a, ListEntry b)
        {
            foreach (var descriptor in m_SortedDescriptionScratchpad)
            {
                var columnKey = ResolveSortColumnKeyFromDescription(descriptor);
                var cmp = string.Compare(FormatColumnText(columnKey, a), FormatColumnText(columnKey, b), StringComparison.OrdinalIgnoreCase);
                if (cmp == 0)
                {
                    continue;
                }

                return descriptor.direction == SortDirection.Descending ? -cmp : cmp;
            }

            return 0;
        }

        string FormatColumnText(string columnName, ListEntry entry) =>
            columnName switch
            {
                k_ColumnTracingName => entry.Type.Name,
                k_ColumnTracingWorld => entry.Kind == TracingTargetKind.System
                    ? TracingTargetTypes.WorldDisplayText(entry.Type)
                    : k_ColumnFilterNoValue,
                k_ColumnTracingPredicted => entry.Kind != TracingTargetKind.System
                    ? k_ColumnFilterNoValue
                    : TracingTargetTypes.IsPredictionSystem(entry.Type) ? k_PredictedFilterValuePredicted : k_PredictedFilterValueNotPredicted,
                k_ColumnTracingUnmanaged => entry.Kind != TracingTargetKind.Component
                    ? k_ColumnFilterNoValue
                    : TracingTargetTypes.IsUnmanagedComponentType(entry.Type) ? k_UnmanagedFilterValueUnmanaged : k_UnmanagedFilterValueManaged,
                k_ColumnTracingNamespace => string.IsNullOrEmpty(entry.Type.Namespace) ? k_ColumnFilterNoValue : entry.Type.Namespace,
                k_ColumnTracingType => entry.Kind == TracingTargetKind.Component && entry.ComponentShape != ComponentDataShapeKind.NotApplicable
                    ? entry.ComponentShape.ToString()
                    : k_ColumnFilterNoValue,
                // Systems sort below every component value in the Filtering Options column.
                k_ColumnTracingFilteringOptions => entry.Kind != TracingTargetKind.Component
                    ? "\uFFFF"
                    : m_Selection.GetComponentRequired(entry.Type) ? "Required" : "Optional",
                _ => string.Empty
            };

        void OnColumnFilterChanged(string columnName)
        {
            RefreshTracingHeaderFilterButton(columnName);
            OnTracingTreeColumnSortingChanged();
            RefreshStatusLabel();
        }

        void OnTracingColumnVisibilityChanged(string columnName)
        {
            if (m_ApplyingTracingTabColumnVisibility)
            {
                return;
            }

            // Showing or hiding the column only changes the rows while it carries an active value filter.
            if (!m_ColumnFilters.IsColumnFilterEnabled(columnName) || !m_ColumnFilters.HasHiddenValues(columnName))
            {
                return;
            }

            OnTracingTreeColumnSortingChanged();
            RefreshStatusLabel();
        }

        internal void CollectUniqueColumnFilterValues(string columnName, List<string> into)
        {
            into.Clear();
            if (!IsColumnValueFilterable(columnName))
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in m_DisplayEntriesUnsortedCache)
            {
                var value = FormatColumnText(columnName, entry);
                if (seen.Add(value))
                {
                    into.Add(value);
                }
            }

            var hasPlaceholder = into.Remove(k_ColumnFilterNoValue);
            into.Sort(static (a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));

            if (columnName == k_ColumnTracingNamespace)
            {
                into.Add(k_NamespaceFilterPatternUnityPrefix);
                into.Add(k_NamespaceFilterPatternGenerated);
            }

            // Rows without a value for the column (components) are ignored by that column's filter;
            if (hasPlaceholder && !ColumnFilterIgnoresPlaceholderRows(columnName))
            {
                into.Add(k_ColumnFilterNoValue);
            }
        }

        bool IsEntryHiddenByColumnValueFilters(ListEntry entry)
        {
            foreach (var columnName in k_FilterableColumns)
            {
                if (!m_ColumnFilters.HasHiddenValues(columnName)
                    || !m_ColumnFilters.IsColumnFilterEnabled(columnName)
                    || !IsTracingColumnCurrentlyVisible(columnName))
                {
                    continue;
                }

                var value = FormatColumnText(columnName, entry);
                // Rows without a value for the column (components) are ignored by that column's filter;
                if (ColumnFilterIgnoresPlaceholderRows(columnName) && value == k_ColumnFilterNoValue)
                {
                    continue;
                }

                if (m_ColumnFilters.HidesValue(columnName, value, entry.Type.Namespace))
                {
                    return true;
                }
            }

            return false;
        }

        bool IsTracingColumnCurrentlyVisible(string columnName) =>
            m_TreeView.columns.Contains(columnName) && m_TreeView.columns[columnName].visible;

        /// <summary>
        /// Hides columns irrelevant for the tab.
        /// </summary>
        void ApplyTracingColumnVisibilityForActiveTab()
        {
            var columns = m_TreeView.columns;
            if (!columns.Contains(k_ColumnTracingWorld)
            || !columns.Contains(k_ColumnTracingPredicted)
            || !columns.Contains(k_ColumnTracingUnmanaged)
            || !columns.Contains(k_ColumnTracingType)
            || !columns.Contains(k_ColumnTracingFilteringOptions))
            {
                return;
            }

            var worldColumn = columns[k_ColumnTracingWorld];
            var predictedColumn = columns[k_ColumnTracingPredicted];
            var unmanagedColumn = columns[k_ColumnTracingUnmanaged];
            var typeColumn = columns[k_ColumnTracingType];
            var filteringColumn = columns[k_ColumnTracingFilteringOptions];

            m_ApplyingTracingTabColumnVisibility = true;
            try
            {
                switch (m_ActiveTab)
                {
                    case TabKind.Systems:
                        worldColumn.visible = true;
                        predictedColumn.visible = true;
                        unmanagedColumn.visible = false;
                        typeColumn.visible = false;
                        filteringColumn.visible = false;
                        break;
                    case TabKind.Components:
                        worldColumn.visible = false;
                        predictedColumn.visible = false;
                        unmanagedColumn.visible = true;
                        typeColumn.visible = true;
                        filteringColumn.visible = true;
                        break;
                    default: // Selected and Combined
                        worldColumn.visible = true;
                        predictedColumn.visible = true;
                        unmanagedColumn.visible = true;
                        typeColumn.visible = true;
                        filteringColumn.visible = true;
                        break;
                }
            }
            finally
            {
                m_ApplyingTracingTabColumnVisibility = false;
            }
        }

        void OnRowToggleChanged(ChangeEvent<bool> evt)
        {
            if (evt.target is not Toggle toggle || toggle.userData is not int index)
            {
                return;
            }

            if (!TryGetDisplayedEntry(index, out var entry))
            {
                return;
            }

            SetSelected(entry.Type, entry.Kind, evt.newValue);
            RefreshAfterSelectionChange();
        }

        internal void SetSelected(Type type, TracingTargetKind kind, bool selected) =>
            m_Selection.SetSelected(type, kind, selected);

        void RefreshAfterSelectionChange()
        {
            RefreshTabLabels();
            RefreshHeaderSelectAllToggleState();
            if (m_ActiveTab == TabKind.Selected)
            {
                RefreshDisplay();
            }
            else
            {
                m_TreeView.RefreshItems();
                RefreshStatusLabel();
            }
        }

        void BuildStatusLabel()
        {
            m_StatusLabel = new Label { name = k_UssNameTracingStatusLabel };
            rootVisualElement.Add(m_StatusLabel);
        }

        internal void OnTabClicked(TabKind tab)
        {
            var index = Array.IndexOf(k_TabOrder, tab);
            if (index >= 0 && index < m_Tabs.Count)
            {
                m_TabView.activeTab = m_Tabs[index];
            }
        }

        void RefreshTabLabels()
        {
            for (var i = 0; i < m_Tabs.Count; i++)
            {
                var kind = k_TabOrder[i];
                m_Tabs[i].label = kind switch
                {
                    TabKind.All => $"All ({m_AllTypes.Count})",
                    TabKind.Systems => $"Systems ({m_SystemTypes.Count})",
                    TabKind.Components => $"Components ({m_ComponentTypes.Count})",
                    TabKind.Selected => $"Selected ({m_Selection.Count})",
                    _ => kind.ToString()
                };
            }
        }

        void RefreshStatusLabel()
        {
            m_Selection.CountByKind(out var selectedSystems, out var selectedComponents);

            // Mirror the shown/total fraction next to the selection counts only while the column filters
            // actually hide at least one row; enabled filters that hide nothing keep the plain text.
            var shownCount = m_DisplayEntries.Count;
            var totalCount = m_DisplayEntriesUnsortedCache.Count;
            m_StatusLabel.text = shownCount < totalCount
                ? $"{selectedSystems} Systems, {selectedComponents} Components selected (showing {shownCount}/{totalCount})"
                : $"{shownCount} entries shown, {selectedSystems} Systems, {selectedComponents} Components selected";
        }

        internal void RefreshDisplay()
        {
            BuildUnsortedDisplayEntries();
            ApplyTracingColumnVisibilityForActiveTab();
            OnTracingTreeColumnSortingChanged();
            RefreshStatusLabel();
        }

        void BuildUnsortedDisplayEntries()
        {
            m_DisplayEntriesUnsortedCache.Clear();
            switch (m_ActiveTab)
            {
                case TabKind.All:
                    for (var i = 0; i < m_AllTypes.Count; i++)
                    {
                        var type = m_AllTypes[i];
                        if (!PassesSearch(type))
                        {
                            continue;
                        }

                        var kind = m_SystemTypeSet.Contains(type) ? TracingTargetKind.System : TracingTargetKind.Component;
                        m_DisplayEntriesUnsortedCache.Add(new ListEntry(type, kind, ResolveComponentShapeForEntry(kind, type)));
                    }

                    break;

                case TabKind.Systems:
                    FilterTypesBySearch(m_SystemTypes, m_ScratchFilterList);
                    for (var i = 0; i < m_ScratchFilterList.Count; i++)
                    {
                        m_DisplayEntriesUnsortedCache.Add(new ListEntry(m_ScratchFilterList[i], TracingTargetKind.System));
                    }

                    break;

                case TabKind.Components:
                    FilterTypesBySearch(m_ComponentTypes, m_ScratchFilterList);
                    for (var i = 0; i < m_ScratchFilterList.Count; i++)
                    {
                        var t = m_ScratchFilterList[i];
                        m_DisplayEntriesUnsortedCache.Add(new ListEntry(t, TracingTargetKind.Component, ResolveComponentDataShape(t)));
                    }

                    break;

                case TabKind.Selected:
                    foreach (var key in m_Selection.SelectedOrder)
                    {
                        if (!TryResolveType(key, out var type, out var kind) || !PassesSearch(type))
                        {
                            continue;
                        }

                        m_DisplayEntriesUnsortedCache.Add(new ListEntry(type, kind, ResolveComponentShapeForEntry(kind, type)));
                    }

                    break;
            }
        }

        internal bool PassesSearch(Type type)
        {
            var name = type.FullName;
            if (string.IsNullOrWhiteSpace(m_Search))
            {
                return true;
            }

            var query = m_Search.Trim();
            if (name != null && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (m_ActiveTab == TabKind.Systems && TracingTargetTypes.IsTracingEcsSystemType(type))
            {
                if (type.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(type.Namespace) && type.Namespace.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (TracingTargetTypes.WorldDisplayText(type).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            if (m_ActiveTab != TabKind.Components || !typeof(IComponentData).IsAssignableFrom(type) || type.IsInterface)
            {
                return false;
            }

            var shape = ResolveComponentDataShape(type);
            return SearchMatchesComponentKindFilter(query, shape);
        }

        internal static bool SearchMatchesComponentKindFilter(string q, ComponentDataShapeKind shape)
        {
            if (string.IsNullOrEmpty(q) || shape is ComponentDataShapeKind.NotApplicable)
            {
                return false;
            }

            if (shape == ComponentDataShapeKind.Tag)
            {
                const string tagString = "tag";
                return tagString.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || q.IndexOf(tagString, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            const string dataString = "data";
            return dataString.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || q.IndexOf(dataString, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Copies entries of <paramref name="types"/> that pass the active search into <paramref name="into"/>,
        /// preserving order.
        /// </summary>
        /// <param name="types">A list that is already sorted by <see cref="Type.FullName"/>.</param>
        /// <param name="into">The list to copy the filtered types into.</param>
        void FilterTypesBySearch(IReadOnlyList<Type> types, List<Type> into)
        {
            into.Clear();
            for (var i = 0; i < types.Count; i++)
            {
                var t = types[i];
                if (PassesSearch(t))
                {
                    into.Add(t);
                }
            }
        }

        bool TryResolveType(string key, out Type type, out TracingTargetKind kind)
        {
            type = null;
            kind = default;
            if (m_TypeByKey.TryGetValue(key, out var t))
            {
                type = t;
                kind = m_SystemTypeSet.Contains(t) ? TracingTargetKind.System : TracingTargetKind.Component;
                return true;
            }

            var loaded = Type.GetType(key);
            if (loaded == null)
            {
                return false;
            }

            type = loaded;
            kind = TracingTargetTypes.IsTracingEcsSystemType(loaded)
                ? TracingTargetKind.System
                : TracingTargetKind.Component;
            return true;
        }

        static void SortTypesByFullName(List<Type> types)
        {
            types.Sort(static (a, b) => StringComparer.Ordinal.Compare(a?.FullName, b?.FullName));
        }

        void RebuildCaches()
        {
            m_SystemTypes.Clear();
            foreach (var t in TypeCache.GetTypesDerivedFrom<ComponentSystemBase>())
            {
                if (!t.IsClass || t.IsAbstract)
                {
                    continue;
                }

                if (!TracingTargetTypes.SystemHasClientOrServerWorldFilter(t))
                {
                    continue;
                }

                m_SystemTypes.Add(t);
            }

            foreach (var t in TypeCache.GetTypesDerivedFrom<ISystem>())
            {
                if (!t.IsValueType || t.IsAbstract)
                {
                    continue;
                }

                if (!TracingTargetTypes.SystemHasClientOrServerWorldFilter(t))
                {
                    continue;
                }

                m_SystemTypes.Add(t);
            }

            SortTypesByFullName(m_SystemTypes);
            m_SystemTypeSet.Clear();
            m_SystemTypeSet.UnionWith(m_SystemTypes);

            TypeManager.Initialize();
            m_ComponentTypes.Clear();
            var componentSeen = new HashSet<Type>();
            foreach (var typeInfo in TypeManager.GetAllTypes())
            {
                if (typeInfo.Type == null)
                {
                    continue;
                }

                if (typeInfo.Category != TypeManager.TypeCategory.ComponentData)
                {
                    continue;
                }

                if (typeInfo.Type.IsAbstract)
                {
                    continue;
                }

                if (!componentSeen.Add(typeInfo.Type))
                {
                    continue;
                }

                m_ComponentTypes.Add(typeInfo.Type);
            }

            SortTypesByFullName(m_ComponentTypes);
            RebuildAllTypesCache();
            RebuildComponentShapeCache();
            RebuildTypeKeyIndex();
        }

        /// <summary>
        /// Precomputes the distinct, FullName-sorted union of systems and components so that the All tab and the
        /// "All (N)" tab label can be served allocation-free from <see cref="m_AllTypes"/>.
        /// </summary>
        void RebuildAllTypesCache()
        {
            m_AllTypes.Clear();
            m_AllTypes.AddRange(m_SystemTypes);
            for (var i = 0; i < m_ComponentTypes.Count; i++)
            {
                var t = m_ComponentTypes[i];
                if (!m_SystemTypeSet.Contains(t))
                {
                    m_AllTypes.Add(t);
                }
            }

            SortTypesByFullName(m_AllTypes);
        }

        void RebuildComponentShapeCache()
        {
            m_ComponentShapeByType.Clear();
            for (var i = 0; i < m_ComponentTypes.Count; i++)
            {
                var t = m_ComponentTypes[i];
                m_ComponentShapeByType[t] = TracingTargetTypes.GetComponentDataShape(t);
            }
        }

        ComponentDataShapeKind ResolveComponentDataShape(Type type) =>
            m_ComponentShapeByType.TryGetValue(type, out var shape) ? shape : TracingTargetTypes.GetComponentDataShape(type);

        ComponentDataShapeKind ResolveComponentShapeForEntry(TracingTargetKind kind, Type type) =>
            kind == TracingTargetKind.Component ? ResolveComponentDataShape(type) : ComponentDataShapeKind.NotApplicable;

        void RebuildTypeKeyIndex()
        {
            m_TypeByKey.Clear();
            foreach (var t in m_SystemTypes)
            {
                m_TypeByKey[TracingTargetTypes.TypeKey(t)] = t;
            }

            foreach (var t in m_ComponentTypes)
            {
                m_TypeByKey[TracingTargetTypes.TypeKey(t)] = t;
            }
        }
    }
}

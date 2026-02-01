using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class GhostSnapshotsTab : NetcodeProfilerTab
    {
        MultiColumnTreeView m_TreeView;
        List<TreeViewItemData<ProfilerGhostTypeData>> m_ItemList;
        VisualElement m_FilterOptionsElement;
        bool m_OverheadEnabled = true;
        static int s_MaxMessageSize;

        internal GhostSnapshotsTab(NetworkRole networkRole)
            : base("Ghost Snapshots", networkRole)
        {
            var networkRolePrefix = m_NetworkRole.ToString();

            s_MaxMessageSize = ProfilerUtils.GetMaxMessageSize();

            viewDataKey = networkRolePrefix + nameof(GhostSnapshotsTab);
            m_FilterOptionsElement = UIFactory.CreateFilterOptionsForSnapshots(FilterTreeView, true, Rebuild);
            Add(m_FilterOptionsElement);
            m_TreeView ??= CreateGhostSnapshotTreeView(networkRolePrefix + nameof(GhostSnapshotsTab) + "TreeView", m_ItemList);
            Add(m_TreeView);

            // Update max message size every time we enter play mode
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange playModeStateChange)
        {
            if (playModeStateChange == PlayModeStateChange.ExitingEditMode)
                s_MaxMessageSize = ProfilerUtils.GetMaxMessageSize();
        }

        internal override void Dispose()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        void Rebuild(bool overheadEnabled)
        {
            m_OverheadEnabled = overheadEnabled;
            var profilerWindow = EditorWindow.GetWindow<ProfilerWindow>();
            // A bit of a hack to force the ProfilerWindow to refresh the selected frame.
            var selectedFrame = profilerWindow.selectedFrameIndex;
            profilerWindow.selectedFrameIndex = profilerWindow.firstAvailableFrameIndex;
            profilerWindow.selectedFrameIndex = selectedFrame;
        }

        internal void ClearTab()
        {
            ShowNoDataInfoLabel(true);
        }

        internal void Update(NetcodeFrameData frameData)
        {
            // No data received for this frame
            var noData = !frameData.isValid;
            // Hide/Show UI
            ShowNoDataInfoLabel(noData);
            if (noData) return;

            // Save expanded items, this could break if the list size changes.
            var expandedIds = m_TreeView.GetExpandedIds();

            m_ItemList = PopulateTreeView(frameData);
            var searchField = m_FilterOptionsElement.Q<ToolbarSearchField>();
            var filteredItemList = FilterTreeViewItemsByName(m_ItemList, searchField.value);
            m_TreeView.SetRootItems(filteredItemList);
            m_TreeView.RefreshItems();

            // Always expand the root item(s)
            m_TreeView.ExpandRootItems();

            // Restore expanded items
            m_TreeView.ExpandItemsById(expandedIds);
            m_TreeView.showAlternatingRowBackgrounds = AlternatingRowBackground.All;
        }

        void ShowNoDataInfoLabel(bool noData)
        {
            m_TreeView.style.display = noData ? DisplayStyle.None : DisplayStyle.Flex;
            m_FilterOptionsElement.style.display = noData ? DisplayStyle.None : DisplayStyle.Flex;
            m_NoDataInfoLabels.style.display = noData ? DisplayStyle.Flex : DisplayStyle.None;
        }

        List<TreeViewItemData<ProfilerGhostTypeData>> PopulateTreeView(NetcodeFrameData frameData)
        {
            var items = new List<TreeViewItemData<ProfilerGhostTypeData>>();
            var id = 0;

            foreach (var tickData in frameData.tickData)
            {
                var snapshotText = tickData.snapshotCount == 1 ? "Snapshot" : "Snapshots";
                var tickItemData = new ProfilerGhostTypeData
                {
                    name = $"{tickData.snapshotCount} {snapshotText} (Tick {tickData.tick.TickValue})",
                    sizeInBits = tickData.snapshotSizeInBits,
                    snapshotCount = tickData.snapshotCount,
                    instanceCount = (int)tickData.totalInstanceCount,
                    isGhostPrefab = false
                };

                var ghostItems = new List<TreeViewItemData<ProfilerGhostTypeData>>();
                if (m_OverheadEnabled && tickData.ghostTypeData.Length > 0)
                {
                    // Add overhead item for the tick
                    var overheadItem = CreateOverheadItem(OverheadType.SnapshotOverhead, tickData.overheadSize, tickData.snapshotSizeInBits, -1,tickData.snapshotCount, ref id);
                    ghostItems.Add(overheadItem);
                }

                for (var i = 0; i < tickData.ghostTypeData.Length; i++)
                {
                    var ghostTypeData = tickData.ghostTypeData[i];
                    if (ghostTypeData.instanceCount == 0)
                        continue;

                    var componentTypeItems = new List<TreeViewItemData<ProfilerGhostTypeData>>();

                    if (m_OverheadEnabled)
                    {
                        // Add overhead item for the ghost type
                        var overheadData = CreateOverheadItem(OverheadType.GhostTypeOverhead, ghostTypeData.overheadSize, tickData.snapshotSizeInBits, ghostTypeData.instanceCount, ghostTypeData.snapshotCount, ref id);
                        var overheadItem = new TreeViewItemData<ProfilerGhostTypeData>(id++, overheadData.data);
                        componentTypeItems.Add(overheadItem);
                    }

                    foreach (var componentTypeData in ghostTypeData.componentsPerType)
                    {
                        var componentTypeItem = new TreeViewItemData<ProfilerGhostTypeData>(id++, componentTypeData);
                        componentTypeItems.Add(componentTypeItem);
                    }

                    var ghostTypeItem = new TreeViewItemData<ProfilerGhostTypeData>(id++, ghostTypeData, componentTypeItems);

                    ghostItems.Add(ghostTypeItem);
                }

                tickItemData.sizeInBits = tickData.snapshotSizeInBits;

                var tickElement = new TreeViewItemData<ProfilerGhostTypeData>(id++, tickItemData, ghostItems);
                items.Add(tickElement);
            }

            return items;
        }

        static TreeViewItemData<ProfilerGhostTypeData> CreateOverheadItem(OverheadType isTickOverhead, uint size, uint totalSize, int instanceCount, uint snapshotCount, ref int id)
        {
            var name = isTickOverhead == OverheadType.SnapshotOverhead ? "Snapshot Overhead" : "Ghost Type Overhead";
            var overheadData = new ProfilerGhostTypeData
            {
                name = name,
                sizeInBits = size,
                overheadType = isTickOverhead,
                instanceCount = instanceCount,
                snapshotCount = snapshotCount
            };
            if (instanceCount >= 0)
                overheadData.avgSizePerEntity = (float)Math.Ceiling((float)size / instanceCount);

            overheadData.percentageOfSnapshot = ProfilerUtils.GetPercentageOfSnapshot(totalSize, overheadData);

            var overheadItem = new TreeViewItemData<ProfilerGhostTypeData>(id++, overheadData);
            return overheadItem;
        }

        static MultiColumnTreeView CreateGhostSnapshotTreeView(string viewDataKey, List<TreeViewItemData<ProfilerGhostTypeData>> itemList)
        {
            var multiColumnTreeView = new MultiColumnTreeView();

            foreach (var columns in NetcodeProfilerConstants.s_ColumnKeysToTitles)
            {
                multiColumnTreeView.columns.Add(new Column
                {
                    name = columns.Key, width = 100, makeHeader = UIFactory.MakeTreeViewColumnHeader(columns.Value)
                });
            }

            // Cell creation functions
            multiColumnTreeView.columns[NetcodeProfilerConstants.nameKey].makeCell = () => new LabelWithIcon(IconPosition.BeforeLabel);
            multiColumnTreeView.columns[NetcodeProfilerConstants.sizeKey].makeCell = () => new LabelWithIcon(IconPosition.AfterLabel);
            multiColumnTreeView.columns[NetcodeProfilerConstants.percentOfSnapshotKey].makeCell = () => new PercentBar();
            multiColumnTreeView.columns[NetcodeProfilerConstants.instanceCountKey].makeCell = UIFactory.CreateTreeViewLabel;
            multiColumnTreeView.columns[NetcodeProfilerConstants.compressionKey].makeCell = () => new LabelWithIcon(IconPosition.AfterLabel);
            multiColumnTreeView.columns[NetcodeProfilerConstants.avgSizePerEntityKey].makeCell = UIFactory.CreateTreeViewLabel;

            // Set specific column widths
            multiColumnTreeView.columns[NetcodeProfilerConstants.nameKey].width = NetcodeProfilerConstants.s_NameHeaderWidth;
            multiColumnTreeView.columns[NetcodeProfilerConstants.sizeKey].width = NetcodeProfilerConstants.s_SizeHeaderWidth;
            multiColumnTreeView.columns[NetcodeProfilerConstants.percentOfSnapshotKey].width = NetcodeProfilerConstants.s_PercentOfSnapshotHeaderWidth;
            multiColumnTreeView.columns[NetcodeProfilerConstants.instanceCountKey].width = NetcodeProfilerConstants.s_InstanceCountHeaderWidth;
            multiColumnTreeView.columns[NetcodeProfilerConstants.compressionKey].width = NetcodeProfilerConstants.s_CompressionHeaderWidth;
            multiColumnTreeView.columns[NetcodeProfilerConstants.avgSizePerEntityKey].width = NetcodeProfilerConstants.s_AvgSizePerEntityHeaderWidth;

            // Cell binding functions
            multiColumnTreeView.columns[NetcodeProfilerConstants.nameKey].bindCell = (element, index) => TreeViewBindings.BindNameCell(element, multiColumnTreeView, index);
            multiColumnTreeView.columns[NetcodeProfilerConstants.sizeKey].bindCell = (element, index) => TreeViewBindings.BindSizeCell(multiColumnTreeView, index, element, s_MaxMessageSize);
            multiColumnTreeView.columns[NetcodeProfilerConstants.percentOfSnapshotKey].bindCell = (element, index) => TreeViewBindings.BindSnapshotPercentageCell(multiColumnTreeView, index, element);
            multiColumnTreeView.columns[NetcodeProfilerConstants.instanceCountKey].bindCell = (element, index) => TreeViewBindings.BindInstanceCountCell(multiColumnTreeView, index, element);
            multiColumnTreeView.columns[NetcodeProfilerConstants.compressionKey].bindCell = (element, index) => TreeViewBindings.BindCompressionEfficiencyCell(multiColumnTreeView, index, element);
            multiColumnTreeView.columns[NetcodeProfilerConstants.avgSizePerEntityKey].bindCell = (element, index) => TreeViewBindings.BindAverageSizeCell(multiColumnTreeView, index, element);

            // Cell unbinding functions
            multiColumnTreeView.columns[NetcodeProfilerConstants.nameKey].unbindCell = (element, _) => TreeViewBindings.UnbindNameCell(element);

            // Column header tooltips
            multiColumnTreeView.columns[NetcodeProfilerConstants.nameKey].bindHeader = element => element.tooltip = NetcodeProfilerConstants.s_NameTooltip;
            multiColumnTreeView.columns[NetcodeProfilerConstants.sizeKey].bindHeader = element => element.tooltip = NetcodeProfilerConstants.s_SizeTooltip;
            multiColumnTreeView.columns[NetcodeProfilerConstants.percentOfSnapshotKey].bindHeader = element => element.tooltip = NetcodeProfilerConstants.s_PercentOfSnapshotTooltip;
            multiColumnTreeView.columns[NetcodeProfilerConstants.instanceCountKey].bindHeader = element => element.tooltip = NetcodeProfilerConstants.s_InstanceCountTooltip;
            multiColumnTreeView.columns[NetcodeProfilerConstants.compressionKey].bindHeader = element => element.tooltip = NetcodeProfilerConstants.s_CompressionEfficiencyTooltip;
            multiColumnTreeView.columns[NetcodeProfilerConstants.avgSizePerEntityKey].bindHeader = element => element.tooltip = NetcodeProfilerConstants.s_AvgSizePerInstanceTooltip;

            multiColumnTreeView.SetRootItems(itemList);
            multiColumnTreeView.viewDataKey = viewDataKey;

            return multiColumnTreeView;
        }

        void FilterTreeView(string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                m_TreeView.SetRootItems(m_ItemList);
                m_TreeView.RefreshItems();
                return;
            }

            var filteredItems = FilterTreeViewItemsByName(m_ItemList, query);
            m_TreeView.SetRootItems(filteredItems);
            m_TreeView.RefreshItems();
        }

        static List<TreeViewItemData<ProfilerGhostTypeData>> FilterTreeViewItemsByName(IEnumerable<TreeViewItemData<ProfilerGhostTypeData>> items, string query)
        {
            var filtered = new List<TreeViewItemData<ProfilerGhostTypeData>>();
            foreach (var item in items)
            {
                var nameMatches = item.data.name != null && item.data.name.ToString().IndexOf(query, StringComparison.InvariantCultureIgnoreCase) >= 0;

                var filteredChildren = item.children != null ? FilterTreeViewItemsByName(item.children, query) : null;

                if (nameMatches || (filteredChildren != null && filteredChildren.Count > 0))
                {
                    filtered.Add(new TreeViewItemData<ProfilerGhostTypeData>(item.id, item.data, filteredChildren));
                }
            }
            return filtered;
        }
    }
}

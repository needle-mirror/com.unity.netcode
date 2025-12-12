using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class GhostSnapshotsTab : NetcodeProfilerTab
    {
        TabHeader m_TabHeader;
        MetricsHeader m_MetricsHeader;
        MultiColumnTreeView m_TreeView;
        VisualElement m_NoDataInfoLabels;
        List<TreeViewItemData<ProfilerGhostTypeData>> m_ItemList;
        NetworkRole m_NetworkRole;
        VisualElement m_FilterOptionsElement;
        bool m_OverheadEnabled = true;
        static int s_MaxMessageSize;

        internal GhostSnapshotsTab(NetworkRole networkRole)
            : base("Ghost Snapshots", networkRole.ToString())
        {
            m_NetworkRole = networkRole;
            var networkRolePrefix = networkRole.ToString();
            var packetDirection = ProfilerUtils.GetPacketDirection(networkRole);

            s_MaxMessageSize = ProfilerUtils.GetMaxMessageSize();

            m_MetricsHeader = new MetricsHeader(m_NetworkRole);
            Add(m_MetricsHeader);

            m_TabHeader = new TabHeader("Ghost Snapshots Overview",
                new List<string>
                {
                    $"Total size {packetDirection} in bits (bytes)",
                    $"Total packet(s) {packetDirection}",
                    $"Servertick of {packetDirection}"
                });

            if (networkRole == NetworkRole.Client)
            {
                m_TabHeader.AddColumn("Snapshot age");
            }
            Add(m_TabHeader);

            m_NoDataInfoLabels = UIFactory.CreateNoDataInfoLabel(packetDirection, NetcodeProfilerConstants.s_ProfilerDocsLink);
            Add(m_NoDataInfoLabels);

            viewDataKey = networkRolePrefix + nameof(GhostSnapshotsTab);
            m_FilterOptionsElement ??= UIFactory.CreateFilterOptionsForSnapshots(FilterTreeView, true, Rebuild);
            Add(m_FilterOptionsElement);
            m_TreeView ??= CreateGhostSnapshotTreeView(networkRolePrefix + nameof(GhostSnapshotsTab) + "TreeView", m_ItemList);
            Add(m_TreeView);
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

        internal void Update(NetcodeFrameData frameData)
        {
            // No data received for this frame
            var noData = !frameData.isValid;
            // Hide/Show UI
            ShowNoDataInfoLabel(noData);

            if (noData)
                return;

            UpdateMetricsHeader(frameData);

            var serverTick = frameData.tickData[0].tick.ToString();
            string bitsAndBytes, packetCount;

            if (m_NetworkRole == NetworkRole.Server)
            {
                var formattedBytes = ProfilerUtils.FormatBitsToBytes(frameData.totalSizeSentByServerInBits);
                bitsAndBytes = $"{frameData.totalSizeSentByServerInBits}b ({formattedBytes})";
                packetCount = frameData.totalPacketCountSentByServer.ToString();
            }
            else
            {
                var formattedBytes = ProfilerUtils.FormatBitsToBytes(frameData.totalSizeReceivedByClientInBits);
                bitsAndBytes = $"{frameData.totalSizeReceivedByClientInBits}b ({formattedBytes})";
                packetCount = frameData.totalPacketCountReceivedByClient.ToString();
            }

            m_TabHeader.SetText(0, bitsAndBytes);
            m_TabHeader.SetText(1, packetCount);
            m_TabHeader.SetText(2, serverTick);

            var snapshotAgeRange = $"{frameData.tickData[0].snapshotAgeMin:F1} to {frameData.tickData[0].snapshotAgeMax:F1} ticks";
            if (m_NetworkRole == NetworkRole.Client)
            {
                m_TabHeader.SetText(3, snapshotAgeRange);
            }

            // Save expanded items, this could break if the list size changes.
            var expandedIds = m_TreeView.GetExpandedIds();

            m_ItemList = PopulateTreeView(frameData);
            m_TreeView.SetRootItems(m_ItemList);
            m_TreeView.RefreshItems();

            // Restore expanded items
            m_TreeView.ExpandItemsById(expandedIds);
            m_TreeView.showAlternatingRowBackgrounds = AlternatingRowBackground.All;
        }

        void ShowNoDataInfoLabel(bool noData)
        {
            m_TabHeader.style.display = noData ? DisplayStyle.None : DisplayStyle.Flex;
            m_TreeView.style.display = noData ? DisplayStyle.None : DisplayStyle.Flex;
            m_FilterOptionsElement.style.display = noData ? DisplayStyle.None : DisplayStyle.Flex;
            m_NoDataInfoLabels.style.display = noData ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void UpdateMetricsHeader(NetcodeFrameData frameData)
        {
            m_MetricsHeader.SetWorldName(ProfilerUtils.GetWorldName(m_NetworkRole));
            m_MetricsHeader.SetServerTick(frameData.serverTickSent);
            if (m_NetworkRole == NetworkRole.Client)
            {
                m_MetricsHeader.SetJitter(frameData.jitter);
                m_MetricsHeader.SetRtt(frameData.rtt);
            }
        }

        List<TreeViewItemData<ProfilerGhostTypeData>> PopulateTreeView(NetcodeFrameData frameData)
        {
            var items = new List<TreeViewItemData<ProfilerGhostTypeData>>();
            var id = 0;

            foreach (var tickData in frameData.tickData)
            {
                var tickItemData = new ProfilerGhostTypeData
                {
                    name = $"Snapshot (Tick {tickData.tick.TickValue})",
                    sizeInBits = tickData.snapshotSizeInBits,
                    instanceCount = (int)tickData.totalInstanceCount,
                    isGhost = false
                };

                var ghostItems = new List<TreeViewItemData<ProfilerGhostTypeData>>();
                if (m_OverheadEnabled && tickData.ghostTypeData.Length > 0)
                {
                    // Add overhead item for the tick
                    var overheadItem = CreateOverheadItem(OverheadType.SnapshotOverhead, tickData.overheadSize, -1, ref id);
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
                        var overheadData = CreateOverheadItem(OverheadType.GhostTypeOverhead, ghostTypeData.overheadSize, ghostTypeData.instanceCount, ref id);
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

        static TreeViewItemData<ProfilerGhostTypeData> CreateOverheadItem(OverheadType isTickOverhead, uint size, int instanceCount, ref int id)
        {
            var name = isTickOverhead == OverheadType.SnapshotOverhead ? "Snapshot Overhead" : "Ghost Type Overhead";
            var overheadData = new ProfilerGhostTypeData
            {
                name = name,
                sizeInBits = size,
                overheadType = isTickOverhead,
                instanceCount = instanceCount,
            };
            if (instanceCount >= 0)
                overheadData.avgSizePerEntity = (float)Math.Ceiling((float)size / instanceCount);
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
            multiColumnTreeView.columns[NetcodeProfilerConstants.nameKey].makeCell = () => UIFactory.CreateTreeViewLabelWithIcon(IconType.Overhead, IconPosition.BeforeLabel);
            multiColumnTreeView.columns[NetcodeProfilerConstants.sizeKey].makeCell = () => UIFactory.CreateTreeViewLabelWithIcon(IconType.Warning, IconPosition.AfterLabel);
            multiColumnTreeView.columns[NetcodeProfilerConstants.percentOfSnapshotKey].makeCell = () => new PercentBar();
            multiColumnTreeView.columns[NetcodeProfilerConstants.instanceCountKey].makeCell = UIFactory.CreateTreeViewLabel;
            multiColumnTreeView.columns[NetcodeProfilerConstants.compressionKey].makeCell = () => UIFactory.CreateTreeViewLabelWithIcon(IconType.Warning, IconPosition.AfterLabel);
            multiColumnTreeView.columns[NetcodeProfilerConstants.avgSizePerEntityKey].makeCell = UIFactory.CreateTreeViewLabel;

            // Set specific column widths
            multiColumnTreeView.columns[NetcodeProfilerConstants.nameKey].width = 250;
            multiColumnTreeView.columns[NetcodeProfilerConstants.sizeKey].width = 120;

            // Cell binding functions
            multiColumnTreeView.columns[NetcodeProfilerConstants.nameKey].bindCell = (element, index) => TreeViewBindings.BindNameCell(element, multiColumnTreeView, index);
            multiColumnTreeView.columns[NetcodeProfilerConstants.sizeKey].bindCell = (element, index) => TreeViewBindings.BindSizeCell(multiColumnTreeView, index, element, s_MaxMessageSize);
            multiColumnTreeView.columns[NetcodeProfilerConstants.percentOfSnapshotKey].bindCell = (element, index) => TreeViewBindings.BindSnapshotPercentageCell(multiColumnTreeView, index, element);
            multiColumnTreeView.columns[NetcodeProfilerConstants.instanceCountKey].bindCell = (element, index) => TreeViewBindings.BindInstanceCountCell(multiColumnTreeView, index, element);
            multiColumnTreeView.columns[NetcodeProfilerConstants.compressionKey].bindCell = (element, index) => TreeViewBindings.BindCompressionEfficiencyCell(multiColumnTreeView, index, element);
            multiColumnTreeView.columns[NetcodeProfilerConstants.avgSizePerEntityKey].bindCell = (element, index) => TreeViewBindings.BindAverageSizeCell(multiColumnTreeView, index, element);

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
            var treeview = this.Q<MultiColumnTreeView>();
            if (string.IsNullOrEmpty(query))
            {
                treeview.SetRootItems(m_ItemList);
                treeview.RefreshItems();
                return;
            }

            // var filteredItems = new List<TreeViewItemData<GhostData>>();
            // foreach (var item in m_ItemList)
            // {
                // if (item.data.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                // {
                //     filteredItems.Add(item);
                // }
                //
                // // assumes we only nest one layer
                // foreach (var child in item.children)
                // {
                //     if (child.data.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                //     {
                //         filteredItems.Add(child);
                //     }
                // }
            // }
            //
            // treeview.SetRootItems(filteredItems);
            // treeview.RefreshItems();
        }
    }
}

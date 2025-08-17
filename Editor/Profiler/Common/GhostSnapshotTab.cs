using System;
using System.Collections.Generic;
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
        List<TreeViewItemData<ProfilerGhostTypeData>> m_ItemList;
        NetworkRole m_NetworkRole;
        VisualElement m_FilterOptionsElement;
        bool m_OverheadEnabled = true;

        internal GhostSnapshotsTab(NetworkRole networkRole)
            : base("Ghost Snapshots", networkRole.ToString())
        {
            m_NetworkRole = networkRole;
            var networkRolePrefix = networkRole.ToString();
            var packetDirection = networkRole == NetworkRole.Client ? "received" : "sent";

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
            if (frameData.tickData.Length == 0)
            {
                m_TabHeader.SetText(0, "No data");
                m_TabHeader.SetText(1, "No data");
                m_TabHeader.SetText(2, "No data");
                if (m_NetworkRole == NetworkRole.Client)
                {
                    m_TabHeader.SetText(3, "No data");
                }
                return;
            }

            UpdateMetricsHeader(frameData);

            var serverTick = frameData.tickData[0].tick.ToString();
            string bitsAndBytes, packetCount;

            if (m_NetworkRole == NetworkRole.Server)
            {
                var formattedBytes = UIUtils.FormatBitsToBytes(frameData.totalSizeSentByServerInBits);
                bitsAndBytes = $"{frameData.totalSizeSentByServerInBits}b ({formattedBytes})";
                packetCount = frameData.totalPacketCountSentByServer.ToString();
            }
            else
            {
                var formattedBytes = UIUtils.FormatBitsToBytes(frameData.totalSizeReceivedByClientInBits);
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
                var tickGhostData = new ProfilerGhostTypeData
                {
                    name = $"Tick {tickData.tick.TickValue}",
                    sizeInBits = tickData.snapshotSizeInBits,
                    instanceCount = (int)tickData.totalInstanceCount
                };

                var children = new List<TreeViewItemData<ProfilerGhostTypeData>>();
                if (m_OverheadEnabled && tickData.ghostTypeData.Length > 0)
                {
                    // Add overhead item
                    var overheadItem = CreateOverheadItem("Overhead", tickData.overheadSize, true, -1, ref id);
                    children.Add(overheadItem);
                }

                for (var i = 0; i < tickData.ghostTypeData.Length; i++)
                {
                    var ghostTypeData = tickData.ghostTypeData[i];
                    if (ghostTypeData.instanceCount == 0)
                        continue;

                    var ghostTypeComponents = new List<TreeViewItemData<ProfilerGhostTypeData>>();

                    if (m_OverheadEnabled)
                    {
                        // Add overhead item
                        var overheadData = CreateOverheadItem("Overhead", ghostTypeData.overheadSize, true, -1, ref id);
                        var overheadItem = new TreeViewItemData<ProfilerGhostTypeData>(id++, overheadData.data);
                        ghostTypeComponents.Add(overheadItem);
                    }

                    foreach (var componentTypeData in ghostTypeData.componentsPerType)
                    {
                        var componentItem = new TreeViewItemData<ProfilerGhostTypeData>(id++, componentTypeData);
                        ghostTypeComponents.Add(componentItem);
                    }

                    var ghostTypeItem = new TreeViewItemData<ProfilerGhostTypeData>(id++, ghostTypeData, ghostTypeComponents);

                    children.Add(ghostTypeItem);
                }

                tickGhostData.sizeInBits = tickData.snapshotSizeInBits;

                var tickElement = new TreeViewItemData<ProfilerGhostTypeData>(id++, tickGhostData, children);
                items.Add(tickElement);
            }

            return items;
        }

        static TreeViewItemData<ProfilerGhostTypeData> CreateOverheadItem(string name, uint size, bool needsIcon, int instanceCount, ref int id)
        {
            var overheadData = new ProfilerGhostTypeData
            {
                name = name,
                sizeInBits = size,
                needsOverheadIcon = needsIcon,
                instanceCount = instanceCount
            };
            var overheadItem = new TreeViewItemData<ProfilerGhostTypeData>(id++, overheadData);
            return overheadItem;
        }

        static MultiColumnTreeView CreateGhostSnapshotTreeView(string viewDataKey, List<TreeViewItemData<ProfilerGhostTypeData>> itemList)
        {
            var columnKeysToColumnTitle = new Dictionary<string, string>
            {
                { "name", "Name" },
                { "size", "Size in bits (bytes)" },
                { "percentOfSnapshot", "% of snapshot size" },
                { "instanceCount", "Instance count" },
                { "compressed", "Compression efficiency" },
                { "avgSizePerEntity", "Avg size / instance" },
                // { "avgSizePerInstance", "Avg size / servertick over last second" }
            };

            var multiColumnTreeView = new MultiColumnTreeView();

            foreach (var columns in columnKeysToColumnTitle)
            {
                multiColumnTreeView.columns.Add(new Column { name = columns.Key, title = columns.Value, width = 100 });
            }

            multiColumnTreeView.columns["name"].makeCell = UIFactory.CreateTreeViewLabelWithIcon;
            multiColumnTreeView.columns["size"].makeCell = UIFactory.CreateTreeViewLabel;
            multiColumnTreeView.columns["percentOfSnapshot"].makeCell = () => new PercentBar();
            multiColumnTreeView.columns["instanceCount"].makeCell = UIFactory.CreateTreeViewLabel;
            multiColumnTreeView.columns["compressed"].makeCell = UIFactory.CreateTreeViewLabel;
            multiColumnTreeView.columns["avgSizePerEntity"].makeCell = UIFactory.CreateTreeViewLabel;
            // multiColumnTreeView.columns["avgSizePerInstance"].makeCell = UIFactory.CreateTreeViewLabel;

            // Ghost type name
            multiColumnTreeView.columns["name"].width = 250;
            multiColumnTreeView.columns["name"].bindCell = (element, index) =>
            {
                ((LabelWithIcon)element).SetText(GetGhostTypeDataAtIndex(multiColumnTreeView, index).name.Value);
                // Insert Overhead icon in front of the name if this is an overhead item
                var isOverhead = GetGhostTypeDataAtIndex(multiColumnTreeView, index).needsOverheadIcon;
                ((LabelWithIcon)element).SetIconEnabled(isOverhead);
            };

            // Ghost type size
            multiColumnTreeView.columns["size"].width = 120;
            multiColumnTreeView.columns["size"].bindCell = (element, index) =>
            {
                var size = GetGhostTypeDataAtIndex(multiColumnTreeView, index).sizeInBits;
                var isOverhead = GetGhostTypeDataAtIndex(multiColumnTreeView, index).needsOverheadIcon;
                var bitsAndBytes = $"{size} ({UIUtils.BitsToBytes(size)})";
                ((Label)element).text = bitsAndBytes;
                element.parent.parent.SetEnabled(isOverhead || size != 0); // Disable the row if size is 0 and not overhead
            };

            // Ghost type size as a percentage of the snapshot size
            multiColumnTreeView.columns["percentOfSnapshot"].bindCell = (element, index) =>
            {
                var item = GetGhostTypeDataAtIndex(multiColumnTreeView, index);
                var percentBar = (PercentBar)element;

                if (item.sizeInBits == 0)
                {
                    percentBar.SetValue(0);
                    return;
                }

                var rootData = GetGhostTypeDataAtIndex(multiColumnTreeView, 0);
                if (rootData.sizeInBits == 0)
                {
                    percentBar.SetValue(0);
                    return;
                }

                var percentage = item.sizeInBits/ (float)rootData.sizeInBits * 100;
                percentBar.SetValue(Mathf.RoundToInt(percentage));
            };

            // Ghost type instance count
            multiColumnTreeView.columns["instanceCount"].bindCell = (element, index) =>
            {
                var count = GetGhostTypeDataAtIndex(multiColumnTreeView, index).instanceCount;
                var newInstances = GetGhostTypeDataAtIndex(multiColumnTreeView, index).newInstancesCount;
                var text = count == -1 ? "-" : count.ToString();
                if (newInstances > 0)
                {
                    text += $" <color=#888888>(+{newInstances})</color>";
                }
                ((Label)element).text = text;
            };

            // Compression efficiency
            multiColumnTreeView.columns["compressed"].bindCell = (element, index) =>
            {
                var ghostTypeData = GetGhostTypeDataAtIndex(multiColumnTreeView, index);
                var compressionEfficiency = ghostTypeData.combinedCompressionEfficiency;
                var compressionEfficiencyString = compressionEfficiency < 0 || ghostTypeData.needsOverheadIcon ? "-" : $"{compressionEfficiency}%";

                ((Label)element).text = compressionEfficiencyString;
            };

            // Average size per entity
            multiColumnTreeView.columns["avgSizePerEntity"].bindCell = (element, index) =>
            {
                var sizePerEntity = GetGhostTypeDataAtIndex(multiColumnTreeView, index).avgSizePerEntity;
                var bitsAndBytes = $"{sizePerEntity} ({UIUtils.BitsToBytes(sizePerEntity)})";
                ((Label)element).text = bitsAndBytes;
            };

            // Average size per servertick over last second
            // multiColumnTreeView.columns["avgSizePerInstance"].bindCell = (element, index) =>
            //     ((Label)element).text = "TODO";

            multiColumnTreeView.SetRootItems(itemList);
            multiColumnTreeView.viewDataKey = viewDataKey;

            return multiColumnTreeView;
        }

        static ProfilerGhostTypeData GetGhostTypeDataAtIndex(MultiColumnTreeView multiColumnTreeView, int index)
        {
            var data = multiColumnTreeView.GetItemDataForIndex<ProfilerGhostTypeData>(index);
            return data;
        }

        static ProfilerGhostTypeData GetGhostTypeDataForId(MultiColumnTreeView multiColumnTreeView, int id)
        {
            var data = multiColumnTreeView.GetItemDataForId<ProfilerGhostTypeData>(id);
            return data;
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

using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class FrameOverviewTab : NetcodeProfilerTab
    {
        BarChart m_SnapshotBarChart;
        BarChart m_CommandsBarChart;
        BarChartCategory m_GhostSnapshotCategory;
        Label m_WorldNameLabel;

        internal FrameOverviewTab(NetworkRole networkRole)
            : base("Frame Overview", networkRole)
        {
            var networkRolePrefix = networkRole.ToString();
            m_NetworkRole = networkRole;
            var worldHeader = new VisualElement();
            const string worldInfoUss = "worldinfo";

            worldHeader.AddToClassList($"{worldInfoUss}-container");
            Add(worldHeader);

            m_WorldNameLabel = new Label($"{networkRolePrefix} World");
            m_WorldNameLabel.AddToClassList($"{worldInfoUss}__name-label");

            worldHeader.Add(m_WorldNameLabel);

            m_SnapshotBarChart = CreateSnapshotBarGraph();
            m_CommandsBarChart = CreateCommandsBarGraph();

            // TODO: Collect total command metrics and show them.
            m_CommandsBarChart.ShowTotalDataLabels(false);

            Add(m_SnapshotBarChart);
            Add(m_CommandsBarChart);
        }

        internal void ClearTab()
        {
            ShowNoDataInfoLabel(true);
        }

        internal void Update(NetcodeFrameData frameData)
        {
            ShowNoDataInfoLabel(!frameData.isValid);

            m_WorldNameLabel.text = ProfilerUtils.GetWorldName(m_NetworkRole);

            m_SnapshotBarChart.Update(frameData);
            m_CommandsBarChart.Update(frameData);
        }

        void ShowNoDataInfoLabel(bool noData)
        {
            m_NoDataInfoLabels.style.display = noData ? DisplayStyle.Flex : DisplayStyle.None;
            m_WorldNameLabel.style.display = !noData ? DisplayStyle.Flex : DisplayStyle.None;
            m_SnapshotBarChart.style.display = !noData ? DisplayStyle.Flex : DisplayStyle.None;
            m_CommandsBarChart.style.display = !noData ? DisplayStyle.Flex : DisplayStyle.None;
        }

        BarChart CreateSnapshotBarGraph()
        {
            var snapshotLegendEntryNames = new Dictionary<string, string>()
            {
                { "totalSizeSnapshot", "Total bits (bytes)" },
                { "numberOfSnapshots", "Number of snapshots" },
                { "numberOfGhostTypes", "Number of ghost types" },
                { "numberOfInstances", "Number of total instances" },
                { "serverTickSnapshot", "Server Tick" },
            };

            // TODO: This is a hacky workaround to best fit the column widths. Auto-sizing did not work.
            var snapshotLegendEntryWidths = new List<int>
            {
                111, // total
                135, // numberOfSnapshots
                147, // numberOfGhostTypes
                163, // numberOfInstances
                80, // serverTick
            };

            if (m_NetworkRole == NetworkRole.Client)
            {
                snapshotLegendEntryNames.Add("snapshotAge", "Ghost Snapshot Age (tick)");
                snapshotLegendEntryWidths.Add(164);
            }

            m_GhostSnapshotCategory = new BarChartCategory("Ghost Snapshots", "ghost-snapshot", snapshotLegendEntryNames, snapshotLegendEntryWidths, true);
            m_GhostSnapshotCategory.Update = frameData =>
            {
                m_SnapshotBarChart.style.display = frameData.isValid ? DisplayStyle.Flex : DisplayStyle.None;
                if (!frameData.isValid)
                    return;

                m_GhostSnapshotCategory.listViewData.Clear();

                if (frameData.tickData.Length == 0)
                {
                    m_GhostSnapshotCategory.listViewData.Add(new LegendEntryData
                    {
                        values = { "N/A", "N/A", "N/A", "N/A", "N/A", "N/A" }
                    });
                    return;
                }

                foreach (var tickData in frameData.tickData)
                {
                    uint ghostTypeCount = 0;
                    foreach (var profilerGhostTypeData in tickData.ghostTypeData)
                    {
                        if (profilerGhostTypeData.instanceCount > 0)
                        {
                            ghostTypeCount++;
                        }
                    }

                    var bitsAndBytes = $"{tickData.snapshotSizeInBits} ({ProfilerUtils.BitsToBytes(tickData.snapshotSizeInBits)})";
                    m_GhostSnapshotCategory.listViewData.Add(new LegendEntryData
                    {
                        values =
                        {
                            bitsAndBytes,
                            tickData.snapshotCount.ToString(),
                            ghostTypeCount.ToString(),
                            tickData.totalInstanceCount.ToString(),
                            tickData.tick.ToString()
                        }
                    });
                    if (m_NetworkRole == NetworkRole.Client)
                    {
                        var snapshotAgeRange = $"{tickData.snapshotAgeMin:F1} - {tickData.snapshotAgeMax:F1}";
                        m_GhostSnapshotCategory.listViewData[^1].values.Add(snapshotAgeRange);
                    }
                }

                // ghostSnapshotCat.SetMainBarValue((float)totalBitsSnapshotSent / frameData.totalBitsSent * 100f);
            };

            return new BarChart(m_NetworkRole, new List<BarChartCategory> { m_GhostSnapshotCategory });
        }

        internal void EnableSnapshotViewDetailsCallback(Action callback, bool enable)
        {
            if (enable)
            {
                m_GhostSnapshotCategory.ViewDetailsButton.clicked += callback;
                return;
            }
            m_GhostSnapshotCategory.ViewDetailsButton.clicked -= callback;
        }

        BarChart CreateCommandsBarGraph()
        {
            var commandsLegendEntries = new Dictionary<string, string>
            {
                { "totalSizeCommands", "Total bits (bytes)" },
                { "inputTargetTick", "Command target tick" },
                { "discardedPackets", "Discarded packets" },
                { "serverTickCommands", "Server Tick" }
            };

            // TODO: This is a hacky workaround to best fit the column widths. Auto-sizing did not work.
            var commandsLegendEntryWidths = new List<int>
            {
                113, // totalSize
                133, // inputTargetTick
                124, // discardedPackets
                80, // serverTick
            };

            if (m_NetworkRole == NetworkRole.Client)
            {
                commandsLegendEntries.Add("commandAge", "Command Age (ticks)");
                commandsLegendEntryWidths.Add(135);
            }

            var commandsCat = new BarChartCategory("Commands", "commands", commandsLegendEntries, commandsLegendEntryWidths);
            commandsCat.Update = frameData =>
            {
                commandsCat.listViewData.Clear();

                if (frameData.tickData.Length == 0)
                {
                    commandsCat.listViewData.Add(new LegendEntryData
                    {
                        values = { "N/A", "N/A", "N/A", "N/A", "N/A" }
                    });
                    return;
                }

                foreach (var tickData in frameData.tickData)
                {
                    var bitsAndBytes = $"{tickData.commandSizeInBits} ({ProfilerUtils.BitsToBytes(tickData.commandSizeInBits)})";
                    commandsCat.listViewData.Add(new LegendEntryData
                    {
                        values =
                        {
                            bitsAndBytes,
                            tickData.inputTargetTick.ToString(),
                            tickData.discardedPackets.ToString(),
                            tickData.tick.ToString()
                        }
                    });
                    if (m_NetworkRole == NetworkRole.Client)
                    {
                        commandsCat.listViewData[^1].values.Add(tickData.commandAge.ToString("F2"));
                    }
                }
            };

            var role = m_NetworkRole == NetworkRole.Server ? NetworkRole.Client : NetworkRole.Server;
            return new BarChart(role, new List<BarChartCategory> { commandsCat });
        }
    }
}

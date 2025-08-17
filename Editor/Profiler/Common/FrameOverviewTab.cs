using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class FrameOverviewTab : NetcodeProfilerTab
    {
        BarChart m_SnapshotBarChart;
        BarChart m_CommandsBarChart;
        Label m_TotalReceivedLabel = new();
        Label m_TotalSentLabel = new();
        NetworkRole m_NetworkRole;
        BarChartCategory m_GhostSnapshotCat;
        Label m_WorldNameLabel;

        internal FrameOverviewTab(NetworkRole networkRole)
            : base("Frame Overview", networkRole.ToString())
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

            Add(m_SnapshotBarChart);
            Add(m_CommandsBarChart);
        }

        internal void Update(NetcodeFrameData frameData)
        {
            m_WorldNameLabel.text = ProfilerUtils.GetWorldName(m_NetworkRole);

            m_SnapshotBarChart.Update(frameData);
            m_CommandsBarChart.Update(frameData);

            if (m_NetworkRole == NetworkRole.Server)
            {
                var formattedBytes = UIUtils.FormatBitsToBytes(frameData.totalSizeSentByServerInBits);
                var bitsAndBytes = $"{frameData.totalSizeSentByServerInBits}b ({formattedBytes})";
                m_TotalReceivedLabel.text = "Received: N/A"; // TODO: Add received data for server
                m_TotalSentLabel.text = $"Sent: {bitsAndBytes}";
            }
            else
            {
                var formattedBytes = UIUtils.FormatBitsToBytes(frameData.totalSizeReceivedByClientInBits);
                var bitsAndBytes = $"{frameData.totalSizeReceivedByClientInBits}b ({formattedBytes})";
                m_TotalReceivedLabel.text = $"Received: {bitsAndBytes}";
                m_TotalSentLabel.text = $"Sent: N/A"; // TODO: Add sent data for client
            }
        }

        BarChart CreateSnapshotBarGraph()
        {
            var snapshotLegendEntryNames = new Dictionary<string, string>()
            {
                { "total", "Total bits (bytes)" },
                { "numberOfPackets", "Number of packets" },
                { "numberOfGhostTypes", "Number of ghost types" },
                { "numberOfInstance", "Number of total instances" },
                { "serverTick", "Server Tick" },
            };

            // TODO: This is a hacky workaround to best fit the column widths. Auto-sizing did not work.
            var snapshotLegendEntryWidths = new List<int>
            {
                111, // total
                124, // numberOfPackets
                147, // numberOfGhostTypes
                163, // numberOfInstance
                80, // serverTick
            };

            if (m_NetworkRole == NetworkRole.Client)
            {
                snapshotLegendEntryNames.Add("snapshotAge", "Ghost Snapshot Age (tick)");
                snapshotLegendEntryWidths.Add(164);
            }

            m_GhostSnapshotCat = new BarChartCategory("Ghost Snapshots", "ghost-snapshot", snapshotLegendEntryNames, snapshotLegendEntryWidths, true);
            m_GhostSnapshotCat.Update = frameData =>
            {
                m_GhostSnapshotCat.listViewData.Clear();

                if (frameData.tickData.Length == 0)
                {
                    m_GhostSnapshotCat.listViewData.Add(new LegendEntryData
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

                    var bitsAndBytes = $"{tickData.snapshotSizeInBits} ({UIUtils.BitsToBytes(tickData.snapshotSizeInBits)})";
                    m_GhostSnapshotCat.listViewData.Add(new LegendEntryData
                    {
                        values =
                        {
                            bitsAndBytes,
                            tickData.packetCount.ToString(),
                            ghostTypeCount.ToString(),
                            tickData.totalInstanceCount.ToString(),
                            tickData.tick.ToString()
                        }
                    });
                    if (m_NetworkRole == NetworkRole.Client)
                    {
                        var snapshotAgeRange = $"{tickData.snapshotAgeMin:F1} - {tickData.snapshotAgeMax:F1}";
                        m_GhostSnapshotCat.listViewData[^1].values.Add(snapshotAgeRange);
                    }
                }

                // ghostSnapshotCat.SetMainBarValue((float)totalBitsSnapshotSent / frameData.totalBitsSent * 100f);
            };

            return new BarChart(m_NetworkRole, new List<BarChartCategory> { m_GhostSnapshotCat });
        }

        internal void SetSnapshotViewDetailsCallback(Action callback)
        {
            m_GhostSnapshotCat.ViewDetailsButton.clicked += callback;
        }

        BarChart CreateCommandsBarGraph()
        {
            var commandsLegendEntries = new Dictionary<string, string>
            {
                { "totalSize", "Total bits (bytes)" },
                { "inputTargetTick", "Command target tick" },
                { "discardedPackets", "Discarded packets" },
                { "serverTick", "Server Tick" }
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
                    var bitsAndBytes = $"{tickData.commandSizeInBits} ({UIUtils.BitsToBytes(tickData.commandSizeInBits)})";
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

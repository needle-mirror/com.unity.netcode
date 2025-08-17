using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class BarChart : VisualElement
    {
        // Graph with headline and legend
        const string k_USSBaseClass = "barchart";

        // Just the graph itself
        internal const string ussClassBarGraph = "bargraph";

        VisualElement m_BarContainer = new();
        Label m_BarName = new();
        Label m_TotalBytesLabel = new();
        Label m_TotalPacketsLabel = new();
        VisualElement[] m_BarElements;
        List<BarChartCategory> m_Categories;
        NetworkRole m_NetworkRole;

        internal BarChart(NetworkRole networkRole, List<BarChartCategory> categories)
        {
            AddToClassList(k_USSBaseClass);
            m_Categories = categories;
            m_NetworkRole = networkRole;
            m_BarName.text = m_NetworkRole == NetworkRole.Server ? "Sent" : "Received";

            var headerInfo = new VisualElement();
            headerInfo.AddToClassList(k_USSBaseClass + "__header");
            m_BarName.AddToClassList(k_USSBaseClass + "__header__name");
            m_TotalBytesLabel.AddToClassList(k_USSBaseClass + "__info-label");
            m_TotalPacketsLabel.AddToClassList(k_USSBaseClass + "__info-label");
            headerInfo.Add(m_BarName);
            headerInfo.Add(m_TotalBytesLabel);
            headerInfo.Add(m_TotalPacketsLabel);
            Add(headerInfo);

            m_BarContainer.AddToClassList(k_USSBaseClass + "__bar-container");
            Add(m_BarContainer);

            foreach (var barChartCategory in categories)
            {
                // m_BarContainer.Add(barChartCategory.mainBarElement); // TODO: Add bar graph once we have RPC and Command data
                Add(barChartCategory.legendListViewContainer);
            }
        }

        void SetFrameDataOverview(uint totalBits, uint totalPackets)
        {
            var formattedBytes = UIUtils.FormatBitsToBytes(totalBits);
            var bitsAndBytes = $"{totalBits}b ({formattedBytes})";
            m_TotalBytesLabel.text = $"Total: {bitsAndBytes}";
            m_TotalPacketsLabel.text = $"Packets: {totalPackets}";
        }

        internal void Update(NetcodeFrameData frameData)
        {
            uint totalBits, totalPackets;
            if (m_NetworkRole == NetworkRole.Server)
            {
                totalBits = frameData.totalSizeSentByServerInBits;
                totalPackets = frameData.totalPacketCountSentByServer;
            }
            else
            {
                totalBits = frameData.totalSizeReceivedByClientInBits;
                totalPackets = frameData.totalPacketCountReceivedByClient;
            }

            SetFrameDataOverview(totalBits, totalPackets);
            foreach (var barChartCategory in m_Categories)
            {
                barChartCategory.Update?.Invoke(frameData);
                barChartCategory.Refresh();
            }

            VisualElement firstShownElement = null;
            VisualElement lastShownElement = null;

            using (var enumerator = m_BarContainer.Children().GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    enumerator.Current?.RemoveFromClassList(ussClassBarGraph + "__element--first");
                    enumerator.Current?.RemoveFromClassList(ussClassBarGraph + "__element--last");

                    if (enumerator.Current != null && enumerator.Current.style.display == DisplayStyle.Flex)
                    {
                        lastShownElement = enumerator.Current;
                        firstShownElement ??= enumerator.Current;
                    }
                }

                firstShownElement?.AddToClassList(ussClassBarGraph + "__element--first");
                lastShownElement?.AddToClassList(ussClassBarGraph + "__element--last");
            }
        }
    }

    class LegendEntryData
    {
        internal List<string> values = new();
    }
}

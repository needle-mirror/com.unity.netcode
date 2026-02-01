using System;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// The title bar that is shown in every tab and contains metrics information as well as tick navigation buttons.
    /// </summary>
    class MetricsHeader : VisualElement
    {
        Label m_WorldNameLabel;
        Label m_SnapshotTickLabel;
        Label m_JitterDataLabel;
        Label m_RttDataLabel;
        Label m_TotalSizeDataLabel;
        Label m_TotalPacketsDataLabel;

        internal const string ussClassName = "metrics-header";
        const string k_MetricsHeaderLeftAlignedUssClass = ussClassName + "__left-aligned";
        const string k_MetricsHeaderRightAlignedUssClass = ussClassName + "__right-aligned";
        const string k_MetricsHeaderLabelLargeUssClass = ussClassName + "__data-label-large";
        const string k_MetricsHeaderLabelMediumUssClass = ussClassName + "__data-label-medium";
        const string k_MetricsHeaderLabelSmallUssClass = ussClassName + "__data-label-small";

        internal MetricsHeader(NetworkRole networkRole)
        {
            AddToClassList(ussClassName);

            // Left-aligned label for the world name and server tick
            var leftAlignedContainer = new VisualElement();
            leftAlignedContainer.AddToClassList(k_MetricsHeaderLeftAlignedUssClass);

            var networkRoleIcon = new VisualElement();
            var roleIconClass = networkRole == NetworkRole.Server ? "icon__server" : "icon__client";
            networkRoleIcon.AddToClassList(roleIconClass);

            m_WorldNameLabel = new Label("World Name");
            SetWorldName(ProfilerUtils.GetWorldName(networkRole));

            m_SnapshotTickLabel = new Label("Snapshot Tick: N/A");
            m_SnapshotTickLabel.AddToClassList(k_MetricsHeaderLabelLargeUssClass);

            var previousTickElement = new IconButton(() => ProfilerUtils.SelectAdjacentTick(-1, networkRole), "icon__arrow-back", "previousTickButton");
            var nextTickElement = new IconButton(() => ProfilerUtils.SelectAdjacentTick(1, networkRole), "icon__arrow-forward", "nextTickButton");
            var correspondingTickIconClass = networkRole == NetworkRole.Server ? "icon__client" : "icon__server";
            var correspondingTickButtonName = networkRole == NetworkRole.Server ? "selectClientTickButton" : "selectServerTickButton";
            var correspondingTickElement = new IconButton(() => ProfilerUtils.SelectCorrespondingTick(networkRole), correspondingTickIconClass, correspondingTickButtonName);

            previousTickElement.tooltip = NetcodeProfilerConstants.s_PreviousTickTooltip;
            nextTickElement.tooltip = NetcodeProfilerConstants.s_NextTickTooltip;
            correspondingTickElement.tooltip = NetcodeProfilerConstants.GetCorrespondingTickTooltip(networkRole);

            leftAlignedContainer.Add(networkRoleIcon);
            leftAlignedContainer.Add(m_WorldNameLabel);
            leftAlignedContainer.Add(m_SnapshotTickLabel);
            leftAlignedContainer.Add(correspondingTickElement);
            leftAlignedContainer.Add(previousTickElement);
            leftAlignedContainer.Add(nextTickElement);
            Add(leftAlignedContainer);

            var rightAlignedContainer = new VisualElement();
            rightAlignedContainer.AddToClassList(k_MetricsHeaderRightAlignedUssClass);

            // Right-aligned labels for jitter and rtt, only on client
            if (networkRole == NetworkRole.Client)
            {
                var jitterLabel = new Label("Jitter: ");
                m_JitterDataLabel = new Label("N/A");
                m_JitterDataLabel.AddToClassList(k_MetricsHeaderLabelMediumUssClass);
                var rttLabel = new Label("RTT: ");
                m_RttDataLabel = new Label("N/A");
                m_RttDataLabel.AddToClassList(k_MetricsHeaderLabelMediumUssClass);
                rightAlignedContainer.Add(jitterLabel);
                rightAlignedContainer.Add(m_JitterDataLabel);
                rightAlignedContainer.Add(rttLabel);
                rightAlignedContainer.Add(m_RttDataLabel);
            }

            var packetDirection = ProfilerUtils.GetPacketDirection(networkRole, true);
            var totalSizeLabel = new Label($"Total Size {packetDirection}: ");
            m_TotalSizeDataLabel = new Label("N/A");
            m_TotalSizeDataLabel.AddToClassList(k_MetricsHeaderLabelMediumUssClass);
            var totalPacketsLabel = new Label($"Total Snapshots {packetDirection}: ");
            m_TotalPacketsDataLabel = new Label("N/A");
            m_TotalPacketsDataLabel.AddToClassList(k_MetricsHeaderLabelSmallUssClass);

            rightAlignedContainer.Add(totalSizeLabel);
            rightAlignedContainer.Add(m_TotalSizeDataLabel);
            rightAlignedContainer.Add(totalPacketsLabel);
            rightAlignedContainer.Add(m_TotalPacketsDataLabel);
            Add(rightAlignedContainer);

            // Background highlight
            AddToClassList(BaseVerticalCollectionView.itemAlternativeBackgroundUssClassName);
        }

        internal void SetWorldName(string worldName)
        {
            m_WorldNameLabel.text = worldName;
        }

        internal void SetSnapshotTick(NetworkTick serverTick)
        {
            m_SnapshotTickLabel.text = $"Snapshot Tick: {serverTick}";
        }

        internal void SetSnapshotTick(string serverTickString)
        {
            m_SnapshotTickLabel.text = $"Snapshot Tick: {serverTickString}";
        }

        internal void SetJitter(float jitter)
        {
            m_JitterDataLabel.text = $"{jitter:F2}ms";
        }

        internal void SetRtt(float rtt)
        {
            m_RttDataLabel.text = $"{rtt:F2}ms";
        }

        internal void SetTotalSize(string totalSize)
        {
            m_TotalSizeDataLabel.text = totalSize;
        }

        internal void SetTotalPackets(uint totalPackets)
        {
            m_TotalPacketsDataLabel.text = totalPackets.ToString();
        }

        internal void ClearValues()
        {
            SetSnapshotTick("N/A");
            if (m_JitterDataLabel != null) m_JitterDataLabel.text = "N/A";
            if (m_RttDataLabel != null) m_RttDataLabel.text = "N/A";
            if (m_TotalSizeDataLabel != null) m_TotalSizeDataLabel.text = "N/A";
            if (m_TotalPacketsDataLabel != null) m_TotalPacketsDataLabel.text = "N/A";
        }
    }
}

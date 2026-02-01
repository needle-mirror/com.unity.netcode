using System;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Binding methods for the MultiColumnTreeView used in the Netcode Profiler GhostSnapshotTab.
    /// </summary>
    internal static class TreeViewBindings
    {
        internal static void BindNameCell(VisualElement element, MultiColumnTreeView multiColumnTreeView, int index)
        {
            var labelWithIcon = (LabelWithIcon)element;
            var ghostTypeData = ProfilerUtils.GetGhostTypeDataAtIndex(multiColumnTreeView, index);
            labelWithIcon.SetText(ghostTypeData.name.Value);
            // Set icon
            if (ghostTypeData.overheadType != OverheadType.None)
            {
                labelWithIcon.SetIcon(IconType.Overhead);
            }
            else if (ghostTypeData.isGhostPrefab)
            {
                labelWithIcon.SetIcon(IconType.GhostPrefab);
            }
            else if (index != 0)
            {
                labelWithIcon.SetIcon(IconType.GhostComponent);
            }
            else
            {
                labelWithIcon.ResetIcon();
            }

            // Add tooltip for overhead items
            labelWithIcon.SetTooltip(ProfilerUtils.GetOverheadTooltip(ghostTypeData.overheadType));

            // Context menu
            if (ghostTypeData.overheadType == OverheadType.None && index != 0) // Can't inspect overhead or the root tick item
            {
                var actionName = ghostTypeData.isGhostPrefab ? "Inspect Ghost Prefab" : "Inspect Component";
                Action<TypeIndex, string> action = ghostTypeData.isGhostPrefab ? ProfilerUtils.SelectGhostPrefab : ProfilerUtils.SelectGhostComponent;
                // To track the manipulator for unbinding later we put it into the userData of the VisualElement.
                labelWithIcon.userData = CreateManipulator(actionName, action, ghostTypeData);
                labelWithIcon.AddManipulator((ContextualMenuManipulator)labelWithIcon.userData);
            }
        }

        static ContextualMenuManipulator CreateManipulator(string actionName, Action<TypeIndex, string> action, ProfilerGhostTypeData data)
        {
            return new ContextualMenuManipulator(evt => MenuBuilder(evt, actionName, action, data));
        }

        static void MenuBuilder(ContextualMenuPopulateEvent evt, string actionName, Action<TypeIndex, string> action, ProfilerGhostTypeData data)
        {
            evt.menu.AppendAction(actionName, _ => action(data.typeIndex, data.name.Value));
        }

        internal static void UnbindNameCell(VisualElement element)
        {
            var labelWithIcon = (LabelWithIcon)element;
            labelWithIcon.RemoveManipulator((ContextualMenuManipulator)labelWithIcon.userData);
        }

        internal static void BindAverageSizeCell(MultiColumnTreeView multiColumnTreeView, int index, VisualElement element)
        {
            var sizePerEntity = ProfilerUtils.GetGhostTypeDataAtIndex(multiColumnTreeView, index).avgSizePerEntity;
            var bitsAndBytes = sizePerEntity != 0 ? ProfilerUtils.FormatFractionalBytes((uint)Math.Ceiling(sizePerEntity)) : "-";
            ((Label)element).text = bitsAndBytes;
        }

        internal static void BindCompressionEfficiencyCell(MultiColumnTreeView multiColumnTreeView, int index, VisualElement element)
        {
            var ghostTypeData = ProfilerUtils.GetGhostTypeDataAtIndex(multiColumnTreeView, index);
            var compressionEfficiency = ghostTypeData.combinedCompressionEfficiency;
            // No efficiency for overheads, empty entries or the root item
            var noCompressionEfficiency = index == 0 || ghostTypeData.overheadType != OverheadType.None || ghostTypeData.sizeInBits == 0;
            var compressionEfficiencyString = noCompressionEfficiency ? "-" : $"{compressionEfficiency}%";

            var labelWithIcon = (LabelWithIcon)element;
            labelWithIcon.ResetIcon();
            labelWithIcon.SetText(compressionEfficiencyString);
            var showWarning = compressionEfficiency <= NetcodeProfilerConstants.s_CompressionEfficiencyWarningThresholdPercentage
                && !noCompressionEfficiency;
            if (showWarning)
            {
                labelWithIcon.SetIcon(IconType.Warning);
                labelWithIcon.SetTooltip(NetcodeProfilerConstants.s_CompressionEfficiencyWarning);
            }
        }

        internal static void BindInstanceCountCell(MultiColumnTreeView multiColumnTreeView, int index, VisualElement element)
        {
            var data = ProfilerUtils.GetGhostTypeDataAtIndex(multiColumnTreeView, index);
            var count = data.instanceCount;
            var newInstances = data.newInstancesCount;
            var text = count == -1 ? "-" : count.ToString();
            if (newInstances > 0)
            {
                text += $" <color=#888888>(+{newInstances})</color>";
            }
            ((Label)element).text = text;
        }

        internal static void BindSnapshotPercentageCell(MultiColumnTreeView multiColumnTreeView, int index, VisualElement element)
        {
            var item = ProfilerUtils.GetGhostTypeDataAtIndex(multiColumnTreeView, index);
            var percentBar = (PercentBar)element;
            var value = index == 0 ? 100 : item.percentageOfSnapshot; // Root item is always 100%
            percentBar.SetValue(value);
        }

        internal static void BindSizeCell(MultiColumnTreeView multiColumnTreeView, int index, VisualElement element, int maxMessageSize)
        {
            var labelWithIcon = (LabelWithIcon)element;
            labelWithIcon.ResetIcon();
            var ghostTypeData = ProfilerUtils.GetGhostTypeDataAtIndex(multiColumnTreeView, index);
            var size = ghostTypeData.sizeInBits;
            var sizeInBytes = ProfilerUtils.BitsToBytes(size);
            var formattedBytes = ProfilerUtils.FormatFractionalBytes(size);
            var isOverhead = ghostTypeData.overheadType != OverheadType.None;
            labelWithIcon.SetText(formattedBytes);
            element.parent.parent.SetEnabled(isOverhead || size != 0); // Disable the row if size is 0 and not overhead
            var snapshotCount = ghostTypeData.snapshotCount;
            if (snapshotCount > 0 && maxMessageSize > 0)
            {
                // Show warning if the item is reaching max message size
                var totalMaxMessageSize = maxMessageSize * ghostTypeData.snapshotCount;
                var showWarning = sizeInBytes >= totalMaxMessageSize * NetcodeProfilerConstants.s_MaxMessageSizeWarningThreshold;
                if (showWarning)
                {
                    labelWithIcon.SetIcon(IconType.Warning);
                    labelWithIcon.SetTooltip(NetcodeProfilerConstants.GetMaxMessageSizeWarning(maxMessageSize, ghostTypeData.snapshotCount));
                }
            }
        }
    }
}

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
            var ghostTypeData = GetGhostTypeDataAtIndex(multiColumnTreeView, index);
            labelWithIcon.SetText(ghostTypeData.name.Value);
            // Insert Overhead icon in front of the name if this is an overhead item
            labelWithIcon.SetIconEnabled(ghostTypeData.overheadType != OverheadType.None);

            // Add tooltip for overhead items
            labelWithIcon.SetTooltip(ProfilerUtils.GetOverheadTooltip(ghostTypeData.overheadType));

            // Context menu
            if (ghostTypeData.overheadType == OverheadType.None && index != 0) // Can't inspect overhead or the root tick item
            {
                var actionName = ghostTypeData.isGhost ? "Inspect Ghost Prefab" : "Inspect Component";
                Action<TypeIndex, string> action = ghostTypeData.isGhost ? ProfilerUtils.SelectGhostPrefab : ProfilerUtils.SelectGhostComponent;
                element.AddManipulator(new ContextualMenuManipulator(evt =>
                {
                    evt.menu.AppendAction(actionName, _ => action(ghostTypeData.typeIndex, labelWithIcon.Label.text));
                }));
            }
        }

        internal static void BindAverageSizeCell(MultiColumnTreeView multiColumnTreeView, int index, VisualElement element)
        {
            var sizePerEntity = GetGhostTypeDataAtIndex(multiColumnTreeView, index).avgSizePerEntity;
            var bitsAndBytes = sizePerEntity != 0 ? $"{sizePerEntity} ({ProfilerUtils.BitsToBytes(sizePerEntity)})" : "-";
            ((Label)element).text = bitsAndBytes;
        }

        internal static void BindCompressionEfficiencyCell(MultiColumnTreeView multiColumnTreeView, int index, VisualElement element)
        {
            var ghostTypeData = GetGhostTypeDataAtIndex(multiColumnTreeView, index);
            var compressionEfficiency = ghostTypeData.combinedCompressionEfficiency;
            // No efficiency for overheads, empty entries or the root item
            var noCompressionEfficiency = index == 0 || ghostTypeData.overheadType != OverheadType.None || ghostTypeData.sizeInBits == 0;
            var compressionEfficiencyString = noCompressionEfficiency ? "-" : $"{compressionEfficiency}%";

            var labelWithIcon = (LabelWithIcon)element;
            labelWithIcon.SetText(compressionEfficiencyString);
            var showWarning = compressionEfficiency <= NetcodeProfilerConstants.s_CompressionEfficiencyWarningThresholdPercentage
                && !noCompressionEfficiency;
            labelWithIcon.SetIconEnabled(showWarning);
            if (showWarning)
            {
                labelWithIcon.SetTooltip(NetcodeProfilerConstants.s_CompressionEfficiencyWarning);
            }
        }

        internal static void BindInstanceCountCell(MultiColumnTreeView multiColumnTreeView, int index, VisualElement element)
        {
            var count = GetGhostTypeDataAtIndex(multiColumnTreeView, index).instanceCount;
            var newInstances = GetGhostTypeDataAtIndex(multiColumnTreeView, index).newInstancesCount;
            var text = count == -1 ? "-" : count.ToString();
            if (newInstances > 0)
            {
                text += $" <color=#888888>(+{newInstances})</color>";
            }
            ((Label)element).text = text;
        }

        internal static void BindSnapshotPercentageCell(MultiColumnTreeView multiColumnTreeView, int index, VisualElement element)
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
        }

        internal static void BindSizeCell(MultiColumnTreeView multiColumnTreeView, int index, VisualElement element, int maxMessageSize)
        {
            var labelWithIcon = (LabelWithIcon)element;
            var size = GetGhostTypeDataAtIndex(multiColumnTreeView, index).sizeInBits;
            var sizeInBytes = ProfilerUtils.BitsToBytes(size);
            var isOverhead = GetGhostTypeDataAtIndex(multiColumnTreeView, index).overheadType != OverheadType.None;
            var bitsAndBytes = $"{size} ({sizeInBytes})";
            labelWithIcon.SetText(bitsAndBytes);
            element.parent.parent.SetEnabled(isOverhead || size != 0); // Disable the row if size is 0 and not overhead
            // Show warning if the root item is reaching max message size
            var showWarning = index == 0 && sizeInBytes >= maxMessageSize * NetcodeProfilerConstants.s_MaxMessageSizeWarningThreshold;
            labelWithIcon.SetIconEnabled(showWarning);
            if (showWarning)
            {
                labelWithIcon.SetTooltip(NetcodeProfilerConstants.GetMaxMessageSizeWarning(maxMessageSize));
            }
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
    }


}

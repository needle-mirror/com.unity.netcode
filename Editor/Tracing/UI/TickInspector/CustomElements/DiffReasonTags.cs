using System.Collections.Generic;
using Unity.NetCode.Tracing;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI.TickInspector
{
    // A row of diff-reason tag: red while the reason's tag is selected in the filters and grey while deselected.
    class DiffReasonTags : VisualElement
    {
        readonly List<Label> m_TagPool = new();

        public void SetReasons(DiffInfo.DiffReasons reasons, DiffInfo.DiffReasons selectedReasons)
        {
            var used = 0;
            foreach (var entry in DiffReasonCatalog.All)
            {
                if ((reasons & entry.Reason) == 0)
                    continue;

                var tag = GetOrCreateTag(used++);
                tag.text = entry.Label;
                var selected = (selectedReasons & entry.Reason) != 0;
                tag.EnableInClassList(TickInspectorUssClasses.TreeViewItemDiffTagSelected, selected);
                tag.EnableInClassList(TickInspectorUssClasses.TreeViewItemDiffTagDeselected, !selected);
                tag.tooltip = entry.Tooltip;
                tag.style.display = DisplayStyle.Flex;
            }

            // Hosts are recycled (tree rows) or updated in place (metadata foldout); hide leftovers.
            for (var i = used; i < m_TagPool.Count; i++)
                m_TagPool[i].style.display = DisplayStyle.None;
        }

        Label GetOrCreateTag(int index)
        {
            if (index < m_TagPool.Count)
                return m_TagPool[index];

            var tag = new Label();
            tag.AddToClassList(TickInspectorUssClasses.TreeViewItemDiffTag);
            m_TagPool.Add(tag);
            Add(tag);
            return tag;
        }
    }
}

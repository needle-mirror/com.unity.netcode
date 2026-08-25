using Unity.NetCode.Tracing;
using UnityEditor;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI.TickInspector
{
    /// <summary>
    /// A heterogeneous tree row: <see cref="SetNode"/> drives the icon, label and action buttons from the
    /// bound <see cref="TickInspectorNode"/>.
    /// </summary>
    class TickInspectorTreeViewItem : VisualElement
    {
        protected VisualElement m_Root;
        protected Label m_Label;
        readonly VisualElement m_ButtonContainer;
        readonly Button m_OpenScriptButton;
        readonly Button m_OpenWindowButton;
        readonly DiffReasonTags m_DiffTagContainer;
        // Lazily created — only rows bound to a MispredictionTable node need it.
        MispredictionDetailTable m_Table;
        TickInspectorNode m_Node;

        static string UssClassName => TickInspectorUssClasses.TreeViewItem;

        public TickInspectorTreeViewItem()
        {
            m_Root = new VisualElement();
            Add(m_Root);
            m_Root.AddToClassList(UssClassName);

            m_Label = new Label();
            m_Label.AddToClassList(TickInspectorUssClasses.TreeViewItemLabel);

            m_DiffTagContainer = new DiffReasonTags();
            m_DiffTagContainer.AddToClassList(TickInspectorUssClasses.TreeViewItemDiffTagContainer);

            m_ButtonContainer = new VisualElement();
            m_ButtonContainer.AddToClassList(TickInspectorUssClasses.TreeViewItemButtonContainer);

            m_OpenScriptButton = new Button();
            m_OpenScriptButton.AddToClassList(TracingWindowUssClasses.IconButton);
            m_OpenScriptButton.AddToClassList(TracingWindowUssClasses.ScriptIcon);
            m_OpenScriptButton.tooltip = "Open script in IDE";

            m_OpenWindowButton = new Button();
            m_OpenWindowButton.AddToClassList(TracingWindowUssClasses.IconButton);
            m_OpenWindowButton.AddToClassList(TracingWindowUssClasses.OpenWindowIcon);

            // Prevent button clicks from toggling the row's TreeView chevron / selection.
            m_OpenScriptButton.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
            m_OpenWindowButton.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
            m_OpenScriptButton.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            m_OpenWindowButton.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());

            // Wire once; handlers read the current m_Node, so recycled rows need no per-bind closures.
            m_OpenScriptButton.clicked += OnOpenScriptClicked;
            m_OpenWindowButton.clicked += OnOpenWindowClicked;

            m_ButtonContainer.Add(m_OpenScriptButton);
            m_ButtonContainer.Add(m_OpenWindowButton);

            m_Root.Add(m_Label);
            m_Root.Add(m_DiffTagContainer);
            m_Root.Add(m_ButtonContainer);

            // Button enablement depends on play mode (see RefreshButtonStates); refresh on changes.
            RegisterCallback<AttachToPanelEvent>(_ => EditorApplication.playModeStateChanged += OnPlayModeStateChanged);
            RegisterCallback<DetachFromPanelEvent>(_ => EditorApplication.playModeStateChanged -= OnPlayModeStateChanged);
            RefreshButtonStates();
        }

        void OnPlayModeStateChanged(PlayModeStateChange _) => RefreshButtonStates();

        public void SetNode(TickInspectorNode node)
        {
            m_Node = node;
            ApplyVariantForType(node.NodeType);

            // The misprediction table replaces the row's label/badge/buttons with the detail grid.
            if (node.NodeType == TickInspectorNodeType.MispredictionTable)
            {
                ShowTable(node.Detail);
                return;
            }

            ShowRow();
            SetText(node.DisplayName);
            m_Root.EnableInClassList(TickInspectorUssClasses.TreeViewItemHasDiff, node.HasDiff);
            m_DiffTagContainer.SetReasons(node.DiffReasonFlags, node.SelectedDiffReasons);
            RefreshButtonStates();
        }

        void ShowTable(MispredictionDetail detail)
        {
            m_Label.style.display = DisplayStyle.None;
            m_DiffTagContainer.style.display = DisplayStyle.None;
            m_ButtonContainer.style.display = DisplayStyle.None;

            if (m_Table == null)
            {
                m_Table = new MispredictionDetailTable();
                m_Root.Add(m_Table);
            }
            m_Table.style.display = DisplayStyle.Flex;
            m_Table.SetDetail(detail);
        }

        void ShowRow()
        {
            m_Label.style.display = DisplayStyle.Flex;
            m_ButtonContainer.style.display = DisplayStyle.Flex;
            m_DiffTagContainer.style.display = DisplayStyle.Flex;
            if (m_Table != null)
                m_Table.style.display = DisplayStyle.None;
        }

        void ApplyVariantForType(TickInspectorNodeType nodeType)
        {
            var variant = nodeType switch
            {
                TickInspectorNodeType.SystemGroup => TickInspectorUssClasses.TreeViewItemSystemGroup,
                TickInspectorNodeType.Ghost => TickInspectorUssClasses.TreeViewItemGhost,
                TickInspectorNodeType.Component => TickInspectorUssClasses.TreeViewItemComponent,
                TickInspectorNodeType.MispredictionTable => TickInspectorUssClasses.TreeViewItemMispredictionTable,
                _ => TickInspectorUssClasses.TreeViewItemSystem,
            };
            ApplyVariantClass(variant);
        }

        // Variants are mutually exclusive — apply exactly one so only one icon renders.
        void ApplyVariantClass(string variantClass)
        {
            m_Root.RemoveFromClassList(TickInspectorUssClasses.TreeViewItemSystem);
            m_Root.RemoveFromClassList(TickInspectorUssClasses.TreeViewItemSystemGroup);
            m_Root.RemoveFromClassList(TickInspectorUssClasses.TreeViewItemGhost);
            m_Root.RemoveFromClassList(TickInspectorUssClasses.TreeViewItemComponent);
            m_Root.RemoveFromClassList(TickInspectorUssClasses.TreeViewItemMispredictionTable);
            m_Root.AddToClassList(variantClass);
        }

        public void SetText(string text)
        {
            m_Label.text = text;
        }

        void OnOpenScriptClicked()
        {
            if (m_Node != null)
                OpenScript(m_Node);
        }

        void OnOpenWindowClicked()
        {
            if (m_Node != null)
                OpenAssociatedWindow(m_Node);
        }

        static void OpenScript(TickInspectorNode node)
        {
            switch (node.NodeType)
            {
                case TickInspectorNodeType.Component:
                    TracingWindowUtility.OpenScriptInIde(node.ComponentType);
                    break;
                case TickInspectorNodeType.Ghost:
                    TracingWindowUtility.OpenGhostAuthoringScriptInIde(node.DisplayName);
                    break;
                default:
                    TracingWindowUtility.OpenScriptInIde(node.OwningSystemType);
                    break;
            }
        }

        static void OpenAssociatedWindow(TickInspectorNode node)
        {
            switch (node.NodeType)
            {
                case TickInspectorNodeType.Component:
                    NetcodeEditorUtility.ShowGhostComponentInspectorContent(node.ComponentType);
                    break;
                case TickInspectorNodeType.Ghost:
                    NetcodeEditorUtility.SelectGhost(node.GhostEntityId, node.DisplayName, node.FromClientWorld);
                    break;
                default:
                    NetcodeEditorUtility.ShowSystemInspectorContent(node.OwningSystemType, node.FromClientWorld);
                    break;
            }
        }

        void RefreshButtonStates()
        {
            if (m_Node == null)
            {
                m_OpenScriptButton.SetEnabled(false);
                m_OpenWindowButton.SetEnabled(false);
                m_OpenWindowButton.tooltip = string.Empty;
                return;
            }

            var isPlaying = EditorApplication.isPlaying;

            // Open Script: system/component need a resolvable type; a ghost is resolved best-effort by name.
            bool scriptEnabled = m_Node.NodeType switch
            {
                TickInspectorNodeType.Component => m_Node.ComponentType != null,
                TickInspectorNodeType.Ghost => !string.IsNullOrEmpty(m_Node.DisplayName),
                _ => m_Node.OwningSystemType != null,
            };
            m_OpenScriptButton.SetEnabled(scriptEnabled);

            // System Inspector needs Play mode; component inspector and ghost selection work in Edit mode too.
            bool windowEnabled;
            string windowTooltip;
            switch (m_Node.NodeType)
            {
                case TickInspectorNodeType.Component:
                    windowEnabled = m_Node.ComponentType != null;
                    windowTooltip = "Open component in Inspector window";
                    break;
                case TickInspectorNodeType.Ghost:
                    windowEnabled = true;
                    windowTooltip = isPlaying ? "Select ghost entity in Inspector" : "Ping ghost prefab in Project";
                    break;
                default:
                    windowEnabled = isPlaying && m_Node.OwningSystemType != null;
                    windowTooltip = isPlaying ? "Open system in Inspector window" : "Available only in Play mode";
                    break;
            }
            m_OpenWindowButton.SetEnabled(windowEnabled);
            m_OpenWindowButton.tooltip = windowTooltip;
        }
    }
}

using System;
using Unity.Netcode.LowLevel.StateSave;
using Unity.Netcode.Tracing;

namespace Unity.Netcode.Editor.Tracing.UI.TickInspector
{
    enum TickInspectorNodeType
    {
        System,
        SystemGroup,
        Ghost,
        Component,
        MispredictionTable,
    }

    /// <summary>
    /// A row in the <see cref="TickInspectorTreeView"/>. The tree is heterogeneous — <see cref="NodeType"/>
    /// selects the icon, buttons and which payload fields apply.
    /// </summary>
    sealed class TickInspectorNode
    {
        // Stable TreeView item id (partitioned by kind; see TickInspectorTreeBuilder).
        public int Id;
        public TickInspectorNodeType NodeType;
        public string DisplayName;
        public bool HasDiff;

        // Every reason this row owns, whether or not its tag is currently selected.
        public DiffInfo.DiffReasons DiffReasonFlags;

        // Every reason in this row's subtree; shown instead of DiffReasonFlags while the row is collapsed.
        public DiffInfo.DiffReasons InclusiveDiffReasonFlags;

        // The selected diff tags.
        public DiffInfo.DiffReasons SelectedDiffReasons = DiffInfo.AllDiffReasons;

        // The owning system (set on every kind, for the row actions); may be null if unresolved.
        public Type OwningSystemType;

        // Resolve inspector actions against the client world (the only data shown today).
        public bool FromClientWorld;

        // Ghost rows: the traced ghost, for live-entity / prefab selection.
        public SavedEntityID GhostEntityId;

        // Component rows: the component type.
        public Type ComponentType;

        // MispredictionTable rows: the client/server before/after values to render in the detail table.
        public MispredictionDetail Detail;
    }
}

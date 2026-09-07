namespace Unity.Netcode.Editor.Tracing.UI.TickInspector
{
    static class TickInspectorUssClasses
    {
        public const string Base = "tick-inspector";
        public const string Toolbar = Base + "__toolbar";
        public const string TreeView = Base + "__tree-view";
        public const string TreeViewItem = Base + "__tree-view-item";
        public const string TreeViewItemLabel = TreeViewItem + "-label";

        public const string TreeViewItemDiffTagContainer = TreeViewItem + "-diff-tag-container";
        public const string TreeViewItemDiffTag = TreeViewItem + "-diff-tag";
        public const string TreeViewItemDiffTagSelected = TreeViewItemDiffTag + "--selected";
        public const string TreeViewItemDiffTagDeselected = TreeViewItemDiffTag + "--deselected";

        public const string TreeViewItemButtonContainer = TreeViewItem + "-button-container";
        public const string TreeViewItemSystem = TreeViewItem + "--system";
        public const string TreeViewItemSystemGroup = TreeViewItem + "--system-group";
        public const string TreeViewItemGhost = TreeViewItem + "--ghost";
        public const string TreeViewItemComponent = TreeViewItem + "--component";
        public const string TreeViewItemHasDiff = TreeViewItem + "--has-diff";
        public const string TreeViewItemMispredictionTable = TreeViewItem + "--misprediction-table";
        public const string TreeViewItemMispredictionTableContainer = TreeViewItemMispredictionTable + "__container";

        // The tick metadata foldout that sits above the tree (see TickInspectorMetadataFoldout).
        public const string MetadataFoldout = Base + "__metadata-foldout";
        public const string MetadataFoldoutLabel = MetadataFoldout + "-label";
        public const string MetadataRow = MetadataFoldout + "-row";
        public const string MetadataRowName = MetadataRow + "__name";
        public const string MetadataRowValue = MetadataRow + "__value";
        public const string MetadataTagContainer = MetadataFoldout + "-tag-container";

        // The per-component misprediction/desync detail table (see MispredictionDetailTable).
        public const string MispredictionTable = Base + "__misprediction-table";
        public const string MispredictionTableHeader = MispredictionTable + "-header";
        public const string MispredictionTableRow = MispredictionTable + "-row";
        public const string MispredictionTableColumn = MispredictionTable + "-column";
        public const string MispredictionTableSideLabel = MispredictionTable + "-side-label";
        public const string MispredictionTableCell = MispredictionTable + "-cell";
        public const string MispredictionTableFields = MispredictionTable + "-fields";
        public const string MispredictionTableFieldLine = MispredictionTable + "-field-line";

        // Misprediction row
        public const string MispredictionRow = Base + "__misprediction-row";

        // Banding; applied to top-level lines only so both cells stripe identically.
        public const string MispredictionTableSpan = MispredictionTable + "-span";
        public const string MispredictionTableFieldName = MispredictionTable + "-field-name";
        public const string MispredictionTableSubFieldName = MispredictionTable + "-sub-field-name";
        public const string MispredictionTablePunctuation = MispredictionTable + "-punctuation";
        public const string MispredictionTableField = MispredictionTable + "-field";
        public const string MispredictionTableFieldDiff = MispredictionTableField + "--diff";
        public const string MispredictionTableFieldValueChanges = MispredictionTableField + "--value-change";

        public const string MispredictionTableFieldServerDiff = MispredictionTableField + "--server-diff";
        public const string MispredictionTableFieldUnsupported = MispredictionTableField + "--unsupported";
        // The expand/collapse toggle placed before the field name on expandable rows.
        // Styled with "unity-foldout__toggle" to match the Editor's standard foldout look.
        public const string MispredictionTableSummaryArrow = MispredictionTable + "-summary-arrow";
        // The clickable "N values, M changed" token standing in for a collapsed field.
        public const string MispredictionTableSummary = MispredictionTable + "-summary";
        // Holds a collapsed field's per-child lines; built up front and hidden, never destroyed.
        public const string MispredictionTableExpandedChildren = MispredictionTable + "-expanded-children";
        public const string MispredictionTableNoData = MispredictionTable + "-no-data";
    }
}

using Unity.NetCode.Tracing;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI.TickInspector
{
    /// <summary>
    /// The timing/batching data of the selected tick, shown in <see cref="TickInspectorMetadataFoldout"/>.
    /// </summary>
    struct TickInspectorTickMetadata
    {
        public bool HasClientTick;
        public float ClientDeltaTimeSeconds;
        public bool IsPartialTick;
        public float PartialTickFraction;
        public float FrameDeltaTimeSeconds;
        public bool HasServerTick;
        public float ServerDeltaTimeSeconds;
        public int ServerBatchSize;
    }

    /// <summary>
    /// The tick metadata foldout above the <see cref="TickInspectorTreeView"/>: the selected tick's
    /// timing/batching values plus the diff tags no system row can carry (DeltaTime, BatchedTick),
    /// so tick-level diffs are explained here rather than by a synthetic tree row.
    /// </summary>
    class TickInspectorMetadataFoldout : Foldout
    {
        const string k_NoValue = "—";
        const string k_TextDiffTags = "Diff tags";
        const string k_TextClientDeltaTime = "Client delta time";
        const string k_TextServerDeltaTime = "Server delta time";
        const string k_TextServerBatchSize = "Server batch size";
        const string k_TextPartialTick = "Partial tick";
        const string k_TextFrameDeltaTime = "Frame delta time";

        const string k_TooltipClientDeltaTime = "Delta time the client simulated this tick with.";
        const string k_TooltipServerDeltaTime = "Delta time the server simulated this tick with. A batched tick's delta time covers the whole batch.";
        const string k_TooltipServerBatchSize = "Number of ticks the server simulated together in one step. More than 1 means this tick was batched.";
        const string k_TooltipPartialTick = "A partial tick is predicted with only a fraction of the tick's delta time.";
        const string k_TooltipFrameDeltaTime = "Delta time of the editor frame this tick was captured in.";

        readonly VisualElement m_TagRow;
        readonly DiffReasonTags m_TagContainer;
        readonly Label m_ClientDeltaTimeValue;
        readonly Label m_ServerDeltaTimeValue;
        readonly Label m_ServerBatchSizeValue;
        readonly Label m_PartialTickValue;
        readonly Label m_FrameDeltaTimeValue;

        public TickInspectorMetadataFoldout()
        {
            AddToClassList(TickInspectorUssClasses.MetadataFoldout);
            text = "Metadata";
            value = false; // collapsed: reads as a single row

            // Tag the title label so USS can style it without relying on UI Toolkit's internal class name.
            this.Q<Label>(className: textUssClassName)?.AddToClassList(TickInspectorUssClasses.MetadataFoldoutLabel);

            m_TagRow = CreateRow(k_TextDiffTags, null, out _);
            m_TagContainer = new DiffReasonTags();
            m_TagContainer.AddToClassList(TickInspectorUssClasses.MetadataTagContainer);
            m_TagRow.Add(m_TagContainer);

            CreateRow(k_TextClientDeltaTime, k_TooltipClientDeltaTime, out m_ClientDeltaTimeValue);
            CreateRow(k_TextServerDeltaTime, k_TooltipServerDeltaTime, out m_ServerDeltaTimeValue);
            CreateRow(k_TextServerBatchSize, k_TooltipServerBatchSize, out m_ServerBatchSizeValue);
            CreateRow(k_TextPartialTick, k_TooltipPartialTick, out m_PartialTickValue);
            CreateRow(k_TextFrameDeltaTime, k_TooltipFrameDeltaTime, out m_FrameDeltaTimeValue);

            SetMetadata(default);
            SetTickDiffReasons(DiffInfo.DiffReasons.Undefined, DiffInfo.AllDiffReasons);
        }

        // A "name: value" line; the value label is null for rows that carry custom content (the tag row).
        VisualElement CreateRow(string name, string tooltip, out Label valueLabel)
        {
            var row = new VisualElement { tooltip = tooltip };
            row.AddToClassList(TickInspectorUssClasses.MetadataRow);

            var nameLabel = new Label(name);
            nameLabel.AddToClassList(TickInspectorUssClasses.MetadataRowName);
            row.Add(nameLabel);

            valueLabel = tooltip == null ? null : new Label(k_NoValue);
            if (valueLabel != null)
            {
                valueLabel.AddToClassList(TickInspectorUssClasses.MetadataRowValue);
                row.Add(valueLabel);
            }

            Add(row);
            return row;
        }

        /// <summary>Fills the value rows from the selected tick's data.</summary>
        public void SetMetadata(in TickInspectorTickMetadata metadata)
        {
            if (!metadata.HasClientTick)
            {
                m_ClientDeltaTimeValue.text = k_NoValue;
                m_ServerDeltaTimeValue.text = k_NoValue;
                m_ServerBatchSizeValue.text = k_NoValue;
                m_PartialTickValue.text = k_NoValue;
                m_FrameDeltaTimeValue.text = k_NoValue;
                return;
            }

            m_ClientDeltaTimeValue.text = FormatSeconds(metadata.ClientDeltaTimeSeconds);
            m_PartialTickValue.text = metadata.IsPartialTick
                ? $"Yes ({metadata.PartialTickFraction:0.##} of a full tick)"
                : "No";
            m_FrameDeltaTimeValue.text = FormatSeconds(metadata.FrameDeltaTimeSeconds);

            if (metadata.HasServerTick)
            {
                m_ServerDeltaTimeValue.text = FormatSeconds(metadata.ServerDeltaTimeSeconds);
                m_ServerBatchSizeValue.text = metadata.ServerBatchSize > 1
                    ? $"{metadata.ServerBatchSize} (this tick was simulated as part of a batch)"
                    : metadata.ServerBatchSize.ToString();
            }
            else
            {
                // No server trace for this tick (e.g. batched away or not recorded yet).
                m_ServerDeltaTimeValue.text = k_NoValue;
                m_ServerBatchSizeValue.text = k_NoValue;
            }
        }

        /// <summary>
        /// Shows the tick-scoped diff reasons as the same tag pills the tree rows use. The foldout
        /// opens only while a selected tick-scoped reason exists, mirroring the tree's expansion.
        /// </summary>
        public void SetTickDiffReasons(DiffInfo.DiffReasons reasons, DiffInfo.DiffReasons selectedReasons)
        {
            m_TagContainer.SetReasons(reasons, selectedReasons);
            m_TagRow.style.display = reasons != DiffInfo.DiffReasons.Undefined ? DisplayStyle.Flex : DisplayStyle.None;
            value = (reasons & selectedReasons) != 0;
        }

        static string FormatSeconds(float seconds) => $"{seconds * 1000f:0.##} ms";
    }
}

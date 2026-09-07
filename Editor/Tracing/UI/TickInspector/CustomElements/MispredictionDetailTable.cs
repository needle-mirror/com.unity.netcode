using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Entities;
using Unity.Netcode.Tracing;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor.Tracing.UI.TickInspector
{
    /// <summary>
    /// The per-component misprediction table (Predicted Tick | Server Authority; header / before / after).
    /// Diverging values are found by comparing the stored client and server instances leaf by leaf against
    /// the component's <see cref="ComponentFieldTree"/>, so the highlight granularity matches what is drawn
    /// however deeply the value is nested.
    /// </summary>
    class MispredictionDetailTable : VisualElement
    {
        const string k_TemplatePath = Constants.Templates + "misprediction-detail-table.uxml";
        // Cached once: this element is re-created on every TreeView row bind, so avoid a per-bind asset load.
        static VisualTreeAsset s_Template;

        const float k_ExactEpsilon = 0f;
        const int k_IndentPixels = 12;
        const string k_CompactFloatFormat = "0.####";
        const string k_TruncatedValue = "{...}";

        // Scratch for TypeDiffer.ComputeUnitDiffs; the table is only ever rebuilt on the UI thread.
        static readonly float[] s_UnitDiffAmounts = new float[TypeDiffer.MaxFieldBits];

        // Keyed by component type + field path so an expanded matrix stays expanded across row rebinds.
        static readonly HashSet<string> s_ExpandedPaths = new();
        static readonly HashSet<string> s_CollapsedPaths = new();

        sealed class ExpandableView
        {
            public FieldNode Node;
            public Toggle Arrow;
            public Label Summary;
            public VisualElement ChildHost;
            public int ChangedLeaves;
            public bool ForceDiff;
            public int EntryCount;

            public Action BuildChildren;
        }

        readonly VisualElement m_BeforeClientFields;
        readonly VisualElement m_BeforeServerFields;
        readonly VisualElement m_AfterClientFields;
        readonly VisualElement m_AfterServerFields;

        readonly Dictionary<string, List<ExpandableView>> m_ExpandableViews = new();

        MispredictionDetail m_Detail;

        public MispredictionDetailTable()
        {
            if (s_Template == null)
                s_Template = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(k_TemplatePath);
            if (s_Template == null)
            {
                Debug.LogError("Could not load misprediction-detail-table.uxml");
                return;
            }

            s_Template.CloneTree(this);

            RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());

            m_BeforeClientFields = this.Q<VisualElement>("before-client-fields");
            m_BeforeServerFields = this.Q<VisualElement>("before-server-fields");
            m_AfterClientFields = this.Q<VisualElement>("after-client-fields");
            m_AfterServerFields = this.Q<VisualElement>("after-server-fields");
        }

        public void SetDetail(MispredictionDetail detail)
        {
            m_Detail = detail;
            Rebuild();
        }

        void Rebuild()
        {
            if (m_Detail == null || m_BeforeClientFields == null)
                return;

            m_ExpandableViews.Clear();
            var root = ComponentFieldTree.GetRoot(m_Detail.ComponentType);
            var clientTemporalDiff = TemporalDiff(root, m_Detail.ClientBefore, m_Detail.ClientAfter);
            var serverTemporalDiff = TemporalDiff(root, m_Detail.ServerBefore, m_Detail.ServerAfter);

            PopulateRow(m_BeforeClientFields, m_BeforeServerFields, m_Detail.ComponentType,
                m_Detail.ClientBefore, m_Detail.ServerBefore, clientTemporalDiff, serverTemporalDiff);

            PopulateRow(m_AfterClientFields, m_AfterServerFields, m_Detail.ComponentType,
                m_Detail.ClientAfter, m_Detail.ServerAfter, clientTemporalDiff, serverTemporalDiff);
        }

        static FieldDiffResult TemporalDiff(FieldNode root, IComponentData before, IComponentData after)
        {
            if ((before == null) != (after == null))
                return ComponentFieldTree.MarkAllLeavesChanged(root);

            return ComponentFieldTree.Compare(root, before, after, k_ExactEpsilon);
        }

        void PopulateRow(VisualElement clientContainer, VisualElement serverContainer, Type componentType,
            IComponentData clientValue, IComponentData serverValue,
            FieldDiffResult clientTemporalDiff, FieldDiffResult serverTemporalDiff)
        {
            clientContainer.Clear();
            serverContainer.Clear();

            var root = ComponentFieldTree.GetRoot(componentType);
            var clientPresent = clientValue != null;
            var serverPresent = serverValue != null;

            // Zero-field tag component: presence on each side is the value, and differing presence is the diff.
            if (root == null || root.Children.Length == 0)
            {
                var presenceDiffers = clientPresent != serverPresent;
                AddPresence(clientContainer, clientPresent, isClient: true, diff: presenceDiffers);
                AddPresence(serverContainer, serverPresent, isClient: false, diff: presenceDiffers);
                return;
            }

            if (!clientPresent || !serverPresent)
            {
                var empty = new FieldDiffResult();
                AddSide(clientContainer, root, clientValue, componentType, empty, isClient: true, forceDiff: false, clientTemporalDiff);
                AddSide(serverContainer, root, serverValue, componentType, empty, isClient: false, forceDiff: false, serverTemporalDiff);
                return;
            }

            // Compare against the same fuzzy threshold the tick row above was highlighted with
            // (TracingViewFilters.FuzzyDiffThreshold), so the table cannot contradict it.
            var threshold = m_Detail.FuzzyDiffThreshold;
            var diff = ComponentFieldTree.Compare(root, clientValue, serverValue, threshold);
            var fieldMask = ComputeUnitDiffMask(componentType, clientValue, serverValue, threshold);

            // Fallback: the differ found a diverging unit that the field-level walk did not resolve to a
            // leaf, so highlight what the mask points at rather than showing an unmarked component.
            var needsFallbackHighlight = fieldMask != 0 && diff.TotalChanged == 0 && !diff.HasLengthChanges;
            var forcedFields = needsFallbackHighlight ? ResolveMaskedFields(root, fieldMask) : null;
            var forceAll = needsFallbackHighlight && forcedFields == null;

            RenderSide(clientContainer, root, clientValue, componentType, diff, isClient: true, forceDiff: forceAll, clientTemporalDiff, forcedFields);
            RenderSide(serverContainer, root, serverValue, componentType, diff, isClient: false, forceDiff: forceAll, serverTemporalDiff, forcedFields);
        }

        void AddSide(VisualElement container, FieldNode root, IComponentData value, Type componentType,
            FieldDiffResult diff, bool isClient, bool forceDiff, FieldDiffResult temporalDiff)
        {
            if (value == null)
                AddNoData(container);
            else
                RenderSide(container, root, value, componentType, diff, isClient, forceDiff, temporalDiff);
        }

        void RenderSide(VisualElement container, FieldNode root, object rootValue, Type componentType,
            FieldDiffResult diff, bool isClient, bool forceDiff, FieldDiffResult temporalDiff, bool[] forcedFields = null)
        {
            for (var i = 0; i < root.Children.Length; i++)
            {
                var forced = forceDiff || (forcedFields != null && forcedFields[i]);
                AddFieldBlock(container, root.Children[i], rootValue, componentType, diff, isClient, forced, temporalDiff, depth: 0, rowIndex: i);
            }
        }

        static bool[] ResolveMaskedFields(FieldNode root, ulong fieldMask)
        {
            var flags = new bool[root.Children.Length];
            var unit = 0;
            for (var i = 0; i < root.Children.Length; i++)
            {
                var child = root.Children[i];
                var isMath = child.Type?.Namespace == ComponentFieldTree.MathNamespace;
                if (isMath && child.IsLeaf)
                    return null;

                var units = isMath ? Math.Max(1, child.LeafCount) : 1;
                if (unit + units > TypeDiffer.MaxFieldBits)
                    return null;

                for (var bit = unit; bit < unit + units; bit++)
                    flags[i] |= (fieldMask & (1UL << bit)) != 0;
                unit += units;
            }

            // A mask with no bit inside the mapped range means the layout does not line up after all.
            foreach (var flag in flags)
                if (flag)
                    return flags;
            return null;
        }

        void AddFieldBlock(VisualElement container, FieldNode node, object ownerValue, Type componentType,
            FieldDiffResult diff, bool isClient, bool forceDiff, FieldDiffResult temporalDiff, int depth, int rowIndex)
        {
            var line = new VisualElement();
            line.AddToClassList(TickInspectorUssClasses.MispredictionTableFieldLine);
            if (depth > 0)
                line.style.marginLeft = depth * k_IndentPixels;

            if (node.Kind == FieldDisplayKind.Expandable)
            {
                AddExpandableBlock(container, line, node, ownerValue, componentType, diff, isClient, forceDiff, temporalDiff, depth);
                return;
            }

            // Wrap content in an inline element so border-bottom ends at the last value rather than
            // stretching across the full width of the row.
            var contentWrap = new VisualElement();
            contentWrap.style.flexDirection = FlexDirection.Row;
            contentWrap.style.flexWrap = Wrap.NoWrap;

            contentWrap.Add(FieldNameSpan(node.Name));
            contentWrap.Add(PunctuationSpan(" ("));
            var first = true;
            AppendValueTokens(contentWrap, node, ownerValue, diff, isClient, forceDiff, ref first, labelled: false, temporalDiff);
            contentWrap.Add(PunctuationSpan(")"));


            if (AllLeavesChanged(node, diff, forceDiff))
            {
                var diffClass = isClient
                    ? TickInspectorUssClasses.MispredictionTableFieldDiff
                    : TickInspectorUssClasses.MispredictionTableFieldServerDiff;
                contentWrap.AddToClassList(diffClass);
                contentWrap.Query<Label>(className: diffClass).ForEach(l => l.RemoveFromClassList(diffClass));
            }

            line.Add(contentWrap);
            container.Add(line);
        }

        static bool AllLeavesChanged(FieldNode node, FieldDiffResult diff, bool forceDiff)
        {
            if (forceDiff) return true;
            if (node.IsLeaf) return diff.ChangedPaths.Contains(node.Path);
            return diff.ChangedLeafCounts.TryGetValue(node.Path, out var count) && count >= node.LeafCount;
        }

        static bool HasChangeUnder(FieldNode node, FieldDiffResult diff, bool forceDiff)
        {
            if (forceDiff)
                return true;
            if (node.IsLeaf)
                return diff.ChangedPaths.Contains(node.Path);
            return diff.ChangedLeafCounts.TryGetValue(node.Path, out var count) && count > 0;
        }

        void AddExpandableBlock(VisualElement container, VisualElement line, FieldNode node, object ownerValue,
            Type componentType, FieldDiffResult diff, bool isClient, bool forceDiff, FieldDiffResult temporalDiff, int depth)
        {
            var changed = diff.ChangedLeafCounts.TryGetValue(node.Path, out var count) ? count : 0;
            var autoExpanded = (changed > 0 || forceDiff) && !IsCollapsedByUser(componentType, node.Path);
            var expanded = autoExpanded || IsExpanded(componentType, node.Path);

            var ownValue = node.ReadValue(ownerValue);
            var entryCount = node.IsList ? ComponentFieldTree.ReadListLength(ownValue) : 0;
            var rows = node.IsList
                ? (diff.ListRowCounts.TryGetValue(node.Path, out var maxRows) ? maxRows : entryCount)
                : node.Children.Length;

            var arrow = new Toggle { value = expanded };
            arrow.AddToClassList("unity-foldout__toggle");
            arrow.AddToClassList(TickInspectorUssClasses.MispredictionTableSummaryArrow);
            var summary = ValueSpan(FormatSummary(node, changed, forceDiff, entryCount), forceDiff || changed > 0, isClient);
            summary.AddToClassList(TickInspectorUssClasses.MispredictionTableSummary);
            var nameSpan = FieldNameSpan(node.Name);
            line.Add(arrow);
            line.Add(nameSpan);
            line.Add(PunctuationSpan(" "));
            line.Add(summary);
            container.Add(line);

            var childHost = new VisualElement();
            childHost.AddToClassList(TickInspectorUssClasses.MispredictionTableExpandedChildren);
            childHost.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            container.Add(childHost);

            var view = new ExpandableView { Node = node, Arrow = arrow, Summary = summary, ChildHost = childHost, ChangedLeaves = changed, ForceDiff = forceDiff, EntryCount = entryCount };
            // Built on first expand, then kept: a collapsed field costs one label however many leaves it
            view.BuildChildren = () =>
            {
                for (var i = 0; i < rows && i < node.Children.Length; i++)
                {
                    if (node.IsList && i >= entryCount)
                        childHost.Add(AbsentEntryLine(node.Children[i], depth + 1));
                    else
                        AddFieldBlock(childHost, node.Children[i], ownValue, componentType, diff, isClient, forceDiff, temporalDiff, depth + 1, i);
                }
            };
            if (expanded)
                BuildChildrenOnce(view);

            if (!m_ExpandableViews.TryGetValue(node.Path, out var views))
            {
                views = new List<ExpandableView>();
                m_ExpandableViews[node.Path] = views;
            }
            views.Add(view);

            ApplyDivergenceTooltip(view);

            arrow.RegisterValueChangedCallback(evt =>
            {
                evt.StopPropagation();
                SetExpanded(componentType, node.Path, evt.newValue);
            });

            EventCallback<PointerDownEvent> toggleOnClick = evt =>
            {
                evt.StopPropagation();
                SetExpanded(componentType, node.Path, !arrow.value);
            };

            summary.RegisterCallback(toggleOnClick);
            nameSpan.RegisterCallback(toggleOnClick);
        }

        internal void SetExpanded(Type componentType, string path, bool expanded)
        {
            var key = ExpansionKey(componentType, path);
            if (expanded)
            {
                s_ExpandedPaths.Add(key);
                s_CollapsedPaths.Remove(key);
            }
            else
            {
                s_ExpandedPaths.Remove(key);
                s_CollapsedPaths.Add(key);
            }

            if (!m_ExpandableViews.TryGetValue(path, out var views))
                return;

            foreach (var view in views)
            {
                if (expanded)
                    BuildChildrenOnce(view);
                view.ChildHost.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                view.Arrow.SetValueWithoutNotify(expanded);
                view.Summary.text = FormatSummary(view.Node, view.ChangedLeaves, view.ForceDiff, view.EntryCount);
                ApplyDivergenceTooltip(view);
            }
        }

        static void BuildChildrenOnce(ExpandableView view)
        {
            if (view.BuildChildren == null)
                return;
            view.BuildChildren();
            view.BuildChildren = null;
        }

        // No click hint: clicking the row is quicker than waiting for a tooltip to say it can be clicked.
        static void ApplyDivergenceTooltip(ExpandableView view)
        {
            var diverging = view.ForceDiff || view.ChangedLeaves > 0;
            var noun = view.ChangedLeaves == 1 ? "value" : "values";
            view.Summary.tooltip = diverging ? $"{view.ChangedLeaves} diverging {noun}" : null;
        }

        static string FormatSummary(FieldNode node, int changed, bool forceDiff, int entryCount)
        {
            var total = node.IsList ? entryCount : node.LeafCount;
            var noun = node.IsList
                ? (total == 1 ? "entry" : "entries")
                : (total == 1 ? "value" : "values");
            if (forceDiff)
                return $"{total} {noun}, {total} changed";
            return changed > 0
                ? $"{total} {noun}, {changed} changed"
                : $"{total} {noun}";
        }

        static VisualElement AbsentEntryLine(FieldNode entry, int depth)
        {
            var line = new VisualElement();
            line.AddToClassList(TickInspectorUssClasses.MispredictionTableFieldLine);
            line.style.marginLeft = depth * k_IndentPixels;
            line.Add(FieldNameSpan(entry.Name));
            var absent = new Label("No entry");
            absent.AddToClassList(TickInspectorUssClasses.MispredictionTableSpan);
            absent.AddToClassList(TickInspectorUssClasses.MispredictionTableField);
            absent.AddToClassList(TickInspectorUssClasses.MispredictionTableFieldUnsupported);
            absent.tooltip = "The other side has an entry at this index; this one does not.";
            line.Add(absent);
            return line;
        }

        static void AppendValueTokens(VisualElement line, FieldNode node, object ownerValue,
            FieldDiffResult diff, bool isClient, bool forceDiff, ref bool first, bool labelled, FieldDiffResult temporalDiff)
        {
            var value = node.ReadValue(ownerValue);

            if (node.IsLeaf)
            {
                if (!first)
                    line.Add(PunctuationSpan(", "));
                first = false;
                if (labelled)
                    line.Add(SubFieldNameSpan($"{node.Name}="));
                line.Add(BuildLeafToken(node, value, diff, isClient, forceDiff, temporalDiff));
                return;
            }

            if (labelled)
            {
                if (!first)
                    line.Add(PunctuationSpan(", "));
                first = false;
                line.Add(SubFieldNameSpan($"{node.Name}="));
                line.Add(PunctuationSpan("("));
                var nested = true;
                foreach (var child in node.Children)
                    AppendValueTokens(line, child, value, diff, isClient, forceDiff, ref nested, !node.PositionalChildren, temporalDiff);
                line.Add(PunctuationSpan(")"));
                return;
            }

            foreach (var child in node.Children)
                AppendValueTokens(line, child, value, diff, isClient, forceDiff, ref first, !node.PositionalChildren, temporalDiff);
        }

        static Label BuildLeafToken(FieldNode node, object value, FieldDiffResult diff, bool isClient, bool forceDiff, FieldDiffResult temporalDiff)
        {
            if (node.Kind == FieldDisplayKind.Unsupported)
            {
                var unsupported = new Label("—");
                unsupported.AddToClassList(TickInspectorUssClasses.MispredictionTableSpan);
                unsupported.AddToClassList(TickInspectorUssClasses.MispredictionTableField);
                unsupported.AddToClassList(TickInspectorUssClasses.MispredictionTableFieldUnsupported);
                unsupported.tooltip = $"{node.Type?.Name} holds a reference or pointer; tracing cannot capture or compare it.";
                return unsupported;
            }

            // A value the tree stopped descending into is not its own ToString: say it was truncated.
            var text = node.DepthTruncated ? k_TruncatedValue : FormatLeafValue(value);
            var truncationLine = node.DepthTruncated
                ? $"Nested more than {ComponentFieldTree.MaxDepth} levels deep, so it is not expanded."
                : null;
            var temporalLine = BuildTemporalLine(node, temporalDiff);

            var isDiff = forceDiff || diff.ChangedPaths.Contains(node.Path);
            var token = ValueSpan(text, isDiff, isClient);

            if (isDiff && !forceDiff && diff.LeafValues.TryGetValue(node.Path, out var pair))
            {
                if (temporalLine != null)
                    token.AddToClassList(TickInspectorUssClasses.MispredictionTableFieldValueChanges);
                token.tooltip = BuildDiffTooltip(pair, isClient, temporalLine);
            }
            else if (temporalLine != null)
            {
                token.AddToClassList(TickInspectorUssClasses.MispredictionTableFieldValueChanges);
                token.tooltip = temporalLine;
            }

            if (truncationLine != null)
                token.tooltip = string.IsNullOrEmpty(token.tooltip) ? truncationLine : $"{token.tooltip}\n{truncationLine}";
            return token;
        }

        static string BuildTemporalLine(FieldNode node, FieldDiffResult temporalDiff)
        {
            if (!temporalDiff.ChangedPaths.Contains(node.Path))
                return null;

            return temporalDiff.LeafValues.TryGetValue(node.Path, out var temporalPair)
                ? FormatTemporalChange(temporalPair)
                : "Value changed before to after";
        }

        static string FormatLeafValue(object value) =>
            value switch
            {
                null => string.Empty,
                float f => f.ToString(k_CompactFloatFormat, CultureInfo.InvariantCulture),
                double d => d.ToString(k_CompactFloatFormat, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };

        static bool IsExpanded(Type componentType, string path) => s_ExpandedPaths.Contains(ExpansionKey(componentType, path));

        static bool IsCollapsedByUser(Type componentType, string path) => s_CollapsedPaths.Contains(ExpansionKey(componentType, path));

        internal static void ClearExpansionState()
        {
            s_ExpandedPaths.Clear();
            s_CollapsedPaths.Clear();
        }

        static string ExpansionKey(Type componentType, string path) => $"{componentType?.FullName}|{path}";

        /// <summary>
        /// The differ's per-unit diff mask for this pair, with the units whose recorded amount does not
        /// exceed <paramref name="threshold"/> cleared — the same rule the views apply to the stored
        /// component-level amounts, so a component the threshold silenced cannot force a highlight here.
        /// Bit i is unit i; see <see cref="ResolveMaskedFields"/> for how those map back to fields.
        /// </summary>
        static ulong ComputeUnitDiffMask(Type componentType, object clientValue, object serverValue, float threshold)
        {
            var mask = TypeDiffer.ComputeUnitDiffs(componentType, clientValue, serverValue, s_UnitDiffAmounts);
            if (threshold <= 0f)
                return mask;

            for (var bit = 0; bit < TypeDiffer.MaxFieldBits; bit++)
            {
                if ((mask & (1UL << bit)) != 0 && !(s_UnitDiffAmounts[bit] > threshold))
                    mask &= ~(1UL << bit);
            }

            return mask;
        }

        static string BuildDiffTooltip((object Client, object Server) pair, bool isClient, string temporalLine)
        {
            var line1 = TryFormatDelta(pair.Client, pair.Server, out var delta)
                ? $"Diff value from server to client: {delta}"
                : isClient ? "Diverging predicted value" : "Diverging server authority value";

            return temporalLine == null ? line1 : $"{line1}\n{temporalLine}";
        }

        static string FormatTemporalChange((object Client, object Server) temporalPair) =>
            TryFormatDelta(temporalPair.Client, temporalPair.Server, out var temporalDelta)
                ? $"Value changed before to after: {temporalDelta}"
                : $"Value changed before to after: {FormatLeafValue(temporalPair.Client)} → {FormatLeafValue(temporalPair.Server)}";

        static bool TryFormatDelta(object clientVal, object serverVal, out string delta)
        {
            delta = null;
            if (clientVal == null || serverVal == null)
                return false;

            if (TryToDecimal(clientVal, out var clientDecimal) && TryToDecimal(serverVal, out var serverDecimal))
            {
                var integralDelta = serverDecimal - clientDecimal;
                delta = Format(integralDelta >= 0, integralDelta.ToString(CultureInfo.InvariantCulture));
                return true;
            }

            var clientStr = Convert.ToString(clientVal, CultureInfo.InvariantCulture);
            var serverStr = Convert.ToString(serverVal, CultureInfo.InvariantCulture);
            if (!double.TryParse(clientStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var clientValue) ||
                !double.TryParse(serverStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var serverValue))
                return false;
            var value = serverValue - clientValue;
            delta = Format(value >= 0, value.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        static string Format(bool nonNegative, string magnitude) => nonNegative ? $"+{magnitude}" : magnitude;

        static bool TryToDecimal(object value, out decimal result)
        {
            switch (value)
            {
                case byte v: result = v; return true;
                case sbyte v: result = v; return true;
                case short v: result = v; return true;
                case ushort v: result = v; return true;
                case int v: result = v; return true;
                case uint v: result = v; return true;
                case long v: result = v; return true;
                case ulong v: result = v; return true;
                case IntPtr v: result = v.ToInt64(); return true;
                case UIntPtr v: result = v.ToUInt64(); return true;
                default: result = 0m; return false;
            }
        }

        static Label StructuralSpan(string text)
        {
            var label = new Label(text);
            label.AddToClassList(TickInspectorUssClasses.MispredictionTableSpan);
            return label;
        }

        static Label FieldNameSpan(string name)
        {
            var label = StructuralSpan(name);
            label.AddToClassList(TickInspectorUssClasses.MispredictionTableFieldName);
            return label;
        }

        static Label SubFieldNameSpan(string text)
        {
            var label = StructuralSpan(text);
            label.AddToClassList(TickInspectorUssClasses.MispredictionTableSubFieldName);
            return label;
        }

        static Label PunctuationSpan(string text)
        {
            var label = StructuralSpan(text);
            label.AddToClassList(TickInspectorUssClasses.MispredictionTablePunctuation);
            return label;
        }

        static Label ValueSpan(string text, bool diff, bool isClient)
        {
            var label = new Label(text);
            label.AddToClassList(TickInspectorUssClasses.MispredictionTableSpan);
            label.AddToClassList(TickInspectorUssClasses.MispredictionTableField);
            if (diff)
            {
                label.AddToClassList(isClient ? TickInspectorUssClasses.MispredictionTableFieldDiff : TickInspectorUssClasses.MispredictionTableFieldServerDiff);
                label.tooltip = isClient ? "Diverging predicted value" : "Diverging server authority value";
            }
            return label;
        }

        static void AddPresence(VisualElement container, bool present, bool isClient, bool diff) =>
            container.Add(ValueSpan(present ? "Present" : "Not present", diff, isClient));

        static void AddNoData(VisualElement container)
        {
            var label = new Label("No data");
            label.AddToClassList(TickInspectorUssClasses.MispredictionTableNoData);
            container.Add(label);
        }
    }
}

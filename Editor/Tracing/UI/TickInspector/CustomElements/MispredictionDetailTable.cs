using System;
using System.Reflection;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Unity.NetCode.Tracing;

namespace Unity.NetCode.Editor.Tracing.UI.TickInspector
{
    /// <summary>
    /// The per-component misprediction table (Predicted Tick | Server Authority; header / before / after).
    /// Diverging fields are reddened by re-diffing the two displayed values with the processing differ.
    /// </summary>
    class MispredictionDetailTable : VisualElement
    {
        const string k_TemplatePath = Constants.Templates + "misprediction-detail-table.uxml";
        // Cached once: this element is re-created on every TreeView row bind, so avoid a per-bind asset load.
        static VisualTreeAsset s_Template;

        readonly VisualElement m_BeforeClientFields;
        readonly VisualElement m_BeforeServerFields;
        readonly VisualElement m_AfterClientFields;
        readonly VisualElement m_AfterServerFields;

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
            m_BeforeClientFields = this.Q<VisualElement>("before-client-fields");
            m_BeforeServerFields = this.Q<VisualElement>("before-server-fields");
            m_AfterClientFields = this.Q<VisualElement>("after-client-fields");
            m_AfterServerFields = this.Q<VisualElement>("after-server-fields");
        }

        // Which units highlight: the two displayed values re-diffed with the processing differ
        // With a side missing there is nothing to diff.
        readonly struct FieldDiffHighlight
        {
            readonly ulong m_Mask;
            readonly float[] m_DiffAmountsByUnit;
            readonly float m_FuzzyDiffThreshold;

            public FieldDiffHighlight(Type componentType, IComponentData client, IComponentData server, float fuzzyDiffThreshold)
            {
                m_FuzzyDiffThreshold = fuzzyDiffThreshold;
                m_DiffAmountsByUnit = new float[TypeDiffer.MaxFieldBits];
                m_Mask = TypeDiffer.ComputeUnitDiffs(componentType, client, server, m_DiffAmountsByUnit);
            }

            public bool IsUnitDiff(ref int unitIndex)
            {
                var bit = unitIndex < TypeDiffer.MaxFieldBits ? unitIndex : TypeDiffer.MaxFieldBits - 1;
                unitIndex++;
                if ((m_Mask & (1UL << bit)) == 0)
                    return false;
                return m_FuzzyDiffThreshold <= 0f || m_DiffAmountsByUnit[bit] > m_FuzzyDiffThreshold;
            }
        }

        public void SetDetail(MispredictionDetail detail)
        {
            if (detail == null || m_BeforeClientFields == null)
                return;

            var beforeHighlight = new FieldDiffHighlight(detail.ComponentType, detail.ClientBefore, detail.ServerBefore, detail.FuzzyDiffThreshold);
            var afterHighlight = new FieldDiffHighlight(detail.ComponentType, detail.ClientAfter, detail.ServerAfter, detail.FuzzyDiffThreshold);
            PopulateRow(m_BeforeClientFields, m_BeforeServerFields, detail.ComponentType, detail.ClientBefore, detail.ServerBefore, beforeHighlight);
            PopulateRow(m_AfterClientFields, m_AfterServerFields, detail.ComponentType, detail.ClientAfter, detail.ServerAfter, afterHighlight);
        }

        static void PopulateRow(VisualElement clientContainer, VisualElement serverContainer, Type componentType,
            IComponentData clientValue, IComponentData serverValue, FieldDiffHighlight highlight)
        {
            clientContainer.Clear();
            serverContainer.Clear();

            var fields = componentType != null ? TypeDiffer.GetAllFields(componentType) : Array.Empty<FieldInfo>();
            var clientPresent = clientValue != null;
            var serverPresent = serverValue != null;

            // Zero-field tag component: presence on each side is the value, and differing presence is the diff.
            if (fields.Length == 0)
            {
                var presenceDiffers = clientPresent != serverPresent;
                AddPresence(clientContainer, clientPresent, isClient: true, diff: presenceDiffers);
                AddPresence(serverContainer, serverPresent, isClient: false, diff: presenceDiffers);
                return;
            }
            // No diff is both values aren't present
            if (!clientPresent || !serverPresent)
            {
                AddSide(clientContainer, componentType, clientValue, highlight, isClient: true);
                AddSide(serverContainer, componentType, serverValue, highlight, isClient: false);
                return;
            }

            AddComponentValue(clientContainer, componentType, clientValue, highlight, isClient: true);
            AddComponentValue(serverContainer, componentType, serverValue, highlight, isClient: false);
        }

        static void AddSide(VisualElement container, Type componentType, IComponentData value, FieldDiffHighlight highlight, bool isClient)
        {
            if (value == null)
                AddNoData(container);
            else
                AddComponentValue(container, componentType, value, highlight, isClient);
        }

        // One field per line: "Name (a, b, c)". Vector components are separate tokens so a single diverging
        // scalar (e.g. only z) is highlighted on its own; non-math fields stay a single highlightable token.
        static void AddComponentValue(VisualElement container, Type componentType, IComponentData instance, FieldDiffHighlight highlight, bool isClient)
        {
            var unitIndex = 0;
            foreach (var field in TypeDiffer.GetAllFields(componentType))
            {
                var line = new VisualElement();
                line.AddToClassList(TickInspectorUssClasses.MispredictionTableFieldLine);
                line.Add(StructuralSpan($"{field.Name} ("));

                if (IsSplittable(field.FieldType))
                {
                    var first = true;
                    AppendScalarSpans(line, field.FieldType, field.GetValue(instance), highlight, isClient, ref unitIndex, ref first);
                }
                else
                {
                    // Non-math field is a single unit (matches the differ).
                    line.Add(ValueSpan(Format(field.GetValue(instance), field.FieldType), highlight.IsUnitDiff(ref unitIndex), isClient));
                }

                line.Add(StructuralSpan(")"));
                container.Add(line);
            }
        }

        static void AppendScalarSpans(VisualElement line, Type type, object value, FieldDiffHighlight highlight, bool isClient, ref int unitIndex, ref bool first)
        {
            if (type.IsPrimitive)
            {
                if (!first)
                    line.Add(StructuralSpan(", "));
                first = false;
                line.Add(ValueSpan(Format(value, type), highlight.IsUnitDiff(ref unitIndex), isClient));
                return;
            }

            foreach (var f in TypeDiffer.GetAllFields(type))
                AppendScalarSpans(line, f.FieldType, f.GetValue(value), highlight, isClient, ref unitIndex, ref first);
        }

        // Split Unity.Mathematics vectors into per-component tokens; everything else stays one token.
        static bool IsSplittable(Type type) => type.Namespace == "Unity.Mathematics";

        static string Format(object value, Type type) => Format(value, type, nested: false);

        // Primitives, enums and types with their own ToString print directly; a plain struct is expanded
        // field-by-field (nested structs parenthesized) so the table shows values instead of a type name.
        static string Format(object value, Type type, bool nested)
        {
            if (value == null)
                return string.Empty;
            if (type.IsPrimitive || type.IsEnum || HasOwnToString(type))
                return value.ToString();

            var fields = TypeDiffer.GetAllFields(type);
            if (fields.Length == 0)
                return value.ToString();

            var parts = new string[fields.Length];
            for (var i = 0; i < fields.Length; i++)
                parts[i] = Format(fields[i].GetValue(value), fields[i].FieldType, nested: true);
            var joined = string.Join(", ", parts);
            return nested ? $"({joined})" : joined;
        }

        static bool HasOwnToString(Type type)
        {
            var method = type.GetMethod("ToString", Type.EmptyTypes);
            return method != null && method.DeclaringType != typeof(object) && method.DeclaringType != typeof(ValueType);
        }

        static Label StructuralSpan(string text)
        {
            var label = new Label(text);
            label.AddToClassList(TickInspectorUssClasses.MispredictionTableSpan);
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

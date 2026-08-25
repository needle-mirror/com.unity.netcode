using System;
using System.Collections.Generic;

namespace Unity.NetCode.Editor.Tracing
{
    /// <summary>
    /// The per-column value filters of <see cref="SelectTracingTargetWindow"/>'s target list: which columns
    /// have their filter enabled and which of their values are hidden. Mutated by the column header buttons
    /// and <see cref="TracingColumnFilterPopupWindow"/>; observers react through <see cref="Changed"/>.
    /// </summary>
    internal class TracingColumnFilters
    {
        internal const string k_ColumnTracingSelectAll = "Mass Selection";
        internal const string k_ColumnTracingName = "tracing-column-name";
        internal const string k_ColumnTracingWorld = "tracing-column-world";
        internal const string k_ColumnTracingPredicted = "tracing-column-predicted";
        internal const string k_ColumnTracingUnmanaged = "tracing-column-unmanaged";
        internal const string k_ColumnTracingNamespace = "tracing-column-namespace";
        internal const string k_ColumnTracingType = "tracing-column-type";
        internal const string k_ColumnTracingFilteringOptions = "tracing-column-filtering-options";

        internal const string k_ColumnFilterNoValue = "—";
        internal const string k_PredictedFilterValuePredicted = "Predicted";
        internal const string k_PredictedFilterValueNotPredicted = "Not predicted";
        internal const string k_UnmanagedFilterValueUnmanaged = "Unmanaged";
        internal const string k_UnmanagedFilterValueManaged = "Managed";
        // Hides every namespace starting with "Unity."
        internal const string k_NamespaceFilterPatternUnityPrefix = "Unity.*";
        // Hides every namespace containing "Generated"
        internal const string k_NamespaceFilterPatternGenerated = "*Generated*";

        // Which values are hidden per column, empty means all column values are shown.
        // Tracing can only capture prediction-loop systems and unmanaged component values,
        // so the Predicted and Unmanaged filters start enabled to hide the rest.
        readonly Dictionary<string, HashSet<string>> m_HiddenValuesByColumn = new(StringComparer.Ordinal)
        {
            { k_ColumnTracingPredicted, new HashSet<string>(StringComparer.Ordinal) { k_PredictedFilterValueNotPredicted } },
            { k_ColumnTracingUnmanaged, new HashSet<string>(StringComparer.Ordinal) { k_UnmanagedFilterValueManaged } }
        };

        // Whether the column filter is enabled for the particular column
        readonly HashSet<string> m_EnabledColumns = new(StringComparer.Ordinal) { k_ColumnTracingPredicted, k_ColumnTracingUnmanaged };

        /// <summary>Raised with the column name after that column's filter state changed.</summary>
        public event Action<string> Changed;

        internal static readonly string[] k_FilterableColumns =
        {
            k_ColumnTracingWorld,
            k_ColumnTracingPredicted,
            k_ColumnTracingUnmanaged,
            k_ColumnTracingNamespace,
        };

        internal static bool IsColumnValueFilterable(string columnName) => Array.IndexOf(k_FilterableColumns, columnName) >= 0;

        // Rows with no value for these columns (components for World/Predicted, systems for Unmanaged) are ignored by their filters
        internal static bool ColumnFilterIgnoresPlaceholderRows(string columnName) =>
            columnName is k_ColumnTracingWorld or k_ColumnTracingPredicted or k_ColumnTracingUnmanaged;

        // Special column filters to be placed with All and None at the bottom
        internal static bool IsSpecialColumnFilterValue(string value) =>
            value is k_ColumnFilterNoValue
                or k_NamespaceFilterPatternUnityPrefix
                or k_NamespaceFilterPatternGenerated;

        public bool IsColumnFilterEnabled(string columnName) => m_EnabledColumns.Contains(columnName);

        public bool IsColumnFilterValueHidden(string columnName, string value) =>
            m_HiddenValuesByColumn.TryGetValue(columnName, out var hiddenValues) && hiddenValues.Contains(value);

        public bool HasHiddenValues(string columnName) =>
            m_HiddenValuesByColumn.TryGetValue(columnName, out var hiddenValues) && hiddenValues.Count > 0;

        /// <summary>
        /// True when the column hides <paramref name="value"/>, including through the namespace pattern
        /// filters for the Namespace column.
        /// </summary>
        public bool HidesValue(string columnName, string value, string typeNamespace)
        {
            if (!m_HiddenValuesByColumn.TryGetValue(columnName, out var hiddenValues))
            {
                return false;
            }

            if (hiddenValues.Contains(value))
            {
                return true;
            }

            return columnName == k_ColumnTracingNamespace && IsNamespaceHiddenByPatternFilters(hiddenValues, typeNamespace);
        }

        static bool IsNamespaceHiddenByPatternFilters(HashSet<string> hiddenValues, string typeNamespace)
        {
            if (string.IsNullOrEmpty(typeNamespace))
            {
                return false;
            }

            if (hiddenValues.Contains(k_NamespaceFilterPatternUnityPrefix)
                && typeNamespace.StartsWith("Unity.", StringComparison.Ordinal))
            {
                return true;
            }

            return hiddenValues.Contains(k_NamespaceFilterPatternGenerated)
                && typeNamespace.IndexOf("Generated", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void SetColumnFilterEnabled(string columnName, bool enabled)
        {
            if (!IsColumnValueFilterable(columnName))
            {
                return;
            }

            var changed = enabled ? m_EnabledColumns.Add(columnName) : m_EnabledColumns.Remove(columnName);
            if (changed)
            {
                Changed?.Invoke(columnName);
            }
        }

        public void SetColumnFilterValueHidden(string columnName, string value, bool hidden)
        {
            if (ApplyValueHidden(columnName, value, hidden))
            {
                OnHiddenValuesChanged(columnName);
            }
        }

        public void SetColumnFilterValuesHidden(string columnName, IReadOnlyList<string> values, bool hidden)
        {
            if (values == null)
            {
                return;
            }

            var changed = false;
            foreach (var value in values)
            {
                changed |= ApplyValueHidden(columnName, value, hidden);
            }

            if (changed)
            {
                OnHiddenValuesChanged(columnName);
            }
        }

        // Pure state change (no notification); true when the value's hidden state actually changed.
        bool ApplyValueHidden(string columnName, string value, bool hidden)
        {
            if (value == null || !IsColumnValueFilterable(columnName))
            {
                return false;
            }

            if (!m_HiddenValuesByColumn.TryGetValue(columnName, out var hiddenValues))
            {
                if (!hidden)
                {
                    return false;
                }

                hiddenValues = new HashSet<string>(StringComparer.Ordinal);
                m_HiddenValuesByColumn[columnName] = hiddenValues;
            }

            return hidden ? hiddenValues.Add(value) : hiddenValues.Remove(value);
        }

        // Hiding a value implies the filter is wanted; an emptied filter is switched back off.
        void OnHiddenValuesChanged(string columnName)
        {
            var hasHiddenValues = m_HiddenValuesByColumn.TryGetValue(columnName, out var hiddenValues) && hiddenValues.Count > 0;
            if (hasHiddenValues)
            {
                m_EnabledColumns.Add(columnName);
            }
            else
            {
                m_EnabledColumns.Remove(columnName);
            }

            Changed?.Invoke(columnName);
        }
    }
}

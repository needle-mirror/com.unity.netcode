using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode.Tracing;

namespace Unity.NetCode.Editor.Tracing.UI
{
    // The active view filters of the tracing tool, evaluated against stored DiffAggregates. Shared by every view.
    internal class TracingViewFilters
    {
        public HashSet<string> HiddenSystems = new();
        public HashSet<string> HiddenComponents = new();

        // The backend always traces GhostInstance, but it's only shown if selected by the user as a tracing target
        public bool HideGhostInstance;

        public DiffInfo.DiffReasons EnabledDiffReasons = DiffInfo.AllDiffReasons;
        public bool ChangedValuesOnly;

        public float FuzzyDiffThreshold;

        public DiffInfo.DiffReasons ApplyFuzzyThreshold(DiffInfo.DiffReasons reasons, float dataDiffAmount)
        {
            if (FuzzyDiffThreshold > 0f
                && (reasons & DiffInfo.DiffReasons.ComponentData) != 0
                && !(dataDiffAmount > FuzzyDiffThreshold))
                reasons &= ~DiffInfo.DiffReasons.ComponentData;
            return reasons;
        }

        // True when the diff info contains at least one aggregate that survives every active filter.
        public bool HasVisibleDiff(in DiffInfo diffInfo)
        {
            if (!diffInfo.AggregatesRO.IsCreated)
                return false;

            foreach (var kvp in diffInfo.AggregatesRO)
            {
                if (PassesFilters(kvp.Key, kvp.Value))
                    return true;
            }

            return false;
        }

        public bool PassesFilters(in DiffAggregate aggregate, float dataDiffAmount)
            => (ApplyFuzzyThreshold(aggregate.Reasons, dataDiffAmount) & EnabledDiffReasons) != 0 && PassesTargetFilters(aggregate);

        public bool IsComponentHidden(TypeIndex component)
            => (HideGhostInstance && component == TypeManager.GetTypeIndex<GhostInstance>())
               || HiddenComponents.Contains(TracingTargetNames.Component(component));

        public bool IsSystemHidden(string systemFullName) => HiddenSystems.Contains(systemFullName);

        // True when the aggregate survives the target filters (hidden systems/components, changed values only).
        bool PassesTargetFilters(in DiffAggregate aggregate)
        {
            if (aggregate.System == default && aggregate.GhostId == DiffAggregate.InvalidGhostId && aggregate.Component == default)
                return true;

            if (ChangedValuesOnly && !aggregate.HasValueChanged)
                return false;
            if (aggregate.Component != default && IsComponentHidden(aggregate.Component))
                return false;
            if (aggregate.System != default && IsSystemHidden(TracingTargetNames.System(aggregate.System)))
                return false;
            return true;
        }
    }
}

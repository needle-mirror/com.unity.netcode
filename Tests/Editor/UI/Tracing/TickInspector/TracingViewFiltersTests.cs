using NUnit.Framework;
using Unity.Entities;
using Unity.Netcode;
using Unity.Netcode.Tracing;

namespace Unity.Netcode.Editor.Tracing.UI.Tests
{
    /// <summary>
    /// Unit tests for <see cref="TracingViewFilters"/> evaluating stored <see cref="DiffAggregate"/>s.
    /// </summary>
    class TracingViewFiltersTests
    {
        DiffInfo m_DiffInfo;

        [SetUp]
        public void SetUp()
        {
            TypeManager.Initialize();
            m_DiffInfo = default;
        }

        [TearDown]
        public void TearDown()
        {
            m_DiffInfo.Dispose();
        }

        [Test(Description = "TracingTargetNames must resolve type indices to Type.FullName — the names the " +
                            "filter rows key on — or hiding a target silently no-ops.")]
        public void TargetNames_UseTheProcessedDataDerivation()
        {
            var system = TypeManager.GetSystemTypeIndex<GhostSendSystem>();
            Assert.AreEqual(typeof(GhostSendSystem).FullName, TracingTargetNames.System(system));

            var component = TypeManager.GetTypeIndex<GhostInstance>();
            Assert.AreEqual(typeof(GhostInstance).FullName, TracingTargetNames.Component(component));
        }

        static DiffAggregate MakeAggregate(bool valueChanged = false)
        {
            return new DiffAggregate
            {
                System = TypeManager.GetSystemTypeIndex<SimulationSystemGroup>(),
                GhostId = 1,
                Component = TypeManager.GetTypeIndex<GhostInstance>(),
                Reasons = DiffInfo.DiffReasons.ComponentData,
                HasValueChanged = valueChanged,
            };
        }

        static TracingViewFilters MakeFilters() => new();

        [Test]
        public void HiddenTargets_HideOnlyTheirOwnAggregates()
        {
            m_DiffInfo.AddDiff(MakeAggregate());
            var filters = MakeFilters();

            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.True, "precondition: visible with nothing hidden");

            filters.HiddenSystems.Add(typeof(PresentationSystemGroup).FullName);
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.True, "hiding an unrelated system changes nothing");

            filters.HiddenSystems.Add(typeof(SimulationSystemGroup).FullName);
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.False, "hiding the causing system hides the diff");

            filters.HiddenSystems.Clear();
            filters.HiddenComponents.Add(typeof(GhostInstance).FullName);
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.False, "hiding the causing component hides the diff");

            // DeltaTime aggregates carry no system/component, so target hiding can't touch them.
            m_DiffInfo.AddDiff(DiffInfo.DiffReasons.DeltaTime);
            filters.HiddenSystems.Add(typeof(SimulationSystemGroup).FullName);
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.True, "untargeted aggregates are unaffected by target hiding");

            // The changed-values filter can't erase them either.
            filters.ChangedValuesOnly = true;
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.True, "untargeted aggregates are unaffected by the changed-values filter");

            // Deselecting their diff tag is the one mechanism that dims them.
            filters.EnabledDiffReasons = DiffInfo.AllDiffReasons & ~DiffInfo.DiffReasons.DeltaTime;
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.False, "deselecting the tag still hides the tick-scoped diff");
        }

        [Test]
        public void HiddenTargets_CombineWithReasonAndValueChangedFilters()
        {
            // A value-changed ComponentData on SimulationSystemGroup, an unchanged MissingSystem on PresentationSystemGroup.
            m_DiffInfo.AddDiff(MakeAggregate(valueChanged: true));
            m_DiffInfo.AddDiff(new DiffAggregate
            {
                System = TypeManager.GetSystemTypeIndex<PresentationSystemGroup>(),
                GhostId = DiffAggregate.InvalidGhostId,
                Reasons = DiffInfo.DiffReasons.MissingSystem,
            });

            // Nothing visible: the changed entry is target-hidden, the other has no value change.
            var filters = new TracingViewFilters { ChangedValuesOnly = true };
            filters.HiddenSystems.Add(typeof(SimulationSystemGroup).FullName);
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.False);

            // Unhide the system: the value-changed entry passes again.
            filters.HiddenSystems.Clear();
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.True);

            // One entry target-hidden, the other reason-filtered: nothing visible.
            filters.ChangedValuesOnly = false;
            filters.HiddenSystems.Add(typeof(SimulationSystemGroup).FullName);
            filters.EnabledDiffReasons = DiffInfo.AllDiffReasons & ~DiffInfo.DiffReasons.MissingSystem;
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.False);
        }

        [Test(Description = "The fuzzy threshold filters ComponentData by stored amount, no reprocessing: 0 " +
                            "disables it, amounts must strictly exceed it, structural reasons are immune.")]
        public void FuzzyThreshold_FiltersComponentDataByStoredAmount()
        {
            m_DiffInfo.AddDiff(MakeAggregate(), 0.5f);
            var filters = MakeFilters();

            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.True, "threshold 0 disables the fuzzy filter");
            Assert.That(filters.ApplyFuzzyThreshold(DiffInfo.DiffReasons.ComponentData, 0.5f),
                Is.EqualTo(DiffInfo.DiffReasons.ComponentData));

            filters.FuzzyDiffThreshold = 0.4f;
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.True, "amounts above the threshold stay visible");
            Assert.That(filters.ApplyFuzzyThreshold(DiffInfo.DiffReasons.ComponentData, 0.5f),
                Is.EqualTo(DiffInfo.DiffReasons.ComponentData));

            filters.FuzzyDiffThreshold = 0.5f;
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.False, "amounts at or below the threshold are filtered");
            Assert.That(filters.ApplyFuzzyThreshold(DiffInfo.DiffReasons.ComponentData, 0.5f),
                Is.EqualTo(DiffInfo.DiffReasons.Undefined),
                "the component row's Diff tag disappears with it");
            Assert.That(filters.ApplyFuzzyThreshold(DiffInfo.DiffReasons.MissingComponent, 0f),
                Is.EqualTo(DiffInfo.DiffReasons.MissingComponent),
                "structural reasons are immune to the threshold");

            // Structural diffs carry no amount and can never be fuzzy-filtered.
            m_DiffInfo.AddDiff(new DiffAggregate
            {
                System = TypeManager.GetSystemTypeIndex<SimulationSystemGroup>(),
                GhostId = 2,
                Reasons = DiffInfo.DiffReasons.MissingGhost,
            });
            filters.FuzzyDiffThreshold = float.MaxValue;
            Assert.That(filters.HasVisibleDiff(m_DiffInfo), Is.True, "structural reasons ignore the threshold");
        }
    }
}

using NUnit.Framework;
using Unity.Entities;
using Unity.NetCode.Tracing;

namespace Tests.Editor
{
    /// <summary>
    /// Unit tests for the deduplicated <see cref="DiffAggregate"/> storage on <see cref="DiffInfo"/>.
    /// </summary>
    class TracingDiffAggregateTests
    {
        DiffInfo m_DiffInfo;
        DiffInfo m_OtherDiffInfo;

        [SetUp]
        public void SetUp()
        {
            TypeManager.Initialize();
            m_DiffInfo = default;
            m_OtherDiffInfo = default;
        }

        [TearDown]
        public void TearDown()
        {
            m_DiffInfo.Dispose();
            m_OtherDiffInfo.Dispose();
        }

        static DiffAggregate MakeAggregate(DiffInfo.DiffReasons reasons, bool valueChanged = false, int ghostId = 1)
        {
            return new DiffAggregate
            {
                System = TypeManager.GetSystemTypeIndex<SimulationSystemGroup>(),
                GhostId = ghostId,
                Component = default,
                Reasons = reasons,
                HasValueChanged = valueChanged,
            };
        }

        [Test]
        public void AddDiff_DeduplicatesIdenticalAggregates_AndKeepsDistinctOnes()
        {
            m_DiffInfo.AddDiff(MakeAggregate(DiffInfo.DiffReasons.ComponentData, valueChanged: true));
            m_DiffInfo.AddDiff(MakeAggregate(DiffInfo.DiffReasons.ComponentData, valueChanged: true)); // duplicate
            m_DiffInfo.AddDiff(MakeAggregate(DiffInfo.DiffReasons.MissingComponent, ghostId: 2)); // distinct

            Assert.That(m_DiffInfo.m_Aggregates.Count, Is.EqualTo(2));
            Assert.That(m_DiffInfo.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.ComponentData | DiffInfo.DiffReasons.MissingComponent));
        }

        [Test]
        public void HasDiff_IsDerivedFromAggregates()
        {
            Assert.That(m_DiffInfo.HasDiff, Is.False);
            m_DiffInfo.AddDiff(DiffInfo.DiffReasons.DeltaTime);
            Assert.That(m_DiffInfo.HasDiff, Is.True);
        }

        [Test]
        public void UnionWith_RollsUpAndDeduplicates()
        {
            // Two children reporting the same aggregate roll up into one entry at the parent level.
            m_DiffInfo.AddDiff(MakeAggregate(DiffInfo.DiffReasons.ComponentData));
            m_OtherDiffInfo.AddDiff(MakeAggregate(DiffInfo.DiffReasons.ComponentData));
            m_OtherDiffInfo.AddDiff(MakeAggregate(DiffInfo.DiffReasons.ExtraGhost, ghostId: 7));

            m_DiffInfo.UnionWith(m_OtherDiffInfo);

            Assert.That(m_DiffInfo.m_Aggregates.Count, Is.EqualTo(2));
            Assert.That(m_DiffInfo.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.ComponentData | DiffInfo.DiffReasons.ExtraGhost));
        }

        [Test(Description = "Duplicates keep the largest amount (AddDiff collapse and UnionWith roll-up), " +
                            "so any fuzzy threshold stays answerable from the merged entry.")]
        public void DiffAmounts_MaxMergeOnCollapseAndUnion()
        {
            m_DiffInfo.AddDiff(MakeAggregate(DiffInfo.DiffReasons.ComponentData), 0.25f);
            m_DiffInfo.AddDiff(MakeAggregate(DiffInfo.DiffReasons.ComponentData), 2f);
            m_DiffInfo.AddDiff(MakeAggregate(DiffInfo.DiffReasons.ComponentData), 1f);
            Assert.That(m_DiffInfo.m_Aggregates.Count, Is.EqualTo(1));
            Assert.That(m_DiffInfo.m_Aggregates[MakeAggregate(DiffInfo.DiffReasons.ComponentData)], Is.EqualTo(2f));

            m_OtherDiffInfo.AddDiff(MakeAggregate(DiffInfo.DiffReasons.ComponentData), 3f);
            m_DiffInfo.UnionWith(m_OtherDiffInfo);
            Assert.That(m_DiffInfo.m_Aggregates.Count, Is.EqualTo(1));
            Assert.That(m_DiffInfo.m_Aggregates[MakeAggregate(DiffInfo.DiffReasons.ComponentData)], Is.EqualTo(3f));
        }
    }
}

using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.Editor.Tracing.UI.TickInspector;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;

namespace Unity.NetCode.Editor.Tracing.UI.Tests
{
    /// <summary>
    /// Tests for <see cref="MispredictionDetailTable"/> — the per-field red highlight is recomputed
    /// from the displayed values, and the missing/tag cases render "No Data" / "Present" placeholders.
    /// </summary>
    class MispredictionDetailTableTests : UITestFixture
    {
        struct Vec : IComponentData
        {
            public int X;
            public int Y;
        }

        struct Tag : IComponentData
        {
        }

        struct VecComp : IComponentData
        {
            public float3 V;
        }

        struct Inner
        {
            public int A;
            public int B;
        }

        struct Nested : IComponentData
        {
            public Inner Data;
        }

        MispredictionDetailTable Make(MispredictionDetail detail)
        {
            var table = new MispredictionDetailTable();
            rootVisualElement.Add(table);
            simulate.FrameUpdate();
            table.SetDetail(detail);
            simulate.FrameUpdate();
            return table;
        }

        static System.Collections.Generic.List<Label> Fields(VisualElement container) =>
            container.Query<Label>(className: TickInspectorUssClasses.MispredictionTableField).ToList();

        [Test]
        public void DivergingField_HighlightsOnlyThatField()
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Vec),
                ClientBefore = new Vec { X = 1, Y = 2 }, ServerBefore = new Vec { X = 1, Y = 2 },
                ClientAfter = new Vec { X = 9, Y = 2 }, ServerAfter = new Vec { X = 1, Y = 2 },
            };

            var table = Make(detail);

            var afterClient = Fields(table.Q<VisualElement>("after-client-fields"));
            Assert.That(afterClient, Has.Count.EqualTo(2));
            Assert.That(afterClient[0].ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff), Is.True, "X changed, must be flagged");
            Assert.That(afterClient[1].ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff), Is.False, "Y unchanged, must not be flagged");

            // The before row matched on both sides, so nothing is highlighted.
            foreach (var label in Fields(table.Q<VisualElement>("before-client-fields")))
                Assert.That(label.ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff), Is.False);
        }

        [Test]
        public void VectorField_HighlightsOnlyTheChangedScalar()
        {
            // float3 renders as three scalar tokens; only the one whose unit bit is set is highlighted.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(VecComp),
                ClientBefore = new VecComp { V = new float3(1f, 2f, 3f) }, ServerBefore = new VecComp { V = new float3(1f, 2f, 3f) },
                ClientAfter = new VecComp { V = new float3(1f, 2f, 9f) }, ServerAfter = new VecComp { V = new float3(1f, 2f, 3f) },
            };

            var table = Make(detail);

            var afterClient = Fields(table.Q<VisualElement>("after-client-fields"));
            Assert.That(afterClient, Has.Count.EqualTo(3), "float3 renders as three scalar tokens");
            Assert.That(afterClient[0].ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff), Is.False, "x unchanged");
            Assert.That(afterClient[1].ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff), Is.False, "y unchanged");
            Assert.That(afterClient[2].ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff), Is.True, "only z changed");
        }

        [Test]
        public void MissingServer_ShowsNoData_WithoutHighlights()
        {
            // The absent side reads "No data"; with one state there is nothing to diff, so no highlights.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Vec),
                ClientBefore = new Vec { X = 1, Y = 2 }, ServerBefore = null,
                ClientAfter = new Vec { X = 9, Y = 2 }, ServerAfter = null,
            };

            var table = Make(detail);

            Assert.That(table.Q<VisualElement>("after-server-fields").Q<Label>(className: TickInspectorUssClasses.MispredictionTableNoData), Is.Not.Null);

            foreach (var label in Fields(table.Q<VisualElement>("before-client-fields")))
                Assert.That(label.ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff), Is.False,
                    "a one-sided row must not be reddened");
            foreach (var label in Fields(table.Q<VisualElement>("after-client-fields")))
                Assert.That(label.ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff), Is.False,
                    "a one-sided row must not be reddened");
        }

        [Test]
        public void TagComponent_RendersPresentAndNotPresent()
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Tag),
                ClientBefore = new Tag(), ServerBefore = new Tag(),
                ClientAfter = new Tag(), ServerAfter = null, // present on the client only after the system ran
            };

            var table = Make(detail);

            var clientLabel = table.Q<VisualElement>("after-client-fields").Q<Label>(className: TickInspectorUssClasses.MispredictionTableField);
            var serverLabel = table.Q<VisualElement>("after-server-fields").Q<Label>(className: TickInspectorUssClasses.MispredictionTableField);
            Assert.That(clientLabel.text, Is.EqualTo("Present"));
            Assert.That(serverLabel.text, Is.EqualTo("Not present"));
            Assert.That(clientLabel.ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff), Is.True);
            Assert.That(serverLabel.ClassListContains(TickInspectorUssClasses.MispredictionTableFieldServerDiff), Is.True);
        }

        [Test]
        public void NestedStruct_ShowsFieldValues_NotTypeName()
        {
            // A plain struct without a ToString override must render its field values, not "Inner".
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Nested),
                ClientBefore = new Nested { Data = new Inner { A = 3, B = 4 } }, ServerBefore = new Nested { Data = new Inner { A = 3, B = 4 } },
                ClientAfter = new Nested { Data = new Inner { A = 3, B = 4 } }, ServerAfter = new Nested { Data = new Inner { A = 3, B = 4 } },
            };

            var table = Make(detail);

            var afterClient = Fields(table.Q<VisualElement>("after-client-fields"));
            Assert.That(afterClient, Has.Count.EqualTo(1), "the non-math struct is a single unit");
            Assert.That(afterClient[0].text, Is.EqualTo("3, 4"));
        }

        [TearDown]
        public void TearDown()
        {
            rootVisualElement.Clear();
        }
    }
}

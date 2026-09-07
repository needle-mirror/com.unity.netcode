using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode.Editor.Tracing.UI.TickInspector;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;

namespace Unity.Netcode.Editor.Tracing.UI.Tests
{
    /// <summary>
    /// Tests for <see cref="MispredictionDetailTable"/> — values are compared leaf by leaf against the
    /// component's <see cref="ComponentFieldTree"/>, and the missing/tag cases render "No Data" / "Present".
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

        struct MatrixComp : IComponentData
        {
            public float4x4 M;
        }

        struct EntityComp : IComponentData
        {
            public Entity Target;
        }

        struct BigBlock
        {
            public float4x4 A;
            public float4x4 B;
            public float4x4 C;
            public float4x4 D;
            public float4x4 E;
            public float4x4 F;
            public float4x4 G;
            public float4x4 H;
        }

        struct BigComp : IComponentData
        {
            public BigBlock Block;
            public int Tail;
        }

        // One field per level, so the innermost value sits past ComponentFieldTree.MaxDepth.
        struct D9 { public int Value; }
        struct D8 { public D9 Next; }
        struct D7 { public D8 Next; }
        struct D6 { public D7 Next; }
        struct D5 { public D6 Next; }
        struct D4 { public D5 Next; }
        struct D3 { public D4 Next; }
        struct D2 { public D3 Next; }
        struct D1 { public D2 Next; }

        struct TooDeepComp : IComponentData
        {
            public D1 Next;
        }

        enum Zone : byte
        {
            Idle,
            Running,
        }

        struct MixedInner
        {
            public bool Flag;
            public float Value;
            public FixedString32Bytes Tag;
        }

        struct MixedComp : IComponentData
        {
            public MixedInner Inner;
            public float3 Pos;
            public float4x4 Matrix;
            public Entity Owner;
            public Zone State;
            public bool Enabled;
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

        static Label Summary(VisualElement container) =>
            container.Q<Label>(className: TickInspectorUssClasses.MispredictionTableSummary);

        static Toggle Arrow(VisualElement container) =>
            container.Q<Toggle>(className: TickInspectorUssClasses.MispredictionTableSummaryArrow);

        static bool IsDiffHighlighted(Label label) =>
            label.ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff);

        [Test]
        public void ChangedField_IsHighlightedAlone()
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Vec),
                ClientBefore = new Vec { X = 1, Y = 2 }, ServerBefore = new Vec { X = 1, Y = 2 },
                ClientAfter = new Vec { X = 9, Y = 2 }, ServerAfter = new Vec { X = 1, Y = 2 },
            };

            var table = Make(detail);

            // X is a single-scalar field: when its only leaf changes, the diff class moves to the row (not the token).
            var afterClient = table.Q<VisualElement>("after-client-fields");
            Assert.That(Lines(afterClient), Has.Count.EqualTo(2));
            Assert.That(LineHasDiffClass(afterClient, 0, isClient: true), Is.True, "X changed, row must be flagged");
            Assert.That(LineHasDiffClass(afterClient, 1, isClient: true), Is.False, "Y unchanged, must not be flagged");

            // The before row matched on both sides, so nothing is highlighted.
            foreach (var label in Fields(table.Q<VisualElement>("before-client-fields")))
                Assert.That(IsDiffHighlighted(label), Is.False);
        }

        [Test]
        public void VectorField_HighlightsOnlyTheChangedScalar()
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(VecComp),
                ClientBefore = new VecComp { V = new float3(1f, 2f, 3f) }, ServerBefore = new VecComp { V = new float3(1f, 2f, 3f) },
                ClientAfter = new VecComp { V = new float3(1f, 2f, 9f) }, ServerAfter = new VecComp { V = new float3(1f, 2f, 3f) },
            };

            var table = Make(detail);

            var afterClient = Fields(table.Q<VisualElement>("after-client-fields"));
            Assert.That(afterClient, Has.Count.EqualTo(3), "float3 renders as three scalar tokens");
            Assert.That(IsDiffHighlighted(afterClient[0]), Is.False, "x unchanged");
            Assert.That(IsDiffHighlighted(afterClient[1]), Is.False, "y unchanged");
            Assert.That(IsDiffHighlighted(afterClient[2]), Is.True, "only z changed");
        }

        // The row above this table stops counting a component as diffing once its recorded amount falls
        // within the view's fuzzy threshold, so the leaves under it have to go quiet at the same point —
        // otherwise an unhighlighted row expands into a red field.
        [TestCase(0f, true)]
        [TestCase(0.25f, true)]
        [TestCase(1f, false)]
        public void FuzzyThreshold_SilencesLeavesWithinIt(float threshold, bool expectHighlight)
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(VecComp),
                ClientBefore = new VecComp { V = new float3(1f, 2f, 3f) }, ServerBefore = new VecComp { V = new float3(1f, 2f, 3f) },
                ClientAfter = new VecComp { V = new float3(1f, 2f, 3.5f) }, ServerAfter = new VecComp { V = new float3(1f, 2f, 3f) },
                FuzzyDiffThreshold = threshold,
            };

            var table = Make(detail);

            var afterClient = Fields(table.Q<VisualElement>("after-client-fields"));
            Assert.That(IsDiffHighlighted(afterClient[2]), Is.EqualTo(expectHighlight),
                "z moved by 0.5, so it counts as a diff only while the threshold is below that");
            Assert.That(IsDiffHighlighted(afterClient[0]), Is.False, "x never moved");
        }

        [Test]
        public void NestedStruct_HighlightsOnlyTheChangedSubField()
        {
            // Previously the whole nested struct was one token, so both values reddened together.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Nested),
                ClientBefore = new Nested { Data = new Inner { A = 3, B = 4 } }, ServerBefore = new Nested { Data = new Inner { A = 3, B = 4 } },
                ClientAfter = new Nested { Data = new Inner { A = 3, B = 9 } }, ServerAfter = new Nested { Data = new Inner { A = 3, B = 4 } },
            };

            var table = Make(detail);

            var afterClient = table.Q<VisualElement>("after-client-fields");
            Assert.That(Summary(afterClient).text, Is.EqualTo("2 values, 1 changed"));
            Assert.That(LineText(afterClient, 1), Is.EqualTo("A (3)"));
            Assert.That(LineText(afterClient, 2), Is.EqualTo("B (9)"));
            Assert.That(LineHasDiffClass(afterClient, 1, isClient: true), Is.False, "A unchanged");
            Assert.That(LineHasDiffClass(afterClient, 2, isClient: true), Is.True, "only B changed");
        }

        [Test]
        public void MissingServer_ShowsNoData_AndLeavesThePresentSideUnhighlighted()
        {
            // The absent side reads "No data"; the present side is not flooded red. With one side gone there
            // is nothing to compare, so no field can be blamed. Flooding it was rejected on trunk.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Vec),
                ClientBefore = new Vec { X = 1, Y = 2 }, ServerBefore = null,
                ClientAfter = new Vec { X = 9, Y = 8 }, ServerAfter = null,
            };

            var table = Make(detail);

            Assert.That(table.Q<VisualElement>("after-server-fields").Q<Label>(className: TickInspectorUssClasses.MispredictionTableNoData), Is.Not.Null);
            foreach (var label in Fields(table.Q<VisualElement>("after-client-fields")))
                Assert.That(IsDiffHighlighted(label), Is.False, "a one-sided row must not be reddened");
        }

        [Test]
        public void ComponentAddedDuringTheSystem_MarksItsFieldsAsChanged()
        {
            // The client did not have the component before the system ran. CollectValueChanges counts that
            // presence transition as a value change, and there is no before value to subtract from, so every
            // field the component now has is part of the change.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Vec),
                ClientBefore = null, ServerBefore = new Vec { X = 1, Y = 2 },
                ClientAfter = new Vec { X = 1, Y = 2 }, ServerAfter = new Vec { X = 1, Y = 2 },
            };

            var table = Make(detail);

            var afterClient = Fields(table.Q<VisualElement>("after-client-fields"));
            Assert.That(afterClient, Has.Count.EqualTo(2));
            foreach (var label in afterClient)
                Assert.That(label.ClassListContains(TickInspectorUssClasses.MispredictionTableFieldValueChanges), Is.True,
                    "the component appeared this system, so its fields are the change");

            foreach (var label in Fields(table.Q<VisualElement>("after-server-fields")))
                Assert.That(label.ClassListContains(TickInspectorUssClasses.MispredictionTableFieldValueChanges), Is.False,
                    "the server had it all along");
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
            Assert.That(IsDiffHighlighted(clientLabel), Is.True);
            Assert.That(serverLabel.ClassListContains(TickInspectorUssClasses.MispredictionTableFieldServerDiff), Is.True);
        }

        [Test]
        public void ValueNestedPastTheDepthLimit_ReadsAsTruncated_NotAsItsToString()
        {
            var client = new TooDeepComp();
            var server = new TooDeepComp();
            server.Next.Next.Next.Next.Next.Next.Next.Next.Next.Value = 42;

            var table = Make(new MispredictionDetail
            {
                ComponentType = typeof(TooDeepComp),
                ClientBefore = client, ServerBefore = client,
                ClientAfter = client, ServerAfter = server,
            });

            var afterClient = table.Q<VisualElement>("after-client-fields");
            var truncated = afterClient.Query<Label>().ToList().Find(l => l.text == "{...}");

            Assert.That(truncated, Is.Not.Null, "the deepest value the tree stopped at stands in as {...}");
            Assert.That(truncated.tooltip, Does.Contain($"more than {ComponentFieldTree.MaxDepth} levels deep"),
                "and the tooltip says why it is not expanded");
            // A single-leaf row carries the highlight on its wrapper so the underline stops at the value.
            Assert.That(truncated.parent.ClassListContains(TickInspectorUssClasses.MispredictionTableFieldDiff),
                Is.True, "it still diverges, so it is still highlighted");
        }

        [Test]
        public void EntityField_IsMispredicted_LikeAnyOtherDivergingValue()
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(EntityComp),
                ClientBefore = new EntityComp { Target = new Entity { Index = 3, Version = 1 } },
                ServerBefore = new EntityComp { Target = new Entity { Index = 3, Version = 1 } },
                ClientAfter = new EntityComp { Target = new Entity { Index = 3, Version = 1 } },
                ServerAfter = new EntityComp { Target = new Entity { Index = 77, Version = 2 } },
            };

            var table = Make(detail);

            var afterClient = table.Q<VisualElement>("after-client-fields");
            var tokens = Fields(afterClient);
            Assert.That(tokens, Has.Count.EqualTo(1), "an entity reference reads as one token");
            // The reference is the field's only leaf, so the diff class sits on the row rather than the token.
            Assert.That(LineHasDiffClass(afterClient, 0, isClient: true), Is.True,
                "a client relying on an entity reference the server does not share is a real misprediction");
            Assert.That(tokens[0].ClassListContains(TickInspectorUssClasses.MispredictionTableFieldValueChanges), Is.False,
                "the client's reference is the same before and after");
        }

        [Test]
        public void EntityField_ThatChangesDuringTheSystem_IsStillMarkedAsAValueChange()
        {
            // The client's reference moves between before and after. That is one world comparing against
            // itself, which the trace records as a value change, so the row carries the value-change mark
            // on top of whatever the client/server comparison says.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(EntityComp),
                ClientBefore = new EntityComp { Target = new Entity { Index = 3, Version = 1 } },
                ServerBefore = new EntityComp { Target = new Entity { Index = 77, Version = 2 } },
                ClientAfter = new EntityComp { Target = new Entity { Index = 5, Version = 1 } },
                ServerAfter = new EntityComp { Target = new Entity { Index = 77, Version = 2 } },
            };

            var table = Make(detail);

            var afterClient = Fields(table.Q<VisualElement>("after-client-fields"));
            Assert.That(afterClient[0].ClassListContains(TickInspectorUssClasses.MispredictionTableFieldValueChanges), Is.True);
            Assert.That(afterClient[0].tooltip, Does.Contain("Value changed before to after:"),
                "an entity reference has no numeric delta, so the tooltip names both values");

            var afterServer = Fields(table.Q<VisualElement>("after-server-fields"));
            Assert.That(afterServer[0].ClassListContains(TickInspectorUssClasses.MispredictionTableFieldValueChanges), Is.False,
                "the server's reference never moved");
        }

        [Test]
        public void LargeField_CollapsesToASummary_AndTogglesOnClick()
        {
            var server = float4x4.identity;
            server.c2.z = 42f;
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(MatrixComp),
                ClientBefore = new MatrixComp { M = float4x4.identity }, ServerBefore = new MatrixComp { M = float4x4.identity },
                ClientAfter = new MatrixComp { M = float4x4.identity }, ServerAfter = new MatrixComp { M = server },
            };

            var table = Make(detail);
            var afterClient = table.Q<VisualElement>("after-client-fields");
            var afterServer = table.Q<VisualElement>("after-server-fields");

            var summary = Summary(afterClient);
            var arrowToggle = Arrow(afterClient);
            Assert.That(summary, Is.Not.Null, "16 scalars collapse instead of spilling across the cell");
            Assert.That(arrowToggle, Is.Not.Null, "foldout-style toggle must exist before the field name");
            Assert.That(summary.text, Does.Contain("16 values").And.Contain("1 changed"));
            // Auto-expanded because the diff has 1 changed leaf.
            Assert.That(arrowToggle.value, Is.True, "auto-expanded when diffs exist");
            Assert.That(IsChildrenVisible(afterClient), Is.True, "auto-expanded when diffs exist");
            // The four float4 columns are inline, so the summary plus all 16 scalars are visible.
            Assert.That(Fields(afterClient), Has.Count.EqualTo(17));

            // Clicking an already-expanded row collapses it.
            simulate.Click(arrowToggle);
            simulate.FrameUpdate();

            Assert.That(arrowToggle.value, Is.False, "toggle flips on collapse");
            Assert.That(IsChildrenVisible(afterClient), Is.False);
            Assert.That(IsChildrenVisible(afterServer), Is.False, "both cells collapse together so the values line up");

            // Driven directly: verify the API also re-expands correctly.
            table.SetExpanded(typeof(MatrixComp), "M", expanded: true);

            Assert.That(arrowToggle.value, Is.True, "toggle flips back on expand");
            Assert.That(IsChildrenVisible(afterClient), Is.True);
            Assert.That(IsChildrenVisible(afterServer), Is.True);
        }

        [Test]
        public void AutoExpandedField_CollapsesOnTheFirstHeaderClick()
        {
            // A diverging field opens itself without being recorded as user-expanded. The header has to
            // toggle what is on screen: negating the saved state would ask for "expanded" on a row that
            // already is, leaving the name and summary dead until a second click.
            var server = float4x4.identity;
            server.c2.z = 42f;
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(MatrixComp),
                ClientBefore = new MatrixComp { M = float4x4.identity }, ServerBefore = new MatrixComp { M = float4x4.identity },
                ClientAfter = new MatrixComp { M = float4x4.identity }, ServerAfter = new MatrixComp { M = server },
            };

            var table = Make(detail);
            var afterClient = table.Q<VisualElement>("after-client-fields");
            Assert.That(Arrow(afterClient).value, Is.True, "1 changed leaf auto-expands the field");

            simulate.Click(Summary(afterClient));
            simulate.FrameUpdate();

            Assert.That(Arrow(afterClient).value, Is.False, "the first click on the summary must collapse it");
            Assert.That(IsChildrenVisible(afterClient), Is.False);
        }

        static bool IsChildrenVisible(VisualElement cell) =>
            cell.Q<VisualElement>(className: TickInspectorUssClasses.MispredictionTableExpandedChildren)
                .style.display.value == DisplayStyle.Flex;

        static string LineText(VisualElement cell, int index)
        {
            var lines = cell.Query<VisualElement>(className: TickInspectorUssClasses.MispredictionTableFieldLine).ToList();
            var text = string.Empty;
            foreach (var span in lines[index].Query<Label>(className: TickInspectorUssClasses.MispredictionTableSpan).ToList())
                text += span.text;
            return text;
        }

        static System.Collections.Generic.List<VisualElement> Lines(VisualElement cell) =>
            cell.Query<VisualElement>(className: TickInspectorUssClasses.MispredictionTableFieldLine).ToList();

        [Test]
        public void ClicksInsideTheTable_DoNotReachTheTreeRow()
        {
            // The tree selects on PointerDown bubbling up from the row content; expanding a value or just
            // clicking one must not drag the row's selection tint over the diff colours with it.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(MatrixComp),
                ClientBefore = new MatrixComp { M = float4x4.identity }, ServerBefore = new MatrixComp { M = float4x4.identity },
                ClientAfter = new MatrixComp { M = float4x4.identity }, ServerAfter = new MatrixComp { M = float4x4.identity },
            };

            var host = new VisualElement();
            rootVisualElement.Add(host);
            var reachedHost = 0;
            host.RegisterCallback<PointerDownEvent>(_ => reachedHost++);

            var table = new MispredictionDetailTable();
            host.Add(table);
            simulate.FrameUpdate();
            table.SetDetail(detail);
            simulate.FrameUpdate();

            simulate.Click(Summary(table.Q<VisualElement>("after-client-fields")));
            simulate.FrameUpdate();

            Assert.That(reachedHost, Is.Zero, "the click must be swallowed before it reaches the row");
            Assert.That(IsChildrenVisible(table.Q<VisualElement>("after-client-fields")), Is.True,
                "and it must still have expanded the field");
        }

        [Test]
        public void LongFloats_AreTrimmed_AndNoTooltipOnUnchangedValue()
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(VecComp),
                ClientBefore = new VecComp { V = new float3(0.9287948f, 0f, 0f) },
                ServerBefore = new VecComp { V = new float3(0.9287948f, 0f, 0f) },
                ClientAfter = new VecComp { V = new float3(0.9287948f, 0f, 0f) },
                ServerAfter = new VecComp { V = new float3(0.9287948f, 0f, 0f) },
            };

            var table = Make(detail);
            var first = Fields(table.Q<VisualElement>("after-client-fields"))[0];

            Assert.That(first.text, Is.EqualTo("0.9288"), "full float precision is too wide for the cell");
            Assert.That(first.tooltip, Is.Null.Or.Empty, "no tooltip on an unchanged value");
        }

        [Test]
        public void MixedComponent_LabelsSubFields_ButKeepsVectorsPositional()
        {
            var client = new MixedComp
            {
                Inner = new MixedInner { Flag = true, Value = 1.5f, Tag = "abc" },
                Pos = new float3(1f, 2f, 3f),
                Matrix = float4x4.identity,
                Owner = new Entity { Index = 3, Version = 1 },
                State = Zone.Idle,
                Enabled = true,
            };
            var server = client;
            server.Inner.Value = 9f;

            var detail = new MispredictionDetail
            {
                ComponentType = typeof(MixedComp),
                ClientBefore = client, ServerBefore = client,
                ClientAfter = client, ServerAfter = server,
            };

            var table = Make(detail);
            var afterClient = table.Q<VisualElement>("after-client-fields");

            Assert.That(LineText(afterClient, 0), Is.EqualTo("Inner 3 values, 1 changed"));
            Assert.That(LineText(afterClient, 1), Is.EqualTo("Flag (True)"));
            Assert.That(LineText(afterClient, 2), Is.EqualTo("Value (1.5)"));
            Assert.That(LineText(afterClient, 3), Is.EqualTo("Tag (abc)"));
            // A math vector keeps the conventional bare tuple.
            Assert.That(LineText(afterClient, 4), Is.EqualTo("Pos (1, 2, 3)"));

            // Only the one diverging leaf is flagged, across all those neighbouring types. Its row changed
            var flagged = afterClient.Query<Label>(className: TickInspectorUssClasses.MispredictionTableFieldDiff).ToList();
            Assert.That(flagged.ConvertAll(l => l.text), Is.EquivalentTo(new[] { "3 values, 1 changed" }));

            var lines = Lines(afterClient);
            Assert.That(lines[2].Q<VisualElement>(className: TickInspectorUssClasses.MispredictionTableFieldDiff),
                Is.Not.Null, "the Value row diverged, so it is highlighted");
            foreach (var index in new[] { 1, 3, 4 })
            {
                Assert.That(lines[index].Q<VisualElement>(className: TickInspectorUssClasses.MispredictionTableFieldDiff),
                    Is.Null, $"line {index} matches on both sides");
            }

            // The bool, enum and entity all coexist: rendered, and none of them a false positive.
            Assert.That(Fields(afterClient).ConvertAll(f => f.text),
                Does.Contain("True").And.Contain("Idle"));
        }

        struct SlotEntry
        {
            public int Key;
            public float Value;
        }

        struct ListComp : IComponentData
        {
            public FixedList128Bytes<SlotEntry> Lookup;
        }

        static ListComp MakeList(params (int key, float value)[] entries)
        {
            var component = new ListComp();
            foreach (var (key, value) in entries)
                component.Lookup.Add(new SlotEntry { Key = key, Value = value });
            return component;
        }

        [Test]
        public void FixedList_RendersEntries_AndMarksAMissingOne()
        {
            var client = MakeList((0, 1f), (1, 2f));
            var server = MakeList((0, 1f), (1, 99f), (2, 3f));
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(ListComp),
                ClientBefore = client, ServerBefore = client,
                ClientAfter = client, ServerAfter = server,
            };

            var table = Make(detail);
            var afterClient = table.Q<VisualElement>("after-client-fields");
            var afterServer = table.Q<VisualElement>("after-server-fields");

            // Each side counts its own entries; the divergence is visible before expanding.
            Assert.That(Summary(afterClient).text, Does.Contain("2 entries"));
            Assert.That(Summary(afterServer).text, Does.Contain("3 entries"));

            simulate.Click(Summary(afterClient));
            simulate.FrameUpdate();

            Assert.That(EntryLines(afterClient), Has.Count.EqualTo(3), "three entry rows");
            Assert.That(EntryLines(afterServer), Has.Count.EqualTo(3));
            Assert.That(EntryLines(afterClient)[0], Is.EqualTo("[0] 2 values"));
            Assert.That(EntryLines(afterClient)[1], Is.EqualTo("[1] 2 values, 1 changed"));
            Assert.That(EntryLines(afterClient)[2], Does.Contain("No entry"), "the client is one entry short");
            Assert.That(EntryLines(afterServer)[2], Is.EqualTo("[2] 2 values, 2 changed"));
        }

        [Test]
        public void FixedList_ThatGrowsDuringTheSystem_MarksTheNewEntry()
        {
            var before = MakeList((0, 1f));
            var after = MakeList((0, 1f), (1, 2f));
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(ListComp),
                ClientBefore = before, ServerBefore = before,
                ClientAfter = after, ServerAfter = after,
            };

            var table = Make(detail);
            var afterClient = table.Q<VisualElement>("after-client-fields");
            simulate.Click(Summary(afterClient));
            simulate.FrameUpdate();


            var summaries = Summaries(afterClient);
            Assert.That(summaries, Has.Count.EqualTo(3), "the list plus its two entries");
            simulate.Click(summaries[1]);
            simulate.FrameUpdate();
            simulate.Click(summaries[2]);
            simulate.FrameUpdate();

            var marked = afterClient.Query<Label>(className: TickInspectorUssClasses.MispredictionTableFieldValueChanges).ToList();
            Assert.That(marked.ConvertAll(l => l.text), Is.EquivalentTo(new[] { "1", "2" }),
                "entry [1] is new as of this tick; entry [0] was already there, unchanged");
        }

        static System.Collections.Generic.List<Label> Summaries(VisualElement cell) =>
            cell.Query<Label>(className: TickInspectorUssClasses.MispredictionTableSummary).ToList();

        /// <summary>
        /// The text of the entry rows only. An expanded entry is followed by the rows of its own fields, and
        /// only an entry row is named "[i]".
        /// </summary>
        static System.Collections.Generic.List<string> EntryLines(VisualElement cell)
        {
            var texts = new System.Collections.Generic.List<string>();
            for (var i = 0; i < Lines(cell).Count; i++)
            {
                var text = LineText(cell, i);
                if (text.StartsWith("["))
                    texts.Add(text);
            }
            return texts;
        }

        [Test]
        public void LargeComponent_RendersOneLevelAtATime()
        {
            // 128 scalars behind one field. The diff resolves the single changed leaf; auto-expand
            // opens only the path to that leaf, leaving unrelated nodes collapsed.
            var server = new BigBlock();
            server.H.c3.w = 42f;
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(BigComp),
                ClientBefore = new BigComp(), ServerBefore = new BigComp(),
                ClientAfter = new BigComp(), ServerAfter = new BigComp { Block = server },
            };

            var table = Make(detail);
            var afterClient = table.Q<VisualElement>("after-client-fields");

            // Block auto-expands because it contains the changed leaf; the summary text is still correct.
            var block = Summary(afterClient);
            Assert.That(block.text, Does.Contain("128 values").And.Contain("1 changed"),
                "the count is exact even while auto-expanded");

            // One level: the eight matrices appear as their own summaries; only the diff path is auto-expanded.
            var summaries = afterClient.Query<Label>(className: TickInspectorUssClasses.MispredictionTableSummary).ToList();
            Assert.That(summaries, Has.Count.EqualTo(9), "Block plus its eight children");

            // Exactly one child owns the change, so the drill-down path is unambiguous.
            var changedChildren = summaries.FindAll(s => s != block && s.text.Contains("changed"));
            Assert.That(changedChildren, Has.Count.EqualTo(1));
            Assert.That(changedChildren[0].text, Does.Contain("16 values").And.Contain("1 changed"));
        }

        struct EnumComp : IComponentData
        {
            public Zone State;
        }

        struct BoolComp : IComponentData
        {
            public bool Flag;
        }

        static bool LineHasDiffClass(VisualElement cell, int lineIndex, bool isClient)
        {
            var diffClass = isClient
                ? TickInspectorUssClasses.MispredictionTableFieldDiff
                : TickInspectorUssClasses.MispredictionTableFieldServerDiff;
            // The row highlight sits on the content wrapper inside the line, not on the line itself: the
            // wrapper hugs the values so the tint ends at the last one instead of running to the cell edge.
            var line = Lines(cell)[lineIndex];
            return line.ClassListContains(diffClass) ||
                (line.childCount > 0 && line[0].ClassListContains(diffClass));
        }

        [Test]
        public void AllValuesChanged_DiffClassMovesToRow()
        {
            // All three components of V differ — the whole row should be highlighted, not individual tokens.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(VecComp),
                ClientBefore = new VecComp { V = new float3(1f, 2f, 3f) }, ServerBefore = new VecComp { V = new float3(1f, 2f, 3f) },
                ClientAfter = new VecComp { V = new float3(9f, 8f, 7f) }, ServerAfter = new VecComp { V = new float3(1f, 2f, 3f) },
            };

            var table = Make(detail);
            var afterClient = table.Q<VisualElement>("after-client-fields");

            Assert.That(LineHasDiffClass(afterClient, 0, isClient: true), Is.True,
                "row must carry the diff class when all leaves changed");
            foreach (var token in Fields(afterClient))
                Assert.That(IsDiffHighlighted(token), Is.False,
                    "individual tokens must not carry the diff class when the row does");
        }

        [Test]
        public void SomeValuesChanged_DiffClassStaysOnTokens()
        {
            // Only z differs — the row must not get the class; only the z token does.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(VecComp),
                ClientBefore = new VecComp { V = new float3(1f, 2f, 3f) }, ServerBefore = new VecComp { V = new float3(1f, 2f, 3f) },
                ClientAfter = new VecComp { V = new float3(1f, 2f, 9f) }, ServerAfter = new VecComp { V = new float3(1f, 2f, 3f) },
            };

            var table = Make(detail);
            var afterClient = table.Q<VisualElement>("after-client-fields");

            Assert.That(LineHasDiffClass(afterClient, 0, isClient: true), Is.False,
                "row must not carry the diff class when only some leaves changed");
            var tokens = Fields(afterClient);
            Assert.That(IsDiffHighlighted(tokens[2]), Is.True, "only the z token is highlighted");
            Assert.That(IsDiffHighlighted(tokens[0]), Is.False);
            Assert.That(IsDiffHighlighted(tokens[1]), Is.False);
        }

        [Test]
        public void SingleScalarChanged_DiffClassMovesToRow()
        {
            // A single scalar field — "all" leaves changed is trivially true.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Vec),
                ClientBefore = new Vec { X = 1, Y = 2 }, ServerBefore = new Vec { X = 1, Y = 2 },
                ClientAfter = new Vec { X = 9, Y = 2 }, ServerAfter = new Vec { X = 1, Y = 2 },
            };

            var table = Make(detail);
            var afterClient = table.Q<VisualElement>("after-client-fields");

            Assert.That(LineHasDiffClass(afterClient, 0, isClient: true), Is.True,
                "single changed scalar — class must be on the row");
            Assert.That(IsDiffHighlighted(Fields(afterClient)[0]), Is.False,
                "and not on the token");
            // Y unchanged — neither row nor token highlighted.
            Assert.That(LineHasDiffClass(afterClient, 1, isClient: true), Is.False);
            Assert.That(IsDiffHighlighted(Fields(afterClient)[1]), Is.False);
        }

        [Test]
        public void DiffTooltip_Float_ShowsServerClientDelta()
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(VecComp),
                ClientBefore = new VecComp { V = new float3(1f, 2f, 3f) }, ServerBefore = new VecComp { V = new float3(1f, 2f, 3f) },
                ClientAfter = new VecComp { V = new float3(1f, 2f, 9f) }, ServerAfter = new VecComp { V = new float3(1f, 2f, 3f) },
            };

            var table = Make(detail);

            var changedLabel = Fields(table.Q<VisualElement>("after-client-fields"))[2]; // z changed
            Assert.That(changedLabel.tooltip, Does.Contain("Diff value from server to client:"));
        }

        [Test]
        public void DiffTooltip_Int_ShowsServerClientDeltaAndTemporalDelta()
        {
            // X differs client vs server; and X also changed between before and after on the client side.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Vec),
                ClientBefore = new Vec { X = 1, Y = 2 }, ServerBefore = new Vec { X = 1, Y = 2 },
                ClientAfter = new Vec { X = 9, Y = 2 }, ServerAfter = new Vec { X = 1, Y = 2 },
            };

            var table = Make(detail);

            var changedLabel = Fields(table.Q<VisualElement>("after-client-fields"))[0];
            Assert.That(changedLabel.tooltip, Does.Contain("Diff value from server to client: -8"),
                "server minus client");
            Assert.That(changedLabel.tooltip, Does.Contain("Value changed before to after: +8"),
                "client after minus client before");
        }

        [Test]
        public void ValueChangedClass_AddedWhenTemporalDeltaExists_EvenWithNoServerClientDiff()
        {
            // X changed on both sides identically (no server/client diff), Y did not change at all.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Vec),
                ClientBefore = new Vec { X = 1, Y = 2 }, ServerBefore = new Vec { X = 1, Y = 2 },
                ClientAfter = new Vec { X = 9, Y = 2 }, ServerAfter = new Vec { X = 9, Y = 2 },
            };

            var table = Make(detail);

            var afterClient = Fields(table.Q<VisualElement>("after-client-fields"));
            Assert.That(afterClient[0].ClassListContains(TickInspectorUssClasses.MispredictionTableFieldValueChanges), Is.True,
                "value-change class must be added even when client and server agree");
            Assert.That(afterClient[1].ClassListContains(TickInspectorUssClasses.MispredictionTableFieldValueChanges), Is.False,
                "value-change class must not be added when before and after are the same");
            Assert.That(afterClient[0].tooltip, Does.Contain("Value changed before to after:"),
                "temporal tooltip appears even without a server/client diff");
        }

        [Test]
        public void DiffTooltip_NoTemporalChange_OmitsSecondLine()
        {
            // X differs client vs server, but the client value was already 9 before — no temporal change.
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Vec),
                ClientBefore = new Vec { X = 9, Y = 2 }, ServerBefore = new Vec { X = 1, Y = 2 },
                ClientAfter = new Vec { X = 9, Y = 2 }, ServerAfter = new Vec { X = 1, Y = 2 },
            };

            var table = Make(detail);

            var changedLabel = Fields(table.Q<VisualElement>("after-client-fields"))[0];
            Assert.That(changedLabel.tooltip, Does.Contain("Diff value from server to client: -8"));
            Assert.That(changedLabel.tooltip, Does.Not.Contain("Value changed before to after:"),
                "no temporal delta when before and after values are the same");
        }

        [Test]
        public void DiffTooltip_Enum_FallsBackToGenericMessage()
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(EnumComp),
                ClientBefore = new EnumComp { State = Zone.Idle }, ServerBefore = new EnumComp { State = Zone.Idle },
                ClientAfter = new EnumComp { State = Zone.Running }, ServerAfter = new EnumComp { State = Zone.Idle },
            };

            var table = Make(detail);

            var changedLabel = Fields(table.Q<VisualElement>("after-client-fields"))[0];
            Assert.That(changedLabel.tooltip, Does.Not.Contain("Diff value from server to client:"),
                "no numeric delta for a non-numeric type");
            Assert.That(changedLabel.tooltip, Is.Not.Null.And.Not.Empty,
                "falls back to the generic diverging message");
        }

        [Test]
        public void DiffTooltip_Bool_FallsBackToGenericMessage()
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(BoolComp),
                ClientBefore = new BoolComp { Flag = false }, ServerBefore = new BoolComp { Flag = false },
                ClientAfter = new BoolComp { Flag = true }, ServerAfter = new BoolComp { Flag = false },
            };

            var table = Make(detail);

            var changedLabel = Fields(table.Q<VisualElement>("after-client-fields"))[0];
            Assert.That(changedLabel.tooltip, Does.Not.Contain("Diff value from server to client:"));
            Assert.That(changedLabel.tooltip, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void UnchangedField_HasNoDiffTooltip()
        {
            var detail = new MispredictionDetail
            {
                ComponentType = typeof(Vec),
                ClientBefore = new Vec { X = 1, Y = 2 }, ServerBefore = new Vec { X = 1, Y = 2 },
                ClientAfter = new Vec { X = 9, Y = 2 }, ServerAfter = new Vec { X = 1, Y = 2 },
            };

            var table = Make(detail);

            // Y did not change — its label must not carry any diff tooltip
            var unchangedLabel = Fields(table.Q<VisualElement>("after-client-fields"))[1];
            Assert.That(IsDiffHighlighted(unchangedLabel), Is.False);
            Assert.That(unchangedLabel.tooltip, Is.Null.Or.Empty,
                "no tooltip on an unchanged field");
        }

        [TearDown]
        public void TearDown()
        {
            rootVisualElement.Clear();
            MispredictionDetailTable.ClearExpansionState();
        }
    }
}

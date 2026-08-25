using System.Collections.Generic;
using NUnit.Framework;
using Unity.NetCode.Tracing;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;
using Unity.NetCode.Editor.Tracing.UI.TickInspector;

namespace Unity.NetCode.Editor.Tracing.UI.Tests
{
    /// <summary>
    /// Tests for the metadata foldout that sits above the tick inspector tree, decoupled from tree data.
    /// </summary>
    class TickInspectorMetadataFoldoutTests : UITestFixture
    {
        [Test]
        public void Foldout_HasMetadataStyledLabel()
        {
            var foldout = new TickInspectorMetadataFoldout();
            rootVisualElement.Add(foldout);
            simulate.FrameUpdate();

            // The title label carries the metadata-foldout label class so the stylesheet gives it the
            // tree-row icon + bold treatment.
            var label = foldout.Q<Label>(className: TickInspectorUssClasses.MetadataFoldoutLabel);
            Assert.That(label, Is.Not.Null);
        }

        [Test]
        public void Foldout_HasNoActionButtons()
        {
            var foldout = new TickInspectorMetadataFoldout();
            rootVisualElement.Add(foldout);
            simulate.FrameUpdate();

            // Unlike tree rows, the metadata header has no open-script / open-window buttons.
            Assert.That(foldout.Q<Button>(), Is.Null);
        }

        [Test]
        public void SetMetadata_FillsValueRows_AndShowsPlaceholdersWithoutServerTick()
        {
            var foldout = new TickInspectorMetadataFoldout();
            rootVisualElement.Add(foldout);
            foldout.SetMetadata(new TickInspectorTickMetadata
            {
                HasClientTick = true,
                ClientDeltaTimeSeconds = 1f / 60f,
                IsPartialTick = true,
                PartialTickFraction = 0.25f,
                FrameDeltaTimeSeconds = 0.02f,
                HasServerTick = true,
                ServerDeltaTimeSeconds = 1f / 30f,
                ServerBatchSize = 2,
            });
            simulate.FrameUpdate();

            var values = ValueTexts(foldout);
            Assert.That(values, Has.Some.Contains("16.67 ms"), "client delta time");
            Assert.That(values, Has.Some.Contains("33.33 ms"), "server delta time");
            Assert.That(values, Has.Some.Contains("2 (this tick was simulated as part of a batch)"), "server batch size");
            Assert.That(values, Has.Some.Contains("Yes (0.25 of a full tick)"), "partial tick");
            Assert.That(values, Has.Some.Contains("20 ms"), "frame delta time");

            // Without a server tick, the server rows fall back to placeholders.
            foldout.SetMetadata(new TickInspectorTickMetadata
            {
                HasClientTick = true,
                ClientDeltaTimeSeconds = 1f / 60f,
                FrameDeltaTimeSeconds = 0.02f,
            });
            simulate.FrameUpdate();
            Assert.That(ValueTexts(foldout), Has.Exactly(2).EqualTo("—"), "server delta time and batch size have no data");
        }

        [Test]
        public void SetTickDiffReasons_ShowsTags_AndAutoExpands()
        {
            var foldout = new TickInspectorMetadataFoldout();
            rootVisualElement.Add(foldout);

            foldout.SetTickDiffReasons(DiffInfo.DiffReasons.DeltaTime | DiffInfo.DiffReasons.BatchedTick, DiffInfo.AllDiffReasons);
            simulate.FrameUpdate();

            var tags = VisibleTags(foldout);
            Assert.That(tags, Has.Count.EqualTo(2));
            Assert.That(foldout.value, Is.True, "a tick-level diff auto-expands the foldout so it can't hide");

            // Deselecting the metadata reasons folds the header; the tags stay visible (greyed).
            foldout.SetTickDiffReasons(DiffInfo.DiffReasons.DeltaTime | DiffInfo.DiffReasons.BatchedTick,
                DiffInfo.AllDiffReasons & ~(DiffInfo.DiffReasons.DeltaTime | DiffInfo.DiffReasons.BatchedTick));
            simulate.FrameUpdate();
            Assert.That(VisibleTags(foldout), Has.Count.EqualTo(2), "deselected tags stay visible");
            Assert.That(foldout.value, Is.False, "no selected tick-level diff left to reveal");

            // Clearing the reasons hides the tag row and keeps the foldout collapsed.
            foldout.SetTickDiffReasons(DiffInfo.DiffReasons.Undefined, DiffInfo.AllDiffReasons);
            simulate.FrameUpdate();
            Assert.That(VisibleTags(foldout), Is.Empty);
            Assert.That(foldout.value, Is.False);
        }

        static List<string> ValueTexts(TickInspectorMetadataFoldout foldout)
        {
            var texts = new List<string>();
            foldout.Query<Label>(className: TickInspectorUssClasses.MetadataRowValue)
                .ForEach(label => texts.Add(label.text));
            return texts;
        }

        static List<Label> VisibleTags(TickInspectorMetadataFoldout foldout)
        {
            var tags = new List<Label>();
            foldout.Query<Label>(className: TickInspectorUssClasses.TreeViewItemDiffTag)
                .ForEach(tag =>
                {
                    if (tag.resolvedStyle.display != DisplayStyle.None)
                        tags.Add(tag);
                });
            return tags;
        }

        [Test]
        public void Foldout_CollapsedByDefault()
        {
            var foldout = new TickInspectorMetadataFoldout();
            rootVisualElement.Add(foldout);
            simulate.FrameUpdate();

            // Collapsed so it reads as a single row, matching the look of the old metadata item.
            Assert.That(foldout.value, Is.False);
        }

        [TearDown]
        public void TearDown()
        {
            rootVisualElement.Clear();
        }
    }
}

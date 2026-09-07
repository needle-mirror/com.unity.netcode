using System.Collections;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Netcode;
using Unity.Netcode.Editor.Tracing.UI;
using Unity.Netcode.Editor.Tracing.UI.TracingToolbar;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;
using UnityEngine.TestTools;
using Unity.Netcode.Editor.Tracing.UI.TickInspector;
using Unity.Netcode.Tracing;

namespace Tests.Editor.UI.Tracing.TickInspector
{
    /// <summary>
    /// Tests for TickInspectorView.
    /// </summary>
    class TickInspectorViewTests : UITestFixture
    {
        TickInspectorView m_View;

        [SetUp]
        public void Setup()
        {
            m_View = new TickInspectorView();
            rootVisualElement.Add(m_View.Create());
            simulate.FrameUpdate();
        }

        [TearDown]
        public void TearDown()
        {
            m_View?.Dispose();
            rootVisualElement.Clear();
        }

        [UnityTest]
        public IEnumerator WhenDataAvailable_Initial_ViewIsHidden()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3, withTicks: true);

            yield return m_View.OnDataAvailable(data);

            var root = rootVisualElement.Q(className: TickInspectorUssClasses.Base);
            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.True);

            data.ClientWorldData.Dispose();
            data.ServerWorldData.Dispose();
        }

        [UnityTest]
        public IEnumerator WhenDataAvailable_WithoutSelectedTick_ShowsSelectATickEmptyState()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3, withTicks: true);

            yield return m_View.OnDataAvailable(data);

            var root = rootVisualElement.Q(className: TickInspectorUssClasses.Base);

            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.True);

            // Change to a valid tick first to ensure it's visible
            var tickId = new TickID { value = new NetworkTick(10) };
            var frameId = new FrameID { value = 1 };

            yield return m_View.OnSelectedTickOrFrameChanged(new TracingSelectionChange() { tickID = tickId, frameID = frameId });

            simulate.FrameUpdate();
            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);

            // Deselecting the tick while a frame is still selected keeps the view visible and
            // shows the "Select a tick to view detail" empty state instead of the tree.
            yield return m_View.OnSelectedTickOrFrameChanged(new TracingSelectionChange() { tickID = default, frameID = frameId });
            simulate.FrameUpdate();

            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);

            var emptyStateLabel = root.Query<Label>().Where(l => l.text == TickInspectorView.k_SelectTickText).First();
            Assert.That(emptyStateLabel, Is.Not.Null);
            Assert.That(emptyStateLabel.parent.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);

            data.ClientWorldData.Dispose();
            data.ServerWorldData.Dispose();
        }


        [UnityTest]
        public IEnumerator OnSelectedTickOrFrameChanged_UpdatesToolbarLabels()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3, withTicks: true);
            yield return m_View.OnDataAvailable(data);


            var frameId = new FrameID { value = 2 };
            var tickId = new TickID { value = new NetworkTick(20) };
            var change = new TracingSelectionChange { frameID = frameId, tickID = tickId };

            yield return m_View.OnSelectedTickOrFrameChanged(change);
            simulate.FrameUpdate();

            var root = rootVisualElement.Q(className: TickInspectorUssClasses.Base);
            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);

            var frameLabel = rootVisualElement.Q<Label>(TickInspectorView.k_TimelineFrameLabel);
            Assert.That(frameLabel?.text, Does.Contain("Frame 2"));

            var tickLabel = rootVisualElement.Q<Label>(TickInspectorView.k_TimelineTickLabel);
            Assert.That(tickLabel?.text, Does.Contain("Tick 20"));

            data.ClientWorldData.Dispose();
            data.ServerWorldData.Dispose();
        }

        [Test]
        public void SetFilters_ShowsFilterStatus_OnlyWhenTheViewIsNarrowed()
        {
            var statusLabel = rootVisualElement.Q<Label>(TickInspectorView.k_FilterStatusLabel);
            Assert.That(statusLabel, Is.Not.Null);

            TracingDataAccess.Config.Data.Init();
            try
            {
                var tracedSystem = TypeManager.GetSystemTypeIndex<SimulationSystemGroup>();
                TracingDataAccess.Config.Data.SystemTypesToTrace.Add(tracedSystem);

                var totalTargets = TracingFilterDropdown.CreateListItems().Count;
                Assert.That(totalTargets, Is.GreaterThanOrEqualTo(1), "the traced system must be a target");

                // Nothing narrowed: the status note stays hidden.
                m_View.SetFilters(new TracingViewFilters());
                Assert.That(statusLabel.ClassListContains(TracingWindowUssClasses.Hidden), Is.True);

                // One hidden target, one deselected tag, changed values only: every narrowing is spelled out.
                var narrowed = new TracingViewFilters { ChangedValuesOnly = true };
                narrowed.HiddenSystems.Add(TracingTargetNames.System(tracedSystem));
                narrowed.EnabledDiffReasons = DiffInfo.AllDiffReasons & ~DiffInfo.DiffReasons.ComponentData;
                m_View.SetFilters(narrowed);

                var total = DiffReasonCatalog.All.Length;
                Assert.That(statusLabel.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);
                Assert.That(statusLabel.text,
                    Is.EqualTo($"Showing {totalTargets - 1}/{totalTargets} tracing targets and {total - 1}/{total} diff tags, changed values only"));

                // A stale hidden name (not in the current target list) doesn't skew the counts.
                narrowed.HiddenSystems.Add("Some.Stale.System");
                m_View.SetFilters(narrowed);
                Assert.That(statusLabel.text, Does.StartWith($"Showing {totalTargets - 1}/{totalTargets} tracing targets"));

                // With every target shown, the narrowed tags still spell the targets out as "all".
                var tagsOnly = new TracingViewFilters
                {
                    EnabledDiffReasons = DiffInfo.AllDiffReasons & ~DiffInfo.DiffReasons.ComponentData,
                };
                m_View.SetFilters(tagsOnly);
                Assert.That(statusLabel.text,
                    Is.EqualTo($"Showing all tracing targets and {total - 1}/{total} diff tags"));

                // A fuzzy threshold alone is also a narrowing and is spelled out.
                m_View.SetFilters(new TracingViewFilters { FuzzyDiffThreshold = 0.5f });
                Assert.That(statusLabel.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);
                Assert.That(statusLabel.text, Does.EndWith("fuzzy threshold > 0.5"));

                // Widening back out hides the note again.
                m_View.SetFilters(new TracingViewFilters());
                Assert.That(statusLabel.ClassListContains(TracingWindowUssClasses.Hidden), Is.True);
            }
            finally
            {
                TracingDataAccess.Config.Data.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Clear_HidesView()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3, withTicks: true);
            yield return m_View.OnDataAvailable(data);


            var frameId = new FrameID { value = 2 };
            var tickId = new TickID { value = new NetworkTick(20) };
            var change = new TracingSelectionChange { frameID = frameId, tickID = tickId };

            yield return m_View.OnSelectedTickOrFrameChanged(change);
            simulate.FrameUpdate();
            var searchField = rootVisualElement.Q<ToolbarSearchField>(TickInspectorView.k_TimelineSearchInput);
            searchField.value = "test search";
            simulate.FrameUpdate();

            var root = rootVisualElement.Q(className: TickInspectorUssClasses.Base);
            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);

            m_View.Clear();
            simulate.FrameUpdate();

            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.True);
            Assert.That(searchField.value, Is.Empty, "Search field should be cleared");

            data.ClientWorldData.Dispose();
            data.ServerWorldData.Dispose();
        }

        // Creates minimal TracingData with the given frame range. Caller is responsible for disposing.
        static TracingData CreateTracingData(int firstFrame, int lastFrame, bool withTicks = false)
        {
            var clientWorldData = new WorldData(Allocator.Persistent);
            var serverWorldData = new WorldData(Allocator.Persistent);

            for (int f = firstFrame; f <= lastFrame; f++)
            {
                var frameId = new FrameID { value = f };
                var frameData = new FrameData(0.016f);

                if (withTicks)
                {
                    // Two ticks per frame; tick indices are f*10 and f*10+1.
                    var tickId1 = new TickID { value = new NetworkTick((uint)(f * 10)) };
                    var tickId2 = new TickID { value = new NetworkTick((uint)(f * 10 + 1)) };
                    var tickData1 = new TickData(0.016f, default, TraceType.Default);
                    var tickData2 = new TickData(0.016f, default, TraceType.Default);

                    frameData.TickIDs.Add(tickId1);
                    frameData.TickIDs.Add(tickId2);
                    frameData.PerTickData.Add(tickId1, tickData1);
                    frameData.PerTickData.Add(tickId2, tickData2);

                    var serverTickData1 = new TickData(0.016f, default, TraceType.Default);
                    var serverTickData2 = new TickData(0.016f, default, TraceType.Default);
                    serverWorldData.PerTickData.Add(tickId1, serverTickData1);
                    serverWorldData.PerTickData.Add(tickId2, serverTickData2);
                }

                clientWorldData.FrameIDs.Add(frameId);
                clientWorldData.PerFrameData.Add(frameId, frameData);
            }

            return new TracingData
            {
                ClientWorldData = clientWorldData,
                ServerWorldData = serverWorldData
            };
        }
    }
}

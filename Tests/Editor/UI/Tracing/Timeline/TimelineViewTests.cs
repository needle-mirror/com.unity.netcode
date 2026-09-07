using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Tracing;
using Unity.Netcode.Editor.Tracing.UI;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;
using Unity.Netcode.Editor.Tracing.UI.Timeline;
using Unity.Netcode.NetcodeTime;
using Unity.Netcode.Tests;

namespace Tests.Editor.UI.Tracing.Timeline
{
    /// <summary>
    /// Tests for TimelineView.
    /// </summary>
    class TimelineViewTests : UITestFixture
    {
        NetCodeTestWorld m_TestWorld;

        [SetUp]
        public void Setup()
        {
            Unity.Entities.TypeManager.Initialize(); // fixture aggregates are stamped with real type indices
            m_TestWorld = new NetCodeTestWorld(); // useful to setup/teardown global netcode states
        }

        [TearDown]
        public void TearDown()
        {
            rootVisualElement.Clear();
            m_TestWorld.Dispose();
        }

        // Creates a view, calls Create(), adds it to the hierarchy, and runs one frame update.
        TimelineView CreateAndAddView()
        {
            var view = new TimelineView();
            rootVisualElement.Add(view.Create());
            simulate.FrameUpdate();
            return view;
        }

        // Creates minimal TracingData with the given frame range. Caller is responsible for disposing.
        // Frame diffs are recorded as ComponentData aggregates; withValueChanges controls their
        // HasValueChanged bit (what the ViewChangedValues filter keys on).
        static TracingData CreateTracingData(int firstFrame, int lastFrame, bool withTicks = false, bool withFrameDiffs = false, bool withValueChanges = false)
        {
            var clientWorldData = new WorldData(Allocator.Persistent);
            var serverWorldData = new WorldData(Allocator.Persistent);

            for (int f = firstFrame; f <= lastFrame; f++)
            {
                var frameId = new FrameID { value = f };
                var frameData = new FrameData(0.016f);
                if (withFrameDiffs && f % 2 == 0)
                {
                    frameData.DiffInfo.AddDiff(new DiffAggregate
                    {
                        System = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Entities.SimulationSystemGroup>(),
                        GhostId = DiffAggregate.InvalidGhostId,
                        Component = Unity.Entities.TypeManager.GetTypeIndex<GhostInstance>(),
                        Reasons = DiffInfo.DiffReasons.ComponentData,
                        HasValueChanged = withValueChanges,
                    });
                }

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

        [Test]
        public void Create_FrameSliderLabelLine1HasCorrectText()
        {
            var view = CreateAndAddView();

            var label = rootVisualElement.Query<Label>().Where(x=> x.text == "Highlights").First();
            Assert.That(label, Is.Not.Null);
            view.Dispose();
        }

        [Test]
        public void Create_FrameSliderLabelLine2HasCorrectText()
        {
            var view = CreateAndAddView();

            var label = rootVisualElement.Q<Label>(className: TimelineUssClasses.FrameSelectorLabelSmall);
            Assert.That(label, Is.Not.Null);
            Assert.That(label.text, Is.EqualTo("Target Diff Range"));

            view.Dispose();
        }

        [Test]
        public async Task OnDataAvailable_SetsFrameSliderLowValue()
        {
            var data = CreateTracingData(firstFrame: 5, lastFrame: 20);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);
                var frameSlider = rootVisualElement.Q<SliderInt>(TimelineView.k_FrameSlider);
                Assert.That(frameSlider.lowValue, Is.EqualTo(5));
            });
        }

        [Test]
        public async Task OnDataAvailable_SetsFrameSliderHighValue()
        {
            var data = CreateTracingData(firstFrame: 5, lastFrame: 20);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);

                var frameSlider = rootVisualElement.Q<SliderInt>(TimelineView.k_FrameSlider);
                var rangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);

                Assert.That(frameSlider.lowValue, Is.EqualTo(5));
                Assert.That(frameSlider.highValue, Is.EqualTo(9));

                Assert.That(rangeSlider.lowLimit, Is.EqualTo(5f));
                Assert.That(rangeSlider.highLimit, Is.EqualTo(20f));

                Assert.That(rangeSlider.minValue, Is.EqualTo(5f));
                Assert.That(rangeSlider.maxValue, Is.EqualTo(9f));
            });
        }

        [Test]
        public async Task OnDataAvailable_SetsRangeSliderLowLimit()
        {
            var data = CreateTracingData(firstFrame: 5, lastFrame: 20);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);

                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);
                Assert.That(frameRangeSlider.lowLimit, Is.EqualTo(5f));
            });
        }

        [Test]
        public async Task OnDataAvailable_SetsRangeSliderHighLimit()
        {
            var data = CreateTracingData(firstFrame: 5, lastFrame: 20);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);

                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);
                Assert.That(frameRangeSlider.highLimit, Is.EqualTo(20f));
            });
        }

        [Test]
        public async Task OnDataAvailable_SetsRangeSliderWindowValues()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 9);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);

                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);
                Assert.That(frameRangeSlider.minValue, Is.EqualTo(1f));
                Assert.That(frameRangeSlider.maxValue, Is.EqualTo(3f));
            });
        }

        [Test]
        public void DisplayProgressBar_ShowsProgressBar()
        {
            var view = CreateAndAddView();

            view.DisplayProgressBar();
            simulate.FrameUpdate();

            var progressBar = rootVisualElement.Q<ProgressBar>(TimelineView.k_ProgressBar);
            Assert.That(progressBar.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);

            view.Dispose();
        }

        [Test]
        public void DisplayProgressBar_SetsProgressBarValueToZero()
        {
            var view = CreateAndAddView();

            view.DisplayProgressBar();

            var progressBar = rootVisualElement.Q<ProgressBar>(TimelineView.k_ProgressBar);
            Assert.That(progressBar.value, Is.EqualTo(0f));

            view.Dispose();
        }

        [Test]
        public void HideProgressBar_HidesProgressBar()
        {
            var view = CreateAndAddView();

            var progressBarContainer = rootVisualElement.Q<VisualElement>(TimelineView.k_ProgressBarContainer);
            view.DisplayProgressBar();
            Assert.That(progressBarContainer.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);
            view.HideProgressBar();
            simulate.FrameUpdate();

            Assert.That(progressBarContainer.ClassListContains(TracingWindowUssClasses.Hidden), Is.True);

            view.Dispose();
        }

        [Test]
        public async Task OnDataAvailable_HidesProgressBarAfterCompletion()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3);
            await WrapWithTryCatch(data, async (view) =>
            {
                view.DisplayProgressBar();
                await view.OnDataAvailable(data);

                await Task.Yield();

                var progressBarContainer = rootVisualElement.Q<VisualElement>(TimelineView.k_ProgressBarContainer);
                Assert.That(progressBarContainer.ClassListContains(TracingWindowUssClasses.Hidden), Is.True);
            });
        }

        [Test]
        public async Task OnDataAvailable_ShowsFrameSliderAfterCompletion()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);

                var frameSlider = rootVisualElement.Q<SliderInt>(TimelineView.k_FrameSlider);
                Assert.That(frameSlider.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);
            });
        }

        [Test]
        public async Task OnDataAvailable_ShowsFrameRangeSliderContainerAfterCompletion()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);

                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);
                Assert.That(frameRangeSlider.ClassListContains(TracingWindowUssClasses.Hidden), Is.False);
            });
        }




        [Test]
        public async Task OnDataAvailable_CreatesDiffMarkersForFrameAndRangeSlidersWithChanges_ThenFramesShouldBeHighlighted()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 8, withFrameDiffs: true);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);
                simulate.FrameUpdate();
                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);
                var frameSlider = rootVisualElement.Q<SliderInt>(TimelineView.k_FrameSlider);
                Assert.That(frameRangeSlider.Query(className: TimelineUssClasses.FrameSelectorFrameMarker).ToList().Count, Is.EqualTo(4));
                Assert.That(frameSlider.Query(className: TimelineUssClasses.FrameSelectorFrameMarker).ToList().Count, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task OnDataAvailable_CreatesDiffMarkersForFrameAndRangeSlidersWhenNoChanges_ThenNoFramesShouldBeHighlighted()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 8, withFrameDiffs: true);
            await WrapWithTryCatch(data, async (view) =>
            {
                var filters = new TracingViewFilters();
                filters.ChangedValuesOnly = true;
                view.SetFilters(filters);
                await view.OnDataAvailable(data);
                simulate.FrameUpdate();
                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);
                var frameSlider = rootVisualElement.Q<SliderInt>(TimelineView.k_FrameSlider);

                Assert.That(frameRangeSlider.Query(className: TimelineUssClasses.FrameSelectorFrameMarker).ToList().Count, Is.EqualTo(0));
                Assert.That(frameSlider.Query(className: TimelineUssClasses.FrameSelectorFrameMarker).ToList().Count, Is.EqualTo(0));
            });
        }

        [Test]
        public async Task OnDataAvailable_DiffsWithValueChanges_KeepMarkersUnderChangedValuesFilter()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 8, withFrameDiffs: true, withValueChanges: true);
            await WrapWithTryCatch(data, async (view) =>
            {
                var filters = new TracingViewFilters();
                filters.ChangedValuesOnly = true;
                view.SetFilters(filters);
                await view.OnDataAvailable(data);
                simulate.FrameUpdate();
                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);

                Assert.That(frameRangeSlider.Query(className: TimelineUssClasses.FrameSelectorFrameMarker).ToList().Count, Is.EqualTo(4),
                    "aggregates recorded with value changes should stay visible under the changed-values filter");
            });
        }

        [Test]
        public async Task SetFilters_DisablingTheDiffReason_RemovesMarkers_WithoutReprocessing()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 8, withFrameDiffs: true);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);
                simulate.FrameUpdate();
                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);
                Assert.That(frameRangeSlider.Query(className: TimelineUssClasses.FrameSelectorFrameMarker).ToList().Count, Is.EqualTo(4),
                    "precondition: all markers visible with every reason enabled");

                // Untick the only reason the fixture records: the markers must disappear...
                var filters = new TracingViewFilters
                {
                    EnabledDiffReasons = DiffInfo.AllDiffReasons & ~DiffInfo.DiffReasons.ComponentData,
                };
                view.SetFilters(filters);
                simulate.FrameUpdate();
                Assert.That(frameRangeSlider.Query(className: TimelineUssClasses.FrameSelectorFrameMarker).ToList().Count, Is.EqualTo(0));

                // ...and come back when it is ticked again, all from the stored aggregates.
                filters.EnabledDiffReasons = DiffInfo.AllDiffReasons;
                view.SetFilters(filters);
                simulate.FrameUpdate();
                Assert.That(frameRangeSlider.Query(className: TimelineUssClasses.FrameSelectorFrameMarker).ToList().Count, Is.EqualTo(4));
            });
        }

        [Test]
        public async Task WhenRangeSliderValueChanges_ThenFrameSliderRangeIsUpdated()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 9, withFrameDiffs: true);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);

                var frameSlider = rootVisualElement.Q<SliderInt>(TimelineView.k_FrameSlider);
                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);

                frameRangeSlider.value = new UnityEngine.Vector2(3f, 6f);
                simulate.FrameUpdate();

                Assert.That(frameSlider.lowValue, Is.EqualTo(3));
                Assert.That(frameSlider.highValue, Is.EqualTo(6));

            });
        }

        [Test]
        public async Task WhenRangeSliderValueChanges_ThenRangeDiffMarkersAreUpdated()
        {

            var data = CreateTracingData(firstFrame: 1, lastFrame: 9, withFrameDiffs: true);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);

                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);
                frameRangeSlider.value = new UnityEngine.Vector2(3f, 6f);
                simulate.FrameUpdate();

                var markers = frameRangeSlider.Query(className: TimelineUssClasses.FrameSelectorFrameMarker).ToList();
                Assert.That(markers.Count, Is.EqualTo(4));
                Assert.That(markers[0].ClassListContains(TimelineUssClasses.FrameSelectorFrameMarkerInactive), Is.True);
                Assert.That(markers[1].ClassListContains(TimelineUssClasses.FrameSelectorFrameMarkerInactive), Is.False);
            });
        }

        [Test]
        public void Clear_HidesFrameSlider()
        {
            var view = CreateAndAddView();

            view.Clear();
            simulate.FrameUpdate();

            var frameSlider = rootVisualElement.Q<SliderInt>(TimelineView.k_FrameSlider);
            Assert.That(frameSlider.ClassListContains(TracingWindowUssClasses.Hidden), Is.True);

            view.Dispose();
        }

        [Test]
        public async Task Clear_AfterDataDisposed_WithSelectedFrame_DoesNotThrow()
        {
            var view = CreateAndAddView();

            var data = CreateTracingData(firstFrame: 1, lastFrame: 3, withTicks: true);

            await view.OnDataAvailable(data);
            await view.OnSelectedFrameChanged(new FrameID { value = 1 });

            // Free the native data the way the engine does on play-mode enter, while the view still holds its
            // TracingData copy (whose IsCreated bool still reads true).
            data.ClientWorldData.Dispose();
            data.ServerWorldData.Dispose();

            Assert.DoesNotThrow(() => view.Clear());

            view.Dispose();
        }

        // Regression test for the missing-timelines window state: the unpause teardown calls Reset()
        // (nulling the view's cancellation source) while OnDataAvailable's UI build is still yielding.
        // The build must observe the cancellation and bail out; it used to NRE on the nulled field,
        // which aborted TracingWindow's view fan-out so the views after the timeline never got data.
        [Test]
        public async Task OnDataAvailable_ResetDuringUIBuild_DoesNotThrow()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 8, withFrameDiffs: true);
            var view = CreateAndAddView();
            try
            {
                view.SetFilters(new TracingViewFilters());
                var build = view.OnDataAvailable(data); // suspends at its first yield
                view.Reset(); // the unpause teardown, while the build is in flight
                await build; // must complete without throwing
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
                view.Dispose();
            }
        }

        [Test]
        public void Clear_HidesFrameRangeSliderContainer()
        {
            var view = CreateAndAddView();

            view.Clear();
            simulate.FrameUpdate();

            var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);
            Assert.That(frameRangeSlider.ClassListContains(TracingWindowUssClasses.Hidden), Is.True);

            view.Dispose();
        }

        [Test]
        public void Clear_HidesTickSelectorContainer()
        {
            var view = CreateAndAddView();

            view.Clear();
            simulate.FrameUpdate();

            var tickSelectorContainer = rootVisualElement.Q<VisualElement>(TimelineView.k_TickSelectorContainer);
            Assert.That(tickSelectorContainer.ClassListContains(TracingWindowUssClasses.Hidden), Is.True);

            view.Dispose();
        }


        [Test]
        public void WhenPlayModeStateChanges_ToExitingEditMode_ThenRangeSliderLimitsAreReset()
        {
            var view = CreateAndAddView();
            var rangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);

            // Set initial limits (non-zero)
            rangeSlider.highLimit = 100f;
            rangeSlider.lowLimit = 10f;

            InvokePlayModeChanged(view, PlayModeStateChange.ExitingEditMode);
            simulate.FrameUpdate();

            Assert.That(rangeSlider.lowLimit, Is.EqualTo(0f), "Range slider lowLimit should be reset to 0");
            Assert.That(rangeSlider.highLimit, Is.EqualTo(0f), "Range slider highLimit should be reset to 0");

            view.Dispose();
        }

        [Test]
        public async Task WhenPlayModeStateChanges_ToExitingEditMode_ThenSelectedTickIDIsReset()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3, withTicks: true);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);
                await view.OnSelectedFrameChanged(new FrameID { value = 1 });
                await view.OnSelectedTickChanged(data.ClientWorldData.PerFrameData[new FrameID { value = 1 }].TickIDs[0]);

                InvokePlayModeChanged(view, PlayModeStateChange.ExitingEditMode);
                simulate.FrameUpdate();

                // After reset, tick selector should be cleared
                var tickSelector = rootVisualElement.Q<ToggleButtonGroup>(TimelineView.k_TickSelector);
                Assert.That(tickSelector.Children().ToList().Count, Is.EqualTo(1),
                    "Tick selector should be cleared after reset only 1 button remains (the placeholder)");
            });
        }

        [Test]
        public void WhenPlayModeStateChanges_ToExitingEditMode_ThenRootVisibilityIsSet()
        {
            var view = CreateAndAddView();
            var root = rootVisualElement.Q(className: TimelineUssClasses.Base);

            TracingWindowUtility.SetHidden(root, false);
            simulate.FrameUpdate();

            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.False,
                "Precondition: root should not be visible");

            InvokePlayModeChanged(view, PlayModeStateChange.ExitingEditMode);
            simulate.FrameUpdate();

            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.True,
                "Root should be hidden after ExitingEditMode");

            view.Dispose();
        }

        [Test]
        public async Task WhenPlayModeStateChanges_ToExitingEditMode_ThenSubsequentUnpause_HidesRoot()
        {
            // Verifies ExitingEditMode subscribes the pause handler so that a later Unpaused
            // call (simulated directly) still applies the correct hidden behaviour.
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);
                var root = rootVisualElement.Q(className: TimelineUssClasses.Base);
                Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.False,
                    "Precondition: root should be visible after data is available");

                // ExitingEditMode registers the pause handler AND hides the root.
                // Restore visibility to isolate the Unpaused step.
                InvokePlayModeChanged(view, PlayModeStateChange.ExitingEditMode);
                root?.RemoveFromClassList(TracingWindowUssClasses.Hidden);
                simulate.FrameUpdate();

                InvokePauseStateChanged(view, PauseState.Unpaused);
                simulate.FrameUpdate();

                Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.True,
                    "Root should be hidden after Unpaused");
            });
        }

        [Test]
        public async Task WhenPlayModeStateChanges_ToExitingPlayMode_ThenUnpause_DoesNotHideRoot()
        {
            // Verifies ExitingPlayMode unsubscribes the pause handler so a later Unpaused
            // no longer affects the root.
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);
                var root = rootVisualElement.Q(className: TimelineUssClasses.Base);

                // Subscribe the pause handler via ExitingEditMode, then immediately
                // unsubscribe it via ExitingPlayMode.
                InvokePlayModeChanged(view, PlayModeStateChange.ExitingEditMode);
                InvokePlayModeChanged(view, PlayModeStateChange.ExitingPlayMode);

                // Restore visibility so we can see whether Unpaused changes it.
                root?.RemoveFromClassList(TracingWindowUssClasses.Hidden);
                simulate.FrameUpdate();

                // Fire the real EditorApplication event; the handler should NOT be invoked.
                FireEditorPauseStateChanged(PauseState.Unpaused);
                simulate.FrameUpdate();

                Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.False,
                    "Root should remain visible — pause handler should have been unsubscribed");
            });
        }


        [Test]
        public async Task WhenPauseStateChanges_ToUnpausedAndTracingEnabled_ThenRootIsHidden()
        {
            var view = CreateAndAddView();
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3);

            await view.OnDataAvailable(data);
            var root = rootVisualElement.Q(className: TimelineUssClasses.Base);
            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.False,
                "Precondition: root should be visible");

            TracingDataAccess.Config.Data.EnableTracing = true;
            InvokePauseStateChanged(view, PauseState.Unpaused);
            simulate.FrameUpdate();

            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.True,
                "Root should be hidden after Unpaused");

            data.ClientWorldData.Dispose();
            data.ServerWorldData.Dispose();
            view.Dispose();
        }

        [Test]
        public async Task WhenPauseStateChanges_ToUnpaused_AndTracingIsDisabled_ThenRootVisibilityIsUnchanged()
        {
            // The dead else-if block for PauseState.Unpaused was removed; when tracing is disabled
            // and the editor unpauses, OnPauseStateChanged should be a no-op for root visibility.
            var view = CreateAndAddView();
            var data = CreateTracingData(firstFrame: 1, lastFrame: 3);

            await view.OnDataAvailable(data);
            var root = rootVisualElement.Q(className: TimelineUssClasses.Base);

            // Make root visible and confirm the precondition.
            root?.RemoveFromClassList(TracingWindowUssClasses.Hidden);
            simulate.FrameUpdate();
            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.False,
                "Precondition: root should be visible before Unpaused");

            // Unpausing with tracing disabled must NOT hide the root.
            TracingDataAccess.Config.Data.EnableTracing = false;
            InvokePauseStateChanged(view, PauseState.Unpaused);
            simulate.FrameUpdate();

            Assert.That(root?.ClassListContains(TracingWindowUssClasses.Hidden), Is.False,
                "Root should remain visible when unpausing with tracing disabled");

            data.ClientWorldData.Dispose();
            data.ServerWorldData.Dispose();
            view.Dispose();
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        static void InvokePlayModeChanged(TimelineView view, PlayModeStateChange state)
        {
            var method = typeof(TimelineView).GetMethod(
                "OnPlayModeChanged",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method, "OnPlayModeChanged method not found via reflection");
            method.Invoke(view, new object[] { state });
        }

        static void InvokePauseStateChanged(TimelineView view, PauseState state)
        {
            var method = typeof(TimelineView).GetMethod(
                "OnPauseStateChanged",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method, "OnPauseStateChanged method not found via reflection");
            method.Invoke(view, new object[] { state });
        }

        /// <summary>
        /// Fires <see cref="EditorApplication.pauseStateChanged"/> via its backing delegate field so that
        /// subscribed handlers receive the event — used to verify subscribe/unsubscribe behaviour.
        /// </summary>
        static void FireEditorPauseStateChanged(PauseState state)
        {
            // The C# compiler stores the backing field under the same name as the event.
            var field = typeof(EditorApplication).GetField(
                "pauseStateChanged",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            if (field == null) return; // graceful skip if Unity changes the implementation

            var del = field.GetValue(null) as System.Action<PauseState>;
            del?.Invoke(state);
        }

        [Test]
        public async Task WhenRangeSliderChangesWithFloatValues_ThenMarkersAreHighlightedBasedOnFlooredIntegers()
        {
            var data = CreateTracingData(firstFrame: 1, lastFrame: 9, withFrameDiffs: true);
            await WrapWithTryCatch(data, async (view) =>
            {
                await view.OnDataAvailable(data);

                var frameRangeSlider = rootVisualElement.Q<MinMaxSlider>(TimelineView.k_FrameRangeSlider);

                // Set range slider to float values (3.7, 6.2) which should floor to (3, 6)
                frameRangeSlider.value = new UnityEngine.Vector2(3.7f, 6.2f);
                simulate.FrameUpdate();

                var markers = frameRangeSlider.Query(className: TimelineUssClasses.FrameSelectorFrameMarker).ToList();

                // With withFrameDiffs: true, only even frames (2, 4, 6, 8) have diff markers created.
                // After flooring range to [3, 6]:
                // - markers[0] (frame 2): 2 < 3, should be inactive
                // - markers[1] (frame 4): 3 <= 4 <= 6, should be active
                // - markers[2] (frame 6): 3 <= 6 <= 6, should be active
                // - markers[3] (frame 8): 8 > 6, should be inactive

                Assert.That(markers.Count, Is.EqualTo(4), "Should have 4 markers for even frames 2,4,6,8");

                Assert.That(markers[0].ClassListContains(TimelineUssClasses.FrameSelectorFrameMarkerInactive), Is.True,
                    "Frame 2: should be inactive (before floored range [3-6])");

                Assert.That(markers[1].ClassListContains(TimelineUssClasses.FrameSelectorFrameMarkerInactive), Is.False,
                    "Frame 4: should be active (within floored range [3-6])");

                Assert.That(markers[2].ClassListContains(TimelineUssClasses.FrameSelectorFrameMarkerInactive), Is.False,
                    "Frame 6: should be active (within floored range [3-6])");

                Assert.That(markers[3].ClassListContains(TimelineUssClasses.FrameSelectorFrameMarkerInactive), Is.True,
                    "Frame 8: should be inactive (after floored range [3-6])");
            });
        }
        // Creates TracingData with a single frame holding the given ticks, replicating how
        // TracingDataSingleton fills FrameData (TickIDs and PerTickData populated together, in
        // capture order). Caller is responsible for disposing.
        static TracingData CreateTracingDataWithTicks(int frame, uint[] ticks)
        {
            var clientWorldData = new WorldData(Allocator.Persistent);
            var serverWorldData = new WorldData(Allocator.Persistent);

            var frameId = new FrameID { value = frame };
            var frameData = new FrameData(0.016f);
            // ServerTickFraction of 1 marks the ticks as full: a default NetworkTime would read as
            // partial and change the button labels.
            var fullTickTime = new NetworkTime { ServerTickFraction = 1f };
            foreach (var tick in ticks)
            {
                var tickId = new TickID { value = new NetworkTick(tick) };
                frameData.TickIDs.Add(tickId);
                frameData.PerTickData.Add(tickId, new TickData(0.016f, fullTickTime, TraceType.Default));
            }

            clientWorldData.FrameIDs.Add(frameId);
            clientWorldData.PerFrameData.Add(frameId, frameData);

            return new TracingData
            {
                ClientWorldData = clientWorldData,
                ServerWorldData = serverWorldData
            };
        }

        [Test]
        public async Task TickSelector_ButtonsFollowTickIDsOrder_AndClickSelectsTheLabeledTick()
        {
            var ticks = new uint[] { 512, 505, 509, 506, 511, 507, 510, 508 };
            var data = CreateTracingDataWithTicks(frame: 637, ticks);
            await WrapWithTryCatch(data, async view =>
            {
                await view.OnDataAvailable(data);
                await view.OnSelectedFrameChanged(new FrameID { value = 637 });
                simulate.FrameUpdate();

                var tickSelector = rootVisualElement.Q<ToggleButtonGroup>(TimelineView.k_TickSelector);
                var buttons = tickSelector.Children().OfType<Button>().ToList();
                Assert.That(buttons.Count, Is.EqualTo(ticks.Length));
                for (var i = 0; i < buttons.Count; i++)
                {
                    Assert.That(buttons[i].text, Is.EqualTo($"Tick {ticks[i]}"),
                        "tick buttons must follow the TickIDs (capture) order");
                }

                TracingSelectionChange received = default;
                view.OnTracingSelectionChange += (_, change) => received = change;

                // "Click" the last button, labeled "Tick 508".
                tickSelector.value = new ToggleButtonGroupState(1UL << (buttons.Count - 1), buttons.Count);
                simulate.FrameUpdate();

                Assert.That(received.tickID.value.TickIndexForValidTick, Is.EqualTo(ticks[^1]),
                    "clicking a tick button must select the tick it is labeled with");
            });
        }

        async Task WrapWithTryCatch(TracingData data, Func<TimelineView, Task> action)
        {
            var view = CreateAndAddView();
            Exception err = null;
            try
            {
                var filters = new TracingViewFilters();
                view.SetFilters(filters);
                await action(view);
            }
            catch (Exception ex)
            {
                err = ex;
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
                view.Dispose();
                if (err != null)
                    throw err;
            }
        }
    }
}

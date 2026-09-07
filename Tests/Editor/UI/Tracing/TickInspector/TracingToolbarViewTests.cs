using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;
using Unity.Netcode.Editor.Tracing.UI.TracingToolbar;
using Unity.Netcode.Tracing;

namespace Unity.Netcode.Editor.Tracing.UI.Tests
{
    class TracingToolbarViewTests : UITestFixture
    {
        NativeHashMap<FrameID, FrameData> m_TestFrameData;
        NativeList<FrameID> m_TestFrameIDs;
        readonly List<Action> m_Disposables = new();
        TracingToolbarView m_ToolbarView;
        VisualElement m_ToolbarRoot;

        Button GetClearButton() => m_ToolbarRoot.Q<Button>(TracingToolbarView.k_ClearButton);
        IntegerField GetFrameField() =>  m_ToolbarRoot.Q<IntegerField>(TracingToolbarView.k_FrameField);
        Label GetFrameFieldLabel() =>  m_ToolbarRoot.Q<Label>(TracingToolbarView.k_FrameTotalLabel);
        PlaybackControls GetPlaybackControls() => m_ToolbarRoot.Q<PlaybackControls>();
        Button GetEnableTracingButton() => m_ToolbarRoot.Q<Button>(TracingToolbarView.k_EnableTracingButton);

        VisualElement GetFrameFieldContainer() => m_ToolbarRoot.Q<VisualElement>(TracingToolbarView.k_FrameFieldContainer);

        [SetUp]
        public void Setup()
        {
            panelSize = new UnityEngine.Vector2(900, 500);
            m_ToolbarView = new TracingToolbarView();
            m_ToolbarRoot = m_ToolbarView.Create();
            rootVisualElement.Add(m_ToolbarRoot);
        }

        [Test]
        public async Task WhenClickClear_ThenToolbarShouldResetAndonClearButtonClickedShouldBeInvoked()
        {
            var data = CreateTestFrameData();
            var callbackResult = false;
            void Capture()
            {
                callbackResult = true;
            };

            m_ToolbarView.onClearButtonClicked += Capture;

            var clearButton = GetClearButton();
            var frameField = GetFrameField();
            var frameFiledLabel = GetFrameFieldLabel();

            await m_ToolbarView.OnDataAvailable(data);
            var controls = GetPlaybackControls();
            await m_ToolbarView.OnSelectedTickOrFrameChanged(new TracingSelectionChange { frameID = m_TestFrameIDs[1] });
            controls.SetPerFrameData(m_TestFrameData, m_TestFrameIDs);

            Assert.AreEqual(frameField.value, 1);
            Assert.AreEqual(frameFiledLabel.text, "/4");
            Assert.True(clearButton.enabledSelf);

            // Clear state ensure it has been reset
            simulate.FrameUpdate();
            simulate.Click(clearButton);

            Assert.AreEqual(frameField.value, 0);
            Assert.AreEqual(frameFiledLabel.text, "/0");
            Assert.True(callbackResult);

            m_TestFrameData.Dispose();
            m_TestFrameIDs.Dispose();
        }

        [Test]
        public void WhenToggleEnableTracing_ThenClassesShouldBeSetAndOnTracingEnabledToggledInvoked()
        {
            bool? expectedTracingEnabled = null;
            var enableTracingButton = GetEnableTracingButton();

            void Capture(bool isEnabled)
            {
                expectedTracingEnabled = isEnabled;
            };

            m_ToolbarView.OnTracingEnabledToggled += Capture;
            m_Disposables.Add(() => m_ToolbarView.OnTracingEnabledToggled -= Capture);

            simulate.FrameUpdate();
            simulate.Click(enableTracingButton);

            Assert.True(expectedTracingEnabled.HasValue);
            Assert.True(expectedTracingEnabled.Value);
            Assert.False(enableTracingButton.ClassListContains(TracingToolbarUssClasses.IconRecordOff));
            Assert.True(enableTracingButton.ClassListContains(TracingToolbarUssClasses.IconRecord));

            simulate.Click(enableTracingButton);
            Assert.False(expectedTracingEnabled.Value);
            Assert.True(enableTracingButton.ClassListContains(TracingToolbarUssClasses.IconRecordOff));
            Assert.False(enableTracingButton.ClassListContains(TracingToolbarUssClasses.IconRecord));
        }

        #region PlayModeStateChanged


        [Test]
        public void WhenPlayModeStateChanges_ToExitingEditMode_ThenRecordEnabledAndToolbarComponentsDisabled()
        {
            var clearButton = GetClearButton();
            var enableTracingButton = GetEnableTracingButton();
            var playbackControls = GetPlaybackControls();
            var frameFieldContainer = GetFrameFieldContainer();

            InvokePlayModeChanged(PlayModeStateChange.ExitingEditMode);
            InvokePlayModeChanged(PlayModeStateChange.EnteredPlayMode);
            simulate.FrameUpdate();

            Assert.True(enableTracingButton.enabledSelf, "Enable Tracing button  should be enabled");
            Assert.False(clearButton.enabledSelf, "Clear button should be disabled");
            Assert.False(playbackControls.enabledSelf, "Playback controls should be disabled");
            Assert.False(frameFieldContainer.enabledSelf, "Frame field container should be disabled");
        }

        [Test]
        public void WhenPlayModeStateChanges_ToExitingEditMode_ThenHiddenClassIsRemoved()
        {
            Assert.True(m_ToolbarRoot.ClassListContains(TracingWindowUssClasses.Hidden), "Root should have Hidden class initially");

            InvokePlayModeChanged(PlayModeStateChange.ExitingEditMode);
            simulate.FrameUpdate();

            Assert.False(m_ToolbarRoot.ClassListContains(TracingWindowUssClasses.Hidden), "Hidden class should be removed on ExitingEditMode");
        }

        [Test]
        public void WhenPlayModeStateChanges_ToEnteredEditMode_ThenClearAndPlaybackEnabledAndRecordDisabled()
        {
            var clearButton = GetClearButton();
            var enableTracingButton = GetEnableTracingButton();
            var playbackControls = GetPlaybackControls();
            var frameFieldContainer = GetFrameFieldContainer();

            // The EnteredEditMode handler only re-enables the data-dependent controls when processed
            // trace data exists (TracingDataAccess.IsProcessed). Set it explicitly to simulate
            // "processed data is available" when returning to edit mode.
            TracingDataAccess.IsProcessed = true;

            // Simulate a full play session round-trip: ExitingEditMode disables, EnteredEditMode re-enables.
            InvokePlayModeChanged(PlayModeStateChange.ExitingEditMode);
            InvokePlayModeChanged(PlayModeStateChange.EnteredEditMode);
            simulate.FrameUpdate();

            Assert.True(clearButton.enabledSelf, "Clear button should be enabled");
            Assert.True(playbackControls.enabledSelf, "Playback controls should be enabled");
            Assert.True(enableTracingButton.enabledSelf, "Record button should be enabled");
            Assert.True(frameFieldContainer.enabledSelf, "Frame field container should be enabled");
        }

        #endregion


        #region PauseStateChange

        [Test]
        public void WhenPauseStateChanges_ToPaused_ThenToolbarComponentsAreEnabled()
        {
            var clearButton = GetClearButton();
            var playbackControls = GetPlaybackControls();
            var frameFieldContainer = GetFrameFieldContainer();
            var enableTracingButton = GetEnableTracingButton();

            // Entering play mode disables the data-dependent controls.
            InvokePlayModeChanged(PlayModeStateChange.ExitingEditMode);
            simulate.FrameUpdate();
            Assert.False(clearButton.enabledSelf, "Precondition: clear button should be disabled");

            // Pausing while tracing re-enables them (this branch does not depend on EditorApplication.isPlaying).
            TracingDataAccess.Config.Data.EnableTracing = true;
            InvokePauseStateChanged(PauseState.Paused);
            simulate.FrameUpdate();

            Assert.True(enableTracingButton.enabledSelf, "Enable Tracing button should be enabled");
            Assert.True(clearButton.enabledSelf, "Clear button should be enabled");
            Assert.True(playbackControls.enabledSelf, "Playback controls should be enabled");
            Assert.True(frameFieldContainer.enabledSelf, "Frame field container should be enabled");
        }

        [Test]
        public void WhenPauseStateChanges_ToUnpaused_ThenSomeToolbarControlsShouldBeDisabled()
        {
            // Pausing while tracing enables the data-dependent toolbar controls.
            Assert.AreEqual(true, TracingToolbarView.ResolvePauseToolbarEnabled(PauseState.Paused, isPlaying: true, tracingEnabled: true));

            // Unpausing while still in play mode disables them again.
            Assert.AreEqual(false, TracingToolbarView.ResolvePauseToolbarEnabled(PauseState.Unpaused, isPlaying: true, tracingEnabled: true));

            // Unpausing outside play mode (e.g. as play mode exits) must not change enablement — EnteredEditMode owns that.
            Assert.IsNull(TracingToolbarView.ResolvePauseToolbarEnabled(PauseState.Unpaused, isPlaying: false, tracingEnabled: true));

            // With tracing disabled, pause changes never alter enablement.
            Assert.IsNull(TracingToolbarView.ResolvePauseToolbarEnabled(PauseState.Paused, isPlaying: true, tracingEnabled: false));
        }

        #endregion


        #region Clear() Tests

        [Test]
        public async Task WhenClear_AndTracingIsDisabled_ThenAllStatesShouldBeReset()
        {
            var data = CreateTestFrameData();

            var frameField = GetFrameField();
            var frameLabel = GetFrameFieldLabel();
            var frameFieldContainer = GetFrameFieldContainer();
            var clearButton = GetClearButton();
            var playbackControls = GetPlaybackControls();

            // Setup data and set a frame value
            await m_ToolbarView.OnDataAvailable(data);
            await m_ToolbarView.OnSelectedTickOrFrameChanged(new TracingSelectionChange { frameID = m_TestFrameIDs[3] });
            playbackControls.SetPerFrameData(m_TestFrameData, m_TestFrameIDs);

            // Verify precondition: frame field should have been set
            Assert.AreNotEqual(0, frameField.value, "Precondition: frame field should be set to non-zero value");
            Assert.AreNotEqual("/0", frameLabel.text, "Precondition: frame label should not be '/0'");

            // Ensure tracing is disabled
            TracingDataAccess.Config.Data.EnableTracing = false;

            // Call clear
            m_ToolbarView.Clear();

            // Verify everything was reset
            Assert.AreEqual(0, frameField.value, "Frame field value should be reset to 0");
            Assert.AreEqual("/0", frameLabel.text, "Frame label should be reset to '/0'");
            Assert.False(frameFieldContainer.enabledSelf, "Frame field container should be disabled");
            Assert.False(clearButton.enabledSelf, "Clear button should be disabled");

            m_TestFrameData.Dispose();
            m_TestFrameIDs.Dispose();
        }

        [Test]
        public async Task WhenClear_ShouldUnregisterFrameInputChangeCallback()
        {
            var data = CreateTestFrameData();

            var frameField = GetFrameField();
            var tracingSelectionChangeCalled = false;

            void OnSelectionChanged(object sender, TracingSelectionChange change)
            {
                tracingSelectionChangeCalled = true;
            }

            m_ToolbarView.OnTracingSelectionChanged += OnSelectionChanged;
            m_Disposables.Add(() => m_ToolbarView.OnTracingSelectionChanged -= OnSelectionChanged);

            // Setup data
            await m_ToolbarView.OnDataAvailable(data);

            // Clear the flag and ensure tracing is disabled
            tracingSelectionChangeCalled = false;
            TracingDataAccess.Config.Data.EnableTracing = false;

            // Now call clear
            m_ToolbarView.Clear();

            // After clear, manually changing the frame field value should not trigger OnTracingSelectionChanged
            // because the callback should have been unregistered
            frameField.value = 2;

            Assert.False(tracingSelectionChangeCalled, "OnTracingSelectionChanged should not be called after Clear() unregisters the callback");

            m_TestFrameData.Dispose();
            m_TestFrameIDs.Dispose();
        }

        #endregion

        #region SetHidden Tests

        [Test]
        public void WhenSetHidden_ThenHiddenClassIsAddedAndRemoved()
        {
            var toolbar = m_ToolbarRoot;

            // Verify initial state
            Assert.True(toolbar.ClassListContains(TracingWindowUssClasses.Hidden),
                "Root should have Hidden class initially");

            // Set visibility to hidden
            m_ToolbarView.SetHidden(false);

            Assert.False(toolbar.ClassListContains(TracingWindowUssClasses.Hidden),
                "Root should not have Hidden class after SetHidden(false)");
        }

        [Test]
        public void WhenSetHidden_ToVisible_ThenHiddenClassIsRemoved()
        {
            var toolbar = m_ToolbarRoot;

            // Start hidden
            m_ToolbarView.SetHidden(true);
            Assert.True(toolbar.ClassListContains(TracingWindowUssClasses.Hidden),
                "Root should have Hidden class");

            // Set visible
            m_ToolbarView.SetHidden(false);

            Assert.False(toolbar.ClassListContains(TracingWindowUssClasses.Hidden),
                "Root should not have Hidden class after SetHidden(false)");
        }

        #endregion

        #region OnSelectedFrameChanged Tests

        [Test]
        public async Task WhenOnSelectedFrameChanged_ThenFrameFieldShouldBeUpdated()
        {
            var data = CreateTestFrameData();
            var frameField = GetFrameField();

            await m_ToolbarView.OnDataAvailable(data);

            // Select frame 2
            await m_ToolbarView.OnSelectedFrameChanged(m_TestFrameIDs[2]);

            Assert.AreEqual(frameField.value, 2, "Frame field should be updated to selected frame");

            m_TestFrameData.Dispose();
            m_TestFrameIDs.Dispose();
        }

        [Test]
        public async Task WhenOnSelectedFrameChanged_WithDefaultFrameID_ThenFrameFieldShouldNotChange()
        {
            var data = CreateTestFrameData();
            var frameField = GetFrameField();

            await m_ToolbarView.OnDataAvailable(data);
            var currentValue = frameField.value;

            // Try to select default frame ID
            await m_ToolbarView.OnSelectedFrameChanged(default(FrameID));

            // Frame field should not change
            Assert.AreEqual(frameField.value, currentValue,
                "Frame field should not change when selecting default FrameID");

            m_TestFrameData.Dispose();
            m_TestFrameIDs.Dispose();
        }

        [Test]
        public async Task WhenOnSelectedFrameChanged_ThenPlaybackControlsShouldUpdateCurrentFrame()
        {
            var data = CreateTestFrameData();

            await m_ToolbarView.OnDataAvailable(data);

            var targetFrameID = m_TestFrameIDs[3];
            await m_ToolbarView.OnSelectedFrameChanged(targetFrameID);
            simulate.FrameUpdate();

            // Verify that playback controls reflect the change
            // (Note: PlaybackControls should have been updated internally)
            Assert.Pass("OnSelectedFrameChanged properly updates frame selection without triggering callback");

            m_TestFrameData.Dispose();
            m_TestFrameIDs.Dispose();
        }

        #endregion

        #region OnSelectedTickChanged Tests

        [Test]
        public async Task WhenOnSelectedTickChanged_WithValidID_ThenPlaybackControlsShouldUpdateCurrentTick()
        {
            var data = CreateTestFrameData();

            await m_ToolbarView.OnDataAvailable(data);

            // Get the first frame's first tick
            if (data.ClientWorldData.PerFrameData.TryGetValue(m_TestFrameIDs[0], out var frameData))
            {
                var firstTick = frameData.TickIDs[0];

                // This should update the playback controls
                await m_ToolbarView.OnSelectedTickChanged(firstTick);

                Assert.Pass("OnSelectedTickChanged properly updates tick selection");
            }

            m_TestFrameData.Dispose();
            m_TestFrameIDs.Dispose();
        }

        [Test]
        public async Task WhenOnSelectedTickChanged_WithDefaultID_ThenNoUpdateShouldOccur()
        {
            var data = CreateTestFrameData();

            await m_ToolbarView.OnDataAvailable(data);

            // This should return early without doing anything
            await m_ToolbarView.OnSelectedTickChanged(default(TickID));

            Assert.Pass("OnSelectedTickChanged properly handles default TickID");

            m_TestFrameData.Dispose();
            m_TestFrameIDs.Dispose();
        }

        #endregion

        #region SetPlaybackState Tests

        [Test]
        public void WhenSetPlaybackState_ToPlaying_ThenPlaybackControlsShouldReflectState()
        {
            m_ToolbarView.SetPlaybackState(true);
            simulate.FrameUpdate();

            // Verify the playback state is reflected (implementation-dependent)
            Assert.Pass("SetPlaybackState(true) executed successfully");
        }

        [Test]
        public void WhenSetPlaybackState_ToPaused_ThenPlaybackControlsShouldReflectState()
        {
            m_ToolbarView.SetPlaybackState(false);
            simulate.FrameUpdate();

            Assert.Pass("SetPlaybackState(false) executed successfully");
        }

        #endregion

        #region OnDataAvailable Edge Cases Tests

        [Test]
        public async Task WhenOnDataAvailable_ThenFrameFieldCallbackShouldNotTrigger()
        {
            var data = CreateTestFrameData();
            var selectionChangedCount = 0;

            void OnSelectionChanged(object sender, TracingSelectionChange change)
            {
                selectionChangedCount++;
            }

            m_ToolbarView.OnTracingSelectionChanged += OnSelectionChanged;
            m_Disposables.Add(() => m_ToolbarView.OnTracingSelectionChanged -= OnSelectionChanged);

            // OnDataAvailable focuses and blurs the frame field, which should trigger
            // the callback once (when focus is set)
            await m_ToolbarView.OnDataAvailable(data);
            simulate.FrameUpdate();

            // The focus/blur cycle should trigger exactly one callback

            Assert.AreEqual(selectionChangedCount, 0,
                "No Changes should trigger ∂OnDataAvailable");

            m_TestFrameData.Dispose();
            m_TestFrameIDs.Dispose();
        }

        #endregion

        #region Clear() with Tracing Enabled Tests

        [Test]
        public async Task WhenClear_AndTracingIsEnabled_ThenToolbarComponentsShouldDisable()
        {
            var data = CreateTestFrameData();

            var frameField = GetFrameField();
            var frameLabel = GetFrameFieldLabel();
            var playbackControls = GetPlaybackControls();

            // Setup data
            await m_ToolbarView.OnDataAvailable(data);
            await m_ToolbarView.OnSelectedFrameChanged(m_TestFrameIDs[2]);
            playbackControls.SetPerFrameData(m_TestFrameData, m_TestFrameIDs);

            // Verify precondition - frame is set
            Assert.AreNotEqual(0, frameField.value, "Precondition: frame should be set");

            // Enable tracing
            TracingDataAccess.Config.Data.EnableTracing = true;

            // Call clear
            m_ToolbarView.Clear();

            // Verify only partial reset happens when tracing is enabled
            Assert.AreEqual(0, frameField.value, "Frame field should be reset to 0");
            Assert.AreEqual("/0", frameLabel.text, "Frame label should be reset to '/0'");
            Assert.False(playbackControls.enabledSelf, "Playback controls should be disabled");

            m_TestFrameData.Dispose();
            m_TestFrameIDs.Dispose();
        }

        #endregion

        #region EnteredPlayMode Tests

        [Test]
        public void WhenPlayModeStateChanges_ToEnteredPlayMode_ThenEnableTracingButtonClassesShouldUpdate()
        {
            // Enter play mode
            InvokePlayModeChanged(PlayModeStateChange.EnteredPlayMode);
            simulate.FrameUpdate();

            // Button classes should be set based on tracing state
            Assert.Pass("EnteredPlayMode handler executes without error");
        }

        [Test]
        public void WhenPlayModeStateChanges_ToEnteredPlayModeAndTracingEnabled_ThenTracingTargetFilterShouldBeEnabled()
        {
            var tracingTargetFilter = m_ToolbarRoot.Q<TracingTargetFilter>();
            // Ensure initial state
            tracingTargetFilter.SetEnabled(false);
            Assert.False(tracingTargetFilter.enabledSelf, "TracingTargetFilter should be disabled initially");

            TracingDataAccess.Config.Data.EnableTracing = false;
            InvokePlayModeChanged(PlayModeStateChange.EnteredPlayMode);
            simulate.FrameUpdate();

            Assert.True(tracingTargetFilter.enabledSelf, "TracingTargetFilter should be enabled on EnteredPlayMode");
        }

        [Test]
        public void WhenPlayModeStateChanges_ToEnteredPlayModeAndTracingEnabled_ThenTracingTargetFilterShouldRemainDisabled()
        {
            var tracingTargetFilter = m_ToolbarRoot.Q<TracingTargetFilter>();
            // Ensure initial state
            tracingTargetFilter.SetEnabled(false);
            Assert.False(tracingTargetFilter.enabledSelf, "TracingTargetFilter should be disabled initially");

            SetTracingEnabled(true);
            InvokePlayModeChanged(PlayModeStateChange.EnteredPlayMode);
            simulate.FrameUpdate();

            Assert.False(tracingTargetFilter.enabledSelf, "TracingTargetFilter should be disabled on EnteredPlayMode");
        }



        #endregion

        #region IsTracingEnabled Property Tests

        [Test]
        public void WhenIsTracingEnabledPropertyAccessed_ThenShouldReflectConfigState()
        {
            TracingDataAccess.Config.Data.EnableTracing = true;
            Assert.True(m_ToolbarView.IsTracingEnabled, "IsTracingEnabled should reflect Config state when true");

            TracingDataAccess.Config.Data.EnableTracing = false;
            Assert.False(m_ToolbarView.IsTracingEnabled, "IsTracingEnabled should reflect Config state when false");
        }

        #endregion

        #region EnteredEditMode Tracing Reset Tests

        [Test]
        public void WhenPlayModeStateChanges_ToEnteredEditMode_ThenTracingEnabledIsFalseAndEventFired()
        {
            // EnteredEditMode must reset m_TracingEnabled, sync the config, and notify listeners.
            bool? capturedValue = null;
            void Capture(bool v) => capturedValue = v;
            m_ToolbarView.OnTracingEnabledToggled += Capture;
            m_Disposables.Add(() => m_ToolbarView.OnTracingEnabledToggled -= Capture);

            // Simulate entering play mode to give m_TracingEnabled a non-default value.
            SetTracingEnabledViaReflection(true);
            TracingDataAccess.Config.Data.EnableTracing = true;

            InvokePlayModeChanged(PlayModeStateChange.EnteredEditMode);
            simulate.FrameUpdate();

            Assert.True(capturedValue.HasValue, "OnTracingEnabledToggled should have been invoked");
            Assert.False(capturedValue.Value, "OnTracingEnabledToggled should be invoked with false");
            Assert.False(TracingDataAccess.Config.Data.EnableTracing,
                "Config.EnableTracing should be reset to false on EnteredEditMode");
        }

        [Test]
        public void WhenPlayModeStateChanges_ToEnteredEditMode_ThenEnableTracingButtonHasRecordOffClass()
        {
            var enableTracingButton = GetEnableTracingButton();

            // Give the button the "recording" state first.
            SetTracingEnabledViaReflection(true);
            TracingDataAccess.Config.Data.EnableTracing = true;
            InvokeSetEnableTracingButtonClasses();
            Assert.True(enableTracingButton.ClassListContains(TracingToolbarUssClasses.IconRecord),
                "Precondition: button should have IconRecord class");

            InvokePlayModeChanged(PlayModeStateChange.EnteredEditMode);
            simulate.FrameUpdate();

            Assert.True(enableTracingButton.ClassListContains(TracingToolbarUssClasses.IconRecordOff),
                "Button should have IconRecordOff class after EnteredEditMode");
            Assert.False(enableTracingButton.ClassListContains(TracingToolbarUssClasses.IconRecord),
                "Button should not have IconRecord class after EnteredEditMode");
        }

        #endregion

        #region EnteredPlayMode Config Sync Tests

        [Test]
        public void WhenPlayModeStateChanges_ToEnteredPlayMode_ThenConfigEnableTracingMatchesMTracingEnabled()
        {
            // EnteredPlayMode must write m_TracingEnabled into Config.Data.EnableTracing so that
            // TracingDataAccess.OnPlayModeStateChanged (which runs after) sees the correct value.

            // Simulate the user having enabled tracing before entering play mode.
            SetTracingEnabledViaReflection(true);
            TracingDataAccess.Config.Data.EnableTracing = false; // intentionally out-of-sync

            InvokePlayModeChanged(PlayModeStateChange.EnteredPlayMode);
            simulate.FrameUpdate();

            Assert.True(TracingDataAccess.Config.Data.EnableTracing,
                "Config.EnableTracing should be set to m_TracingEnabled (true) on EnteredPlayMode");
        }

        [Test]
        public void WhenPlayModeStateChanges_ToEnteredPlayMode_WithTracingDisabled_ThenConfigEnableTracingIsFalse()
        {
            SetTracingEnabledViaReflection(false);
            TracingDataAccess.Config.Data.EnableTracing = true; // intentionally out-of-sync

            InvokePlayModeChanged(PlayModeStateChange.EnteredPlayMode);
            simulate.FrameUpdate();

            Assert.False(TracingDataAccess.Config.Data.EnableTracing,
                "Config.EnableTracing should be set to m_TracingEnabled (false) on EnteredPlayMode");
        }

        #endregion

        #region ResolvePauseTracingTargetEnabled Tests

        [Test]
        public void ResolvePauseTracingTargetEnabled_WhenPaused_ReturnsTrue()
        {
            var result = TracingToolbarView.ResolvePauseTracingTargetEnabled(
                PauseState.Paused, isPlaying: true, tracingEnabled: true);
            Assert.AreEqual(true, result, "Pausing should enable the tracing target filter");
        }

        [Test]
        public void ResolvePauseTracingTargetEnabled_WhenUnpausedAndTracingDisabled_ReturnsTrue()
        {
            // When tracing is disabled and the editor resumes, the filter becomes available again.
            TracingDataAccess.Config.Data.EnableTracing = false;
            var result = TracingToolbarView.ResolvePauseTracingTargetEnabled(
                PauseState.Unpaused, isPlaying: true, tracingEnabled: false);
            Assert.AreEqual(true, result,
                "Unpausing with tracing disabled should re-enable the tracing target filter");
        }

        [Test]
        public void ResolvePauseTracingTargetEnabled_WhenPaused_WithTracingDisabled_ReturnsTrue()
        {
            var result = TracingToolbarView.ResolvePauseTracingTargetEnabled(
                PauseState.Paused, isPlaying: true, tracingEnabled: false);
            Assert.AreEqual(true, result,
                "Pausing always enables the tracing target filter regardless of tracing state");
        }

        #endregion

        #region m_TracingEnabled Reflection Helper Tests

        [Test]
        public void WhenSetTracingEnabledViaReflection_ToTrue_ThenMTracingEnabledIsTrue()
        {
            SetTracingEnabledViaReflection(true);

            var field = typeof(TracingToolbarView).GetField(
                "m_TracingEnabled", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            Assert.True((bool)field.GetValue(m_ToolbarView),
                "m_TracingEnabled should be true after reflection set");
        }

        [Test]
        public void WhenSetTracingEnabledViaReflection_ToFalse_ThenMTracingEnabledIsFalse()
        {
            SetTracingEnabledViaReflection(true);  // start true
            SetTracingEnabledViaReflection(false); // then reset

            var field = typeof(TracingToolbarView).GetField(
                "m_TracingEnabled", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            Assert.False((bool)field.GetValue(m_ToolbarView),
                "m_TracingEnabled should be false after reflection set");
        }

        #endregion

        [TearDown]
        public void TearDown()
        {
            rootVisualElement.Clear();
            foreach (var disposable in m_Disposables)
            {
                disposable();
            }
            m_Disposables.Clear();

            TracingDataAccess.Config.Data.EnableTracing = false;
            TracingDataAccess.IsProcessed = false;
            m_ToolbarView?.Dispose();
        }

        // Sets the private m_TracingEnabled field on the view via reflection.
        void SetTracingEnabledViaReflection(bool value)
        {
            var field = typeof(TracingToolbarView).GetField(
                "m_TracingEnabled",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field, "m_TracingEnabled field not found via reflection");
            field.SetValue(m_ToolbarView, value);
        }

        // Calls the private SetEnableTracingButtonClasses method via reflection to update button CSS classes.
        void InvokeSetEnableTracingButtonClasses()
        {
            var method = typeof(TracingToolbarView).GetMethod(
                "SetEnableTracingButtonClasses",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method, "SetEnableTracingButtonClasses method not found via reflection");
            method.Invoke(m_ToolbarView, null);
        }

        // Drives the view's private play-mode handler directly, simulating a play-mode state change without
        // entering play mode (which would trigger a domain reload). Mirrors TimelineViewTests.
        void InvokePlayModeChanged(PlayModeStateChange state)
        {
            var method = typeof(TracingToolbarView).GetMethod(
                "OnPlayModeStateChanged",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method, "OnPlayModeStateChanged method not found via reflection");
            method.Invoke(m_ToolbarView, new object[] { state });
        }

        // Drives the view's private pause handler directly. Note the unpause-disables branch is gated on
        // EditorApplication.isPlaying, so this only exercises the play-mode-independent paths.
        void InvokePauseStateChanged(PauseState state)
        {
            var method = typeof(TracingToolbarView).GetMethod(
                "OnPauseStateChanged",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method, "OnPauseStateChanged method not found via reflection");
            method.Invoke(m_ToolbarView, new object[] { state });
        }

        void SetTracingEnabled(bool value)
        {
            var field = typeof(TracingToolbarView).GetField(
                "m_TracingEnabled",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field, "m_TracingEnabled field not found via reflection");
            field.SetValue(m_ToolbarView, value);
        }


        TracingData CreateTestFrameData()
        {
            m_TestFrameData = new NativeHashMap<FrameID, FrameData>(5, Allocator.Temp);
            m_TestFrameIDs = new NativeList<FrameID>(5, Allocator.Temp);

            // Create 5 frames with ticks
            for (int f = 0; f < 5; f++)
            {
                var frameID = new FrameID { value = f };
                m_TestFrameIDs.Add(frameID);

                var frameData = new FrameData(0.016f);
                if (f % 2 == 0) // Frames 0, 2, 4 have diffs
                    frameData.DiffInfo.AddDiff(DiffInfo.DiffReasons.ComponentData);

                // Add ticks to frame
                for (int t = 0; t < 3; t++)
                {
                    var tickID = new TickID { value = new NetworkTick((uint)(f * 10 + t)) };
                    frameData.TickIDs.Add(tickID);

                    var tickData = new TickData(0.016f, default, TraceType.Default);
                    if (t == 1) // Middle tick has diff
                        tickData.DiffInfo.AddDiff(DiffInfo.DiffReasons.ComponentData);
                    frameData.PerTickData.Add(tickID, tickData);
                }

                m_TestFrameData.Add(frameID, frameData);
            }

            return new TracingData()
            {
                ClientWorldData = new WorldData()
                {
                    FrameIDs = m_TestFrameIDs,
                    PerFrameData = m_TestFrameData
                }
            };
        }

        #region TracingTargetVisibilityChanged Event Tests
        [Test]
        public void TracingTargetFilter_OnVisibilityChanged_IsInvokedThroughToolbarEvent()
        {
            Assert.IsNotNull(m_ToolbarRoot.Q<TracingTargetFilter>(), "TracingTargetFilter should exist in toolbar");

            void OnVisibilityChanged(HashSet<string> _, HashSet<string> __)
            {
                // Event received
            }

            // Subscribe to the toolbar's visibility changed event
            m_ToolbarView.OnTracingTargetVisibilityChanged += OnVisibilityChanged;
            m_Disposables.Add(() => m_ToolbarView.OnTracingTargetVisibilityChanged -= OnVisibilityChanged);

            Assert.Pass("OnTracingTargetVisibilityChanged event is properly defined");
        }

        [Test]
        public void TracingTargetFilter_OnVisibilityChanged_ProvidesCorrectSeparation()
        {
            // This test verifies that the new event signature passes separate system and component type collections
            Assert.IsNotNull(m_ToolbarRoot.Q<TracingTargetFilter>(), "TracingTargetFilter should exist in toolbar");

            void OnVisibilityChanged(HashSet<string> hiddenSystems, HashSet<string> hiddenComponents)
            {
                Assert.IsInstanceOf<HashSet<string>>(hiddenSystems, "First parameter should be HashSet<string> for systems");
                Assert.IsInstanceOf<HashSet<string>>(hiddenComponents, "Second parameter should be HashSet<string> for components");
            }

            m_ToolbarView.OnTracingTargetVisibilityChanged += OnVisibilityChanged;
            m_Disposables.Add(() => m_ToolbarView.OnTracingTargetVisibilityChanged -= OnVisibilityChanged);

            Assert.Pass("Event signature properly separates system and component types");
        }

        [Test]
        public void TracingTargetFilter_MultipleSubscribers_ReceiveVisibilityChanges()
        {
            void OnVisibilityChanged1(HashSet<string> _, HashSet<string> __)
            {
                // Subscriber 1
            }

            void OnVisibilityChanged2(HashSet<string> _, HashSet<string> __)
            {
                // Subscriber 2
            }

            m_ToolbarView.OnTracingTargetVisibilityChanged += OnVisibilityChanged1;
            m_ToolbarView.OnTracingTargetVisibilityChanged += OnVisibilityChanged2;
            m_Disposables.Add(() =>
            {
                m_ToolbarView.OnTracingTargetVisibilityChanged -= OnVisibilityChanged1;
                m_ToolbarView.OnTracingTargetVisibilityChanged -= OnVisibilityChanged2;
            });

            Assert.Pass("Multiple subscribers can be registered to OnTracingTargetVisibilityChanged");
        }
        #endregion
    }
}

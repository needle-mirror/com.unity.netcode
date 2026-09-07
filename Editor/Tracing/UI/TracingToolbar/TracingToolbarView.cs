using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode.Tracing;
using UnityEditor;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor.Tracing.UI.TracingToolbar
{
    class TracingToolbarView : DataObserverView
    {
        const string k_TemplatePath = Constants.Templates + "tracing-toolbar.uxml";
        public const string k_FrameTotalLabel = "frame-total-label";
        public const string k_FrameField = "frame-field";
        public const string k_EnableTracingButton = "record-button";
        public const string k_ClearButton = "clear-button";
        public const string k_FrameFieldContainer = "frame-field-container";

        PlaybackControls m_PlaybackControls;
        IntegerField m_FrameField;
        Label m_FrameTotalLabel;
        Button m_EnableTracingButton;
        Button m_ClearButton;
        VisualElement m_FrameFieldContainer;
        TracingTargetFilter m_TracingTargetFilter;
        bool m_TracingEnabled;

        public bool IsTracingEnabled => TracingDataAccess.Config.Data.EnableTracing;

        #region Actions and events
        public event EventHandler<TracingSelectionChange> OnTracingSelectionChanged;
        public event EventHandler<ReplayRenderingFrequency> OnReplayRenderingFrequencyChanged;
        public event EventHandler<float> OnReplaySpeedChanged;
        public event Action<bool> OnChangedValuesOnlyToggled;
        public event Action<float> OnFuzzyDiffThresholdChanged;
        public event Action<DiffInfo.DiffReasons> OnSelectedDiffReasonsChanged;
        public event Action<bool> OnTracingEnabledToggled;
        public event Action<bool> OnPlaybackToggled;
        public event TracingTargetFilter.TracingVisibilityChangedHandler OnTracingTargetVisibilityChanged;

        public Action onClearButtonClicked;

        #endregion

        protected override string UssClassName => TracingToolbarUssClasses.Base;
        void LoadTemplate()
        {
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(k_TemplatePath);
            visualTree.CloneTree(m_Root);
            SetHidden(!TracingDataAccess.Config.Data.TracingTypesSelected());

            m_FrameField = m_Root.Q<IntegerField>(k_FrameField);
            m_FrameFieldContainer = m_Root.Q<VisualElement>(k_FrameFieldContainer);
            m_EnableTracingButton = m_Root.Q<Button>(k_EnableTracingButton);
            m_PlaybackControls = m_Root.Q<PlaybackControls>();
            m_FrameTotalLabel = m_Root.Q<Label>(k_FrameTotalLabel);
            m_TracingTargetFilter = m_Root.Q<TracingTargetFilter>();
            m_ClearButton = m_Root.Q<Button>(k_ClearButton);
            m_ClearButton.clicked += HandleClickClear;
            m_PlaybackControls.SetEnabled(false);
            m_FrameFieldContainer.SetEnabled(false);
            m_ClearButton.SetEnabled(false);

            m_EnableTracingButton.clicked += HandleClickEnableTracingButton;
            m_PlaybackControls.OnFrameChange += HandlePlaybackControlsFrameChange;
            m_PlaybackControls.OnTickChange += HandlePlaybackControlsTickChange;
            m_PlaybackControls.OnSelectFrequency += HandlePlaybackControlsSelectFrequency;
            m_PlaybackControls.OnChangeReplaySpeed += HandlePlaybackControlsChangeReplaySpeed;
            m_PlaybackControls.OnClickPlay += HandleClickPlay;
            m_TracingTargetFilter.OnVisibilityTargetsChanged += HandleVisibilityTargetsChanged;
            m_TracingTargetFilter.OnSelectedDiffReasonsChanged += HandleSelectedDiffReasonsChanged;
            m_TracingTargetFilter.OnChangedValuesOnlyChanged += HandleChangedValuesOnlyToggled;
            m_TracingTargetFilter.OnFuzzyDiffThresholdChanged += HandleFuzzyDiffThresholdChanged;
        }

        #region Internal handlers
        void HandleVisibilityTargetsChanged(HashSet<string> hiddenSystemType, HashSet<string> hiddenComponentTypes)
        {
            OnTracingTargetVisibilityChanged?.Invoke( hiddenSystemType, hiddenComponentTypes);
        }

        public void SetHidden(bool isHidden)
        {
            TracingWindowUtility.SetHidden(m_Root, isHidden);
        }
        void HandleChangedValuesOnlyToggled(bool changedValuesOnly)
        {
            OnChangedValuesOnlyToggled?.Invoke(changedValuesOnly);
        }

        void HandleFuzzyDiffThresholdChanged(float threshold)
        {
            OnFuzzyDiffThresholdChanged?.Invoke(threshold);
        }

        void HandleSelectedDiffReasonsChanged(DiffInfo.DiffReasons selectedReasons)
        {
            OnSelectedDiffReasonsChanged?.Invoke(selectedReasons);
        }

        void HandlePlaybackControlsTickChange (TickID tickID, FrameID frameID)
        {
            m_FrameField.SetValueWithoutNotify(frameID.value);
            var change = new TracingSelectionChange { tickID = tickID, frameID = frameID };
            OnTracingSelectionChanged?.Invoke(this, change);
        }

        void HandlePlaybackControlsSelectFrequency(ReplayRenderingFrequency frequency)
        {
            OnReplayRenderingFrequencyChanged?.Invoke(this, frequency);
        }

        void HandlePlaybackControlsChangeReplaySpeed(float speed)
        {
            OnReplaySpeedChanged?.Invoke(this, speed);
        }

        void HandleClickPlay(bool isPlaying)
        {
            OnPlaybackToggled?.Invoke(isPlaying);
        }

        // Reflects the actual Scene view playback state on the play button (play vs pause icon).
        public void SetPlaybackState(bool playing) => m_PlaybackControls?.SetPlaybackState(playing);

        // Applies the active diff filters to the next/previous-diff navigation, so filtered-out diffs are skipped.
        public void SetFilters(TracingViewFilters filters)
        {
            m_PlaybackControls?.SetDiffVisibilityResolver(filters == null ? null : diffInfo => filters.HasVisibleDiff(diffInfo));
            if (filters == null)
                return;
            // The tick inspector can also toggle a diff tag, so re-sync the dropdown state.
            m_TracingTargetFilter?.SetSelectedDiffReasons(filters.EnabledDiffReasons);
            m_TracingTargetFilter?.SetChangedValuesOnly(filters.ChangedValuesOnly);
            m_TracingTargetFilter?.SetFuzzyDiffThreshold(filters.FuzzyDiffThreshold);
        }

        void HandlePlaybackControlsFrameChange(FrameID frameID)
        {
            m_FrameField.value = frameID.value;
            var change = new TracingSelectionChange { frameID = frameID, tickID = default};
            OnTracingSelectionChanged?.Invoke(this, change);
        }

        void HandleClickClear()
        {
            Clear();
            onClearButtonClicked?.Invoke();
        }

        void HandleClickEnableTracingButton()
        {
            m_TracingEnabled = !m_TracingEnabled;
            TracingDataAccess.Config.Data.EnableTracing = m_TracingEnabled;
            if (!EditorApplication.isPaused && EditorApplication.isPlaying)
            {
                m_TracingTargetFilter.SetEnabled(!m_TracingEnabled);
            }

            if (!m_TracingEnabled && EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                // Recording just stopped mid-play: there is data to browse, re-enable the toolbar.
                ToggleEnabledToolbarComponents(true);
            }
            SetEnableTracingButtonClasses();
            OnTracingEnabledToggled?.Invoke(TracingDataAccess.Config.Data.EnableTracing);
        }

        void HandleFrameInputChange (ChangeEvent<int> evt)
        {
            var frameID = new FrameID { value = evt.newValue };
            if (m_Data.ClientWorldData.FrameIDs.IsEmpty ||
                evt.newValue < m_Data.ClientWorldData.FrameIDs[0].value ||
                evt.newValue > m_Data.ClientWorldData.FrameIDs[^1].value)
            {
                return;
            }
            m_PlaybackControls.SetCurrentFrame(frameID);
            var change = new TracingSelectionChange { frameID = frameID, tickID = default };
            OnTracingSelectionChanged?.Invoke(this, change);
        }

        #endregion

        void SetEnableTracingButtonClasses()
        {
            if (TracingDataAccess.Config.Data.EnableTracing)
            {
                m_EnableTracingButton.RemoveFromClassList(TracingToolbarUssClasses.IconRecordOff);
                m_EnableTracingButton.AddToClassList(TracingToolbarUssClasses.IconRecord);
            }
            else
            {
                m_EnableTracingButton.RemoveFromClassList(TracingToolbarUssClasses.IconRecord);
                m_EnableTracingButton.AddToClassList(TracingToolbarUssClasses.IconRecordOff);
            }
        }

        public override VisualElement Create()
        {
            m_TracingEnabled = false;
            m_Root.Clear();
            m_Root.AddToClassList(UssClassName);
            LoadTemplate();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.pauseStateChanged += OnPauseStateChanged;
            return m_Root;
        }

        public override Task OnDataAvailable(TracingData data)
        {
            m_Data = data;
            if (data.ClientWorldData.FrameIDs.Length > 0)
            {
                m_FrameField.SetValueWithoutNotify(data.ClientWorldData.FrameIDs[0].value);
                m_FrameField.UnregisterValueChangedCallback(HandleFrameInputChange);
                m_FrameField.RegisterValueChangedCallback(HandleFrameInputChange);

                m_FrameTotalLabel.text = $"/{data.ClientWorldData.FrameIDs[^1].value}";
                m_PlaybackControls.SetPerFrameData(data.ClientWorldData.PerFrameData, data.ClientWorldData.FrameIDs);
                // Focus and blur the frame field to trigger the callback that sets the initial frame selection in the
                // timeline view. This is needed because the initial frame selection is based on the selected frame ID,
                // which is set in OnSelectedFrameChanged, and that callback is only triggered by user interaction with
                // the frame field.
                m_FrameField.Focus();
                m_FrameField.Blur();

                if (!EditorApplication.isPlaying || EditorApplication.isPaused)
                {
                    ToggleEnabledToolbarComponents(true);
                }
            }
            SetHidden(false);
            return Task.CompletedTask;
        }

        #region External FrameID and TickID setters

        protected internal override Task OnSelectedFrameChanged(FrameID frameID)
        {

            if (!frameID.Equals(default(FrameID)))
            {
                m_FrameField.SetValueWithoutNotify(frameID.value);
                m_FrameField.MarkDirtyRepaint();
                m_PlaybackControls.SetCurrentFrame(frameID);
            }
            return Task.CompletedTask;
        }

        protected internal override Task OnSelectedTickChanged(TickID tickID)
        {
            if (tickID.Equals(default(TickID))) return Task.CompletedTask;

            m_PlaybackControls.SetCurrentTick(tickID);
            return Task.CompletedTask;
        }

        #endregion

        void ToggleEnabledToolbarComponents(bool enabled)
        {
            m_PlaybackControls.SetEnabled(enabled);
            m_FrameFieldContainer.SetEnabled(enabled);
            m_TracingTargetFilter.SetEnabled(enabled);
            m_ClearButton.SetEnabled(enabled);
        }

        void OnPauseStateChanged(PauseState state)
        {
            var enabledToolbar = ResolvePauseToolbarEnabled(state, EditorApplication.isPlaying, TracingDataAccess.Config.Data.EnableTracing);
            if (enabledToolbar.HasValue)
                ToggleEnabledToolbarComponents(enabledToolbar.Value);

            var enabledTracingTargetFilterSelector = ResolvePauseTracingTargetEnabled(state, EditorApplication.isPlaying, TracingDataAccess.Config.Data.EnableTracing);
            if (enabledTracingTargetFilterSelector.HasValue)
            {
                m_TracingTargetFilter.SetEnabled(enabledTracingTargetFilterSelector.Value);
            }
        }


        internal static bool? ResolvePauseTracingTargetEnabled(PauseState state, bool isPlaying, bool tracingEnabled)
        {
            if (state == PauseState.Paused)
                return true;
            if (state == PauseState.Unpaused && !TracingDataAccess.Config.Data.EnableTracing)
                return true;
            if (state == PauseState.Unpaused && TracingDataAccess.Config.Data.EnableTracing)
                return false;
            return null;
        }

        internal static bool? ResolvePauseToolbarEnabled(PauseState state, bool isPlaying, bool tracingEnabled)
        {
            if (!tracingEnabled)
                return null;
            if (state == PauseState.Paused)
                return true;
            if (state == PauseState.Unpaused && isPlaying)
                return false;
            return null;
        }

        void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    SetHidden(false);
                    ToggleEnabledToolbarComponents(false);
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    if (TracingDataAccess.IsProcessed)
                    {
                        ToggleEnabledToolbarComponents(true);
                    }
                    m_TracingTargetFilter.SetEnabled(true);
                    m_TracingEnabled = false;
                    TracingDataAccess.Config.Data.EnableTracing = m_TracingEnabled;
                    SetEnableTracingButtonClasses();
                    OnTracingEnabledToggled?.Invoke(TracingDataAccess.Config.Data.EnableTracing);
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    // schedule this to execute after the parent has executed OnPlayModeStateChanged
                    // need due UnityEditor.AssemblyReloadEvents listener in TracingDataAccess.OnPlayModeStateChanged,
                    // which is executed after this method, and sets the EnableTracing to false when entering play mode.
                    // TODO: Apply changes from ticket https://jira.unity3d.com/browse/MTT-15450
                    TracingDataAccess.Config.Data.EnableTracing = m_TracingEnabled;
                    SetEnableTracingButtonClasses();

                    if (EditorApplication.isPaused || !m_TracingEnabled)
                    {
                        m_TracingTargetFilter.SetEnabled(true);
                    }
                    else
                    {
                        m_TracingTargetFilter.SetEnabled(false);
                    }
                    break;
            }
        }

        public override void Clear()
        {
            if (TracingDataAccess.Config.Data.EnableTracing)
            {
                ToggleEnabledToolbarComponents(false);
                m_FrameField.SetValueWithoutNotify(0);
                m_FrameTotalLabel.text = "/0";
                return;
            }

            m_FrameField.SetValueWithoutNotify(0);
            m_FrameTotalLabel.text = "/0";
            m_PlaybackControls?.Reset();
            m_PlaybackControls?.SetEnabled(false);
            m_FrameField.UnregisterValueChangedCallback(HandleFrameInputChange);
            m_FrameFieldContainer.SetEnabled(false);
            m_ClearButton.SetEnabled(false);
        }

        public override void Dispose()
        {
            m_EnableTracingButton.clicked -= HandleClickEnableTracingButton;
            m_PlaybackControls.OnFrameChange -= HandlePlaybackControlsFrameChange;
            m_PlaybackControls.OnTickChange -= HandlePlaybackControlsTickChange;
            m_PlaybackControls.OnSelectFrequency -= HandlePlaybackControlsSelectFrequency;
            m_PlaybackControls.OnChangeReplaySpeed -= HandlePlaybackControlsChangeReplaySpeed;
            m_PlaybackControls.OnClickPlay -= HandleClickPlay;
            m_TracingTargetFilter.OnVisibilityTargetsChanged -= HandleVisibilityTargetsChanged;
            m_TracingTargetFilter.OnSelectedDiffReasonsChanged -= HandleSelectedDiffReasonsChanged;
            m_TracingTargetFilter.OnChangedValuesOnlyChanged -= HandleChangedValuesOnlyToggled;
            m_TracingTargetFilter.OnFuzzyDiffThresholdChanged -= HandleFuzzyDiffThresholdChanged;
            m_ClearButton.clicked -= HandleClickClear;
            m_FrameField.UnregisterValueChangedCallback(HandleFrameInputChange);
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.pauseStateChanged -= OnPauseStateChanged;
        }
    }
}

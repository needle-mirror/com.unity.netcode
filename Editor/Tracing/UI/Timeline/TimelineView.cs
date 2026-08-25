using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.NetCode.Tracing;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI.Timeline
{
    class TimelineView : DataObserverView
    {
        SliderInt m_FrameSlider;
        MinMaxSlider m_RangeSlider;
        ToggleButtonGroup m_TickSelector;
        VisualElement m_FrameSliderBackground;
        VisualElement m_RangeSliderBackground;
        VisualElement m_TickSelectorContainer;
        VisualElement m_FrameSelectorContainer;
        VisualElement m_ReplayIndicator;
        ProgressBar m_ProgressBar;
        VisualElement m_ProgressBarContainer;
        VisualElement m_LoadingSpinnerContainer;
        VisualElement m_LoadingSpinner;
        IVisualElementScheduledItem m_SpinnerSchedule;
        int m_SpinnerFrame;
        const int k_SpinnerFrameCount = 12;
        const long k_SpinnerFrameIntervalMs = 80;
        FrameID m_SelectedFrameID;
        TickID m_SelectedTickID;
        int m_CurrentFrameChangeTaskId;
        public event EventHandler<TracingSelectionChange> OnTracingSelectionChange;

        internal const string k_RawTracesStepTitle = "[Step 1/2] Processing raw traces";
        internal const string k_DiffStepTitle = "[Step 2/2] Processing diffs";
        internal const string k_BuildUIStepTitle = "Building UI";

        const string k_TemplatePath = Constants.Templates + "timeline-view.uxml";
        public const string k_ProgressBarContainer = "progress-bar-container";
        public const string k_ProgressBar = "progress-bar";
        public const string k_FrameSlider = "frame-slider";
        public const string k_FrameRangeSlider = "frame-range-slider";
        public const string k_TickSelector = "tick-selector";
        public const string k_TickSelectorContainer = "tick-selector-container";
        public const string k_FrameSelectorContainer = "frame-selector-container";

        bool HasSelectedFrame => !m_SelectedFrameID.Equals(default);
        CancellationTokenSource m_Cts = new CancellationTokenSource();
        int m_RangeMarkerBuildGeneration;
        TracingViewFilters m_TracingViewFilters;

        protected override string UssClassName => TimelineUssClasses.Base;

        void LoadTemplate()
        {
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(k_TemplatePath);

            if (visualTree == null)
            {
                Debug.LogError("Could not load timeline-view.uxml");
                return;
            }

            visualTree.CloneTree(m_Root);
            m_Root.AddToClassList(UssClassName);
            TracingWindowUtility.SetHidden(m_Root,true);
            m_FrameSlider = m_Root.Q<SliderInt>(k_FrameSlider);
            m_RangeSlider = m_Root.Q<MinMaxSlider>(k_FrameRangeSlider);
            m_TickSelector = m_Root.Q<ToggleButtonGroup>(k_TickSelector);
            m_TickSelectorContainer = m_Root.Q<VisualElement>(k_TickSelectorContainer);
            m_ProgressBar = m_Root.Q<ProgressBar>(k_ProgressBar);
            m_ProgressBarContainer = m_Root.Q<VisualElement>(k_ProgressBarContainer);
            m_FrameSelectorContainer = m_Root.Q<VisualElement>(k_FrameSelectorContainer);
        }

        void OnPauseStateChanged(PauseState state)
        {
            if (state == PauseState.Unpaused && TracingDataAccess.Config.Data.EnableTracing)
            {
                m_Cts?.Cancel();
                TracingWindowUtility.SetHidden(m_Root, true);
            }
        }

        void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                EditorApplication.pauseStateChanged -= OnPauseStateChanged;
            }
            else if (state == PlayModeStateChange.ExitingEditMode)
            {
                m_RangeSlider.lowLimit = 0;
                m_RangeSlider.highLimit = 0;
                EditorApplication.pauseStateChanged += OnPauseStateChanged;
                m_SelectedTickID = default;
                m_SelectedFrameID = default;
                TracingWindowUtility.SetHidden(m_Root, true);
            }
        }

        /// <summary>
        /// Handles changes from the frame slider, updating the selected frame and invoking the <see cref="OnTracingSelectionChange"/> event.
        /// <param name="evt">The change event containing the new frame index.</param>
        /// </summary>
        async void OnFrameChanged(ChangeEvent<int> evt)
        {
            int taskId = ++m_CurrentFrameChangeTaskId;
            m_TickSelector.UnregisterValueChangedCallback(OnTickChanged);
            m_SelectedFrameID = new FrameID() { value = evt.newValue };
            m_SelectedTickID = default;
            UpdateTickSelectorForSelectedFrame();
            await Task.Yield();
            if (taskId != m_CurrentFrameChangeTaskId || m_Cts is { Token: { IsCancellationRequested: true } }) return;
            m_TickSelector.RegisterValueChangedCallback(OnTickChanged);
            var change = new TracingSelectionChange { frameID = m_SelectedFrameID, tickID = m_SelectedTickID };
            OnTracingSelectionChange?.Invoke(this, change);
        }

        void OnFrameRangeChanged(ChangeEvent<Vector2> evt)
        {
            var newLowValue = Mathf.CeilToInt(evt.newValue.x);
            var newHighValue = Mathf.FloorToInt(evt.newValue.y);
            if (m_FrameSlider.lowValue == newLowValue && m_FrameSlider.highValue == newHighValue)
            {
                return;
            }

            m_FrameSlider.lowValue = newLowValue;
            m_FrameSlider.highValue = newHighValue;

            SetDiffMarkersForRangeSliders();
            SetDiffMarkerForFrameSlider();
        }

        void SetDiffMarkersForRangeSliders()
        {
            var startIndex = Mathf.CeilToInt(m_RangeSlider.minValue);
            var endIndex = Mathf.FloorToInt(m_RangeSlider.maxValue);

            foreach (var child in m_RangeSliderBackground.Children())
            {
                var frameId = (FrameID)child.userData;

                child.EnableInClassList(TimelineUssClasses.FrameSelectorFrameMarkerInactive, false);
                if (frameId.value < startIndex || frameId.value > endIndex)
                {
                    child.EnableInClassList(TimelineUssClasses.FrameSelectorFrameMarkerInactive, true);
                }
            }
        }


        // Evaluates the stored diff aggregates against the active filters; no recomputation.
        bool PassesDiffFilters(in DiffInfo diffInfo)
        {
            if (m_TracingViewFilters == null)
                return diffInfo.HasDiff;
            return m_TracingViewFilters.HasVisibleDiff(diffInfo);
        }


        void SetDiffMarkerForFrameSlider()
        {
            if (m_FrameSliderBackground == null)
            {
                var tracker = m_FrameSlider.Q<VisualElement>("unity-tracker");
                m_FrameSliderBackground = new VisualElement();
                m_FrameSliderBackground.AddToClassList(TimelineUssClasses.FrameSelectorBackground);
                tracker.Insert(0, m_FrameSliderBackground);
            }

            m_FrameSliderBackground.Clear();

            var startIndex = m_Data.ClientWorldData.FrameIDs.IndexOf(new FrameID() { value = m_FrameSlider.lowValue });
            var endIndex = m_Data.ClientWorldData.FrameIDs.IndexOf(new FrameID() { value = m_FrameSlider.highValue });

            if (startIndex == -1)
            {
                startIndex = 0;
            }

            float range = m_FrameSlider.highValue - m_FrameSlider.lowValue == 0
                ? 1
                : m_FrameSlider.highValue - m_FrameSlider.lowValue;


            while (startIndex <= endIndex)
            {
                var frameId = m_Data.ClientWorldData.FrameIDs[startIndex];
                var frameData = m_Data.ClientWorldData.PerFrameData[frameId];

                if (PassesDiffFilters(frameData.DiffInfo))
                {
                    float percent = ((frameId.value - m_FrameSlider.lowValue) / range) * 100f;
                    // Create the marker
                    VisualElement marker = new VisualElement { style = { left = Length.Percent(percent) }, userData = frameId.value };
                    marker.AddToClassList(TimelineUssClasses.FrameSelectorFrameMarker);

                    m_FrameSliderBackground.Add(marker);
                }
                startIndex++;
            }
        }

        protected internal override async Task OnSelectedFrameChanged(FrameID frameID)
        {
            if (m_SelectedFrameID.Equals(frameID) || frameID.Equals(default))
            {
                return;
            }

            m_TickSelector.UnregisterValueChangedCallback(OnTickChanged);
            m_SelectedFrameID = frameID;

            if (m_SelectedFrameID.value < m_RangeSlider.minValue)
            {
                var diff = Mathf.RoundToInt(m_RangeSlider.minValue - m_SelectedFrameID.value);
                m_RangeSlider.value = new Vector2(m_SelectedFrameID.value, m_RangeSlider.maxValue - diff);
                m_FrameSlider.lowValue = m_SelectedFrameID.value;
                m_FrameSlider.highValue -= diff;
            }
            else if (m_SelectedFrameID.value > m_RangeSlider.maxValue)
            {
                var diff = Mathf.RoundToInt(m_SelectedFrameID.value - m_RangeSlider.maxValue);
                m_RangeSlider.value = new Vector2(m_RangeSlider.minValue + diff, m_SelectedFrameID.value);
                m_FrameSlider.lowValue += diff ;
                m_FrameSlider.highValue = m_SelectedFrameID.value;
            }

            SetDiffMarkerForFrameSlider();
            SetDiffMarkersForRangeSliders();
            UpdateTickSelectorForSelectedFrame();
            m_FrameSlider.SetValueWithoutNotify(m_SelectedFrameID.value);
            await Task.Yield();
            m_TickSelector.RegisterValueChangedCallback(OnTickChanged);
            // return Task.CompletedTask;
        }

        /// <summary>
        /// Updates the tick selector UI to show ticks for the currently selected frame, and sets up the necessary callbacks for tick selection.
        /// Must be followed by Task.Yield() and registering the tick changed callback to ensure the UI updates correctly before user interaction is possible.
        /// </summary>
        void UpdateTickSelectorForSelectedFrame()
        {
            var frameData = GetSelectedFrameData();
            m_TickSelector.Clear();

            if (!HasSelectedFrame)
            {
                TracingWindowUtility.SetHidden(m_TickSelectorContainer, true);
                return;
            }

            for (var index = 0; index < frameData.TickIDs.Length; index++)
            {
                var tickId = frameData.TickIDs[index];
                var button = CreateTickButton(tickId, frameData.PerTickData[tickId], index);
                m_TickSelector.Add(button);
            }


            // No tick is auto-selected on frame selection: the tick inspector shows a
            // "Select a tick to view detail" empty state until the user explicitly picks one.
            m_TickSelector.SetValueWithoutNotify(new ToggleButtonGroupState(0, frameData.PerTickData.Count));
            m_SelectedTickID = default;

            TracingWindowUtility.SetHidden(m_TickSelectorContainer, false);
        }

        Button CreateTickButton(TickID tickId, TickData tickData, int index)
        {
            var text = $"Tick {tickId.value.TickIndexForValidTick}";

            if (tickData.NetworkTime.IsPartialTick)
            {
                text = $"Partial {text}";
            }

            if (tickData.TraceType == TraceType.NetcodeGhostUpdateVsSendComparison)
            {
                text = $"Snapshot {text}";

            }

            var button = new Button { text = text, userData = tickData };
            button.AddToClassList(TimelineUssClasses.TickSelectorButtonGroupButton);
            ApplyTickButtonDiffClass(button, tickData, index);
            return button;
        }

        // Kept separate from CreateTickButton so a filter change can restyle the existing buttons in place.
        void ApplyTickButtonDiffClass(Button button, TickData tickData, int index)
        {
            button.RemoveFromClassList(TimelineUssClasses.TickSelectorButtonGroupButtonDiff);
            button.RemoveFromClassList(TimelineUssClasses.TickSelectorButtonGroupButtonDiffPartialStart);
            button.RemoveFromClassList(TimelineUssClasses.TickSelectorButtonGroupButtonDiffPartialEnd);

            if (!PassesDiffFilters(tickData.DiffInfo)) return;

            var selectedFrameData = GetSelectedFrameData();
            string diffClass = tickData.NetworkTime.IsPartialTick switch
            {
                false => TimelineUssClasses.TickSelectorButtonGroupButtonDiff,
                true when index == 0 => TimelineUssClasses.TickSelectorButtonGroupButtonDiffPartialStart,
                true => PassesDiffFilters(selectedFrameData.PerTickData[selectedFrameData.TickIDs[index - 1]].DiffInfo)
                    ? TimelineUssClasses.TickSelectorButtonGroupButtonDiffPartialEnd
                    : TimelineUssClasses.TickSelectorButtonGroupButtonDiffPartialStart
            };

            button.AddToClassList(diffClass);
        }

        // Restyles the existing tick buttons after a filter change.
        void RefreshTickSelectorDiffHighlights()
        {
            if (!HasSelectedFrame)
                return;
            var frameData = GetSelectedFrameData();
            if (!frameData.PerTickData.IsCreated)
                return;

            using var buttons = m_TickSelector.Children().GetEnumerator();
            for (var index = 0; index < frameData.TickIDs.Length; index++)
            {
                if (!buttons.MoveNext())
                    break;
                if (buttons.Current is Button button && frameData.PerTickData.TryGetValue(frameData.TickIDs[index], out var tickData))
                    ApplyTickButtonDiffClass(button, tickData, index);
            }
        }

        /// <summary>
        /// Handles changes from the tick selector, updating the selected tick and invoking the <see cref="OnTracingSelectionChange"/> event.
        /// <param name="evt">The change event containing the new toggle state for the tick selector buttons.</param>
        /// </summary>
        void OnTickChanged(ChangeEvent<ToggleButtonGroupState> evt)
        {
            var state = evt.newValue;
            var i = 0;
            var hasSelection = false;
            foreach (var button in m_TickSelector.Children())
            {
                button.EnableInClassList(TimelineUssClasses.TickSelectorSelected, false);
                if (state[i] && button is Button selectedButton)
                {
                    hasSelection = true;
                    m_SelectedTickID = m_Data.ClientWorldData.PerFrameData[m_SelectedFrameID].TickIDs[i];
                    selectedButton.EnableInClassList(TimelineUssClasses.TickSelectorSelected, true);
                    var change = new TracingSelectionChange { tickID = m_SelectedTickID, frameID = m_SelectedFrameID };
                    OnTracingSelectionChange?.Invoke(this, change);
                }

                i++;
            }

            // The user deselected the tick (allow-empty-selection): fall back to the empty state.
            if (!hasSelection)
            {
                m_SelectedTickID = default;
                var change = new TracingSelectionChange { tickID = default, frameID = m_SelectedFrameID };
                OnTracingSelectionChange?.Invoke(this, change);
            }
        }

        protected internal override async Task OnSelectedTickChanged(TickID tickID)
        {
            if (tickID.Equals(default))
                return;

            m_SelectedTickID = tickID;
            var tickIdIndex = GetSelectedFrameData().TickIDs.IndexOf(tickID);
            m_TickSelector.SetValueWithoutNotify(new ToggleButtonGroupState(1UL << tickIdIndex, GetSelectedFrameData().TickIDs.Count));

            var i = 0;
            foreach (var button in m_TickSelector.Children())
            {
                button.EnableInClassList(TimelineUssClasses.TickSelectorSelected, false);
                if (i == tickIdIndex && button is Button selectedButton)
                {
                    selectedButton.EnableInClassList(TimelineUssClasses.TickSelectorSelected, true);
                }
                i++;
            }

            await Task.Yield();
        }

        public override VisualElement Create()
        {
            m_Root.Clear();
            m_Root.AddToClassList(UssClassName);
            LoadTemplate();
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            return m_Root;
        }

        async Task CreateRangeDiffMarkers(CancellationToken cancellationToken)
        {
            // Filter changes start overlapping builds on the same token; only the newest build may keep adding markers after a yield.
            var generation = ++m_RangeMarkerBuildGeneration;
            if (m_RangeSliderBackground == null)
            {
                var tracker = m_RangeSlider.Q<VisualElement>("unity-tracker").parent;
                m_RangeSliderBackground = new VisualElement();
                m_RangeSliderBackground.AddToClassList(TimelineUssClasses.FrameSelectorBackground);
                tracker.Insert(0, m_RangeSliderBackground);
            }

            m_RangeSliderBackground.Clear();


            var frameTimer = System.Diagnostics.Stopwatch.StartNew();
            var frameBudgetMs = 1000f / Mathf.Max(1f, NetCodeConfig.Global.TracingConfig._targetFPSDuringProcessing);
            var rangeStartIndex = m_Data.ClientWorldData.FrameIDs.IndexOf(new FrameID() { value = m_FrameSlider.lowValue });
            var rangeEndIndex = m_Data.ClientWorldData.FrameIDs.IndexOf(new FrameID() { value = m_FrameSlider.highValue });
            var lowLimit = m_RangeSlider.lowLimit;
            var range = m_RangeSlider.highLimit - lowLimit;

            for (var i = 0; i < m_Data.ClientWorldData.FrameIDs.Length; i++)
            {
                var frameId = m_Data.ClientWorldData.FrameIDs[i];
                var percent = ((frameId.value - lowLimit) / range) * 100f;

                if (PassesDiffFilters(m_Data.ClientWorldData.PerFrameData[frameId].DiffInfo))
                {
                    VisualElement marker = new VisualElement { style = { left = Length.Percent(percent) }, userData = frameId };
                    marker.AddToClassList(TimelineUssClasses.FrameSelectorFrameMarker);
                    if (i < rangeStartIndex  || i > rangeEndIndex)
                    {
                        marker.EnableInClassList(TimelineUssClasses.FrameSelectorFrameMarkerInactive, true);
                    }
                    m_RangeSliderBackground.Add(marker);
                }

                if (frameTimer.Elapsed.TotalMilliseconds > frameBudgetMs)
                {
                    // The UI build phase shows the indeterminate spinner; just yield to keep the editor responsive.
                    await Task.Yield();
                    frameTimer.Restart();
                    if (cancellationToken.IsCancellationRequested || generation != m_RangeMarkerBuildGeneration) return;
                }
            }
        }

        public override async Task OnDataAvailable(TracingData data)
        {
            // Backend processing is done; the remaining UI build has no meaningful progress fraction.
            ShowUIBuildSpinner();
            m_Data = data;
            m_Cts?.Cancel();
            m_Cts?.Dispose();
            m_Cts = new CancellationTokenSource();
            var token = m_Cts.Token;
            m_RangeSlider.highLimit = m_Data.ClientWorldData.FrameIDs[^1].value;
            m_RangeSlider.lowLimit = m_Data.ClientWorldData.FrameIDs[0].value;
            var totalFrames = m_Data.ClientWorldData.FrameIDs.Count;
            if (!HasSelectedFrame)
            {
                m_RangeSlider.value = new Vector2(m_RangeSlider.lowLimit, totalFrames < 400
                    ? m_Data.ClientWorldData.FrameIDs[totalFrames/4].value
                    : m_Data.ClientWorldData.FrameIDs[100].value);
            }

            m_FrameSlider.lowValue = (int)m_RangeSlider.minValue;
            m_FrameSlider.highValue = (int)m_RangeSlider.maxValue;


            TracingWindowUtility.SetHidden(m_Root, false);
            await Task.Yield();
            if (token.IsCancellationRequested) return;

            await CreateRangeDiffMarkers(token);
            if (token.IsCancellationRequested || !m_Data.ClientWorldData.IsCreated) return;
            SetDiffMarkerForFrameSlider();

            TracingWindowUtility.SetHidden(m_RangeSlider, false);
            m_RangeSlider.UnregisterValueChangedCallback(OnFrameRangeChanged);
            m_RangeSlider.RegisterValueChangedCallback(OnFrameRangeChanged);
            TracingWindowUtility.SetHidden(m_FrameSlider, false);
            HideProgressBar();
            TracingWindowUtility.SetHidden(m_Root, false);
            TracingWindowUtility.SetHidden(m_FrameSelectorContainer, false);
            m_FrameSlider.UnregisterValueChangedCallback(OnFrameChanged);
            m_FrameSlider.RegisterValueChangedCallback(OnFrameChanged);

            if (HasSelectedFrame)
            {
                TracingWindowUtility.SetHidden(m_TickSelectorContainer, false);
                m_TickSelector.UnregisterValueChangedCallback(OnTickChanged);
                m_TickSelector.RegisterValueChangedCallback(OnTickChanged);
            }

            await Task.Yield();
        }

        public void SetReplayFrame(FrameID frameID)
        {
            if (m_ReplayIndicator == null)
            {
                var tracker = m_FrameSlider.Q<VisualElement>("unity-tracker");
                m_ReplayIndicator = new VisualElement();
                m_ReplayIndicator.AddToClassList(TimelineUssClasses.ReplayIndicator);
                tracker.Add(m_ReplayIndicator);
            }

            float range = m_FrameSlider.highValue - m_FrameSlider.lowValue;
            float percent = range > 0 ? ((frameID.value - m_FrameSlider.lowValue) / range) * 100f : 0f;
            m_ReplayIndicator.style.left = Length.Percent(percent);
            TracingWindowUtility.SetHidden(m_ReplayIndicator, false);
        }

        public void HideReplayIndicator()
        {
            TracingWindowUtility.SetHidden(m_ReplayIndicator, true);
        }

        public void DisplayProgressBar()
        {
            HideUIBuildSpinner();
            TracingWindowUtility.SetHidden(m_ProgressBar, false);
            SetStepProgress(k_RawTracesStepTitle, 0f);
            TracingWindowUtility.SetHidden(m_Root, false);
            TracingWindowUtility.SetHidden(m_FrameSelectorContainer, true);
            TracingWindowUtility.SetHidden(m_TickSelectorContainer, true);
            TracingWindowUtility.SetHidden(m_ProgressBarContainer, false);
            m_Root.EnableInClassList(TimelineUssClasses.Grow, true);
        }

        /// <summary>
        /// Reports backend trace processing progress on the single progress bar: raw trace processing
        /// fills the first half, diff processing the second half.
        /// <param name="step">The backend processing step being reported.</param>
        /// <param name="stepFraction">Progress of that step in [0,1].</param>
        /// </summary>
        public void SetProcessingProgress(TracingProcessingStep step, float stepFraction)
        {
            var overallFraction = step == TracingProcessingStep.RawTraces
                ? Mathf.Clamp01(stepFraction) * 0.5f
                : 0.5f + Mathf.Clamp01(stepFraction) * 0.5f;
            SetStepProgress(step == TracingProcessingStep.RawTraces ? k_RawTracesStepTitle : k_DiffStepTitle, overallFraction);
        }

        /// <summary>
        /// Sets the progress bar title to the given step and its fill to the overall fraction in [0,1].
        /// </summary>
        void SetStepProgress(string stepTitle, float overallFraction)
        {
            m_ProgressBar.title = stepTitle;
            m_ProgressBar.value = Mathf.Clamp01(overallFraction) * m_ProgressBar.highValue;
        }

        /// <summary>Swaps the progress bar for an indeterminate spinner while the UI is built.</summary>
        public void ShowUIBuildSpinner()
        {
            if (m_LoadingSpinnerContainer == null)
            {
                m_LoadingSpinnerContainer = new VisualElement();
                m_LoadingSpinnerContainer.AddToClassList(TimelineUssClasses.LoadingSpinnerContainer);
                m_LoadingSpinner = new VisualElement();
                m_LoadingSpinner.AddToClassList(TimelineUssClasses.LoadingSpinner);
                m_LoadingSpinnerContainer.Add(m_LoadingSpinner);
                m_LoadingSpinnerContainer.Add(new Label(k_BuildUIStepTitle));
                m_ProgressBarContainer.Add(m_LoadingSpinnerContainer);
            }

            TracingWindowUtility.SetHidden(m_ProgressBar, true);
            TracingWindowUtility.SetHidden(m_ProgressBarContainer, false);
            TracingWindowUtility.SetHidden(m_LoadingSpinnerContainer, false);
            m_SpinnerSchedule ??= m_LoadingSpinner.schedule.Execute(TickSpinner).Every(k_SpinnerFrameIntervalMs);
            m_SpinnerSchedule.Resume();
            TickSpinner();
        }

        void HideUIBuildSpinner()
        {
            m_SpinnerSchedule?.Pause();
            if (m_LoadingSpinnerContainer != null)
                TracingWindowUtility.SetHidden(m_LoadingSpinnerContainer, true);
        }

        // Cycles through the editor's WaitSpin00..WaitSpin11 busy icons.
        void TickSpinner()
        {
            m_SpinnerFrame = (m_SpinnerFrame + 1) % k_SpinnerFrameCount;
            var icon = EditorGUIUtility.IconContent($"WaitSpin{m_SpinnerFrame:00}");
            if (icon?.image is Texture2D texture)
                m_LoadingSpinner.style.backgroundImage = new StyleBackground(texture);
        }

        public void HideProgressBar()
        {
            HideUIBuildSpinner();
            TracingWindowUtility.SetHidden(m_ProgressBarContainer, true);
            TracingWindowUtility.SetHidden(m_Root, true);
            m_Root.EnableInClassList(TimelineUssClasses.Grow, false);
        }
        public override void Dispose()
        {
            OnTracingSelectionChange = null;
            m_FrameSlider.UnregisterValueChangedCallback(OnFrameChanged);
            m_RangeSlider.UnregisterValueChangedCallback(OnFrameRangeChanged);
            m_TickSelector.UnregisterValueChangedCallback(OnTickChanged);
            m_Cts?.Cancel();
            m_Cts?.Dispose();
            m_Cts = null;
            EditorApplication.pauseStateChanged -= OnPauseStateChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        FrameData GetSelectedFrameData()
        {
            if (!HasSelectedFrame || m_Data.ClientWorldData.PerFrameData.IsEmpty ||
                !m_Data.ClientWorldData.PerFrameData.TryGetValue(m_SelectedFrameID, out var frameData))
            {
                return default;
            }

            return frameData;
        }

        public override void Clear()
        {
            Reset();
            m_Data = default;
            m_SelectedFrameID = default;
            m_SelectedTickID = default;
        }

        public void Reset()
        {
            TracingWindowUtility.SetHidden(m_Root, true);
            TracingWindowUtility.SetHidden(m_FrameSlider, true);
            TracingWindowUtility.SetHidden(m_RangeSlider, true);
            TracingWindowUtility.SetHidden(m_TickSelectorContainer, true);
            TracingWindowUtility.SetHidden(m_FrameSelectorContainer, false);
            m_FrameSlider.UnregisterValueChangedCallback(OnFrameChanged);
            m_RangeSlider.UnregisterValueChangedCallback(OnFrameRangeChanged);
            m_TickSelector.UnregisterValueChangedCallback(OnTickChanged);
            m_Cts?.Cancel();
            m_Cts?.Dispose();
            m_Cts = null;

            // Pure UI reset
            var tickGroupLength = m_TickSelector.value.length;
            if (tickGroupLength > 0)
                m_TickSelector.SetValueWithoutNotify(new ToggleButtonGroupState(0, tickGroupLength));

            foreach (var element in m_TickSelector.Children())
            {
                element.EnableInClassList(TimelineUssClasses.TickSelectorSelected, false);
            }

            if (!EditorApplication.isPaused && !EditorApplication.isPlaying)
            {
                m_FrameSlider.value = 0;
                m_RangeSlider.lowLimit = 0;
                m_RangeSlider.highLimit = 0;
            }
            m_Data = default;
        }

        public void SetFilters(TracingViewFilters tickInspectorFilters)
        {
            m_TracingViewFilters = tickInspectorFilters;
            var cts = m_Cts;
            if (m_Data.ClientWorldData.IsCreated && cts != null)
            {
                SetDiffMarkerForFrameSlider();
                _ = CreateRangeDiffMarkers(cts.Token);
                RefreshTickSelectorDiffHighlights();
            }
        }
    }
}

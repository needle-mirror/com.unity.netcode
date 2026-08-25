using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.NetCode.Editor.Tracing.UI.TickInspector;
using Unity.NetCode.Editor.Tracing.UI.Timeline;
using Unity.NetCode.Tracing;
using Unity.NetCode.Editor.Tracing.UI.TracingToolbar;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI
{
    /// <summary>
    /// Editor window that hosts tracing views for the Prediction Tracing Tool.
    /// Views are categorized into three types: static views, data observers, and selection observers.
    /// </summary>
    class TracingWindow : EditorWindow
    {
        const string k_WindowTitle = "Tracing Tool";
        const string k_StyleSheetPath = Constants.Stylesheets + "tracing-window.uss";
        const string k_VariablesDarkPath = Constants.Stylesheets + "tracing-vars-dark.uss";
        const string k_VariablesLightPath = Constants.Stylesheets + "tracing-vars-light.uss";
        const string k_EnterPlayModeText = "Enter play mode to start tracing...";
        const string k_SetTracingTarget = "Set a tracing target to get started";
        const string k_PausePlayModeText = "Pause to view data";
        const string k_UnpausePlayModeNoDataDisabledText = "Unpause to resume playing without capturing data";
        const string k_UnpausePlayModeNoDataText = "Unpause to resume capturing data";
        const string k_TracingDisabledText = "Enable tracing to capture data";

        // Separate registries by view type for selective updates
        List<IDataObserver> m_DataObservers = new();
        List<ITracingView> m_StaticViews = new();
        FrameID m_SelectedFrameID;
        TickID m_SelectedTickID;
        TimelineView m_TimelineView;
        TickInspectorView m_TickInspector;
        Label m_PlayModeLabel;
        VisualElement m_PlayModeLabelContainer;
        Button m_SetTracingTargetButton;
        CancellationTokenSource m_Cts;
        TracingToolbarView m_Toolbar;
        SceneVisualizationView m_SceneVisView;
        bool m_TracingSelectionHasBeenSetAtLeastOnce;
        TracingDataAccess.ProcessedWorldsData m_Data { get; set; }
        bool m_HasBeenCleared;
        TracingViewFilters m_TracingViewFilters = new();
        // Defers the tick inspector's tree rebuild while the fuzzy-threshold slider is dragged.
        IVisualElementScheduledItem m_TickInspectorFilterDebounce;
        const long k_TickInspectorFilterDebounceMs = 150;


#if NETCODE_TRACING_TOOL
        [MenuItem("Window/Multiplayer/Tracing Tool")]
#endif
        static void ShowWindow()
        {
            GetWindow<TracingWindow>(false, k_WindowTitle, true);
        }

        #region Load Gui
        public void CreateGUI()
        {
            LoadStylesheets();
            TracingWindowUtility.SetTypesToTrace();
            m_TracingViewFilters.HideGhostInstance = !TracingWindowUtility.IsGhostInstanceSelected();
            m_TracingSelectionHasBeenSetAtLeastOnce = TracingDataAccess.Config.Data.TracingTypesSelected();
            CreateViews();
            EditorApplication.pauseStateChanged -= OnPauseStateChanged;
            EditorApplication.pauseStateChanged += OnPauseStateChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            NetCodeTracingTargetSettings.OnSelectionChanged -= OnNetcodeSelectionChanged;
            NetCodeTracingTargetSettings.OnSelectionChanged += OnNetcodeSelectionChanged;
        }

        void LoadStylesheets()
        {
            var ussFile = AssetDatabase.LoadAssetAtPath<StyleSheet>(k_StyleSheetPath);

            var ussVariables = EditorGUIUtility.isProSkin
                ? AssetDatabase.LoadAssetAtPath<StyleSheet>(k_VariablesDarkPath)
                : AssetDatabase.LoadAssetAtPath<StyleSheet>(k_VariablesLightPath);

            rootVisualElement.styleSheets.Add(ussFile);
            rootVisualElement.styleSheets.Add(ussVariables);
        }
        void CreateViews()
        {
            // Categorize views by type for selective updates in future PRs
            m_Toolbar = new TracingToolbarView();
            m_Toolbar.onClearButtonClicked += OnClearButtonClicked;
            m_Toolbar.OnTracingSelectionChanged += OnTracingSelectionChanged;
            m_Toolbar.OnTracingEnabledToggled += OnTracingEnabledToggled;
            m_Toolbar.OnChangedValuesOnlyToggled += OnChangedValuesOnlyToggled;
            m_Toolbar.OnFuzzyDiffThresholdChanged += OnFuzzyDiffThresholdChanged;
            m_Toolbar.OnSelectedDiffReasonsChanged += OnSelectedDiffReasonsChanged;
            m_Toolbar.OnReplayRenderingFrequencyChanged += HandleReplayFrequencyChanged;
            m_Toolbar.OnPlaybackToggled += HandlePlaybackToggled;
            m_Toolbar.OnTracingTargetVisibilityChanged += OnTracingTargetVisibilityChanged;
            m_DataObservers.Add(m_Toolbar);
            rootVisualElement.Add(m_Toolbar.Create());

            m_SceneVisView = new SceneVisualizationView();
            m_DataObservers.Add(m_SceneVisView);
            m_Toolbar.OnReplaySpeedChanged += HandleReplaySpeedChanged;
            m_SceneVisView.PlaybackStateChanged += HandleSceneVisPlaybackStateChanged;
            m_SceneVisView.ReplayFrameChanged += HandleSceneVisReplayFrameChanged;
            m_SceneVisView.ReplayPausedAtSelection += HandleSceneVisReplayPausedAtSelection;

            // Data observers (auto-update when data is available)
            // Example:
            // var frameList = new FrameListView();
            // m_DataObservers.Add(frameList);
            // rootVisualElement.Add(frameList.Create());

            // Selection observers (update when selection changes)

            m_TimelineView = new TimelineView();
            m_TimelineView.OnTracingSelectionChange += OnTracingSelectionChanged;
            m_TimelineView.SetFilters(m_TracingViewFilters);
            m_DataObservers.Add(m_TimelineView);
            rootVisualElement.Add(m_TimelineView.Create());

            m_TickInspector = new TickInspectorView();
            m_DataObservers.Add(m_TickInspector);
            rootVisualElement.Add(m_TickInspector.Create());
            m_TickInspector.SetFilters(m_TracingViewFilters);
            m_Toolbar.SetFilters(m_TracingViewFilters);

            // Root view for play mode label while dataless or play mode not paused
            CreatePlayModeContainer();
            rootVisualElement.Add(m_PlayModeLabelContainer);
            rootVisualElement.AddToClassList(TracingWindowUssClasses.Base);

        }

        void CreatePlayModeContainer()
        {
            m_PlayModeLabelContainer = new VisualElement();

            var label = TracingDataAccess.Config.Data.TracingTypesSelected()
                ? k_EnterPlayModeText
                : k_SetTracingTarget;
            m_PlayModeLabel = new Label(label);
            m_PlayModeLabelContainer.AddToClassList(TracingWindowUssClasses.CenteredContent);
            m_PlayModeLabelContainer.Add(m_PlayModeLabel);
            m_PlayModeLabel.AddToClassList(TracingWindowUssClasses.Label);

            m_SetTracingTargetButton = new Button()
            {
                text = "Set tracing target...",
                tooltip = "Change the tracing target to capture data from a different source"
            };

            m_SetTracingTargetButton.AddToClassList(TracingWindowUssClasses.SetTracingTargetButton);
            TracingWindowUtility.SetHidden(m_SetTracingTargetButton, TracingDataAccess.Config.Data.TracingTypesSelected());
            m_SetTracingTargetButton.clicked += SelectTracingTargetWindow.ShowWindow;
            m_PlayModeLabelContainer.Add(m_SetTracingTargetButton);

            rootVisualElement.Add(m_PlayModeLabelContainer);
        }

        void OnTracingTargetVisibilityChanged(HashSet<string> hiddenSystemType, HashSet<string> hiddenComponentTypes)
        {
            m_TracingViewFilters.HiddenSystems = hiddenSystemType;
            m_TracingViewFilters.HiddenComponents = hiddenComponentTypes;
            ApplyFiltersToViews();
        }

        void OnChangedValuesOnlyToggled(bool changedValuesOnly)
        {
            m_TracingViewFilters.ChangedValuesOnly = changedValuesOnly;
            ApplyFiltersToViews();
        }

        void OnFuzzyDiffThresholdChanged(float fuzzyDiffThreshold)
        {
            if (m_TracingViewFilters.FuzzyDiffThreshold == fuzzyDiffThreshold)
                return;
            m_TracingViewFilters.FuzzyDiffThreshold = fuzzyDiffThreshold;
            ApplyFiltersToViews(debounceTickInspector: true);
        }

        void OnSelectedDiffReasonsChanged(DiffInfo.DiffReasons selectedReasons)
        {
            m_TracingViewFilters.EnabledDiffReasons = selectedReasons;
            ApplyFiltersToViews();
        }

        // Re-renders the already-built views from the stored aggregates; no diff recomputation.
        void ApplyFiltersToViews(bool debounceTickInspector = false)
        {
            m_TimelineView.SetFilters(m_TracingViewFilters);
            m_Toolbar.SetFilters(m_TracingViewFilters);
            if (debounceTickInspector)
            {
                m_TickInspectorFilterDebounce ??= rootVisualElement.schedule.Execute(() => m_TickInspector.SetFilters(m_TracingViewFilters));
                m_TickInspectorFilterDebounce.ExecuteLater(k_TickInspectorFilterDebounceMs);
            }
            else
            {
                m_TickInspectorFilterDebounce?.Pause();
                m_TickInspector.SetFilters(m_TracingViewFilters);
            }
        }
        #endregion

        #region Child view action and event handlers

        void HandleReplayFrequencyChanged(object sender, ReplayRenderingFrequency frequency)
        {
            m_SceneVisView.PlaybackMode = frequency;
        }

        void HandleReplaySpeedChanged(object sender, float speed)
        {
            m_SceneVisView.PlaybackSpeed = speed;
        }

        void HandlePlaybackToggled(bool isPlaying)
        {
            if (isPlaying)
                m_SceneVisView.StartReplay(m_SceneVisView.PlaybackMode);
            else
                m_SceneVisView.StopReplay();
        }

        void HandleSceneVisPlaybackStateChanged()
        {
            // Keep the toolbar play/pause icon in sync with the actual replay state, including when
            // playback stops on its own (e.g. frequency change, clear, or exiting play mode).
            m_Toolbar.SetPlaybackState(m_SceneVisView.IsPlaying);
            if (!m_SceneVisView.IsPlaying)
                m_TimelineView?.HideReplayIndicator();
        }

        void HandleSceneVisReplayFrameChanged(FrameID frameID)
        {
            m_TimelineView?.SetReplayFrame(frameID);
        }

        // When the user pauses the replay, select the frame/tick it stopped on so the timeline, tick selector,
        // and tick inspector all sync to the paused position. The scene-vis view already updated itself, so
        // pass it as the sender to skip it in the fan-out.
        void HandleSceneVisReplayPausedAtSelection(FrameID frameID, TickID tickID)
        {
            OnTracingSelectionChanged(m_SceneVisView, new TracingSelectionChange { frameID = frameID, tickID = tickID });
        }

        async void OnTracingEnabledToggled(bool enabled)
        {
            if (enabled && EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                m_SelectedFrameID = default;
                m_SelectedTickID = default;
                m_Data = null;
                // The record button restarts the recording with the selection as it stands now.
                TracingRecordingTargets.CaptureFromConfig();
            }
            SetPlaymodeLabel();

            if (!enabled && EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                await ProcessTrace();
            }

            if (enabled && !EditorApplication.isPaused && EditorApplication.isPlaying)
            {
                await ClearAllViewsAsync();
            }
        }

        void OnTracingSelectionChanged(object sender,TracingSelectionChange tracingSelectionChange)
        {
            m_SelectedFrameID = tracingSelectionChange.frameID;
            m_SelectedTickID = tracingSelectionChange.tickID;

            foreach (var view in m_DataObservers)
            {
                if (sender == view)
                {
                    continue;
                };
                view.OnSelectedTickOrFrameChanged(tracingSelectionChange);
            }
        }

        #endregion

        #region Playmode LifeCycle

        async void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                // Stop the playback before we free statics by entering playmode
                await m_SceneVisView.StopPlaybackAsync();
            }
            else if (state == PlayModeStateChange.EnteredPlayMode)
            {
                await ClearAllViewsAsync();
                m_Data = null;
                m_SelectedFrameID = default;
                m_SelectedTickID = default;
                m_HasBeenCleared = false;
                if (!TracingDataAccess.Config.Data.Initialized) TracingWindowUtility.SetTypesToTrace();
                // The recording that starts this play session uses the selection as it stands now.
                TracingRecordingTargets.CaptureFromConfig();
            }
            else if (
                    state == PlayModeStateChange.EnteredEditMode
                    && !m_HasBeenCleared
                    && m_Data == null
                    && TracingDataAccess.Config.Data.TracingTypesSelected()
                )
            {
                await ProcessTrace();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                SetPlaymodeLabel();
            }
        }

        async void OnPauseStateChanged(PauseState state)
        {
            if(!EditorApplication.isPlaying)
                return;

            if (state == PauseState.Paused
                && TracingDataAccess.Config.Data.EnableTracing
                && TracingDataAccess.Config.Data.IsTracingEnabledAndReady()
               )
            {
                m_HasBeenCleared = false;
                await ProcessTrace();
            }
            else if (state == PauseState.Paused && !TracingDataAccess.Config.Data.EnableTracing)
            {
                SetPlaymodeLabel();
            }
            else if (state == PauseState.Unpaused && !TracingDataAccess.Config.Data.EnableTracing)
            {
                await m_SceneVisView.StopPlaybackAsync();
                SetPlaymodeLabel();
            }
            else if (state == PauseState.Unpaused)
            {
                try
                {
                    await m_SceneVisView.StopPlaybackAsync();

                    TracingRecordingTargets.MergeFromConfig();
                    // ResetViews cannot be used for this edge case
                    // Clear() sets selected frame and tick to default, but we want to keep the selection when unpausing
                    foreach (var view in m_StaticViews)
                    {
                        view.Clear();
                    }

                    foreach (var view in m_DataObservers)
                    {
                        if (view is TimelineView timelineView)
                        {
                            timelineView.Reset();
                        }
                        else
                        {
                            view.Clear();
                        }
                    }
                }
                finally
                {
                    m_Data = null;
                    m_Cts?.Cancel();
                    SetPlaymodeLabel();
                    TracingWindowUtility.SetHidden(m_PlayModeLabelContainer, false);
                    TracingDataAccess.DisposeProcessedWorldData();
                    TracingDisplayState.SetShowingTraces(false);
                }
            }
        }
        #endregion

        void OnNetcodeSelectionChanged()
        {
            TracingWindowUtility.SetTypesToTrace();
            var hideGhostInstance = !TracingWindowUtility.IsGhostInstanceSelected();
            if (hideGhostInstance != m_TracingViewFilters.HideGhostInstance)
            {
                m_TracingViewFilters.HideGhostInstance = hideGhostInstance;
                // GhostInstance is always traced, so (de)selecting it re-renders the views
                // instead of asking for new traces like any other target.
                ApplyFiltersToViews();
            }
            if (!m_TracingSelectionHasBeenSetAtLeastOnce)
            {
                m_TracingSelectionHasBeenSetAtLeastOnce = true;
                TracingWindowUtility.SetHidden(m_SetTracingTargetButton, TracingDataAccess.Config.Data.TracingTypesSelected());
            }
            m_Toolbar.SetHidden(!m_TracingSelectionHasBeenSetAtLeastOnce);
            SetPlaymodeLabel();
        }

        async Task ProcessTrace()
        {
            m_Cts = new CancellationTokenSource();
            TracingData tracingData;
            TracingWindowUtility.SetHidden(m_PlayModeLabelContainer, true);
            m_TimelineView.DisplayProgressBar();

            try
            {
                await Task.Yield();
                m_Data  = await TracingDataAccess.GetProcessedWorldsData(m_Cts.Token, m_TimelineView.SetProcessingProgress);
                m_Cts.Token.ThrowIfCancellationRequested();

                if (m_Data == null)
                {
                    ShowPlaymodeLabelState();
                    return;
                }

                tracingData = new TracingData()
                {
                    ClientWorldData = m_Data.ClientWorldData,
                    ServerWorldData = m_Data.ServerWorldData
                };
            }
            catch (Exception e)
            {
                ShowPlaymodeLabelState();
                if (e is not OperationCanceledException)
                {
                    Debug.LogError($"Error while processing trace data:");
                    Debug.LogException(e);
                }
                return;
            }

            foreach (var view in m_DataObservers)
            {
                await view.OnDataAvailable(tracingData);
                if (!m_SelectedFrameID.Equals(default))
                {
                    await view.OnSelectedTickOrFrameChanged(new TracingSelectionChange
                    {
                        frameID = m_SelectedFrameID,
                        tickID = m_SelectedTickID
                    });
                }
            }

            TracingDisplayState.SetShowingTraces(true);
        }

        void ShowPlaymodeLabelState()
        {
            m_TimelineView.HideProgressBar();
            SetPlaymodeLabel();
            TracingWindowUtility.SetHidden(m_PlayModeLabelContainer, false);
            TracingDisplayState.SetShowingTraces(false);
        }

        void SetPlaymodeLabel()
        {
            string text = "";


            if (!EditorApplication.isPlaying && !TracingDataAccess.Config.Data.TracingTypesSelected() && !m_TracingSelectionHasBeenSetAtLeastOnce)
            {
                text = k_SetTracingTarget;
            }
            else if (!EditorApplication.isPlaying && TracingDataAccess.Config.Data.EnableTracing)
            {
                text = k_EnterPlayModeText;
            }
            else if (EditorApplication.isPaused && !TracingDataAccess.Config.Data.EnableTracing)
            {
                text = k_UnpausePlayModeNoDataDisabledText;
            }
            else if (EditorApplication.isPaused)
            {
                text = k_UnpausePlayModeNoDataText;
            }
            else if (TracingDataAccess.Config.Data.EnableTracing)
            {
                text = k_PausePlayModeText;
            }

            if (!TracingDataAccess.Config.Data.EnableTracing)
            {
                text = text.Length == 0 ? k_TracingDisabledText : $"{k_TracingDisabledText}\n{text}";
            }

            m_PlayModeLabel.text = text;
        }

        void ResetViews()
        {
            foreach (var view in m_StaticViews)
            {
                view.Clear();
            }

            foreach (var view in m_DataObservers)
            {
                view.Clear();
            }

            TracingDisplayState.SetShowingTraces(false);
        }


        async void OnClearButtonClicked()
        {
            m_SelectedFrameID = default;
            m_SelectedTickID = default;
            m_Data = null;
            await ClearAllViewsAsync();
            m_HasBeenCleared = true;
            TracingRecordingTargets.CaptureFromConfig();
        }

        async Task ClearAllViewsAsync()
        {
            await m_SceneVisView.StopPlaybackAsync();
            ResetViews();
            m_Data = null;
            m_Cts?.Cancel();
            SetPlaymodeLabel();
            TracingWindowUtility.SetHidden(m_PlayModeLabelContainer, false);
            TracingDataAccess.DisposeAllWorldData();
        }



        public void OnDestroy()
        {
            // Dispose all views in all registries
            foreach (var view in m_StaticViews)
                view.Dispose();

            foreach (var view in m_DataObservers)
                view.Dispose();

            ResetViews();

            // A window restored from the layout can be closed before its GUI finished building.
            if (m_SetTracingTargetButton != null)
            {
                m_SetTracingTargetButton.clicked -= SelectTracingTargetWindow.ShowWindow;
            }

            if (m_Toolbar != null)
            {
                m_Toolbar.OnTracingSelectionChanged -= OnTracingSelectionChanged;
                m_Toolbar.onClearButtonClicked -= OnClearButtonClicked;
                m_Toolbar.OnTracingEnabledToggled -= OnTracingEnabledToggled;
                m_Toolbar.OnReplayRenderingFrequencyChanged -= HandleReplayFrequencyChanged;
                m_Toolbar.OnReplaySpeedChanged -= HandleReplaySpeedChanged;
                m_Toolbar.OnPlaybackToggled -= HandlePlaybackToggled;
                m_Toolbar.OnChangedValuesOnlyToggled -= OnChangedValuesOnlyToggled;
                m_Toolbar.OnFuzzyDiffThresholdChanged -= OnFuzzyDiffThresholdChanged;
                m_Toolbar.OnSelectedDiffReasonsChanged -= OnSelectedDiffReasonsChanged;
                m_Toolbar.OnTracingTargetVisibilityChanged -= OnTracingTargetVisibilityChanged;
            }

            if (m_SceneVisView != null)
            {
                m_SceneVisView.PlaybackStateChanged -= HandleSceneVisPlaybackStateChanged;
                m_SceneVisView.ReplayFrameChanged -= HandleSceneVisReplayFrameChanged;
                m_SceneVisView.ReplayPausedAtSelection -= HandleSceneVisReplayPausedAtSelection;
            }

            if (m_TimelineView != null)
            {
                m_TimelineView.OnTracingSelectionChange -= OnTracingSelectionChanged;
            }

            EditorApplication.pauseStateChanged -= OnPauseStateChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            NetCodeTracingTargetSettings.OnSelectionChanged -= OnNetcodeSelectionChanged;
            m_SelectedFrameID = default;
            m_SelectedTickID = default;
            m_Data = null;

            m_Cts?.Cancel();
            m_Cts?.Dispose();
            m_Cts = null;
        }
    }
}

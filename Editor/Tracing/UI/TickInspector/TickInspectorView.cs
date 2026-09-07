using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using Unity.Netcode.Editor.Tracing.UI.TracingToolbar;
using Unity.Netcode.Tracing;


namespace Unity.Netcode.Editor.Tracing.UI.TickInspector
{
    /// <summary>
    /// The visual container for everything related to tick inspection.
    /// </summary>
    class TickInspectorView : DataObserverView
    {
        protected override string UssClassName => TickInspectorUssClasses.Base;
        const string k_TemplatePath = Constants.Templates + "tick-inspector-toolbar.uxml";
        public const string k_TimelineTickLabel = "tick-inspector-tick-label";
        public const string k_TimelineFrameLabel = "tick-inspector-frame-label";
        public const string k_TimelineSearchInput = "tick-inspector-search";
        public const string k_FilterStatusLabel = "tick-inspector-filter-status";
        public const string k_SelectTickText = "Select a tick to view details";

        Label m_FilterStatusLabel;
        FrameID m_SelectedFrameID;
        TickID m_SelectedTickID = default(TickID);
        TickInspectorMetadataFoldout m_MetadataFoldout;
        TickInspectorTreeView m_TickInspectorTreeView;
        VisualElement m_Toolbar;
        VisualElement m_TreeViewRoot;
        VisualElement m_EmptyStateContainer;
        ToolbarSearchField m_SearchField;
        TracingViewFilters m_Filters;

        bool HasSelectedFrame => !m_SelectedFrameID.Equals(default(FrameID));

        public override VisualElement Create()
        {
            m_Root.Clear();
            m_Root.AddToClassList(UssClassName);
            // Static UI, above the tree and outside it.
            m_MetadataFoldout = new TickInspectorMetadataFoldout();
            m_TickInspectorTreeView = new TickInspectorTreeView();
            m_TickInspectorTreeView.TickReasonsComputed += HandleTickReasonsComputed;
            AddToolBar();
            m_Root.Add(m_MetadataFoldout);
            m_TreeViewRoot = m_TickInspectorTreeView.Create();
            m_Root.Add(m_TreeViewRoot);
            AddEmptyState();
            TracingWindowUtility.SetHidden(m_Root, true);
            UpdateMetadataVisibility();
            return m_Root;
        }

        void AddEmptyState()
        {
            m_EmptyStateContainer = new VisualElement();
            m_EmptyStateContainer.AddToClassList(TracingWindowUssClasses.CenteredContent);
            var label = new Label(k_SelectTickText);
            label.AddToClassList(TracingWindowUssClasses.Label);
            m_EmptyStateContainer.Add(label);
            TracingWindowUtility.SetHidden(m_EmptyStateContainer, true);
            m_Root.Add(m_EmptyStateContainer);
        }

        // The foldout shares the tree's visibility: shown only once a (non-default) tick is selected.
        void UpdateMetadataVisibility()
        {
            if (m_MetadataFoldout == null)
                return;
            TracingWindowUtility.SetHidden(m_MetadataFoldout, m_SelectedTickID.Equals(default(TickID)));
        }


        void AddToolBar()
        {
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(k_TemplatePath);
            if (visualTree == null) return;
            m_Toolbar = new VisualElement();
            visualTree.CloneTree(m_Toolbar);

            m_SearchField = m_Toolbar.Q<ToolbarSearchField>(k_TimelineSearchInput);
            m_SearchField.RegisterValueChangedCallback(OnSearchFieldValueChanged);
            m_FilterStatusLabel = m_Toolbar.Q<Label>(k_FilterStatusLabel);
            m_Root.Add(m_Toolbar);
        }

        // The tree computes the tick-scoped reasons; the metadata foldout is where they surface.
        void HandleTickReasonsComputed(DiffInfo.DiffReasons reasons)
            => m_MetadataFoldout?.SetTickDiffReasons(reasons, m_Filters?.EnabledDiffReasons ?? DiffInfo.AllDiffReasons);

        // Timing/batching values of the selected tick — the data that explains DeltaTime/BatchedTick diffs.
        void UpdateTickMetadata()
        {
            if (m_MetadataFoldout == null)
                return;

            if (m_SelectedTickID.Equals(default(TickID)) || !m_Data.ClientWorldData.IsCreated
                || !m_Data.ClientWorldData.PerFrameData.TryGetValue(m_SelectedFrameID, out var frameData)
                || !frameData.PerTickData.TryGetValue(m_SelectedTickID, out var clientTick))
            {
                m_MetadataFoldout.SetMetadata(default);
                return;
            }

            var metadata = new TickInspectorTickMetadata
            {
                HasClientTick = true,
                ClientDeltaTimeSeconds = clientTick.CurrentDeltaTimeSeconds,
                IsPartialTick = clientTick.NetworkTime.IsPartialTick,
                PartialTickFraction = clientTick.NetworkTime.ServerTickFraction,
                FrameDeltaTimeSeconds = frameData.m_CurrentDeltaTimeSeconds,
            };

            if (m_Data.ServerWorldData.IsCreated
                && m_Data.ServerWorldData.PerTickData.TryGetValue(m_SelectedTickID, out var serverTick))
            {
                metadata.HasServerTick = true;
                metadata.ServerDeltaTimeSeconds = serverTick.CurrentDeltaTimeSeconds;
                metadata.ServerBatchSize = serverTick.NetworkTime.SimulationStepBatchSize;
            }

            m_MetadataFoldout.SetMetadata(metadata);
        }

        void UpdateFilterStatus(TracingViewFilters filters)
        {
            if (m_FilterStatusLabel == null)
                return;

            var targetItems = TracingFilterDropdown.CreateListItems();
            var shownTargets = 0;
            foreach (var item in targetItems)
            {
                var hiddenNames = item.IsSystem ? filters.HiddenSystems : filters.HiddenComponents;
                if (!hiddenNames.Contains(item.Name))
                    shownTargets++;
            }

            var selectable = DiffReasonCatalog.All;
            var selectedTags = 0;
            foreach (var entry in selectable)
            {
                if ((filters.EnabledDiffReasons & entry.Reason) != 0)
                    selectedTags++;
            }

            var allTargets = shownTargets == targetItems.Count;
            var allTags = selectedTags == selectable.Length;
            var fuzzyActive = filters.FuzzyDiffThreshold > 0f;
            if (allTargets && allTags && !filters.ChangedValuesOnly && !fuzzyActive)
            {
                TracingWindowUtility.SetHidden(m_FilterStatusLabel, true);
                return;
            }

            var targetText = allTargets ? "all tracing targets" : $"{shownTargets}/{targetItems.Count} tracing targets";
            var text = $"Showing {targetText} and {selectedTags}/{selectable.Length} diff tags";
            if (filters.ChangedValuesOnly)
                text += ", changed values only";
            if (fuzzyActive)
                text += $", fuzzy threshold > {filters.FuzzyDiffThreshold}";

            m_FilterStatusLabel.text = text;
            TracingWindowUtility.SetHidden(m_FilterStatusLabel, false);
        }

        public override Task OnDataAvailable(TracingData data)
        {
            m_Data = data;
            // The TreeView was already created and attached in Create(); just forward the data.
            return m_TickInspectorTreeView.OnDataAvailable(data);
        }

        protected internal override Task OnSelectedFrameChanged(FrameID selectedFrameID)
        {
            m_SelectedFrameID = selectedFrameID;
            return Task.CompletedTask;
        }

        protected internal override Task OnSelectedTickChanged(TickID tickID)
        {
            m_SelectedTickID = tickID;
            UpdateMetadataVisibility();
            if (m_SelectedTickID.Equals(default))
            {
                var showEmptyState = HasSelectedFrame && m_Data.ClientWorldData.IsCreated;
                TracingWindowUtility.SetHidden(m_Root, !showEmptyState);
                TracingWindowUtility.SetHidden(m_EmptyStateContainer, !showEmptyState);
                TracingWindowUtility.SetHidden(m_TreeViewRoot, true);
                return Task.CompletedTask;
            }

            TracingWindowUtility.SetHidden(m_Root, false);
            TracingWindowUtility.SetHidden(m_EmptyStateContainer, true);
            TracingWindowUtility.SetHidden(m_TreeViewRoot, false);

            return Task.CompletedTask;
        }

        public override async Task OnSelectedTickOrFrameChanged(TracingSelectionChange tracingSelectionChange)
        {
            await base.OnSelectedTickOrFrameChanged(tracingSelectionChange);
            await m_TickInspectorTreeView.OnSelectedTickOrFrameChanged(tracingSelectionChange);
            UpdateTickMetadata();
            SetToolBarLabels();
        }

        public override void Clear()
        {
            m_SelectedFrameID = default(FrameID);
            m_SelectedTickID = default(TickID);
            m_Data = default(TracingData);
            TracingWindowUtility.SetHidden(m_Root, true);
            TracingWindowUtility.SetHidden(m_EmptyStateContainer, true);
            TracingWindowUtility.SetHidden(m_TreeViewRoot, false);
            m_SearchField?.SetValueWithoutNotify(String.Empty);
            // Don't m_Root.Clear() — that would permanently detach the inner TreeView container,
            // and since OnDataAvailable no longer re-attaches it, the UI would stay blank.
            // Delegate to the child view, which clears its own state.
            m_TickInspectorTreeView?.Clear();
            m_MetadataFoldout?.SetMetadata(default);
            m_MetadataFoldout?.SetTickDiffReasons(DiffInfo.DiffReasons.Undefined, DiffInfo.AllDiffReasons);
            UpdateMetadataVisibility();
        }

        void SetToolBarLabels()
        {
            var tickLabel = m_Toolbar.Q<Label>(k_TimelineTickLabel);
            var frameLabel = m_Toolbar.Q<Label>(k_TimelineFrameLabel);
            var hasTick = m_SelectedTickID.value.IsValid;

            frameLabel.text = $"Frame {m_SelectedFrameID.value}";
            if (hasTick)
                tickLabel.text = $"Tick {m_SelectedTickID.value.TickIndexForValidTick}";

            // In the empty state (frame selected, no tick) only the frame chevron is shown.
            TracingWindowUtility.SetHidden(tickLabel, !hasTick);
        }

        void OnSearchFieldValueChanged(ChangeEvent<string> evt)
        {
            // TODO: Implement search input filtering (MTT-15336 / follow-up ticket)
        }

        public override void Dispose()
        {
            m_TickInspectorTreeView?.Dispose();
            m_SearchField?.UnregisterValueChangedCallback(OnSearchFieldValueChanged);
        }

        public void SetFilters(TracingViewFilters filters)
        {
            m_Filters = filters;
            m_TickInspectorTreeView.OnSetFilters(filters);
            UpdateFilterStatus(filters);
        }
    }
}

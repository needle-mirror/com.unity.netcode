using System;
using System.Collections.Generic;
using Unity.Netcode.Tracing;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor.Tracing.UI.TracingToolbar
{
    /// <summary>
    /// The toolbar entry point of the tracing filter: the dropdown button and the owner of the persistent
    /// <see cref="TracingFilterState"/>. The popup itself is a per-open <see cref="TracingFilterDropdown"/>.
    /// </summary>
    [UxmlElement]
    partial class TracingTargetFilter: VisualElement
    {
        const string k_ClassUnityArrowIcon = "unity-base-popup-field__arrow";
        internal const string k_ButtonText = "Tracing Target and Filters";
        internal const string k_TooltipTargetsChanged = "One or more tracing target is changed.";

        readonly TracingFilterState m_State = new();
        TracingFilterDropdown m_Dropdown;
        Button m_Button;
        VisualElement m_ButtonContainer;
        Label m_ButtonLabel;
        VisualElement m_ButtonIconContainer;
        VisualElement m_ChangedIndicator;

        /// <summary>
        /// Represents a callback for when tracing target visibility changes.
        /// </summary>
        ///
        /// <param name="hiddenSystemTypes">Contains Hidden System Names.</param>
        /// <param name="hiddenComponentTypes">Contains Hidden Component Types</param>
        public delegate void TracingVisibilityChangedHandler(HashSet<string> hiddenSystemTypes, HashSet<string> hiddenComponentTypes);
        /// <summary>
        /// Invoked when the visibility state of tracing targets changes.
        /// Passes a list of hidden target type names to subscribers.
        /// </summary>
        public event TracingVisibilityChangedHandler OnVisibilityTargetsChanged;

        /// <summary>
        /// Invoked when the user selects or deselects a diff tag. Passes the mask of selected reasons.
        /// </summary>
        public event Action<DiffInfo.DiffReasons> OnSelectedDiffReasonsChanged;

        /// <summary>
        /// Invoked when the user flips "Changed values only".
        /// </summary>
        public event Action<bool> OnChangedValuesOnlyChanged;

        /// <summary>
        /// Invoked when the user moves the fuzzy diff threshold slider or edits its field.
        /// </summary>
        public event Action<float> OnFuzzyDiffThresholdChanged;

        /// <summary>The persistent filter state the dropdown reads and mutates.</summary>
        internal TracingFilterState State => m_State;

        public void SetSelectedDiffReasons(DiffInfo.DiffReasons reasons)
        {
            m_State.SelectedDiffReasons = reasons;
            m_Dropdown?.SyncFromState();
        }

        /// <summary>Adopts the "changed values only" state.</summary>
        public void SetChangedValuesOnly(bool changedValuesOnly)
        {
            m_State.ChangedValuesOnly = changedValuesOnly;
            m_Dropdown?.SyncFromState();
        }

        /// <summary>Adopts the fuzzy diff threshold.</summary>
        public void SetFuzzyDiffThreshold(float threshold)
        {
            m_State.FuzzyDiffThreshold = threshold;
            m_Dropdown?.SyncFromState();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TracingTargetFilter"/> class.
        /// Registers callbacks for panel attachment and detachment to manage UI initialization and cleanup.
        /// </summary>
        public TracingTargetFilter()
        {
            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
        }

        /// <summary>
        /// Builds the popup for the current state and registers it as the open dropdown, so state syncs
        /// reach it. Unhooked and discarded when it closes.
        /// </summary>
        internal TracingFilterDropdown CreateDropdown()
        {
            CloseDropdown();

            var dropdown = new TracingFilterDropdown(m_State);
            dropdown.VisibilityTargetsChanged += HandleDropdownVisibilityTargetsChanged;
            dropdown.SelectedDiffReasonsChanged += HandleDropdownSelectedDiffReasonsChanged;
            dropdown.ChangedValuesOnlyChanged += HandleDropdownChangedValuesOnlyChanged;
            dropdown.FuzzyDiffThresholdChanged += HandleDropdownFuzzyDiffThresholdChanged;
            dropdown.Closed += HandleDropdownClosed;
            m_Dropdown = dropdown;
            return dropdown;
        }

        void HandleDropdownVisibilityTargetsChanged(HashSet<string> hiddenSystems, HashSet<string> hiddenComponents)
            => OnVisibilityTargetsChanged?.Invoke(hiddenSystems, hiddenComponents);

        void HandleDropdownSelectedDiffReasonsChanged(DiffInfo.DiffReasons reasons)
            => OnSelectedDiffReasonsChanged?.Invoke(reasons);

        void HandleDropdownChangedValuesOnlyChanged(bool changedValuesOnly)
            => OnChangedValuesOnlyChanged?.Invoke(changedValuesOnly);

        void HandleDropdownFuzzyDiffThresholdChanged(float threshold)
            => OnFuzzyDiffThresholdChanged?.Invoke(threshold);

        void HandleDropdownClosed()
        {
            var dropdown = m_Dropdown;
            if (dropdown == null)
                return;
            m_Dropdown = null;
            dropdown.VisibilityTargetsChanged -= HandleDropdownVisibilityTargetsChanged;
            dropdown.SelectedDiffReasonsChanged -= HandleDropdownSelectedDiffReasonsChanged;
            dropdown.ChangedValuesOnlyChanged -= HandleDropdownChangedValuesOnlyChanged;
            dropdown.FuzzyDiffThresholdChanged -= HandleDropdownFuzzyDiffThresholdChanged;
            dropdown.Closed -= HandleDropdownClosed;
        }

        // Closes the open popup, if any, and unhooks it even when the close could not fire Closed
        // (the popup was never shown or is already detached).
        void CloseDropdown()
        {
            m_Dropdown?.Close();
            HandleDropdownClosed();
        }

        void OpenDropdown()
        {
            RefreshChangedIndicator();
            CreateDropdown().Show(m_Button.worldBound, m_Button);
        }

        void CreateDropdownButton()
        {
            m_Button = new Button();
            m_ButtonContainer = new VisualElement();
            m_Button.AddToClassList(TracingToolbarUssClasses.DropdownButton);
            m_ButtonContainer.AddToClassList(TracingToolbarUssClasses.DropdownContainer);
            m_Button.Add(m_ButtonContainer);
            m_ButtonLabel = new Label(k_ButtonText);
            m_ButtonContainer.Add(m_ButtonLabel);
            m_ChangedIndicator = new VisualElement { tooltip = k_TooltipTargetsChanged };
            m_ChangedIndicator.AddToClassList(TracingToolbarUssClasses.DropdownChangedIndicator);
            m_ButtonContainer.Add(m_ChangedIndicator);
            m_ButtonIconContainer = new VisualElement();
            m_ButtonIconContainer.AddToClassList(k_ClassUnityArrowIcon);
            m_ButtonContainer.Add(m_ButtonIconContainer);
        }

        internal void RefreshChangedIndicator()
        {
            if (m_ChangedIndicator == null)
                return;
            var showIndicator = TracingDisplayState.IsShowingTraces && TracingRecordingTargets.SelectionDiffersFromRecording();
            m_ChangedIndicator.EnableInClassList(TracingToolbarUssClasses.Hidden, !showIndicator);
        }

        internal void RefreshTargetList() => m_Dropdown?.RefreshTargetList();

        // The selection-changed event fires before the tracing window re-applies the selection to the
        // config, so re-evaluate one tick later, once both are in their final state.
        void OnRecordingTargetsChanged() => m_Button?.schedule.Execute(() =>
        {
            RefreshChangedIndicator();
            RefreshTargetList();
        });

        void OnAttachToPanel(AttachToPanelEvent evt)
        {
            CreateDropdownButton();
            m_Button.clicked += OpenDropdown;
            NetcodeTracingTargetSettings.OnSelectionChanged += OnRecordingTargetsChanged;
            TracingRecordingTargets.Changed += OnRecordingTargetsChanged;
            TracingDisplayState.Changed += OnRecordingTargetsChanged;
            RefreshChangedIndicator();
            Add(m_Button);
        }

        void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            CloseDropdown();
            Remove(m_Button);
            m_Button.clicked -= OpenDropdown;
            NetcodeTracingTargetSettings.OnSelectionChanged -= OnRecordingTargetsChanged;
            TracingRecordingTargets.Changed -= OnRecordingTargetsChanged;
            TracingDisplayState.Changed -= OnRecordingTargetsChanged;
        }
    }
}

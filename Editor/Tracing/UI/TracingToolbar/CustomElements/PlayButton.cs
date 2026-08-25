using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI.TracingToolbar
{
    [UxmlElement]
    partial class PlayButton : VisualElement
    {
        const string k_UssPath = Constants.Stylesheets + "tracing-toolbar.uss";
        public const string k_MTextOncePerFrame = "Once per frame (last value)";
        public const string k_MTextOncePerTick = "Once per tick (last value)";
        public const string k_MTextOncePerSystem = "Once per system";
        const string k_MTextReplayRenderingFrequency = "Replay rendering frequency";
        const string k_MTextReplaySpeed = "Replay speed";
        const string k_ClassUnityPopIcon = "unity-base-popup-field__arrow";
        const string k_TooltipDropdownButton = "Playback frequency";
        const string k_TooltipTextOncePerTick = "Play tracing data in Scene view once per tick";
        const string k_TooltipTextOncePerFrame = "Play tracing data in Scene view once per frame";
        const string k_TooltipTextOncePerSystem = "Play tracing data in Scene view once per system";
        const string k_TooltipReplaySpeed = "Playback speed multiplier, applied to all rendering frequencies";
        public const string k_PlayButton = "play-button";
        public const string k_FrequencyDropdown = "frequency-dropdown";
        public const string k_ReplaySpeedSlider = "replay-speed-slider";
        public const string k_ReplaySpeedField = "replay-speed-field";

        StyleSheet m_StyleSheet;
        readonly Button m_DropdownButton = new();
        Button m_PlayButton;
        GenericDropdownMenu m_Menu = new();
        bool m_IsPlaying;

        public Action<ReplayRenderingFrequency> OnSelectFrequency;
        public Action<bool> OnClickPlay;
        public Action<float> OnChangeReplaySpeed;
        ReplayRenderingFrequency m_SelectedFrequency;
        float m_ReplaySpeed = UI.ReplaySpeed.Default;

        public float ReplaySpeed => m_ReplaySpeed;

        public PlayButton()
        {
            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
        }

        void SetSelectedFrequency(ReplayRenderingFrequency frequency)
        {
            m_PlayButton.tooltip = frequency switch
            {
                ReplayRenderingFrequency.OncePerSystem => k_TooltipTextOncePerSystem,
                ReplayRenderingFrequency.OncePerTick => k_TooltipTextOncePerTick,
                _ => k_TooltipTextOncePerFrame,
            };

            m_SelectedFrequency = frequency;
            UpdatePlayButtonIcon();
            OnSelectFrequency?.Invoke(frequency);
        }

        void OnClickDropdown()
        {
            m_Menu = new GenericDropdownMenu();
            m_Menu.contentContainer.styleSheets.Add(m_StyleSheet);
            var containerLabel = new Label(k_MTextReplayRenderingFrequency);
            containerLabel.AddToClassList(TracingToolbarUssClasses.DropdownContainerLabel);
            m_Menu.contentContainer.Add(containerLabel);
            m_Menu.AddSeparator("");
            m_Menu.AddItem(k_MTextOncePerFrame, m_SelectedFrequency == ReplayRenderingFrequency.OncePerFrame, _ => { SetSelectedFrequency(ReplayRenderingFrequency.OncePerFrame); }, null);
            m_Menu.AddItem(k_MTextOncePerTick, m_SelectedFrequency == ReplayRenderingFrequency.OncePerTick, _ => { SetSelectedFrequency(ReplayRenderingFrequency.OncePerTick); }, null);
            m_Menu.AddItem(k_MTextOncePerSystem, m_SelectedFrequency == ReplayRenderingFrequency.OncePerSystem, _ => { SetSelectedFrequency(ReplayRenderingFrequency.OncePerSystem); }, null);
            m_Menu.AddSeparator("");
            m_Menu.contentContainer.Add(CreateReplaySpeedRow());
            m_Menu.DropDown(m_DropdownButton.worldBound, m_DropdownButton, DropdownMenuSizeMode.Auto);
        }

        VisualElement CreateReplaySpeedRow()
        {
            var row = new VisualElement { tooltip = k_TooltipReplaySpeed };
            row.AddToClassList(TracingToolbarUssClasses.DropdownSpeedRow);

            var label = new Label(k_MTextReplaySpeed);
            label.AddToClassList(TracingToolbarUssClasses.DropdownSpeedRowLabel);
            row.Add(label);

            var slider = new Slider(UI.ReplaySpeed.Min, UI.ReplaySpeed.Max) { name = k_ReplaySpeedSlider };
            slider.AddToClassList(TracingToolbarUssClasses.DropdownSpeedRowSlider);
            slider.SetValueWithoutNotify(m_ReplaySpeed);
            row.Add(slider);

            var field = new FloatField { name = k_ReplaySpeedField, isDelayed = true };
            field.AddToClassList(TracingToolbarUssClasses.DropdownSpeedRowField);
            field.SetValueWithoutNotify(m_ReplaySpeed);
            row.Add(field);

            slider.RegisterValueChangedCallback(evt =>
            {
                var speed = SnapReplaySpeed(evt.newValue);
                slider.SetValueWithoutNotify(speed);
                field.SetValueWithoutNotify(speed);
                SetReplaySpeed(speed);
            });

            field.RegisterValueChangedCallback(evt =>
            {
                var speed = SnapReplaySpeed(evt.newValue);
                slider.SetValueWithoutNotify(speed);
                field.SetValueWithoutNotify(speed);
                SetReplaySpeed(speed);
            });

            return row;
        }

        void SetReplaySpeed(float speed)
        {
            if (Mathf.Approximately(m_ReplaySpeed, speed))
                return;
            m_ReplaySpeed = speed;
            OnChangeReplaySpeed?.Invoke(speed);
        }

        /// <summary>Clamps to the replay speed range and snaps to <see cref="UI.ReplaySpeed.Step"/> increments.</summary>
        internal static float SnapReplaySpeed(float value)
        {
            var clamped = Mathf.Clamp(value, UI.ReplaySpeed.Min, UI.ReplaySpeed.Max);
            return Mathf.Round(clamped / UI.ReplaySpeed.Step) * UI.ReplaySpeed.Step;
        }

        void TogglePlayButton()
        {
            m_IsPlaying = !m_IsPlaying;
            UpdatePlayButtonIcon();
            OnClickPlay?.Invoke(m_IsPlaying);
        }

        /// <summary>
        /// Reflects the actual Scene view playback state on the button (e.g. when replay is started or
        /// stopped from somewhere other than this button). Does not raise <see cref="OnClickPlay"/>.
        /// </summary>
        public void SetPlaying(bool playing)
        {
            if (m_IsPlaying == playing)
                return;
            m_IsPlaying = playing;
            UpdatePlayButtonIcon();
        }

        // Show the pause icon while playing back, otherwise the replay icon matching the selected rendering frequency.
        void UpdatePlayButtonIcon()
        {
            if (m_PlayButton == null)
                return;
            m_PlayButton.EnableInClassList(TracingToolbarUssClasses.IconPause, m_IsPlaying);
            m_PlayButton.EnableInClassList(TracingToolbarUssClasses.IconPlay, !m_IsPlaying && m_SelectedFrequency == ReplayRenderingFrequency.OncePerFrame);
            m_PlayButton.EnableInClassList(TracingToolbarUssClasses.IconPlayPerTick, !m_IsPlaying && m_SelectedFrequency == ReplayRenderingFrequency.OncePerTick);
            m_PlayButton.EnableInClassList(TracingToolbarUssClasses.IconPlayPerSystem, !m_IsPlaying && m_SelectedFrequency == ReplayRenderingFrequency.OncePerSystem);
        }

        void OnAttachToPanel(AttachToPanelEvent evt)
        {
            m_StyleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(k_UssPath);
            AddToClassList(TracingToolbarUssClasses.PlaybackControlsIconContainer);
            m_PlayButton = new Button() {name = k_PlayButton};
            m_PlayButton.AddToClassList(TracingToolbarUssClasses.Icon);
            m_PlayButton.tooltip = k_TooltipTextOncePerFrame;
            UpdatePlayButtonIcon();
            m_PlayButton.clicked += TogglePlayButton;
            Add(m_PlayButton);

            m_DropdownButton.name = k_FrequencyDropdown;
            m_DropdownButton.AddToClassList(k_ClassUnityPopIcon);
            m_DropdownButton.tooltip = k_TooltipDropdownButton;
            m_DropdownButton.clicked += OnClickDropdown;
            Add(m_DropdownButton);
        }

        void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            m_DropdownButton.clicked -= OnClickDropdown;
            m_PlayButton.clicked -= TogglePlayButton;
            Clear();
        }
    }





}

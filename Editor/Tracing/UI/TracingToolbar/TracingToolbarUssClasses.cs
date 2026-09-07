namespace Unity.Netcode.Editor.Tracing.UI.TracingToolbar
{
    internal static class TracingToolbarUssClasses
    {
        public const string Base = "tracing-toolbar";
        public const string Hidden = Base + "--hidden";
        public const string Invisible = Base + "--invisible";
        public const string Icon =  Base + "__icon";
        public const string DropdownContainer = Base + "__dropdown-container__button-container";
        public const string DropdownContainerLabel = Base + "__dropdown-container__label";
        public const string DropdownButton = Base + "__dropdown-container__button";
        // Replay speed row inside the frequency dropdown
        public const string DropdownSpeedRow = Base + "__dropdown-container__speed-row";
        public const string DropdownSpeedRowLabel = DropdownSpeedRow + "__label";
        public const string DropdownSpeedRowSlider = DropdownSpeedRow + "__slider";
        public const string DropdownSpeedRowField = DropdownSpeedRow + "__field";
        // icons for different states of the toolbar buttons, using BEM naming convention for clarity
        public const string IconVisible = Icon + "__visible";
        public const string IconHidden = Icon + "__hidden";
        public const string IconInfo = Icon + "__info";
        public const string IconComponent = Icon + "__component";
        public const string IconSystem = Icon + "__system";
        public const string IconPlay = Icon + "__play";
        public const string IconPlayPerTick = IconPlay + "--per-tick";
        public const string IconPlayPerSystem = IconPlay + "--per-system";
        public const string IconPause = Icon + "__pause";
        public const string IconRecord = Icon + "__record";
        public const string IconRecordOff = Icon + "__record--off";
        // Tracing filter dropdown
        public const string TracingFilter = Base + "__tracing-filter";
        public const string TracingFilterList = TracingFilter + "__list-view";
        public const string TracingFilterContentContainer = TracingFilter + "__content-container";
        public const string TracingFilterRow = TracingFilter + "__row";
        public const string TracingFilterRowEven = TracingFilterRow + "--even";
        // Info icon on the toolbar dropdown button while the selection differs from the current recording.
        public const string DropdownChangedIndicator = Base + "__dropdown-container__changed-indicator";
        // Pinned "Change Tracing Target" footer button
        public const string TracingFilterChangeButton = TracingFilter + "__change-button";
        // Divider above the pinned footer button (the menu's own separators live inside the scroll content)
        public const string TracingFilterFooterSeparator = TracingFilter + "__footer-separator";
        // "Changed values only" + select/deselect-all row under the tab strip
        public const string TracingFilterOptionsRow = TracingFilter + "__options-row";
        public const string TracingFilterRowToggle = TracingFilterRow + "__toggle";
        public const string TracingFilterSelectAll = TracingFilter + "__select-all";
        // "Fuzzy diff threshold" label + slider + field row
        public const string TracingFilterThresholdRow = TracingFilter + "__threshold-row";
        public const string TracingFilterThresholdLabel = TracingFilterThresholdRow + "__label";
        public const string TracingFilterThresholdSlider = TracingFilterThresholdRow + "__slider";
        public const string TracingFilterThresholdField = TracingFilterThresholdRow + "__field";
        // "Tracing Target" / "Diff Tags" tab strip at the top of the dropdown
        public const string TracingFilterTabHeader = TracingFilter + "__tab-header";
        public const string TracingFilterTab = TracingFilter + "__tab";
        public const string TracingFilterTabActive = TracingFilterTab + "--active";
        public const string ChangedValuesToggle = Base + "__changed-values-toggle";
        public const string TracingFilterNoResultsContainer = TracingFilter + "__no-results__container";
        public const string TracingFilterNoResultsIcon = TracingFilter + "__no-results__icon";
        public const string TracingFilterNoResultsLabel = TracingFilter + "__no-results__label";
        // Playback controls
        public const string PlaybackControls = Base + "__playback-controls";
        public const string PlaybackControlsIconContainer = PlaybackControls + "__icon-container";
    }
}

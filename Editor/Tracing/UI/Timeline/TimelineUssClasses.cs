namespace Unity.NetCode.Editor.Tracing.UI.Timeline
{
    static class TimelineUssClasses
    {
        public const string Base = "timeline";
        public const string Grow = Base + "--grow";
        public const string Loaders = Base + "__loader";
        public const string LoadingSpinnerContainer = Base + "__loading-spinner-container";
        public const string LoadingSpinner = Base + "__loading-spinner";
        public const string FrameSelector = Base + "__frame-selector";
        public const string FrameSelectorBackground = FrameSelector + "__background";
        public const string FrameSelectorLabelSmall = FrameSelector + "__label--small";
        public const string FrameSelectorFrameMarker = FrameSelector + "__frame-marker";
        public const string FrameSelectorFrameMarkerInactive = FrameSelectorFrameMarker + "--inactive";
        public const string TickSelector = Base + "__tick-selector";
        public const string TickSelectorSelected= TickSelector + "--selected";
        public const string TickSelectorContainer = TickSelector + "__container";
        public const string TickSelectorLabel = TickSelector + "__label";
        public const string TickSelectorButtonGroupContainer = TickSelector + "__button-group-container";
        public const string TickSelectorButtonGroupButton = TickSelector + "__button-group-button";
        public const string TickSelectorButtonGroupButtonDiff = TickSelectorButtonGroupButton + "--diff";
        public const string TickSelectorButtonGroupButtonDiffPartialStart = TickSelectorButtonGroupButton + "--diff-partial-start";
        public const string TickSelectorButtonGroupButtonDiffPartialEnd = TickSelectorButtonGroupButton + "--diff-partial-end";
        public const string ReplayIndicator = FrameSelector + "__replay-indicator";
    }
}

using UnityEngine.UIElements;

namespace Unity.Netcode.Editor
{
    /// <summary>
    /// Info text provider shown in the client module when profiling a Server or Dedicated Server build.
    /// Explains that only the Server module contains data in this scenario.
    /// </summary>
    class ClientModuleOnServerInfoTextProvider : IInfoTextProvider
    {
        public bool ShouldShow(InfoTextContext context)
        {
            // Show when viewing client module with no data, not in host mode, and no world metadata
            // This indicates profiling a server/dedicated server build (no local client world exists)
            // (host mode case is handled by ClientHostModeInfoTextProvider)
            return context.NetworkRole == NetworkRole.Client
                   && !context.IsFrameDataValid()
                   && !context.IsHost()
                   && !context.HasWorldMetadata();
        }

        public VisualElement CreateInfoText()
        {
            const string text1 = "When profiling a server or dedicated server build, profiler data is only available in the Server World module.";
            var text2 = $"\n\nFor more information, refer to the <a href=\"{NetcodeProfilerConstants.s_ProfilerDocsLink}\">Network Profiler documentation</a>.";
            var element = ProfilerUtils.CreateInfoTextElement(text1, text2);
            return element;
        }
    }
}

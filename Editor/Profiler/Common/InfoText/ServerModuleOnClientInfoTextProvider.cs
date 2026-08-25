using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Info text provider shown in the server module when the editor is acting as a client
    /// (connecting to a remote server or dedicated server build).
    /// Explains that only the Client module contains data in this scenario.
    /// </summary>
    class ServerModuleOnClientInfoTextProvider : IInfoTextProvider
    {
        readonly NetworkRole m_NetworkRole;

        public ServerModuleOnClientInfoTextProvider(NetworkRole networkRole)
        {
            m_NetworkRole = networkRole;
        }

        public bool ShouldShow(InfoTextContext context)
        {
            // Show when viewing server module with no data, not in host mode, and no world metadata
            // This indicates the editor is acting as a client (no local server world exists)
            return m_NetworkRole == NetworkRole.Server
                   && context.NetworkRole == NetworkRole.Server
                   && !context.IsFrameDataValid()
                   && !context.IsHost()
                   && !context.HasWorldMetadata();
        }

        public VisualElement CreateInfoText()
        {
            const string text1 = "When the Editor is acting as a client connected to a remote server, profiler data is only available in the Client World module.";
            var text2 = $"\nFor more information, refer to the <a href=\"{NetcodeProfilerConstants.s_ProfilerDocsLink}\">Network Profiler documentation</a>.";
            var element = ProfilerUtils.CreateInfoTextElement(text1, text2);
            return element;
        }
    }
}

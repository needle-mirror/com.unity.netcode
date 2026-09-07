using UnityEngine.UIElements;

namespace Unity.Netcode.Editor
{
    /// <summary>
    /// Info text provider shown in the server module when profiling a Server or Dedicated Server build
    /// with no clients connected. Explains that data only appears after clients connect.
    /// </summary>
    class ServerModuleNoClientsInfoTextProvider : IInfoTextProvider
    {
        readonly NetworkRole m_NetworkRole;

        public ServerModuleNoClientsInfoTextProvider(NetworkRole networkRole)
        {
            m_NetworkRole = networkRole;
        }

        public bool ShouldShow(InfoTextContext context)
        {
            // Show when viewing server module with no data, not in host mode, and no clients connected
            // World metadata must exist (server is running locally) but no clients are connected
            // (host mode case is handled by HostModeNoClientsInfoTextProvider)
            if (m_NetworkRole != NetworkRole.Server)
                return false;

            if (context.NetworkRole != NetworkRole.Server)
                return false;

            if (context.IsHost())
                return false;

            return !context.IsFrameDataValid() && context.HasWorldMetadata() && !context.HasConnectedClients();
        }

        public VisualElement CreateInfoText()
        {
            const string text1 = "When profiling a server or dedicated server build, profiler data is only available after at least one client connects.";
            var text2 = $"\nFor more information, refer to the <a href=\"{NetcodeProfilerConstants.s_ProfilerDocsLink}\">Network Profiler documentation</a>.";
            var element = ProfilerUtils.CreateInfoTextElement(text1, text2);
            return element;
        }
    }
}

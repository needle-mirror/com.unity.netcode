using UnityEngine.UIElements;

namespace Unity.Netcode.Editor
{
    /// <summary>
    /// Info text provider for Single World Host mode when no clients are connected.
    /// </summary>
    class HostModeNoClientsInfoTextProvider : IInfoTextProvider
    {
        readonly NetworkRole m_NetworkRole;

        public HostModeNoClientsInfoTextProvider(NetworkRole networkRole)
        {
            m_NetworkRole = networkRole;
        }

        public bool ShouldShow(InfoTextContext context)
        {
            // Only for server side when:
            // 1. In host mode (single world with both client and server flags)
            // 2. No clients connected
            if (m_NetworkRole != NetworkRole.Server)
                return false;

            if (context.NetworkRole != NetworkRole.Server)
                return false;

            if (!context.IsHost())
                return false;

            return !context.HasConnectedClients();
        }

        public VisualElement CreateInfoText()
        {
            const string text1 = "In Single-World Host mode, no network data is sent until a client connects.";
            var text2 = $"Use Binary World mode to view network traffic, or connect a client to start viewing data.\n\nFor more information, refer to the <a href=\"{NetcodeProfilerConstants.s_ProfilerDocsLink}\">Network Profiler documentation</a>.";
            var element = ProfilerUtils.CreateInfoTextElement(text1, text2);
            return element;
        }
    }
}

using UnityEngine.UIElements;

namespace Unity.Netcode.Editor
{
    /// <summary>
    /// Info text provider for Client module in Single World Host mode.
    /// Explains why client module shows no data when in host mode.
    /// </summary>
    class ClientHostModeInfoTextProvider : IInfoTextProvider
    {
        readonly NetworkRole m_NetworkRole;

        public ClientHostModeInfoTextProvider(NetworkRole networkRole)
        {
            m_NetworkRole = networkRole;
        }

        public bool ShouldShow(InfoTextContext context)
        {
            // Only for client side when in host mode
            if (m_NetworkRole != NetworkRole.Client)
                return false;

            if (context.NetworkRole != NetworkRole.Client)
                return false;

            return context.IsHost();
        }

        public VisualElement CreateInfoText()
        {
            const string text1 = "In Single-World Host mode, profiler data is displayed in the Server World module.";
            var text2 = $"The client and server run in the same world, so network data appears only once.\nTo view client-specific data, connect a remote client or use Binary World mode.\n\nFor more information, refer to the <a href=\"{NetcodeProfilerConstants.s_ProfilerDocsLink}\">Network Profiler documentation</a>.";
            var element = ProfilerUtils.CreateInfoTextElement(text1, text2);
            return element;
        }
    }
}

using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Info text provider for server-only message in Prediction and Interpolation tab.
    /// Always shows for server since prediction is client-only.
    /// </summary>
    class ServerOnlyInfoTextProvider : IInfoTextProvider
    {
        readonly NetworkRole m_NetworkRole;

        public ServerOnlyInfoTextProvider(NetworkRole networkRole)
        {
            m_NetworkRole = networkRole;
        }

        public bool ShouldShow(InfoTextContext context)
        {
            return m_NetworkRole == NetworkRole.Server;
        }

        public VisualElement CreateInfoText()
        {
            const string text1 = "Prediction and Interpolation stats are not available for servers.";
            const string text2 = "Only clients can use prediction to manage latency and improve responsiveness.";
            var element = ProfilerUtils.CreateInfoTextElement(text1, text2);
            return element;
        }
    }
}

using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Generic info text provider for when no data was sent/received in the current frame.
    /// Should be registered last as a fallback message.
    /// </summary>
    class NoDataInfoTextProvider : IInfoTextProvider
    {
        readonly string m_PacketDirection;

        public NoDataInfoTextProvider(string packetDirection)
        {
            m_PacketDirection = packetDirection;
        }

        public bool ShouldShow(InfoTextContext context)
        {
            // Show when frame data is invalid (no data sent/received)
            return !context.IsFrameDataValid();
        }

        public VisualElement CreateInfoText()
        {
            var text1 = $"No ghost snapshots were {m_PacketDirection} this frame.";
            var text2 = $"This is expected behavior for network scenarios where frame rate is higher than send rate.\n\nFor more information, refer to the <a href=\"{NetcodeProfilerConstants.s_ProfilerDocsLink}\">Network Profiler documentation</a>.";
            var element = ProfilerUtils.CreateInfoTextElement(text1, text2);
            return element;
        }
    }
}

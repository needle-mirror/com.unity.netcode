namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Context object passed to info text providers for evaluating conditions.
    /// Contains frame data and network role with helper methods for accessing captured profiler metadata.
    /// </summary>
    class InfoTextContext
    {
        /// <summary>
        /// The current frame's profiler data
        /// </summary>
        public NetcodeFrameData FrameData { get; set; }

        /// <summary>
        /// The network role (Server or Client) for the current profiler view
        /// </summary>
        public NetworkRole NetworkRole { get; set; }

        /// <summary>
        /// Check if the profiled session was in Single World Host mode (both client and server flags).
        /// Uses captured profiler metadata from when the data was recorded.
        /// </summary>
        /// <returns>True if in host mode, false otherwise</returns>
        public bool IsHost()
        {
            return FrameData.isHostMode;
        }

        /// <summary>
        /// Check if there were connected clients with active NetworkStreamInGame components.
        /// Uses captured profiler metadata from when the data was recorded.
        /// </summary>
        /// <returns>True if at least one client was connected and in-game, false otherwise</returns>
        public bool HasConnectedClients()
        {
            return FrameData.hasConnectedClients;
        }

        /// <summary>
        /// Indicates whether any world metadata has been successfully captured for this network role.
        /// False when the world doesn't exist locally (e.g., editor acting as client has no server world).
        /// Uses captured profiler metadata from when the data was recorded.
        /// </summary>
        /// <returns>True if world metadata exists, false otherwise</returns>
        public bool HasWorldMetadata()
        {
            return FrameData.hasWorldMetadata;
        }

        /// <summary>
        /// Check if the current frame has valid profiler data.
        /// </summary>
        /// <returns>True if frame data is valid, false otherwise</returns>
        public bool IsFrameDataValid() => FrameData.isValid;
    }
}
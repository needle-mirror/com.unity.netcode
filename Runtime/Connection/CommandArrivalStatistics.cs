using Unity.Collections;

namespace Unity.NetCode
{
    /// <summary>
    /// Stores statistics pertaining to the frequency and reliability of received commands from a client.
    /// Thus, only valid on the server.
    /// May help you diagnose input/command issues.
    /// </summary>
    public struct CommandArrivalStatistics
    {
        // TODO - Add support for num commands EXPECTED to arrive, thus allowing users to see command losses.
        /// <summary>
        /// The total count of command PACKETS that arrived.
        /// A single netcode command packet will typically contain multiple commands, for redundancy.
        /// </summary>
        public int NumCommandPacketsArrived;

        /// <summary>
        /// The total count of commands that arrived.
        /// A single netcode command packet will typically contain multiple commands, for redundancy.
        /// </summary>
        public uint NumCommandsArrived;

        /// <summary>
        /// How many commands that arrived were resends of commands we already received?
        /// </summary>
        public uint NumRedundantResends;

        /// <summary>
        /// The number of individual commands that didn't arrive in time to be used.
        /// Therefore, they were pointless to send.
        /// A single netcode command packet will typically contain multiple commands, for redundancy.
        /// </summary>
        /// <remarks>Use this field to optimize <see cref="ClientTickRate.NumAdditionalCommandsToSend"/>.</remarks>
        public uint NumArrivedTooLate;

        /// <summary>
        /// Rolling average of the payload size of the received input (i.e. command) packets, not including Transport headers.
        /// </summary>
        public float AvgCommandPayloadSizeInBits;

        /// <summary>Percentage of commands arrived too late.</summary>
        public double ArrivedTooLatePercent => NumCommandsArrived != 0 ? ((double) NumArrivedTooLate / NumCommandsArrived) : 0;

        /// <summary>Percentage of commands that were resent redundantly.</summary>
        public double ResendPercent => NumCommandsArrived != 0 ? ((double) NumRedundantResends / NumCommandsArrived) : 0;

        /// <summary>Average commands packed into each packet.</summary>
        public double AvgCommandsPerPacket => NumCommandPacketsArrived != 0 ? ((double)NumCommandsArrived / NumCommandPacketsArrived) : 0;

        /// <summary>
        /// Debug string.
        /// </summary>
        /// <returns>A formatted debug string.</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString128Bytes ToFixedString()
        {
            var commandsPerPacket = AvgCommandsPerPacket;
            var resendPercent = (int) (ResendPercent * 100);
            var tooLatePercent = (int) (ArrivedTooLatePercent * 100);
            return $"CAS[packets:{NumCommandPacketsArrived},commands:{NumCommandsArrived},avgCommandsPerPacket:{commandsPerPacket},resends:{NumRedundantResends} {resendPercent}%,late:{NumArrivedTooLate} {tooLatePercent}%,avgSize:{CommandDataUtility.FormatBitsBytes((int)AvgCommandPayloadSizeInBits)}]";
        }

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }
}

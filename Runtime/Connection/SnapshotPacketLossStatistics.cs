using Unity.Collections;
using Unity.Mathematics;

namespace Unity.NetCode
{
    /// <summary>
    /// Stores packet loss causes and statistics for all received snapshots. Thus, client-only (with one exception).
    /// Access via <see cref="NetworkSnapshotAck"/>.
    /// </summary>
    /// <remarks>Very similar approach to <see cref="Unity.Networking.Transport.UnreliableSequencedPipelineStage"/> Statistics.</remarks>
    public struct SnapshotPacketLossStatistics
    {
        /// <summary>
        ///     On the client, it counts the number of snapshot packets received by said client from the server.
        ///     On the server, it stores the number of snapshots sent.
        /// </summary>
        public ulong NumPacketsReceived;
        /// <summary>Server-only. Stores the number of snapshots the client has successfully replied that they have acked.</summary>
        public ulong NumPacketsAcked;
        /// <summary>Counts the number of snapshot packets dropped (i.e. "culled") due to invalid SequenceId. I.e. Implies the packet arrived, but out of order.</summary>
        public ulong NumPacketsCulledOutOfOrder;
        /// <summary>
        /// The Netcode package can only process one snapshot per render frame. If 2 or more arrive on the same frame, we'll delete all but one, without processing them.
        /// Therefore, this form of packet loss is common when your connection jitter is higher than a <see cref="ClientServerTickRate.NetworkTickRate"/> interval.
        /// E.g. If your jitter is ±20ms, but your NetworkTickRate is 60Hz (16.67ms), you can expect a lot of packet clobbering.
        /// </summary>
        /// <remarks>This is also called a "Packet Burst".</remarks>
        public ulong NumPacketsCulledAsArrivedOnSameFrame;
        /// <summary>Detects gaps in <see cref="NetworkSnapshotAck.CurrentSnapshotSequenceId"/> to determine real packet loss.</summary>
        public ulong NumPacketsDroppedNeverArrived;
        /// <summary>Denotes how many times the client has reported a snapshot ack error, leading to the ack history buffer to have to be reset.</summary>
        public ulong NumClientAckErrorsEncountered;

        /// <summary>Server-only. Percentage of all snapshot packets sent that the client has acked.</summary>
        public double AckPercent => NumPacketsReceived != 0 ? NumPacketsAcked / (double) (NumPacketsReceived) : 0;
        /// <summary>Percentage of all snapshot packets - that we assume must have been sent to us (based on SequenceId) - which are lost due to network-caused packet loss.</summary>
        public double NetworkPacketLossPercent => NumPacketsReceived != 0 ? NumPacketsDroppedNeverArrived / (double) (NumPacketsReceived + NumPacketsDroppedNeverArrived) : 0;
        /// <summary>Percentage of all snapshot packets - that we assume must have been sent to us (based on SequenceId) - which are lost due to arriving out of order (and thus being culled).</summary>
        public double OutOfOrderPacketLossPercent => NumPacketsReceived != 0 ? NumPacketsCulledOutOfOrder / (double) (NumPacketsReceived + NumPacketsDroppedNeverArrived) : 0;
        /// <summary>Percentage of all snapshot packets - that we assume must have been sent to us (based on SequenceId) - which are culled due to arriving on the same frame as another snapshot.</summary>
        public double ArrivedOnTheSameFrameClobberedPacketLossPercent => NumPacketsReceived != 0 ? NumPacketsCulledAsArrivedOnSameFrame / (double) (NumPacketsReceived + NumPacketsDroppedNeverArrived) : 0;
        /// <summary>Percentage of all snapshot packets - that we assume must have been sent to us (based on SequenceId) - which are dropped (for any reason).</summary>
        public double CombinedPacketLossPercent => NumPacketsReceived != 0 ? (CombinedPacketLossCount) / (double) (NumPacketsReceived + NumPacketsDroppedNeverArrived) : 0;
        /// <summary>Count of packets lost in some form.</summary>
        public ulong CombinedPacketLossCount => NumPacketsDroppedNeverArrived + NumPacketsCulledOutOfOrder + NumPacketsCulledAsArrivedOnSameFrame;

        /// <summary>
        /// Adds two SnapshotPacketLossStatistics
        /// </summary>
        /// <param name="a">First SnapshotPacketLossStatistics</param>
        /// <param name="b">Second SnapshotPacketLossStatistics</param>
        /// <returns>The resulting sum of the two SnapshotPacketLossStatistics.</returns>
        public static SnapshotPacketLossStatistics operator +(SnapshotPacketLossStatistics a, SnapshotPacketLossStatistics b)
        {
            a.NumPacketsReceived += b.NumPacketsReceived;
            a.NumPacketsAcked += b.NumPacketsAcked;
            a.NumPacketsCulledOutOfOrder += b.NumPacketsCulledOutOfOrder;
            a.NumPacketsCulledAsArrivedOnSameFrame += b.NumPacketsCulledAsArrivedOnSameFrame;
            a.NumPacketsDroppedNeverArrived += b.NumPacketsDroppedNeverArrived;
            return a;
        }

        /// <summary>
        /// Subtracts two SnapshotPacketLossStatistics
        /// </summary>
        /// <param name="a">First SnapshotPacketLossStatistics</param>
        /// <param name="b">Second SnapshotPacketLossStatistics</param>
        /// <returns>The resulting difference of the two SnapshotPacketLossStatistics.</returns>
        public static SnapshotPacketLossStatistics operator -(SnapshotPacketLossStatistics a, SnapshotPacketLossStatistics b)
        {
            // Guard subtraction as it can get negative when we're polling 3s intervals.
            a.NumPacketsReceived -= math.min(a.NumPacketsReceived, b.NumPacketsReceived);
            a.NumPacketsAcked -= math.min(a.NumPacketsAcked, b.NumPacketsAcked);
            a.NumPacketsCulledOutOfOrder -= math.min(a.NumPacketsCulledOutOfOrder, b.NumPacketsCulledOutOfOrder);
            a.NumPacketsCulledAsArrivedOnSameFrame -= math.min(a.NumPacketsCulledAsArrivedOnSameFrame, b.NumPacketsCulledAsArrivedOnSameFrame);
            a.NumPacketsDroppedNeverArrived -= math.min(a.NumPacketsDroppedNeverArrived, b.NumPacketsDroppedNeverArrived);
            return a;
        }

        /// <summary>
        /// Formatted dump of statistics for this world-type.
        /// </summary>
        /// <returns>Formatted dump of statistics for this world-type.</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString()
        {
            if (NumPacketsReceived == 0) return "SPLS[default]";
            if (NumPacketsAcked > 0) return $"SPLS[sent:{NumPacketsReceived}, receivedAck:{NumPacketsAcked} {(int) (AckPercent * 100)}%]";
            return $"SPLS[received:{NumPacketsReceived}, combinedPL:{CombinedPacketLossCount} {(int) (CombinedPacketLossPercent * 100)}%, networkPL:{NumPacketsDroppedNeverArrived} {(int) (NetworkPacketLossPercent * 100)}%, outOfOrderPL:{NumPacketsCulledOutOfOrder} {(int) (OutOfOrderPacketLossPercent * 100)}%, clobberedPL:{NumPacketsCulledAsArrivedOnSameFrame} {(int) (ArrivedOnTheSameFrameClobberedPacketLossPercent * 100)}%]";
        }

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }
}

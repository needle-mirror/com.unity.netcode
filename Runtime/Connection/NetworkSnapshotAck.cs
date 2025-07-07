using System;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// Temporary type, used to upgrade to new component type, to be removed before final 1.0
    /// </summary>
    [Obsolete("NetworkSnapshotAckComponent has been deprecated. Use GhostInstance instead (UnityUpgradable) -> NetworkSnapshotAck", true)]
    public struct NetworkSnapshotAckComponent : IComponentData
    {}

    /// <summary>Client and Server Component. One per NetworkId entity, stores SnapshotAck and Ping info for a client.</summary>
    public struct NetworkSnapshotAck : IComponentData
    {
        internal void UpdateReceivedByRemote(NetworkTick tick, uint mask, out int numSnapshotErrorsRequiringReset)
        {
            numSnapshotErrorsRequiringReset = 0;
            if (Hint.Unlikely(!tick.IsValid))
            {
                if (Hint.Unlikely(LastReceivedSnapshotByRemote.IsValid))
                {
                    numSnapshotErrorsRequiringReset = ReceivedSnapshotByRemoteMask.Length;
                    SnapshotPacketLoss.NumClientAckErrorsEncountered++;
                    ReceivedSnapshotByRemoteMask.Clear();
                    LastReceivedSnapshotByRemote = NetworkTick.Invalid;
                    FirstReceivedSnapshotByRemote = NetworkTick.Invalid;
                }
                return;
            }

            // For any ticks SINCE our last stored tick (or if we get the same tick again), we should shift the
            // entire mask LEFT by that delta (shamt), then apply the new mask on top of the existing one,
            // as the client may have more up-to-date ack info.
            var shamt = Hint.Likely(LastReceivedSnapshotByRemote.IsValid) ? tick.TicksSince(LastReceivedSnapshotByRemote) : 0;
            if (Hint.Likely(shamt >= 0))
            {
                ReceivedSnapshotByRemoteMask.ShiftLeftExt(shamt);

                // Note: Clobbering the mask is valid, because the client should never send us a false value
                // after sending us a true value for a given tick. But perform the OR operation anyway,
                // to safeguard against malicious or erring clients.
                const int writeOffset = 0;
                const int numBitsToWrite = 32;
                var previousMask = ReceivedSnapshotByRemoteMask.GetBits(writeOffset, numBitsToWrite);
                mask |= (uint) previousMask;
                ReceivedSnapshotByRemoteMask.SetBits(writeOffset, mask, numBitsToWrite);
                LastReceivedSnapshotByRemote = tick;
                if (Hint.Unlikely(!FirstReceivedSnapshotByRemote.IsValid)) FirstReceivedSnapshotByRemote = tick;
                SnapshotPacketLoss.NumPacketsAcked += (ulong) (math.countbits(mask) - math.countbits(previousMask));
            }
            // Else, for older ticks (because YES - the client can send negative ticks relative to the last acked),
            // we don't do anything, as they cannot correctly contain new ack information (due to the sequential
            // requirement implicit to snapshots).
        }

        /// <summary>
        /// Return true if the snapshot for tick <paramref name="tick"/> has been received (from a client perspective)
        /// or acknowledged (from the servers POV).
        /// </summary>
        /// <param name="tick">Tick to query.</param>
        /// <returns>Whether the snapshot for tick <paramref name="tick"/> has been received (from a client perspective)
        /// or acknowledged (from the servers POV).</returns>
        public bool IsReceivedByRemote(NetworkTick tick) => IsReceivedByRemote(tick, false);

        /// <summary>
        /// Return true if the snapshot for tick <paramref name="tick"/> has been received (from a client perspective)
        /// or acknowledged (from the servers POV).
        /// </summary>
        /// <param name="tick">Tick to query.</param>
        /// <param name="backupValue">Denotes the historic value to use if the real answer is now unknowable.
        /// Pragmatically, this can only occur for extremely rarely sent static-optimized ghosts.</param>
        /// <returns>Whether the snapshot for tick <paramref name="tick"/> has been received (from a client perspective)
        /// or acknowledged (from the servers POV).</returns>
        public bool IsReceivedByRemote(NetworkTick tick, bool backupValue)
        {
            if (!tick.IsValid || !LastReceivedSnapshotByRemote.IsValid || !FirstReceivedSnapshotByRemote.IsValid)
                return false;
            int bit = LastReceivedSnapshotByRemote.TicksSince(tick);
            if (bit < 0)
                return false;
            if (bit >= ReceivedSnapshotByRemoteMask.Length)
            {
                // The following is an optimization: Any acks that are older than our buffer are very likely to be static-optimized ghosts
                // being re-checked for changes. Returning false would mean they fail their `CanUseStaticOptimization` check.
                // However, we no longer know if this snapshot tick is acked, as we've lost that info.
                // Additionally; the client can signal "reset all ack-masks" above, which should invalidate all previous acks.

                // Thus, lets use some additional data to infer whether or not we've acked this tick!
                // We can do so in EITHER of the following cases:
                // A) The client has NEVER sent us a 'reset all acks' event.
                // In this case, we can ack INFINITELY old ticks.
                // B) If the client HAS signalled at least one 'reset all acks' event, we can check to see if their most recent
                // good snapshot (i.e. the first snapshot since the reset) is <= this tick we're checking. This allows us to ack
                // ticks up to half the precision of the tick value itself (which - pragmatically - means "infinitely").
                var isAllowedToInferInfinitelyFarBack = backupValue && SnapshotPacketLoss.NumClientAckErrorsEncountered == 0;
                var isVerifiablyGoodAck = backupValue && tick.TicksSince(FirstReceivedSnapshotByRemote) >= 0;
                return isAllowedToInferInfinitelyFarBack || isVerifiablyGoodAck;
            }
            var set = ReceivedSnapshotByRemoteMask.GetBits(bit) != 0;
            return set;
        }

        /// <summary>
        /// <para>The last snapshot (tick) received from the remote peer.</para>
        /// <para>For the client, it represents the last received snapshot received from the server.</para>
        /// <para>For the server, it is the last acknowledge packet that has been received by client.</para>
        /// </summary>
        public NetworkTick LastReceivedSnapshotByRemote;
        /// <summary>
        /// Denotes the first valid snapshot received by the remote connection.
        /// Only used to safeguard snapshot acking against the client's 'reset all ack-masks' logic.
        /// </summary>
        public NetworkTick FirstReceivedSnapshotByRemote;
        internal UnsafeBitArray ReceivedSnapshotByRemoteMask;

        /// <summary>
        /// <para>The field has a different meaning on the client vs on the server:</para>
        /// <para>Client: it is the last received ghost snapshot from the server.</para>
        /// <para>Server: record the last command tick that has been received. Used to discard either out of order or late commands.</para>
        /// </summary>
        public NetworkTick LastReceivedSnapshotByLocal;

        /// <summary>
        /// <para>
        /// Server Only, denote if the last received command for a full tick from the client. It is used to tune the command age.
        /// </para>
        /// </summary>
        internal NetworkTick MostRecentFullCommandTick;

        /// <summary>
        /// <para>Client: Records the last Snapshot Sequence Id received by this client.</para>
        /// <para>Server: Increments every time a Snapshot is successfully dispatched (thus, assumed sent).</para>
        /// <para><see cref="SnapshotPacketLoss"/></para>
        /// </summary>
        public byte CurrentSnapshotSequenceId;
        /// <summary>
        /// Client-only, a bitmask that indicates which of the last 32 snapshots has been received
        /// from the server.
        /// On the server it is always 0.
        /// </summary>
        public uint ReceivedSnapshotByLocalMask;
        /// <summary>
        /// Server-only, the number of ghost prefabs loaded by remote client. On the client is not used and it is always 0.
        /// </summary>
        public uint NumLoadedPrefabs;

        /// <inheritdoc cref="SnapshotPacketLossStatistics"/>
        public SnapshotPacketLossStatistics SnapshotPacketLoss;

        /// <inheritdoc cref="CommandArrivalStatistics"/>
        public CommandArrivalStatistics CommandArrivalStatistics;

        /// <summary>
        /// Update the number of loaded prefabs nad sync the interpolation delay for the remote connection.
        /// </summary>
        /// <remarks>
        /// The state of the component is not changed if the <paramref name="remoteTime"/> is less than <see cref="LastReceivedRemoteTime"/>,
        /// because that will indicate a more recent message has been already processed.
        /// </remarks>
        /// <param name="remoteTime"></param>
        /// <param name="numLoadedPrefabs"></param>
        /// <param name="interpolationDelay"></param>
        internal void UpdateRemoteAckedData(uint remoteTime, uint numLoadedPrefabs, uint interpolationDelay)
        {
            //Because the remote time is updated also by RPC and there is no order guarante for witch is handled
            //first (snapshost or rpc message) it is necessary to accept update if received remoteTime
            //is also equals to the LastReceivedRemoteTime.
            if (remoteTime != 0 && (!SequenceHelpers.IsNewer(LastReceivedRemoteTime, remoteTime) || LastReceivedRemoteTime == 0))
            {
                NumLoadedPrefabs = numLoadedPrefabs;
                RemoteInterpolationDelay = interpolationDelay;
            }
        }

        /// <summary>
        /// Sanitizes the RTT from the localTime correctly.
        /// </summary>
        /// <param name="localTime"></param>
        /// <param name="localTimeMinusRTT"></param>
        /// <returns>Returns -1 if invalid.</returns>
        internal static int CalculateRttViaLocalTime(uint localTime, uint localTimeMinusRTT)
        {
            if (localTimeMinusRTT == 0)
                return -1;
            // Highest bit set means we got a negative value, which can happen on low ping due to clock difference between client and server
            uint lastReceivedRTT = localTime - localTimeMinusRTT;
            if ((lastReceivedRTT & (1 << 31)) != 0)
                return -1;
            return (int) lastReceivedRTT;
        }

        /// <summary>
        /// Because RPCs are reliable, they may arrive many RTTs after being sent.
        /// Thus, it does not make sense to calculate the RTT of them manually.
        /// We use the transport feature instead.
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="driver"></param>
        /// <param name="driverInstance"></param>
        /// <param name="pipelineStage"></param>
        /// <param name="reliableSequencedPipelineStageId"></param>
        /// <returns></returns>
        internal static unsafe int GetRpcRttFromReliablePipeline(NetworkStreamConnection connection,
            ref NetworkDriver driver, ref NetworkDriverStore.NetworkDriverInstance driverInstance,
            in NetworkPipeline pipelineStage, NetworkPipelineStageId reliableSequencedPipelineStageId)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(pipelineStage.Id == driverInstance.reliablePipeline.Id);
#endif
            driver.GetPipelineBuffers(driverInstance.reliablePipeline, reliableSequencedPipelineStageId, connection.Value, out _, out _, out var sharedBuffer);
            var sharedCtx = (ReliableUtility.SharedContext*)sharedBuffer.GetUnsafePtr();
            // Note: The Transport `RTTInfo` value has already accounted for client CPU processing time.
            var rttInfo = sharedCtx->RttInfo;
            // Bit of a hack: Discard the value if it's EXACTLY identical to the default `RTTInfo` struct.
            // TODO - Fix this if/once transport exposes a way to see if this is the default RTT.
            var isExactlyDefaultRttValue = rttInfo.SmoothedRtt == 50
                                           && rttInfo.LastRtt == 50
                                           && rttInfo.SmoothedVariance == 5
                                           && rttInfo.ResendTimeout == 50;
            return isExactlyDefaultRttValue ? -1 : rttInfo.LastRtt;
        }

        /// <summary>
        /// Store the time (local) at which a message/packet has been received,
        /// as well as the latest received remote time (than will send back to the remote peer) and update the
        /// <see cref="EstimatedRTT"/> and <see cref="DeviationRTT"/> for the connection.
        /// </summary>
        /// <remarks>
        /// The state of the component is not changed if the <paramref name="remoteTime"/> is less than <see cref="LastReceivedRemoteTime"/>,
        /// because that will indicate a more recent message has been already processed.
        /// </remarks>
        /// <param name="remoteTime"></param>
        /// <param name="lastReceivedRTT">Our calculation of the RTT, using whatever metrics are available to us.
        /// Assumes CPU processing time is already accounted for!</param>
        /// <param name="localTime"></param>
        internal void UpdateRemoteTime(uint remoteTime, int lastReceivedRTT, uint localTime)
        {
            //Because we sync time using both RPC and snapshot it is more correct to also accept
            //update the stats for a remoteTime that is equals to the last received one.
            if (remoteTime != 0 && (!SequenceHelpers.IsNewer(LastReceivedRemoteTime, remoteTime) || LastReceivedRemoteTime == 0))
            {
                LastReceivedRemoteTime = remoteTime;
                LastReceiveTimestamp = localTime;
                if (lastReceivedRTT < 0)
                    return;
                if (EstimatedRTT == 0)
                    EstimatedRTT = lastReceivedRTT;
                else
                    EstimatedRTT = EstimatedRTT * 0.875f + lastReceivedRTT * 0.125f;
                var latestDeviationRTT = math.abs(lastReceivedRTT - EstimatedRTT);
                DeviationRTT = DeviationRTT * 0.75f + latestDeviationRTT * 0.25f;
            }
        }

        /// <inheritdoc cref="CalculateSequenceIdDelta(byte,byte,bool)"/>
        internal readonly int CalculateSequenceIdDelta(byte current, bool isSnapshotConfirmedNewer) => CalculateSequenceIdDelta(current, CurrentSnapshotSequenceId, isSnapshotConfirmedNewer);

        /// <summary>
        /// Returns the delta (in ticks) between <see cref="current"/> and <see cref="last"/> SequenceIds, but assumes
        /// that <see cref="NetworkTime.ServerTick"/> logic (to discard old snapshots) is correct.
        /// Thus:
        /// - If the snapshot is confirmed newer, we can check a delta of '0 to byte.MaxValue'.
        /// - If the snapshot is confirmed old, we can check a delta of '0 to -byte.MaxValue'.
        /// </summary>
        internal static int CalculateSequenceIdDelta(byte current, byte last, bool isSnapshotConfirmedNewer)
        {
            if (isSnapshotConfirmedNewer)
                return (byte)(current - last);
            return -(byte)(last - current);
        }

        /// <summary>
        /// The last remote time stamp received by the connection. The remote time is sent back (via command for the client or in the snapshot for the server)
        /// and used to calculate the round trip time for the connection.
        /// </summary>
        public uint LastReceivedRemoteTime;
        /// <summary>
        /// The local time stamp at which the connection has received the last message. Used to calculate the elapsed "processing" time and reported to
        /// the remote peer to correctly update the round trip time.
        /// </summary>
        public uint LastReceiveTimestamp;
        /// <summary>
        /// The calculated exponential smoothing average connection round trip time, in milliseconds.
        /// </summary>
        public float EstimatedRTT;
        /// <summary>
        /// The round trip time average deviation from the <see cref="EstimatedRTT"/>, in milliseconds.
        /// It is not a real standard deviation but an approximation using a simpler exponential smoothing average.
        /// </summary>
        public float DeviationRTT;
        /// <summary>
        /// How late the commands are received by server. Is a negative fixedPoint Q24:8 number that measure how many ticks behind the server
        /// was when he received the command, and it used as feedback by the <see cref="NetworkTimeSystem"/> to synchronize the
        /// <see cref="NetworkTime.ServerTick"/> such that the client always runs ahead of the server.
        /// A positive number indicates that the client is running behind the server.
        /// A negative number indicates that the client is running ahead of the server.
        /// </summary>
        public int ServerCommandAge;
        /// <summary>
        /// The reported interpolation delay reported by the client (in number of ticks).
        /// </summary>
        public uint RemoteInterpolationDelay;

        /// <summary>Modifies the <see cref="LastReceivedRemoteTime"/> by accounting for this machines processing time.</summary>
        /// <param name="localTime"></param>
        /// <returns></returns>
        internal readonly uint CalculateReturnTime(uint localTime)
        {
            var returnTime = LastReceivedRemoteTime;
            if (returnTime != 0)
            {
                var processingTime = (localTime - LastReceiveTimestamp);
                returnTime += processingTime;
            }
            return returnTime;
        }
    }
}

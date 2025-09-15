#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Profiling;

namespace Unity.NetCode
{
    /// <summary>Handles sending of despawn messages in ghost snapshots.</summary>
    internal struct PendingGhostDespawn : IComparable<PendingGhostDespawn>
    {
        private static readonly ProfilerMarker s_AckInFlightDespawns = new ("PendingGhostDespawn-AckInFlightDespawns");
        private static readonly ProfilerMarker s_FindNewDespawns = new ("PendingGhostDespawn-FindNewDespawns");
        private static readonly ProfilerMarker s_FindNewPrespawnDespawns = new ("PendingGhostDespawn-FindNewPrespawnDespawns");
        private static readonly ProfilerMarker s_SortDespawns = new ("PendingGhostDespawn-Sort");
        private static readonly ProfilerMarker s_WriteDespawns = new ("PendingGhostDespawn-Write");
        private static readonly ProfilerMarker s_WriteDespawnsMarker = new ("PendingGhostDespawn-WriteDespawns");
        private static readonly ProfilerMarker s_FindOldestMarker = new ("PendingGhostDespawn-FindOldestTick");
        /// <summary>
        /// Denotes the maximum number of despawn messages that can be in-flight for this ghost despawn, at once.
        /// Maps to <see cref="DespawnSlot0"/> and <see cref="DespawnSlot1"/>.
        /// </summary>
        private const int k_MaxInFlight = 2;

        /// <summary>
        /// This is a compression trick: When encoding ghostId deltas, we expect the next ghostId serialized to be the
        /// last value plus at least one (in the common case), as they're sorted. Therefore, if we add this const
        /// when writing the last ghostId value, our average delta will be smaller i.e. fewer bits i.e. better compressed.
        /// </summary>
        internal const int k_ExpectedGhostIdDelta = 1;

        public enum DespawnReason : byte
        {
            /// <summary>The ghost entity was destroyed.</summary>
            EntityDestroyed = 1,
            /// <summary>The ghost became irrelevant to the current connection.</summary>
            Irrelevant = 2,
            /// <summary>The prespawn scene was unloaded on the server and/or client.</summary>
            PrespawnSceneUnloaded = 3,
        }
        /// <summary>Despawn snapshot slot 0.</summary>
        internal NetworkTick DespawnSlot0;
        /// <summary>Despawn snapshot slot 1.</summary>
        internal NetworkTick DespawnSlot1;

        /// <summary>Details of the despawning ghost.</summary>
        internal GhostCleanup Ghost;
        /// <summary>Denotes how many in-flight despawns there are.</summary>
        internal byte CountInFlight;
        /// <summary>Reason for despawn.</summary>
        public DespawnReason Reason;

        internal static uint WriteDespawns(NetworkTick currentTick, ref UnsafeList<PendingGhostDespawn> pending,
            ref ConnectionStateData.GhostStateList ghostStateData, NativeList<ArchetypeChunk> despawnChunks,
            ref NetworkSnapshotAck ack, ComponentTypeHandle<GhostCleanup> ghostSystemStateType,
            ref DataStreamWriter dataStream, ref StreamCompressionModel compressionModel,
            ref UnsafeList<PrespawnHelper.GhostIdInterval> newLoadedPrespawnRanges, ref NativeList<int> prespawnDespawns,
            ref GhostSendSystemData systemData
#if NETCODE_DEBUG
            , ref PacketDumpLogger netDebugPacket
#endif
            )
        {
            using var m = s_WriteDespawnsMarker.Auto();
            var oldestPendingGhostsDespawnTick = ack.LastReceivedSnapshotByRemote;
            if (oldestPendingGhostsDespawnTick.IsValid)
                oldestPendingGhostsDespawnTick.Increment();

#if NETCODE_DEBUG
            int despawnsAcked = pending.Length;
#endif
            // We first refresh the despawn list with acked despawns and new despawns discovered locally.
            // We then sort the despawn list and send as many as we can

            // Fetch the snapshot ack, and use it to remove (i.e. 'ack' or 'confirm') as many in-flight despawn messages as we can:
            if (!pending.IsEmpty)
            {
                using var _ = s_AckInFlightDespawns.Auto();
                for (var i = 0; i < pending.Length; i++)
                {
                    ref var pendingDespawn = ref pending.ElementAt(i);
                    pendingDespawn.AssertValid();
                    if (pendingDespawn.ClientAckedAnyInFlight(ref ack))
                    {
                        ref var state = ref ghostStateData.GetGhostState(pendingDespawn.Ghost.ghostId, pendingDespawn.Ghost.spawnTick);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        var wasDespawning = (state.Flags & ConnectionStateData.GhostStateFlags.IsDespawning) != 0;
                        UnityEngine.Debug.Assert(wasDespawning, "wasDespawning");
#endif
                        state.Flags &= ~(ConnectionStateData.GhostStateFlags.IsDespawning);
                        state.Flags |= ConnectionStateData.GhostStateFlags.HasBeenDespawnedAtLeastOnce;
                        pending.RemoveAtSwapBack(i);
                        i--;
                    }
                }
            }

#if NETCODE_DEBUG
            despawnsAcked = pending.Length - despawnsAcked;
#endif

            // Find new despawns inside despawnChunks:
            if (!despawnChunks.IsEmpty)
            {
                using var _ = s_FindNewDespawns.Auto();
                for (var chunk = 0; chunk < despawnChunks.Length; ++chunk)
                {
                    var ghostStates = despawnChunks[chunk].GetNativeArray(ref ghostSystemStateType);
                    for (var ent = 0; ent < ghostStates.Length; ++ent)
                    {
                        var ghostCleanup = ghostStates[ent];
                        ref var state = ref ghostStateData.GetGhostState(ghostCleanup);
                        var isRelevant = (state.Flags & ConnectionStateData.GhostStateFlags.IsRelevant) != 0;
                        var isAlreadyDespawning = (state.Flags & ConnectionStateData.GhostStateFlags.IsDespawning) != 0;

                        if (isRelevant && !isAlreadyDespawning)
                        {
                            // TODO: Do we need to clear the snapshot history buffer?
                            AddNewPendingDespawn(ref pending, ref state.Flags, ghostCleanup, DespawnReason.EntityDestroyed);
                        }
                    }
                }
            }

            // Send out the current list of destroyed prespawned entities, for all new client's loaded scenes.
            // TODO - Refactor prespawn despawns to remove the need for this.
            if (prespawnDespawns.Length > 0 && newLoadedPrespawnRanges.Length > 0)
            {
                using var _ = s_FindNewPrespawnDespawns.Auto();
                for (int i = 0; i < prespawnDespawns.Length; ++i)
                {
                    //If not in range, skip:
                    var ghostId = prespawnDespawns[i];
                    if(ghostId < newLoadedPrespawnRanges[0].Begin ||
                       ghostId > newLoadedPrespawnRanges[newLoadedPrespawnRanges.Length-1].End)
                        continue;

                    // TODO: can use a binary search, like lower-bound in c++
                    int idx = 0;
                    while (idx < newLoadedPrespawnRanges.Length && ghostId > newLoadedPrespawnRanges[idx].End)
                        ++idx;

                    if (idx < newLoadedPrespawnRanges.Length)
                    {
                        ref var state = ref ghostStateData.GetPrespawnGhostState(ghostId);
                        // Special case: We need to resend the despawn, as the sub-scene has been reloaded.
                        // TODO: Clean this up by assigning all ghosts within newLoadedPrespawnRanges as relevant.
                        bool hasBeenDespawnedBefore = (state.Flags & ConnectionStateData.GhostStateFlags.HasBeenDespawnedAtLeastOnce) != 0;
                        if (hasBeenDespawnedBefore)
                        {
                            state.Flags |= ConnectionStateData.GhostStateFlags.IsRelevant;
                            AddNewPendingDespawn(ref pending, ref state.Flags, new GhostCleanup
                            {
                                ghostId = ghostId,
                                spawnTick = NetworkTick.Invalid,
                                despawnTick = NetworkTick.Invalid,
                            }, DespawnReason.PrespawnSceneUnloaded);
                        }
                    }
                }
            }

            // Now that we have an up to date pending despawn list, update oldestPendingGhostsDespawnTick from all pending despawns.
            if (!pending.IsEmpty)
            {
                using var a = s_FindOldestMarker.Auto();
                for (int i = 0; i < pending.Length; i++)
                {
                    ref var pendingDespawn = ref pending.ElementAt(i);
                    pendingDespawn.AssertValid();
                    if (pendingDespawn.Ghost.despawnTick.IsValid
                        && (!oldestPendingGhostsDespawnTick.IsValid || oldestPendingGhostsDespawnTick.IsNewerThan(pendingDespawn.Ghost.despawnTick)))
                    {
                        oldestPendingGhostsDespawnTick = pendingDespawn.Ghost.despawnTick;
                    }
                }
            }

            // Send as many despawns as we can:
            uint despawnLen = 0;
            if(!pending.IsEmpty)
            {
                using var _ = s_WriteDespawns.Auto();
                systemData.PercentReservedForDespawnMessages = math.clamp(systemData.PercentReservedForDespawnMessages,
                    GhostSystemConstants.MinPercentReservedForDespawnMessages, GhostSystemConstants.MaxPercentReservedForDespawnMessages);
                const ushort minBytesAssignedToDespawns = 10;
                const int minBytesLeftForSnapshotOverhead = 8;
                const ushort maxCanFitInLength = ushort.MaxValue;
                var maxBytesUsedForDespawns = (ushort)math.clamp(dataStream.Capacity * systemData.PercentReservedForDespawnMessages, minBytesAssignedToDespawns, maxCanFitInLength);

                s_SortDespawns.Begin();
                pending.Sort();
                s_SortDespawns.End();

#if NETCODE_DEBUG
                FixedString128Bytes despawnTitle = $"\tST:{currentTick.ToFixedString()} [Despawn GIDs] ";
                FixedString512Bytes despawnLog = despawnTitle;
                int despawnBits = dataStream.LengthInBits;
#endif
                int nextExpectedGhostId = k_ExpectedGhostIdDelta;
                for (var i = 0; i < pending.Length; i++)
                {
                    ref var pendingDespawn = ref pending.ElementAt(i);
                    if (pendingDespawn.CountInFlight >= k_MaxInFlight // We've reached the (sorted) entries that already have their max number in-flight.
                        || dataStream.Length + minBytesLeftForSnapshotOverhead >= maxBytesUsedForDespawns)
                    {
#if NETCODE_DEBUG
                        if (netDebugPacket.IsCreated)
                        {
                            despawnLog.Append((FixedString128Bytes)$"Hit DespawnMax! Writer:{dataStream.Length}B+{minBytesAssignedToDespawns}B>={maxBytesUsedForDespawns}B ({dataStream.Capacity}B*{(int)(systemData.PercentReservedForDespawnMessages*100)}%)!");
                        }
#endif
                        break;
                    }

                    // Note: Even though these pending ghostIds are sorted by their ghostId (ascending),
                    // they are FIRST sorted by how many messages are in-flight (with fewest sends prioritized first).
                    // E.g.
                    // [ServerTick:1] Send 3, 4, 5, which are new despawns.
                    // [ServerTick:2] Send 10, 11, 12 (also new despawns, as they have not been sent yet), THEN 3, 4, 5 (which are being resent).
                    // Therefore, we must use an `int` delta, we can't assume a `uint` delta.
                    dataStream.WritePackedIntDelta(pendingDespawn.Ghost.ghostId, nextExpectedGhostId, compressionModel);
                    nextExpectedGhostId = pendingDespawn.Ghost.ghostId + k_ExpectedGhostIdDelta;
                    pendingDespawn.TrackWriteOfDespawn(currentTick);
                    despawnLen++;
#if NETCODE_DEBUG
                    if (netDebugPacket.IsCreated)
                    {
                        despawnLog.Append(pendingDespawn.Ghost.ghostId);
                        despawnLog.Append(':');
                        despawnLog.Append(pendingDespawn.Reason switch
                        {
                            DespawnReason.Irrelevant => 'I',
                            DespawnReason.EntityDestroyed => 'D',
                            DespawnReason.PrespawnSceneUnloaded => 'U',
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            _ => throw new InvalidOperationException("Missing enum entry."),
#else
                            _ => '?',
#endif
                        });
                        despawnLog.Append(pendingDespawn.CountInFlight);
                        despawnLog.Append(' ');

                        if (despawnLog.Length > (despawnLog.Capacity >> 1))
                        {
                            netDebugPacket.Log(despawnLog);
                            despawnLog = despawnTitle;
                        }
                    }
#endif
                }

#if NETCODE_DEBUG
                if (despawnLen > 0 && netDebugPacket.IsCreated)
                {
                    despawnBits = dataStream.LengthInBits - despawnBits;
                    despawnLog.Append((FixedString128Bytes) $"\n\tST:{currentTick.ToFixedString()} [Despawn] Sending:{despawnLen} of Pending:{pending.Length} in ~{(despawnBits/8)}B ({despawnBits} bits, ~{(int)((float)despawnBits/despawnLen)} bits/gid), despawnsAcked:{despawnsAcked}!");
                    netDebugPacket.Log(despawnLog);
                }
#endif
            }

            // Update OldestPendingDespawnTick: We can delete a despawnChunk once all clients have acked all despawns
            // for the latest despawnTick in the chunk.
            // So, we track the oldest un-acked ghost's despawnTick for this connection here.
            ghostStateData.OldestPendingDespawnTick = oldestPendingGhostsDespawnTick;

            return despawnLen;
        }

        /// <summary>
        /// Try to ack any of the in-flight snapshots.
        /// And reset all failed acks (from dropped snapshots).
        /// </summary>
        /// <param name="ack"></param>
        /// <returns>True if ack succeeded.</returns>
        private bool ClientAckedAnyInFlight(ref NetworkSnapshotAck ack)
        {
            if (CountInFlight == 0) return false;
            if (AckOrResetInFlightSlot(ref DespawnSlot0, ref CountInFlight, ref ack)) return true;
            if (AckOrResetInFlightSlot(ref DespawnSlot1, ref CountInFlight, ref ack)) return true;
            //if (AckOrResetInFlightSlot(ref DespawnSlot2, ref CountInFlight, ref ack)) return true;
            return false;

            static bool AckOrResetInFlightSlot(ref NetworkTick slot, ref byte countInFlight, ref NetworkSnapshotAck ack)
            {
                if (!slot.IsValid || !ack.LastReceivedSnapshotByRemote.IsValid)
                {
                    // Snapshot not even sent or ackable yet.
                    return false;
                }
                if (slot.IsNewerThan(ack.LastReceivedSnapshotByRemote))
                {
                    // Client not yet sent back an ack for this snapshot, nor any future snapshots,
                    // so we must wait. I.e. Snapshots are still "in-flight".
                    return false;
                }
                if (ack.IsReceivedByRemote(slot))
                {
                    // Ack successful!
                    return true;
                }

                // This slot's snapshot was unfortunately dropped by the client, so we reset its slot entry.
                slot = NetworkTick.Invalid;
                countInFlight--;
                return false;
            }
        }

        /// <summary>
        /// Tracks that we have added a despawn message for this ghost into the snapshot (with id: <see cref="currentTick"/>).
        /// </summary>
        /// <param name="currentTick">The snapshot the despawn message was written into.</param>
        /// <exception cref="InvalidOperationException"></exception>
        private void TrackWriteOfDespawn(NetworkTick currentTick)
        {
            AssertValid();
            if (TryAddDespawnWrite(ref DespawnSlot0, ref CountInFlight, currentTick)) return;
            if (TryAddDespawnWrite(ref DespawnSlot1, ref CountInFlight, currentTick)) return;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            throw new InvalidOperationException("No slots left!");
#endif

            static bool TryAddDespawnWrite(ref NetworkTick slot, ref byte countInFlight, NetworkTick currentTick)
            {
                if (!slot.IsValid)
                {
                    slot = currentTick;
                    countInFlight++;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Ascending by how many times they've been sent.
        /// Then ascending by ghostId.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public int CompareTo(PendingGhostDespawn other)
        {
            var countDelta = CountInFlight.CompareTo(other.CountInFlight);
            return countDelta != 0 ? countDelta : Ghost.ghostId.CompareTo(other.Ghost.ghostId);
        }

        /// <summary>
        /// Called for each ghost we need to begin despawning.
        /// </summary>
        /// <param name="pendingDespawns">List of pending despawns.</param>
        /// <param name="flags">The flags for this ghost instance.</param>
        /// <param name="ghostCleanup">A copy of details of the ghost.</param>
        /// <param name="reason">The despawn reason. Only used for debugging!</param>
        public static void AddNewPendingDespawn(ref UnsafeList<PendingGhostDespawn> pendingDespawns,
            ref ConnectionStateData.GhostStateFlags flags, in GhostCleanup ghostCleanup, DespawnReason reason)
        {
            var isRelevant = (flags & ConnectionStateData.GhostStateFlags.IsRelevant) != 0;
            var isAlreadyDespawning = (flags & ConnectionStateData.GhostStateFlags.IsDespawning) != 0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(isRelevant, "isRelevant");
#endif
            // No need to add this new despawn, if already marked for despawn. However, we'll lose the reason.
            if (Hint.Unlikely(isAlreadyDespawning))
            {
                // If we wanted to track destroyed ghosts vs irrelevant despawns, we'd need to handle this case.
                return;
            }

            // Update flags.
            flags &= (~ConnectionStateData.GhostStateFlags.IsRelevant);
            flags |= ConnectionStateData.GhostStateFlags.IsDespawning | ConnectionStateData.GhostStateFlags.HasBeenDespawnedAtLeastOnce;
            pendingDespawns.Add(new PendingGhostDespawn
            {
                Ghost = ghostCleanup,
                Reason = reason,
            });
            pendingDespawns[^1].AssertValid();
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [Conditional("UNITY_ASSERTIONS")]
        private void AssertValid()
        {
            // Counts:
            UnityEngine.Debug.Assert(CountInFlight <= k_MaxInFlight, "k_MaxInFlight");
            UnityEngine.Debug.Assert(CountInFlight ==
                                     (DespawnSlot0.IsValid ? 1 : 0) +
                                     (DespawnSlot1.IsValid ? 1 : 0), "CountInFlight");// +
                                     //(DespawnSlot2.IsValid ? 1 : 0);
            // No duplicates:
            UnityEngine.Debug.Assert(!DespawnSlot0.IsValid || DespawnSlot0 != DespawnSlot1, "NoDup0vs1");
            // UnityEngine.Debug.Assert(!DespawnSlot0.IsValid || DespawnSlot0 != DespawnSlot2);
            // UnityEngine.Debug.Assert(!DespawnSlot1.IsValid || DespawnSlot1 != DespawnSlot2);

            // Ghost is valid:
            UnityEngine.Debug.Assert(Reason != default, "Reason");
            UnityEngine.Debug.Assert(Ghost.ghostId != default, "ghostId");
            // TODO - Make spawnTick & despawnTick always valid, so that we can assert on them!
        }

        /// <summary>Revert all snapshot despawn writes for the current tick.</summary>
        /// <param name="pendingDespawns"></param>
        /// <param name="currentTick"></param>
        public static void RevertSnapshotDespawnWrites(ref UnsafeList<PendingGhostDespawn> pendingDespawns, NetworkTick currentTick)
        {
            for (int i = 0; i < pendingDespawns.Length; i++)
            {
                ref var pending = ref pendingDespawns.ElementAt(i);
                pending.AssertValid();
                if (pending.CountInFlight <= 0) continue;
                RevertIfSameTick(ref pending.DespawnSlot0, ref pending.CountInFlight, currentTick);
                RevertIfSameTick(ref pending.DespawnSlot1, ref pending.CountInFlight, currentTick);
                //RevertIfSameTick(ref pending.DespawnSlot2, ref pending.CountInFlight, currentTick);
                pending.AssertValid();
            }

            static void RevertIfSameTick(ref NetworkTick slot, ref byte countInFlight, in NetworkTick tick)
            {
                if (slot == tick)
                {
                    slot = NetworkTick.Invalid;
                    countInFlight--;
                }
            }
        }
    }
}

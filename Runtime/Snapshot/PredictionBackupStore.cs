using System;
using Unity.Assertions;
using Unity.Collections;
using Unity.Entities;

namespace Unity.Netcode
{
    /// <summary>
    /// Owns the per-chunk prediction-history collections and the parallel-stage -> serial-commit lifecycle that keeps
    /// them consistent. The parallel backup job records into a <see cref="Writer"/> view, and a serial cleanup job calls
    /// <see cref="Commit"/> to commit newly-created rings, free unused ones, resize, and rebuild the newest-slot map.
    /// GhostUpdateSystem reads the persistent maps through the read-only views exposed on the
    /// <see cref="GhostPredictionHistoryState"/> singleton.
    /// </summary>
    internal unsafe struct PredictionBackupStore : IDisposable
    {
        /// <summary>A newly-created ring staged by the parallel backup job and committed by <see cref="Commit"/>.</summary>
        internal struct StagedEntry
        {
            /// <summary>SequenceNumber of the first-seen chunk this ring belongs to.</summary>
            public ulong chunkSequenceNumber;
            /// <summary>Handle to the freshly-allocated ring header.</summary>
            public RingPtr ring;
        }

        /// <summary>Per-chunk handle (SlotPtr) to the NEWEST slot's PredictionBackupState (source for GhostUpdateSystem's newest-backup reads). Rebuilt from live ring state each <see cref="Commit"/>.</summary>
        NativeParallelHashMap<ulong, SlotPtr> m_PredictionState;
        /// <summary>Per-chunk handle to a PredictionBackupRing header (the multi-tick history ring), source of truth for restore lookups.</summary>
        NativeParallelHashMap<ulong, RingPtr> m_PredictionRing;
        /// <summary>Staging for rings created during the parallel backup job (new keys can't be inserted into the hashmap while jobs read it in parallel).</summary>
        NativeQueue<StagedEntry> m_NewRings;
        /// <summary>Chunks touched this tick; a chunk absent here at commit time has its ring freed.</summary>
        NativeParallelHashMap<ulong, int> m_StillUsed;
        /// <summary>Per-entity (chunk, index) at backup time, so GhostUpdateSystem can restore across structural changes.</summary>
        NativeParallelHashMap<Entity, GhostPredictionHistorySystem.PredictionBufferHistoryData> m_EntityData;

        /// <summary>Allocates all collections with the given initial capacity.</summary>
        public void Allocate(int capacity)
        {
            m_PredictionState = new NativeParallelHashMap<ulong, SlotPtr>(capacity, Allocator.Persistent);
            m_PredictionRing = new NativeParallelHashMap<ulong, RingPtr>(capacity, Allocator.Persistent);
            m_NewRings = new NativeQueue<StagedEntry>(Allocator.Persistent);
            m_StillUsed = new NativeParallelHashMap<ulong, int>(capacity, Allocator.Persistent);
            m_EntityData = new NativeParallelHashMap<Entity, GhostPredictionHistorySystem.PredictionBufferHistoryData>(capacity, Allocator.Persistent);
        }

        /// <summary>Frees every ring (and the slots it owns), then disposes all collections.</summary>
        public void Dispose()
        {
            // Rings own every slot allocation (m_PredictionState only mirrors the newest slot pointer), so free via the
            // rings to cover historical slots and avoid double-freeing the newest.
            var ringValues = m_PredictionRing.GetValueArray(Allocator.Temp);
            for (int i = 0; i < ringValues.Length; ++i)
                ringValues[i].Ref.FreeAll();
            m_PredictionState.Dispose();
            m_PredictionRing.Dispose();
            m_NewRings.Dispose();
            m_StillUsed.Dispose();
            m_EntityData.Dispose();
        }

        /// <summary>Publishes the store's read-only views into the <see cref="GhostPredictionHistoryState"/> singleton that GhostUpdateSystem (and smoothing/debug) read from.</summary>
        public void PopulateSingleton(ref GhostPredictionHistoryState singleton)
        {
            singleton.PredictionState = m_PredictionState.AsReadOnly();
            singleton.EntityData = m_EntityData.AsReadOnly();
            singleton.PredictionRings = m_PredictionRing.AsReadOnly();
        }

        /// <summary>Resets per-tick scratch state before scheduling the backup job, growing the per-entity map to fit.</summary>
        public void BeginFrame(int predictedEntityCount)
        {
            m_StillUsed.Clear();
            m_EntityData.Clear();
            if (predictedEntityCount > m_EntityData.Capacity)
                m_EntityData.Capacity = predictedEntityCount;
            if (m_StillUsed.Capacity < predictedEntityCount)
                m_StillUsed.Capacity = predictedEntityCount;
        }

        /// <summary>Parallel-safe recording view handed to the backup job.</summary>
        public Writer AsWriter() => new Writer
        {
            rings = m_PredictionRing,
            newRings = m_NewRings.AsParallelWriter(),
            stillUsed = m_StillUsed.AsParallelWriter(),
            entityData = m_EntityData.AsParallelWriter(),
        };

        /// <summary>
        /// Frees rings for chunks no longer in use, commits newly-created rings, grows rings to
        /// <paramref name="desiredRingCapacity"/>, and repoints predictionState at each ring's <paramref name="serverTick"/>
        /// slot (the tick just backed up). Serial: run from a single-threaded job after the backup job.
        /// </summary>
        public void Commit(int desiredRingCapacity, NetworkTick serverTick)
        {
            // Free rings for chunks not touched this tick; the two maps' keys are kept in lockstep, so the ring must exist.
            var keys = m_PredictionState.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; ++i)
            {
                if (m_StillUsed.TryGetValue(keys[i], out _))
                    continue;
                m_PredictionState.Remove(keys[i]);
                var ringExists = m_PredictionRing.TryGetValue(keys[i], out var ringPtr);
                Assert.IsTrue(ringExists, "PredictionBackupStore.Commit: predictionState chunk has no ring entry; the maps must stay in lockstep.");
                ringPtr.Ref.FreeAll();
                m_PredictionRing.Remove(keys[i]);
            }
            // Commit newly-created rings (the parallel job can't insert hashmap keys).
            while (m_NewRings.TryDequeue(out var newRing))
            {
                if (!m_PredictionRing.TryAdd(newRing.chunkSequenceNumber, newRing.ring))
                {
                    // Stale ring - free it and replace.
                    if (m_PredictionRing.TryGetValue(newRing.chunkSequenceNumber, out var oldRing))
                        oldRing.Ref.FreeAll();
                    m_PredictionRing[newRing.chunkSequenceNumber] = newRing.ring;
                }
            }
            // In one pass over the live rings: grow each to the desired capacity (a no-op at or below current capacity),
            // then repoint predictionState at the ring's serverTick slot. predictionState must be the slot for the tick
            // we just backed up (== GhostSnapshotLastBackupTick.Value), which is exactly what every consumer assumes;
            // the ring's max-tick slot would be wrong after a backwards tick correction that leaves a stale future slot
            // in a never-cleared ring. Deriving from live ring state each tick means predictionState can never hold a
            // freed pointer (no per-write tracking or dangling-slot patching needed). Every still-used chunk was backed
            // up at serverTick this tick, so the slot exists.
            var rings = m_PredictionRing.GetKeyValueArrays(Allocator.Temp);
            for (int i = 0; i < rings.Length; ++i)
            {
                var ringPtr = rings.Values[i];
                if (desiredRingCapacity > 1)
                {
                    var resized = ringPtr.Ref.Resize(desiredRingCapacity);
                    if (resized.Value != ringPtr.Value)
                    {
                        m_PredictionRing[rings.Keys[i]] = resized;
                        ringPtr = resized;
                    }
                }
                var found = ringPtr.Ref.TryGetSlotForTick(serverTick, out var serverTickSlot);
                Assert.IsTrue(found, "PredictionBackupStore.Commit: live ring has no slot for serverTick; every still-used chunk is backed up at serverTick this tick.");
                m_PredictionState[rings.Keys[i]] = new SlotPtr { Value = (PredictionBackupState*)serverTickSlot };
            }
        }

        /// <summary>Parallel-safe recording view for the backup job: stages ring/slot writes and per-entity data.</summary>
        public unsafe struct Writer
        {
            /// <summary>Read-only ring lookup (new keys can't be inserted from a parallel job; staged using <see cref="StageFreshRing"/> instead).</summary>
            [ReadOnly] public NativeParallelHashMap<ulong, RingPtr> rings;
            internal NativeQueue<StagedEntry>.ParallelWriter newRings;
            internal NativeParallelHashMap<ulong, int>.ParallelWriter stillUsed;
            internal NativeParallelHashMap<Entity, GhostPredictionHistorySystem.PredictionBufferHistoryData>.ParallelWriter entityData;

            /// <summary>Looks up the existing ring for a chunk, if any.</summary>
            public bool TryGetRing(ArchetypeChunk chunk, out RingPtr ringPtr) => rings.TryGetValue(chunk.SequenceNumber, out ringPtr);
            /// <summary>Marks a chunk as still in use this tick (a chunk not marked has its ring freed at commit).</summary>
            public void MarkChunkUsed(ArchetypeChunk chunk) => stillUsed.TryAdd(chunk.SequenceNumber, 1);
            /// <summary>Records an entity's (chunk, index) location at backup time for cross-structural-change restore.</summary>
            public void RecordEntity(Entity entity, GhostPredictionHistorySystem.PredictionBufferHistoryData data) => entityData.TryAdd(entity, data);
            /// <summary>Allocates a fresh ring of <paramref name="capacity"/> for a first-seen chunk, places <paramref name="newestSlot"/> at index 0, and stages it for commit.</summary>
            public void StageFreshRing(ArchetypeChunk chunk, IntPtr newestSlot, int capacity)
            {
                Assert.IsTrue(capacity > 0, "PredictionBackupStore.StageFreshRing: capacity must be >= 1; OnUpdate's ring sizing logic is broken.");
                var ring = PredictionBackupRing.AllocNew(capacity);
                ring.Ref.SetSlot(0, newestSlot); // remaining slots stay null
                newRings.Enqueue(new StagedEntry { chunkSequenceNumber = chunk.SequenceNumber, ring = ring });
            }
        }
    }
}

using System;
using Unity.Assertions;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// A bare pointer typed handle to a <see cref="PredictionBackupRing"/>.
    /// Only valid while the ring is alive.
    /// </summary>
    internal unsafe struct RingPtr
    {
        /// <summary>The ring header this handle points at; null when unset.</summary>
        public PredictionBackupRing* Value;
        /// <summary>
        /// Cast-free access to the <see cref="PredictionBackupRing"/> referenced by <see cref="Value"/>.
        /// Use only while the ring is alive.
        /// </summary>
        public ref PredictionBackupRing Ref => ref *Value;
    }

    /// <summary>
    /// A bare pointer typed handle to a <see cref="PredictionBackupState"/> slot.
    /// Only valid while the slot is alive.
    /// </summary>
    internal unsafe struct SlotPtr
    {
        /// <summary>The slot this handle points at; null when unset.</summary>
        public PredictionBackupState* Value;
        /// <summary>
        /// Cast-free access to the slot referenced by <see cref="Value"/>.
        /// Use only while the slot is alive.
        /// </summary>
        public ref PredictionBackupState Ref => ref *Value;
    }

    /// <summary>
    /// Per-chunk multi-tick prediction history ring.
    /// <br/>
    /// Capacity is 1 unless <see cref="ClientTickRate.AlwaysRollbackAllPredictedGhosts"/> is enabled.
    /// <br/>
    /// Slots are unordered: <see cref="TryGetSlotForTick"/> linearly scans every slot to find the best match
    /// and the backup-write path linearly scans for re-predict / first-empty / oldest-evict. Capacity is bounded
    /// by <c>CommandDataUtility.k_CommandDataMaxSize</c> (64) so these scans are trivially cheap.
    /// </summary>
    /// <remarks>Blob layout: header -> slots[Capacity] (each slot an IntPtr to a <see cref="PredictionBackupState"/>).</remarks>
    internal unsafe struct PredictionBackupRing
    {
        /// <summary>Number of slots in the ring.</summary>
        public int Capacity;

        /// <summary>16-byte-aligned size of this header, before the trailing slot array.</summary>
        public static int GetHeaderSize()
        {
            return (UnsafeUtility.SizeOf<PredictionBackupRing>() + 15) & (~15);
        }

        /// <summary>Pointer to the slot array (<c>IntPtr[Capacity]</c>) that follows this header in memory.</summary>
        public IntPtr* GetSlots()
        {
            return (IntPtr*)((byte*)UnsafeUtility.AddressOf(ref this) + GetHeaderSize());
        }

        /// <summary>Writes <paramref name="slot"/> into the slot at <paramref name="index"/>. Index must be in [0, Capacity).</summary>
        public void SetSlot(int index, IntPtr slot)
        {
            GetSlots()[index] = slot;
        }

        /// <summary>Allocates a new ring with <paramref name="capacity"/> empty slots; returns its handle.</summary>
        public static RingPtr AllocNew(int capacity)
        {
            var headerSize = GetHeaderSize();
            var slotsSize = sizeof(IntPtr) * capacity;
            var ring = (PredictionBackupRing*)UnsafeUtility.Malloc(headerSize + slotsSize, 16, Allocator.Persistent);
            ring->Capacity = capacity;
            UnsafeUtility.MemClear(ring->GetSlots(), slotsSize);
            return new RingPtr { Value = ring };
        }

        /// <summary>
        /// Frees this ring header and all non-null slot allocations it owns.
        /// </summary>
        public void FreeAll()
        {
            var slots = GetSlots();
            for (int i = 0; i < Capacity; ++i)
            {
                if (slots[i] != IntPtr.Zero)
                    UnsafeUtility.Free((void*)slots[i], Allocator.Persistent);
            }
            UnsafeUtility.Free(UnsafeUtility.AddressOf(ref this), Allocator.Persistent);
        }

        /// <summary>
        /// Frees this ring header WITHOUT freeing slot allocations. Used when slot ownership has been transferred elsewhere
        /// (e.g. when the ring is being resized and slot pointers are migrated to a new ring).
        /// </summary>
        public void FreeHeaderOnly()
        {
            UnsafeUtility.Free(UnsafeUtility.AddressOf(ref this), Allocator.Persistent);
        }

        /// <summary>
        /// Logically clears the ring. We do this whenever a new snapshot arrives (and a new rollback begins),
        /// as we don't ever want to restore to a stale prediction.
        /// </summary>
        public void LogicalClear()
        {
            var slots = GetSlots();
            uint invalidTickStamp = NetworkTick.Invalid.SerializedData;
            for (int i = 0; i < Capacity; ++i)
            {
                if (slots[i] != IntPtr.Zero)
                    ((PredictionBackupState*)slots[i])->tickValue = invalidTickStamp;
            }
        }

        /// <summary>
        /// Linearly scans the ring and returns the slot whose stamped <see cref="PredictionBackupState.tickValue"/>
        /// is equal to the <see cref="wantedTick"/>.
        /// </summary>
        public bool TryGetSlotForTick(NetworkTick wantedTick, out IntPtr slot)
        {
            slot = IntPtr.Zero;
            if (!wantedTick.IsValid)
                return false;
            var slots = GetSlots();
            for (int i = 0; i < Capacity; ++i)
            {
                var slotPtr = slots[i];
                if (slotPtr == IntPtr.Zero)
                    continue;
                var slotTick = PredictionBackupState.GetTick(slotPtr);
                if (!slotTick.IsValid)
                    continue;
                if (slotTick != wantedTick)
                    continue;
                slot = slotPtr;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Diagnostic helper. Returns the count of slots whose pointer is non-null AND whose stamped
        /// tickValue is valid. O(Capacity); only call from logging paths.
        /// </summary>
        public int CountValid()
        {
            var slots = GetSlots();
            int count = 0;
            for (int i = 0; i < Capacity; ++i)
            {
                if (slots[i] == IntPtr.Zero) continue;
                if (PredictionBackupState.GetTick(slots[i]).IsValid)
                    ++count;
            }
            return count;
        }

        /// <summary>
        /// Result of <see cref="SelectSlotForWrite"/>: the chosen slot index, the pointer that slot currently holds,
        /// and whether the write is a re-predict (exact-tick) overwrite. Bundled into one value so the three can't
        /// diverge across the method boundary.
        /// </summary>
        public readonly struct SlotSelection
        {
            /// <summary>Index of the chosen slot, always in [0, Capacity).</summary>
            public readonly int Index;
            /// <summary>Pointer currently stored in the chosen slot. IntPtr.Zero for an empty slot; always non-zero when <see cref="IsRePredict"/> is true.</summary>
            public readonly IntPtr ExistingData;
            /// <summary>True when the slot already holds a backup for the wanted tick (a re-predicted catch-up tick): the caller overwrites in place and must NOT refresh predictionState.</summary>
            public readonly bool IsRePredict;
            /// <summary>Binds the slot index to the pointer it holds and the re-predict flag so they stay consistent.</summary>
            public SlotSelection(int index, IntPtr existingData, bool isRePredict)
            {
                Index = index;
                ExistingData = existingData;
                IsRePredict = isRePredict;
            }
        }

        /// <summary>
        /// Slot-selection for a backup write. Linearly scans the ring once and returns the slot the next
        /// BACKUP-WRITE for <paramref name="wantedTick"/> should target, in this order of preference:
        ///   1. RE-PREDICT: a slot whose tickValue == <paramref name="wantedTick"/>. <see cref="SlotSelection.IsRePredict"/>
        ///      is true and the caller overwrites the slot's data in place.
        ///   2. EMPTY: a slot whose pointer is null OR whose tickValue is invalid (logically cleared).
        ///   3. EVICT-OLDEST: the slot with the oldest valid tickValue, when no empty slot exists.
        /// <see cref="SlotSelection.Index"/> is always in <c>[0, Capacity)</c>; <c>Capacity</c> must be &gt; 0 (asserted).
        /// </summary>
        public SlotSelection SelectSlotForWrite(NetworkTick wantedTick)
        {
            Assert.IsTrue(Capacity > 0, "PredictionBackupRing has Capacity == 0; ring allocation/resize logic is broken.");
            var slots = GetSlots();
            int firstEmptyIdx = -1;
            int oldestValidIdx = -1;
            NetworkTick oldestValidTick = NetworkTick.Invalid;
            for (int i = 0; i < Capacity; ++i)
            {
                var slotPtr = slots[i];
                if (slotPtr == IntPtr.Zero)
                {
                    if (firstEmptyIdx < 0) firstEmptyIdx = i;
                    continue;
                }
                var slotTick = PredictionBackupState.GetTick(slotPtr);
                if (!slotTick.IsValid)
                {
                    // Allocated but logically cleared - treat as empty (preferred over eviction).
                    if (firstEmptyIdx < 0) firstEmptyIdx = i;
                    continue;
                }
                if (slotTick == wantedTick)
                    return new SlotSelection(i, slotPtr, isRePredict: true);
                if (oldestValidIdx < 0 || oldestValidTick.IsNewerThan(slotTick))
                {
                    oldestValidIdx = i;
                    oldestValidTick = slotTick;
                }
            }
            // No exact-tick match. Prefer an empty slot; otherwise evict the oldest valid.
            int targetSlotIdx = firstEmptyIdx >= 0 ? firstEmptyIdx : oldestValidIdx;
            Assert.IsTrue(targetSlotIdx >= 0 && targetSlotIdx < Capacity, "PredictionBackupRing slot selection failed: no empty AND no valid slot to evict (Capacity must be > 0).");
            return new SlotSelection(targetSlotIdx, slots[targetSlotIdx], isRePredict: false);
        }

        /// <summary>
        /// Ensures the ring holds at least <paramref name="newCapacity"/> slots, returning the (possibly new) handle.
        /// Grow-only: a request at or below the current capacity is a no-op that keeps the existing ring. This is
        /// deliberate - commandInterpolationDelay can fluctuate ±1 every frame, and shrink-then-grow cycles would
        /// thrash the heap. When growing, populated slots migrate into a fresh, larger ring and this one is freed.
        /// </summary>
        public RingPtr Resize(int newCapacity)
        {
            if (newCapacity <= Capacity)
                return new RingPtr { Value = (PredictionBackupRing*)UnsafeUtility.AddressOf(ref this) };
            var oldCapacity = Capacity;
            var oldSlots = GetSlots();
            var newRingPtr = AllocNew(newCapacity);
            var newSlots = newRingPtr.Ref.GetSlots();

            // Pack valid slots into the new ring's front (order is not significant - lookups linear-scan).
            // Logically-cleared slots (tickValue invalid) are freed rather than migrated; the next
            // backup-write allocates a fresh slot anyway.
            int writeIdx = 0;
            for (int i = 0; i < oldCapacity; ++i)
            {
                if (oldSlots[i] == IntPtr.Zero) continue;
                if (PredictionBackupState.GetTick(oldSlots[i]).IsValid)
                    newSlots[writeIdx++] = oldSlots[i];
                else
                    UnsafeUtility.Free((void*)oldSlots[i], Allocator.Persistent);
            }
            FreeHeaderOnly();
            return newRingPtr;
        }
    }
}

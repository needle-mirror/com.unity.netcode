using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using Unity.Profiling;

namespace Unity.NetCode
{
    /// <summary>
    /// Component present only for ghosts spawned by the client, tracking the latest <see cref="SnapshotDataBuffer"/>
    /// history slot used to store the incoming ghost snapshots from the server.
    /// </summary>
    public struct SnapshotData : IComponentData
    {
        /// <summary>
        /// Internal use only.
        /// </summary>
        public struct DataAtTick
        {
            /// <summary>
            /// Pointer to the snapshot data for which the tick is less than, or equals to, the target tick.
            /// This snapshot is valid for the tick represented by <see cref="Tick"/>.
            /// </summary>
            public System.IntPtr SnapshotBefore;
            /// <summary>
            /// Pointer to snapshot data.
            /// When interpolating, This snapshot is the most recent snapshot <b>after</b> <see cref="Tick"/>).
            /// When extrapolating, This snapshot is the most recent snapshot <b>before</b> <see cref="Tick"/>
            /// </summary>
            public System.IntPtr SnapshotAfter;
            /// <summary>
            /// The current fraction used to interpolate/extrapolated the component field for interpolated ghosts.
            /// </summary>
            public float InterpolationFactor;
            /// <summary>
            /// The target server tick we are currently updating or deserializing.
            /// <see cref="SnapshotBefore"/> is the ghost snapshot received on this tick
            /// </summary>
            public NetworkTick Tick;
            /// <summary>
            /// The history slot index that contains the ghost snapshot for <see cref="Tick"/>).
            /// </summary>
            public int BeforeIdx;
            /// <summary>
            /// A history slot index.
            /// When interpolating, this is the index that contains the ghost snapshot received directly <b>after</b> <see cref="Tick"/>).
            /// When extrapolating, this is the index that contains the ghost snapshot received directly <b>before</b> <see cref="Tick"/>
            /// </summary>
            public int AfterIdx;
            /// <summary>
            /// The required values of the <see cref="GhostComponentAttribute.OwnerSendType"/> property in order for a component to be sent.
            /// The mask depends on the presence and value of the <see cref="GhostOwner"/> component:
            /// <see cref="SendToOwnerType.All"/> if the <see cref="GhostOwner"/> is not present on the entity
            /// <see cref="SendToOwnerType.SendToOwner"/> if the value of the <see cref="GhostOwner"/> is equals to the <see cref="NetworkId"/> of the client.
            /// <see cref="SendToOwnerType.SendToNonOwner"/> if the value of the <see cref="GhostOwner"/> is different than the <see cref="NetworkId"/> of the client.
            /// </summary>
            public SendToOwnerType RequiredOwnerSendMask;
            /// <summary>
            /// The network id of the client owning the ghost. 0 if the ghost does not have a <see cref="NetCode.GhostOwner"/>.
            /// </summary>
            public int GhostOwner;

            internal void PopulateInterpolationFactor(bool isExtrapolating, NetworkTick targetTick, NetworkTick afterTick,
                NetworkTick extrapolateTick, float targetTickFraction, uint MaxExtrapolationTicks)
            {
                if (!afterTick.IsValid && !extrapolateTick.IsValid)
                {
                    InterpolationFactor = 0;
                    return;
                }

                var newestTick = afterTick;
                var oldestTick = Tick;

                // ExtrapolateTick path
                if (isExtrapolating)
                {
                    if (targetTick.TicksSince(Tick) > MaxExtrapolationTicks)
                    {
                        targetTick = Tick;
                        targetTick.Add(MaxExtrapolationTicks);
                    }

                    newestTick = Tick;
                    oldestTick = extrapolateTick;
                }

                InterpolationFactor = (float)(targetTick.TicksSince(oldestTick)) /
                                      (float)(newestTick.TicksSince(oldestTick));
                if (targetTickFraction < 1)
                {
                    InterpolationFactor += targetTickFraction / (float)(newestTick.TicksSince(oldestTick));
                }

                if (isExtrapolating)
                {
                    InterpolationFactor = 1 - InterpolationFactor;
                }
            }

            internal unsafe void PopulateOwnerData(int predictionOwnerOffset, int localNetworkId)
            {
                GhostOwner = predictionOwnerOffset != 0 ? *(int*)(SnapshotBefore + predictionOwnerOffset) : 0;

                if (predictionOwnerOffset == 0)
                    RequiredOwnerSendMask = SendToOwnerType.All;
                else if (localNetworkId == GhostOwner)
                    RequiredOwnerSendMask = SendToOwnerType.SendToOwner;
                else
                    RequiredOwnerSendMask = SendToOwnerType.SendToNonOwner;
            }
        }

        /// <summary>
        /// The size (in bytes) of the ghost snapshots. It is constant after the ghost entity is spawned, and corresponds to the
        /// <see cref="GhostCollectionPrefabSerializer.SnapshotSize"/>.
        /// </summary>
        public int SnapshotSize;
        /// <summary>
        /// The history slot used to store the last received data from the server. It is always less than <see cref="GhostSystemConstants.SnapshotHistorySize"/>.
        /// </summary>
        public int LatestIndex;
        /// <summary>
        /// The latest snapshot history applied to this ghost (via <c>CopyFromSnapshot</c>). Otherwise, <c>Invalid</c>.
        /// </summary>
        /// <remarks>
        /// Set for both predicted and interpolated ghosts.
        /// For predicted ghosts, this value matches <see cref="PredictedGhost.AppliedTick"/>.
        /// </remarks>
        public NetworkTick AppliedTick;

        /// <summary>
        /// Should only ever be valid for a static interpolated ghost.
        /// ResumeTick will be valid when a static ghost that previously stopped moving has received new data.
        /// It represents the tick on the interpolation timeline where it's valid to start processing the new snapshot.
        /// </summary>
        internal NetworkTick ResumeTick;

        private static readonly ProfilerMarker k_GetDataAtTick = new ProfilerMarker("SnapshotData_GetDataAtTick");

        /// <summary>
        /// The latest snapshot server tick received by the client.
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns>A valid tick if the buffer is not empty, otherwise 0.</returns>
        readonly internal unsafe NetworkTick GetLatestTick(in DynamicBuffer<SnapshotDataBuffer> buffer)
        {
            if (buffer.Length == 0)
                return NetworkTick.Invalid;
            byte* snapshotData;
            snapshotData = (byte*)buffer.GetUnsafeReadOnlyPtr() + LatestIndex * SnapshotSize;
            return new NetworkTick{SerializedData = *(uint*)snapshotData};
        }
        /// <summary>
        /// The tick of the oldest snapshot received by the client.
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns>a valid tick if the buffer is not empty, 0 otherwise </returns>
        readonly internal unsafe NetworkTick GetOldestTick(in DynamicBuffer<SnapshotDataBuffer> buffer)
        {
            if (buffer.Length == 0)
                return NetworkTick.Invalid;
            byte* snapshotData;

            // The snapshot store is a ringbuffer. Once it is full, the entry after "latest" is the oldest (i.e. the next one to be overwritten).
            // That might however be uninitialized (tick 0) so we scan forward from that until we find a valid entry.
            var oldestIndex = (LatestIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
            while (oldestIndex != LatestIndex)
            {
                snapshotData = (byte*)buffer.GetUnsafeReadOnlyPtr() + oldestIndex * SnapshotSize;
                var oldestTick = new NetworkTick{SerializedData = *(uint*)snapshotData};
                if (oldestTick.IsValid)
                    return oldestTick;
                oldestIndex = (oldestIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
            }

            snapshotData = (byte*)buffer.GetUnsafeReadOnlyPtr() + LatestIndex * SnapshotSize;
            return new NetworkTick{SerializedData = *(uint*)snapshotData};
        }
        /// <summary>
        /// Returns the snapshot index 'reverseOffset' back from the 'LatestIndex'. So 0 will return the snapshot index at LatestIndex
        /// 12 will return the snapshot index at LatestIndex-12 correcly wrapping if the index goes negative
        /// </summary>
        /// <param name="reverseOffset"></param>
        /// <returns>The snapshot index 'reverseOffset' back from the LatestIndex, returns null on an error </returns>
        internal unsafe int GetPreviousSnapshotIndexAtOffset(int reverseOffset)
        {
            if (reverseOffset > GhostSystemConstants.SnapshotHistorySize)
                return LatestIndex;

            var previousIndex = (LatestIndex - reverseOffset);
            if (previousIndex < 0)
            {
                previousIndex += GhostSystemConstants.SnapshotHistorySize;
            }
            return previousIndex;
        }

        /// <summary>
        /// Try to find the two closest received ghost snapshots for a given <paramref name="targetTick"/>,
        /// and fill the <paramref name="data"/> accordingly.
        /// </summary>
        /// <returns>True if at least one snapshot has been received and if its tick is less or equal the current target tick.</returns>
        internal unsafe bool GetDataAtTick(NetworkTick targetTick, int predictionOwnerOffset,
            int localNetworkId, float targetTickFraction, in DynamicBuffer<SnapshotDataBuffer> buffer,
            out DataAtTick data, uint MaxExtrapolationTicks, bool isStatic)
        {
            using var _ = k_GetDataAtTick.Auto();

            data = default;
            if (buffer.Length == 0)
                return false;
            var numBuffers = buffer.Length / SnapshotSize;

            // setup local variables to avoid assigning to the out var until we know we have valid data
            var afterTick = NetworkTick.Invalid;
            var extrapolateTick = NetworkTick.Invalid;
            var isExtrapolating = false;

            // If last tick is fractional before should not include the tick we are targeting, it should instead be included in after
            var fractionalTargetTick = targetTickFraction < 1;
            if (fractionalTargetTick)
                targetTick.Decrement();

            // Loop from newest/latest available to oldest available snapshot
            int slot;
            var bufferData = (byte*)buffer.GetUnsafeReadOnlyPtr();
            for (slot = 0; slot < numBuffers; ++slot)
            {
                var curIndex = (LatestIndex + GhostSystemConstants.SnapshotHistorySize - slot) % GhostSystemConstants.SnapshotHistorySize;
                var snapshotData = bufferData + curIndex * SnapshotSize;
                var tick = new NetworkTick { SerializedData = *(uint*)snapshotData };

                if (!tick.IsValid)
                    continue;

                // While this tick is ahead of our target, overwrite our afterTick
                if (tick.IsNewerThan(targetTick))
                {
                    afterTick = tick;
                    data.AfterIdx = curIndex;
                    continue;
                }

                // The first time we see a tick that is older or equal to our target, save it as our before tick
                if (!data.Tick.IsValid)
                {
                    data.Tick = tick;
                    data.BeforeIdx = curIndex;

                    // If it's valid to look for an extrapolation tick:
                    //  1. We found no snapshots newer than our target
                    //  2. Our before snapshot is older than our target, or we're simulating a fractional tick
                    //  3. This ghost is dynamic (not static)
                    if (!afterTick.IsValid && (data.Tick != targetTick || fractionalTargetTick) && !isStatic)
                    {
                        isExtrapolating = true;
                        continue;
                    }

                    // If we have a valid afterTick, or it's not valid to extrapolate
                    // We've found all relevant data and can break
                    break;
                }

                // The first valid tick after we've found the beforeTick will be the extrapolateTick
                extrapolateTick = tick;
                // Reuse the AfterIdx even if this tick is before our before tick
                data.AfterIdx = curIndex;
                break;
            }

            if (!data.Tick.IsValid)
            {
                // If we haven't found any valid tick, ensure that we reset data to default before returning
                // This ensures we're not leaking invalid data
                data = default;
                return false;
            }

            data.SnapshotBefore = (IntPtr)(bufferData + data.BeforeIdx * SnapshotSize);

            // Neither afterTick or extrapolateTick will be valid if we only have one valid snapshot
            // Or if we don't have any snapshots newer than our target tick and we're not valid to extrapolate.
            if (!afterTick.IsValid && !extrapolateTick.IsValid)
            {
                // In that case we want to use the BeforeIdx and the before snapshot twice
                // The interpolation factor will be clamped at zero in this case.
                data.AfterIdx = data.BeforeIdx;
            }

            data.SnapshotAfter = (IntPtr)(bufferData + data.AfterIdx * SnapshotSize);

            // ResumeTick can only be valid for a static interpolated ghost.
            // It will be valid after a static ghost that stopped moving has started moving again.
            // The stopped movement will leave a gap in the snapshot buffer, we need to compensate for that gap.
            // The ResumeTick is the tick on which the movement was assumed to start.
            if (afterTick.IsValid && ResumeTick.IsValid)
            {
                // The snapshot data will still be the old data,
                // The interpolation factor will be calculated off of this assumed tick, leading to smoother movement.
                data.Tick = ResumeTick;
            }

            data.PopulateOwnerData(predictionOwnerOffset, localNetworkId);
            data.PopulateInterpolationFactor(isExtrapolating, targetTick, afterTick, extrapolateTick, targetTickFraction, MaxExtrapolationTicks);
            return true;
        }
    }

    /// <summary>
    /// A data structure used to store ghosts snapshot buffers data content.
    /// Typically around 1-12kb per entity. Thus, we always allocate on the heap.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct SnapshotDataBuffer : IBufferElementData
    {
        /// <summary>
        /// An element value.
        /// </summary>
        public byte Value;
    }

    /// <summary>
    /// A data structure used to store ghosts dynamic buffers data content.
    /// BeginArray(SnapshotHistorySize]
    /// uint dataSize, (16 bytes aligned) current serialized data length for each slot. Used for delta compression
    /// EndArray
    /// BeginArray(SnapshotHistorySize]
    ///  for each buffers:
    ///     uint[maskBits] elements change bitmask
    ///     byte[numElements] serialized buffers data
    /// EndArray
    /// The buffer grow in size as necessary to accomodate new data. All slots have the same size, usually larger
    /// than the data size.
    /// The serialized element size is aligned to the 16 bytes boundary
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct SnapshotDynamicDataBuffer : IBufferElementData
    {
        /// <summary>
        /// An element value.
        /// </summary>
        public byte Value;
    }

    /// <summary>
    /// Helper class for managing ghost buffers data. Internal use only.
    /// </summary>
    public unsafe struct SnapshotDynamicBuffersHelper
    {
        /// <summary>
        /// Get the size of the header at the beginning of the dynamic snapshot buffer. The size
        /// of the header is constant.
        /// </summary>
        /// <returns>Size of the header at the beginning of the dynamic snapshot buffer</returns>
        public static uint GetHeaderSize()
        {
            return (uint)GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) * GhostSystemConstants.SnapshotHistorySize);
        }

        /// <summary>
        /// Retrieve the dynamic buffer history slot pointer
        /// </summary>
        /// <param name="dynamicDataBuffer">Dynamic data buffer</param>
        /// <param name="historyPosition">history position in buffer</param>
        /// <param name="bufferLength">Length of buffer</param>
        /// <returns>pointer to dynamic buffer</returns>
        /// <exception cref="System.IndexOutOfRangeException">Thrown if the position is invalid</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if bufferlength is less than headersize</exception>
        static public byte* GetDynamicDataPtr(byte* dynamicDataBuffer, int historyPosition, int bufferLength)
        {
            var headerSize = GetHeaderSize();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            //Must be aligned to 16 bytes
            if (historyPosition < 0 || historyPosition >GhostSystemConstants.SnapshotHistorySize)
                throw new System.IndexOutOfRangeException("invalid history position");
            if(bufferLength < headerSize)
                throw new System.InvalidOperationException($"Snapshot dynamic buffer must always be at least {headerSize} bytes");
#endif
            var slotCapacity = GetDynamicDataCapacity(headerSize, bufferLength);
            return dynamicDataBuffer + headerSize + historyPosition * slotCapacity;
        }
        /// <summary>
        /// Return the currently available space (masks + buffer data) available in each slot.
        /// </summary>
        /// <param name="headerSize">Header size</param>
        /// <param name="length">Length</param>
        /// <returns>The currently available space (masks + buffer data) available in each slot</returns>
        static public uint GetDynamicDataCapacity(uint headerSize, int length)
        {
            if (length < headerSize)
                return 0;
            return (uint)(length - headerSize) / GhostSystemConstants.SnapshotHistorySize;
        }

        /// <summary>
        /// Return the history buffer capacity and the resulting size of each history buffer slot necessary to store
        /// the given dynamic data size.
        /// </summary>
        /// <param name="dynamicDataSize">Dynamic data size</param>
        /// <param name="slotSize">Slot size</param>
        /// <returns>History buffer capacity</returns>
        static public uint CalculateBufferCapacity(uint dynamicDataSize, out uint slotSize)
        {
            var headerSize = GetHeaderSize();
            var newCapacity = headerSize + math.ceilpow2(dynamicDataSize * GhostSystemConstants.SnapshotHistorySize);
            slotSize = (newCapacity - headerSize) / GhostSystemConstants.SnapshotHistorySize;
            return newCapacity;
        }

        /// <summary>
        /// Compute the size of the bitmask for the given number of elements and mask bits. The size is aligned to 16 bytes.
        /// </summary>
        /// <param name="changeMaskBits">Change mask bits</param>
        /// <param name="numElements">Number of elements</param>
        /// <returns>Size of bitmask</returns>
        public static int GetDynamicDataChangeMaskSize(int changeMaskBits, int numElements)
        {
            return GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.ChangeMaskArraySizeInUInts(numElements * changeMaskBits)*4);
        }
    }
}

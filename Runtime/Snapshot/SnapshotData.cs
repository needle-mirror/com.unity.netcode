using Unity.Entities;
using Unity.Mathematics;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    public struct SnapshotData : IComponentData
    {
        public struct DataAtTick
        {
            public System.IntPtr SnapshotBefore;
            public System.IntPtr SnapshotAfter;
            public float InterpolationFactor;
            public uint Tick;
            public int BeforeIdx;
            public int AfterIdx;
            public int GhostOwner;
        }
        public int SnapshotSize;
        public int LatestIndex;

        public unsafe uint GetLatestTick(in DynamicBuffer<SnapshotDataBuffer> buffer)
        {
            if (buffer.Length == 0)
                return 0;
            byte* snapshotData;
            snapshotData = (byte*)buffer.GetUnsafeReadOnlyPtr() + LatestIndex * SnapshotSize;
            return *(uint*)snapshotData;
        }
        public unsafe bool WasLatestTickZeroChange(in DynamicBuffer<SnapshotDataBuffer> buffer, int numChangeUints)
        {
            if (buffer.Length == 0)
                return false;
            byte* snapshotData;
            snapshotData = (byte*)buffer.GetUnsafeReadOnlyPtr() + LatestIndex * SnapshotSize;
            uint* changeMask = (uint*)(snapshotData+4);
            uint anyChange = 0;
            for (int i = 0; i < numChangeUints; ++i)
            {
                anyChange |= changeMask[i];
            }
            return (anyChange == 0);
        }
        public unsafe bool GetDataAtTick(uint targetTick, int predictionOwnerOffset, float targetTickFraction, in DynamicBuffer<SnapshotDataBuffer> buffer, out DataAtTick data, uint MaxExtrapolationTicks)
        {
            data = default;
            if (buffer.Length == 0)
                return false;
            var numBuffers = buffer.Length / SnapshotSize;
            int beforeIdx = 0;
            uint beforeTick = 0;
            int afterIdx = 0;
            uint afterTick = 0;
            // If last tick is fractional before should not include the tick we are targeting, it should instead be included in after
            if (targetTickFraction < 1)
                --targetTick;
            // Loop from latest available to oldest available snapshot
            int slot;
            for (slot = 0; slot < numBuffers; ++slot)
            {
                var curIndex = (LatestIndex + GhostSystemConstants.SnapshotHistorySize - slot) % GhostSystemConstants.SnapshotHistorySize;
                var snapshotData = (byte*)buffer.GetUnsafeReadOnlyPtr() + curIndex * SnapshotSize;
                uint tick = *(uint*)snapshotData;
                if (tick == 0)
                    continue;
                if (SequenceHelpers.IsNewer(tick, targetTick))
                {
                    afterTick = tick;
                    afterIdx = curIndex;
                }
                else
                {
                    beforeTick = tick;
                    beforeIdx = curIndex;
                    break;
                }
            }

            if (beforeTick == 0)
            {
                return false;
            }

            data.SnapshotBefore = (System.IntPtr)((byte*)buffer.GetUnsafeReadOnlyPtr() + beforeIdx * SnapshotSize);
            data.Tick = beforeTick;
            data.GhostOwner = predictionOwnerOffset != 0 ? *(int*) (data.SnapshotBefore + predictionOwnerOffset) : 0;
            if (afterTick == 0)
            {
                data.BeforeIdx = beforeIdx;
                uint beforeBeforeTick = 0;
                int beforeBeforeIdx = 0;
                if (beforeTick != targetTick || targetTickFraction < 1)
                {
                    for (++slot; slot < numBuffers; ++slot)
                    {
                        var curIndex = (LatestIndex + GhostSystemConstants.SnapshotHistorySize - slot) % GhostSystemConstants.SnapshotHistorySize;
                        var snapshotData = (byte*)buffer.GetUnsafeReadOnlyPtr() + curIndex * SnapshotSize;
                        uint tick = *(uint*)snapshotData;
                        if (tick == 0)
                            continue;
                        beforeBeforeTick = tick;
                        beforeBeforeIdx = curIndex;
                        break;
                    }
                }
                if (beforeBeforeTick != 0)
                {
                    data.AfterIdx = beforeBeforeIdx;
                    data.SnapshotAfter = (System.IntPtr)((byte*)buffer.GetUnsafeReadOnlyPtr() + beforeBeforeIdx * SnapshotSize);

                    if (targetTick - beforeTick > MaxExtrapolationTicks)
                        targetTick = beforeTick + MaxExtrapolationTicks;
                    data.InterpolationFactor = (float) (targetTick - beforeBeforeTick) / (float) (beforeTick - beforeBeforeTick);
                    if (targetTickFraction < 1)
                        data.InterpolationFactor += targetTickFraction / (float) (beforeTick - beforeBeforeTick);
                    data.InterpolationFactor = 1-data.InterpolationFactor;
                }
                else
                {
                    data.AfterIdx = beforeIdx;
                    data.SnapshotAfter = data.SnapshotBefore;
                    data.InterpolationFactor = 0;
                }
            }
            else
            {
                data.BeforeIdx = beforeIdx;
                data.AfterIdx = afterIdx;
                data.SnapshotAfter = (System.IntPtr)((byte*)buffer.GetUnsafeReadOnlyPtr() + afterIdx * SnapshotSize);
                data.InterpolationFactor = (float) (targetTick - beforeTick) / (float) (afterTick - beforeTick);
                if (targetTickFraction < 1)
                    data.InterpolationFactor += targetTickFraction / (float) (afterTick - beforeTick);
            }

            return true;
        }
        unsafe byte* AppendData(ref DynamicBuffer<SnapshotDataBuffer> buffer)
        {
            var numBuffers = buffer.Length / SnapshotSize;
            if (numBuffers < 32)
            {
                buffer.ResizeUninitialized(buffer.Length + SnapshotSize);
                LatestIndex = numBuffers;
            }
            else
                LatestIndex = (LatestIndex + 1) % 32;
            // Get the pointer etc
            var ptr = (byte*)buffer.GetUnsafePtr() + LatestIndex * SnapshotSize;
            return ptr;
        }
    }
    public struct SnapshotDataBuffer : IBufferElementData
    {
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
    public struct SnapshotDynamicDataBuffer : IBufferElementData
    {
        public byte Value;
    }

    /// <summary>
    /// Helper class for managing ghost buffers data
    /// </summary>
    public unsafe struct SnapshotDynamicBuffersHelper
    {
        static public uint GetHeaderSize()
        {
            return (uint)GhostCollectionSystem.SnapshotSizeAligned(sizeof(uint) * GhostSystemConstants.SnapshotHistorySize);
        }

        /// <summary>
        /// Retrieve the dynamic buffer history slot pointer
        /// </summary>
        /// <param name="dynamicDataBuffer"></param>
        /// <param name="historyPosition"></param>
        /// <param name="bufferLength"></param>
        /// <returns></returns>
        /// <exception cref="System.IndexOutOfRangeException"></exception>
        /// <exception cref="System.InvalidOperationException"></exception>
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
        /// Return the currently available space (masks + buffer data) avaiable in each slot
        /// </summary>
        /// <param name="headerSize"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        static public uint GetDynamicDataCapacity(uint headerSize, int length)
        {
            if (length < headerSize)
                return 0;
            return (uint)(length - headerSize) / GhostSystemConstants.SnapshotHistorySize;
        }

        static public uint CalculateBufferCapacity(uint dynamicDataSize, out uint slotSize)
        {
            var headerSize = GetHeaderSize();
            var newCapacity = headerSize + math.ceilpow2(dynamicDataSize * GhostSystemConstants.SnapshotHistorySize);
            slotSize = (newCapacity - headerSize) / GhostSystemConstants.SnapshotHistorySize;
            return newCapacity;
        }

        /// <summary>
        /// Compute the size of the bitmask for the given number of elements and mask bits. The size is aligned to 16 bytes
        /// </summary>
        /// <param name="changeMaskBits"></param>
        /// <param name="numElements"></param>
        /// <returns></returns>
        public static int GetDynamicDataChangeMaskSize(int changeMaskBits, int numElements)
        {
            return GhostCollectionSystem.SnapshotSizeAligned(GhostCollectionSystem.ChangeMaskArraySizeInUInts((numElements * changeMaskBits)*4));
        }
    }
}

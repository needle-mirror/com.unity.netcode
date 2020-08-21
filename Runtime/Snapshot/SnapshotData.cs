using Unity.Entities;
using Unity.Networking.Transport;
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
        }
        public int SnapshotSize;
        public int LatestIndex;

        public unsafe uint GetLatestTick(in DynamicBuffer<SnapshotDataBuffer> buffer)
        {
            if (buffer.Length == 0)
                return 0;
            byte* snapshotData;
            snapshotData = (byte*)buffer.GetUnsafePtr() + LatestIndex * SnapshotSize;
            return *(uint*)snapshotData;
        }
        public unsafe bool WasLatestTickZeroChange(in DynamicBuffer<SnapshotDataBuffer> buffer, int numChangeUints)
        {
            if (buffer.Length == 0)
                return false;
            byte* snapshotData;
            snapshotData = (byte*)buffer.GetUnsafePtr() + LatestIndex * SnapshotSize;
            uint* changeMask = (uint*)(snapshotData+4);
            uint anyChange = 0;
            for (int i = 0; i < numChangeUints; ++i)
            {
                anyChange |= changeMask[i];
            }
            return (anyChange == 0);
        }
        public unsafe bool GetDataAtTick(uint targetTick, float targetTickFraction, in DynamicBuffer<SnapshotDataBuffer> buffer, out DataAtTick data)
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
            byte* snapshotData;
            // Loop from latest available to oldest available snapshot
            for (int slot = 0; slot < numBuffers; ++slot)
            {
                var curIndex = (LatestIndex + GhostSystemConstants.SnapshotHistorySize - slot) % GhostSystemConstants.SnapshotHistorySize;
                snapshotData = (byte*)buffer.GetUnsafePtr() + curIndex * SnapshotSize;
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

            data.SnapshotBefore = (System.IntPtr)((byte*)buffer.GetUnsafePtr() + beforeIdx * SnapshotSize);
            data.Tick = beforeTick;
            if (afterTick == 0)
            {
                data.SnapshotAfter = data.SnapshotBefore;
                data.InterpolationFactor = 0;
            }
            else
            {
                data.SnapshotAfter = (System.IntPtr)((byte*)buffer.GetUnsafePtr() + afterIdx * SnapshotSize);
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
}

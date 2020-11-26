using System;
using Unity.Networking.Transport;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
namespace Unity.NetCode.LowLevel.Unsafe
{
    public unsafe struct GhostComponentSerializer
    {
        [Flags]
        public enum SendMask
        {
            None = 0,
            Interpolated = 1,
            Predicted = 2
        }
        public delegate void CopyToFromSnapshotDelegate(IntPtr stateData, IntPtr snapshotData, int SnapshotOffset, int snapshotStride, IntPtr componentData, int componentStride, int count);
        public delegate void RestoreFromBackupDelegate(IntPtr componentData, IntPtr backupData);
        public delegate void PredictDeltaDelegate(IntPtr snapshotData, IntPtr baseline1Data, IntPtr baseline2Data, ref GhostDeltaPredictor predictor);
        public delegate void CalculateChangeMaskDelegate(IntPtr snapshotData, IntPtr baselineData, IntPtr bits, int startOffset);
        public delegate void SerializeDelegate(IntPtr snapshotData, IntPtr baselineData, ref DataStreamWriter writer, ref NetworkCompressionModel compressionModel, IntPtr changeMaskData, int startOffset);
        public delegate void DeserializeDelegate(IntPtr snapshotData, IntPtr baselineData, ref DataStreamReader reader, ref NetworkCompressionModel compressionModel, IntPtr changeMaskData, int startOffset);
        public delegate void ReportPredictionErrorsDelegate(IntPtr componentData, IntPtr backupData, ref UnsafeList<float> errors);
        public struct State : IBufferElementData
        {
            public ulong SerializerHash;
            public ulong GhostFieldsHash;
            public ulong VariantHash;
            public int ExcludeFromComponentCollectionHash;
            public ComponentType ComponentType;
            public int ComponentSize;
            public int SnapshotSize;
            public int ChangeMaskBits;
            public SendMask SendMask;
            public SendToOwnerType SendToOwner;
            public int SendForChildEntities;
            public PortableFunctionPointer<CopyToFromSnapshotDelegate> CopyToSnapshot;
            public PortableFunctionPointer<CopyToFromSnapshotDelegate> CopyFromSnapshot;
            public PortableFunctionPointer<RestoreFromBackupDelegate> RestoreFromBackup;
            public PortableFunctionPointer<PredictDeltaDelegate> PredictDelta;
            public PortableFunctionPointer<CalculateChangeMaskDelegate> CalculateChangeMask;
            public PortableFunctionPointer<SerializeDelegate> Serialize;
            public PortableFunctionPointer<DeserializeDelegate> Deserialize;
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            public PortableFunctionPointer<ReportPredictionErrorsDelegate> ReportPredictionErrors;
            public FixedString512 PredictionErrorNames;
            public int NumPredictionErrorNames;
            #endif
        }
        public static ref T TypeCast<T>(IntPtr value, int offset = 0) where T: struct
        {
            return ref UnsafeUtility.AsRef<T>((byte*)value+offset);
        }
        public static void CopyToChangeMask(IntPtr bitData, uint src, int offset, int numBits)
        {
            var bits = (uint*)bitData;
            int idx = offset >> 5;
            int bitIdx = offset & 0x1f;
            // Align so the first bit of source starts at the specified index and copy the source bits
            bits[idx] |= src << bitIdx;
            // Check how many bits were actually copied, if the source contains more bits than the was copied,
            // align the remaining bits to start at index 0 in the next uint and copy them
            int usedBits = 32 - bitIdx;
            if (numBits > usedBits && usedBits < 32)
                bits[idx+1] |= src >> usedBits;
        }
        public static uint CopyFromChangeMask(IntPtr bitData, int offset, int numBits)
        {
            var bits = (uint*)bitData;
            int idx = offset >> 5;
            int bitIdx = offset & 0x1f;
            // Align so the first bit of the big array starts at index 0 in the copied bit mask
            uint result = bits[idx] >> bitIdx;
            // Check how many bits were actually copied, if the source contains more bits than the was copied,
            // align the remaining bits to start at index 0 in the next uint and copy them
            int usedBits = 32 - bitIdx;
            if (numBits > usedBits && usedBits < 32)
                result |= bits[idx+1] << usedBits;
            return result;
        }
    }
}

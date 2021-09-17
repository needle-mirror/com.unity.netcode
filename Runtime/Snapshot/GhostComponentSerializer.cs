using System;
using System.Collections.Generic;
using Unity.Networking.Transport;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using System.Runtime.InteropServices;
namespace Unity.NetCode
{
    public abstract partial class GhostComponentSerializerRegistrationSystemBase : SystemBase
    {}
}

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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PostSerializeDelegate(IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, int count, IntPtr baselines, ref DataStreamWriter writer, ref NetworkCompressionModel compressionModel, IntPtr entityStartBit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PostSerializeBufferDelegate(IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, int count, IntPtr baselines, ref DataStreamWriter writer, ref NetworkCompressionModel compressionModel, IntPtr entityStartBit, IntPtr snapshotDynamicDataPtr, IntPtr dynamicSizePerEntity, int dynamicSnapshotMaxOffset);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SerializeDelegate(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, IntPtr componentData, int componentStride, int count, IntPtr baselines, ref DataStreamWriter writer, ref NetworkCompressionModel compressionModel, IntPtr entityStartBit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SerializeChildDelegate(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, IntPtr componentData, int count, IntPtr baselines, ref DataStreamWriter writer, ref NetworkCompressionModel compressionModel, IntPtr entityStartBit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SerializeBufferDelegate(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, IntPtr componentData, IntPtr componentDataLen, int count, IntPtr baselines, ref DataStreamWriter writer, ref NetworkCompressionModel compressionModel, IntPtr entityStartBit, IntPtr snapshotDynamicDataPtr, ref int snapshotDynamicDataOffset, IntPtr dynamicSizePerEntity, int dynamicSnapshotMaxOffset);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CopyToFromSnapshotDelegate(IntPtr stateData, IntPtr snapshotData, int SnapshotOffset, int snapshotStride, IntPtr componentData, int componentStride, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void RestoreFromBackupDelegate(IntPtr componentData, IntPtr backupData);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PredictDeltaDelegate(IntPtr snapshotData, IntPtr baseline1Data, IntPtr baseline2Data, ref GhostDeltaPredictor predictor);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DeserializeDelegate(IntPtr snapshotData, IntPtr baselineData, ref DataStreamReader reader, ref NetworkCompressionModel compressionModel, IntPtr changeMaskData, int startOffset);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReportPredictionErrorsDelegate(IntPtr componentData, IntPtr backupData, ref UnsafeList<float> errors);

        /// <summary>
        ///     This buffer is added to the GhostCollection singleton entity.
        ///     Stores serialization meta-data for the ghost.
        ///     Too large to be stored in chunk memory.
        /// </summary>
        [InternalBufferCapacity(0)]
        public struct State : IBufferElementData
        {
            public ulong SerializerHash;
            public ulong GhostFieldsHash;
            public ulong VariantHash;
            public ComponentType ComponentType;
            public int VariantTypeIndex;
            public int ComponentSize;
            public int SnapshotSize;
            public int ChangeMaskBits;
            public SendMask SendMask;
            public SendToOwnerType SendToOwner;
            public int SendForChildEntities;
            public PortableFunctionPointer<PostSerializeDelegate> PostSerialize;
            public PortableFunctionPointer<PostSerializeBufferDelegate> PostSerializeBuffer;
            public PortableFunctionPointer<SerializeDelegate> Serialize;
            public PortableFunctionPointer<SerializeChildDelegate> SerializeChild;
            public PortableFunctionPointer<SerializeBufferDelegate> SerializeBuffer;
            public PortableFunctionPointer<CopyToFromSnapshotDelegate> CopyToSnapshot;
            public PortableFunctionPointer<CopyToFromSnapshotDelegate> CopyFromSnapshot;
            public PortableFunctionPointer<RestoreFromBackupDelegate> RestoreFromBackup;
            public PortableFunctionPointer<PredictDeltaDelegate> PredictDelta;
            public PortableFunctionPointer<DeserializeDelegate> Deserialize;
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            public PortableFunctionPointer<ReportPredictionErrorsDelegate> ReportPredictionErrors;
            public FixedString512Bytes PredictionErrorNames;
            public int NumPredictionErrorNames;
            public Unity.Profiling.ProfilerMarker ProfilerMarker;
            #endif
        }
        public static List<Type> VariantTypes = new List<Type>();

        public static int SizeInSnapshot(in State serializer)
        {
            return serializer.ComponentType.IsBuffer
                ? GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize)
                : GhostCollectionSystem.SnapshotSizeAligned(serializer.SnapshotSize);
        }

        public static ref T TypeCast<T>(IntPtr value, int offset = 0) where T: struct
        {
            return ref UnsafeUtility.AsRef<T>((byte*)value+offset);
        }
        public static IntPtr IntPtrCast<T>(ref T value) where T: struct
        {
            return (IntPtr)UnsafeUtility.AddressOf(ref value);
        }
        public static void CopyToChangeMask(IntPtr bitData, uint src, int offset, int numBits)
        {
            var bits = (uint*)bitData;
            int idx = offset >> 5;
            int bitIdx = offset & 0x1f;
            // Clear the bits we are about to write so this function sets them to the correct value even if they are not already zero
            bits[idx] &= (uint)(((1UL << bitIdx)-1) | ~((1UL << (bitIdx+numBits))-1));
            // Align so the first bit of source starts at the specified index and copy the source bits
            bits[idx] |= src << bitIdx;
            // Check how many bits were actually copied, if the source contains more bits than the was copied,
            // align the remaining bits to start at index 0 in the next uint and copy them
            int usedBits = 32 - bitIdx;
            if (numBits > usedBits && usedBits < 32)
            {
                // Clear the bits we are about to write so this function sets them to the correct value even if they are not already zero
                bits[idx+1] &= ~((1u << (numBits-usedBits))-1);
                bits[idx+1] |= src >> usedBits;
            }
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

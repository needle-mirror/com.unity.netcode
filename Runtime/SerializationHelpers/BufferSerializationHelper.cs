using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// Helper class used by code-gen to setup the serialisation function pointers.
    /// </summary>
    /// <typeparam name="TComponentType">The unmanaged buffer the helper serialise</typeparam>
    /// <typeparam name="TSnapshot">The snaphost data struct that contains the <see cref="IBufferElementData"/> data.</typeparam>
    /// <typeparam name="TSerializer">A concrete type that implement the <see cref="IGhostSerializer"/> interface.</typeparam>
    public static class BufferSerializationHelper<TComponentType, TSnapshot, TSerializer>
        where TComponentType: unmanaged
        where TSnapshot: unmanaged
        where TSerializer: unmanaged, IGhostSerializer
    {
        /// <summary>
        /// Copy the pre-serialized dynamic buffer data to data stream <paramref name="writer"/> using the <paramref name="serializer"/> strategy.
        /// </summary>
        /// <param name="snapshotData">the snapshot buffer</param>
        /// <param name="snapshotOffset">the current offset in the snapshot buffer</param>
        /// <param name="snapshotStride">the stride to apply to each individual entity</param>
        /// <param name="maskOffsetInBits">the offset in the changemask bit array</param>
        /// <param name="changeMaskBits">the changemask bit array</param>
        /// <param name="count">the number of entities</param>
        /// <param name="baselines">the baselines for each entity</param>
        /// <param name="writer">the output data stream</param>
        /// <param name="compressionModel">the compression model used to compressed the stream</param>
        /// <param name="entityStartBit">an array of start/end offset in the data stream, that denote for each individual component where their compressed data is stored in the data stream.</param>
        /// <param name="snapshotDynamicDataPtr">storage for the buffer snapshot</param>
        /// <param name="dynamicSizePerEntity">the total buffer size (in bytes) written into the snapshot buffer for each entity.</param>
        /// <param name="dynamicSnapshotMaxOffset">the dynamic snapshot buffer capacity</param>
        /// <param name="serializer">the IGhostSerialized instance used to serialize the buffer content</param>
        public static void PostSerializeBuffers(IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int changeMaskBits, int count, IntPtr baselines, ref DataStreamWriter writer,
            StreamCompressionModel compressionModel, IntPtr entityStartBit, IntPtr snapshotDynamicDataPtr,
            IntPtr dynamicSizePerEntity, int dynamicSnapshotMaxOffset, TSerializer serializer)
        {
            int dynamicDataSize = UnsafeUtility.SizeOf<TSnapshot>();
            if (serializer.SizeInSnapshot == 0)
            {
                for (int i = 0; i < count; ++i)
                {
                    const int IntSize = 4;
                    ref var startuint = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize*2*i);
                    startuint = writer.Length/IntSize;
                    startuint = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize*2*i+IntSize);
                    startuint = 0;
                }
                return;
            }
            for (int i = 0; i < count; ++i)
            {
                // Get the elements count and the buffer content offset inside the dynamic data history buffer from the pre-serialized snapshot
                int len = GhostComponentSerializer.TypeCast<int>(snapshotData + snapshotStride*i, snapshotOffset);
                int dynamicSnapshotDataOffset = GhostComponentSerializer.TypeCast<int>(snapshotData + snapshotStride*i, snapshotOffset+4);
                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(changeMaskBits, len);
                CheckDynamicDataRange(dynamicSnapshotDataOffset, maskSize, len, dynamicDataSize, dynamicSnapshotMaxOffset);
                SerializeOneBuffer(i, snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, changeMaskBits, baselines, ref writer,
                    compressionModel, entityStartBit, snapshotDynamicDataPtr, dynamicSizePerEntity, len, ref dynamicSnapshotDataOffset, dynamicDataSize, maskSize,
                    serializer);
            }
        }

        /// <summary>
        /// Serialize the dynamic buffer content to the <paramref name="writer"/> stream using the <paramref name="serializer"/> strategy.
        /// </summary>
        /// <param name="stateData">a pointer to the <see cref="GhostSerializerState"/> struct </param>
        /// <param name="snapshotData">the snapshot buffer</param>
        /// <param name="snapshotOffset">the current offset in the snapshot buffer</param>
        /// <param name="snapshotStride">the stride to apply to each individual entity</param>
        /// <param name="maskOffsetInBits">the offset in the changemask bit array</param>
        /// <param name="changeMaskBits">the changemask bit array</param>
        /// <param name="componentData">a pointer to the chunk component data </param>
        /// <param name="componentDataLen">the len of each individual buffer</param>
        /// <param name="count">the number of entities</param>
        /// <param name="baselines">the baselines for each entity</param>
        /// <param name="writer">the output data stream</param>
        /// <param name="compressionModel">the compression model used to compressed the stream</param>
        /// <param name="entityStartBit">an array of start/end offset in the data stream, that denote for each individual component where their compressed data is stored in the data stream.</param>
        /// <param name="snapshotDynamicDataPtr">storage for the buffer snapshot</param>
        /// <param name="dynamicSnapshotDataOffset">the current offset in the dynamic snapshot buffer</param>
        /// <param name="dynamicSizePerEntity">the total buffer size (in bytes) written into the snapshot buffer for each entity.</param>
        /// <param name="dynamicSnapshotMaxOffset">the dynamic snapshot buffer capacity</param>
        /// <param name="serializer">the IGhostSerialized instance used to serialize the buffer content</param>
        public static void SerializeBuffers(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int changeMaskBits, IntPtr componentData, IntPtr componentDataLen, int count,
            IntPtr baselines, ref DataStreamWriter writer, StreamCompressionModel compressionModel, IntPtr entityStartBit,
            IntPtr snapshotDynamicDataPtr, ref int dynamicSnapshotDataOffset, IntPtr dynamicSizePerEntity,
            int dynamicSnapshotMaxOffset, TSerializer serializer)
        {
            int dynamicDataSize = UnsafeUtility.SizeOf<TSnapshot>();
            int componentStride = UnsafeUtility.SizeOf<TComponentType>();
            ref readonly var serializerState = ref GhostComponentSerializer.TypeCastReadonly<GhostSerializerState>(stateData);
            for (int i = 0; i < count; ++i)
            {
                int len = GhostComponentSerializer.TypeCast<int>(componentDataLen, i*4);
                //Set the elements count and the buffer content offset inside the dynamic data history buffer
                GhostComponentSerializer.TypeCast<uint>(snapshotData + snapshotStride*i, snapshotOffset) = (uint)len;
                GhostComponentSerializer.TypeCast<uint>(snapshotData + snapshotStride*i, snapshotOffset+4) = (uint)dynamicSnapshotDataOffset;

                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(changeMaskBits, len);
                CheckDynamicDataRange(dynamicSnapshotDataOffset, maskSize, len, dynamicDataSize, dynamicSnapshotMaxOffset);

                if (len > 0)
                {
                    //Copy the buffer contents
                    IntPtr curCompData = GhostComponentSerializer.TypeCast<IntPtr>(componentData, UnsafeUtility.SizeOf<IntPtr>()*i);
                    IntPtr snapshotData1 = snapshotDynamicDataPtr + maskSize;
                    ref readonly var serializerState1 = ref GhostComponentSerializer.TypeCastReadonly<GhostSerializerState>(stateData);
                    for (int i1 = 0; i1 < len; ++i1)
                    {
                        serializer.CopyToSnapshot(serializerState1, snapshotData1 + dynamicSnapshotDataOffset + dynamicDataSize*i1, curCompData + componentStride*i1);
                    }
                }
                SerializeOneBuffer(i,
                    snapshotData, snapshotOffset, snapshotStride,
                    maskOffsetInBits, changeMaskBits, baselines,
                    ref writer, compressionModel, entityStartBit, snapshotDynamicDataPtr,
                    dynamicSizePerEntity, len,
                    ref dynamicSnapshotDataOffset, dynamicDataSize, maskSize, serializer);
            }
        }

        /// <summary>
        /// Copy the dynamic buffers content to the snapshot, using the <paramref name="serializer"/> strategy.
        /// </summary>
        /// <param name="stateData">a pointer to the <see cref="GhostSerializerState"/> struct </param>
        /// <param name="snapshotData">the snapshot buffer</param>
        /// <param name="snapshotOffset">the current offset in the snapshot buffer</param>
        /// <param name="snapshotStride">the stride to apply to snapshot pointer for each individual entity</param>
        /// <param name="componentData">a pointer to the chunk component data </param>
        /// <param name="componentStride">the stride to apply to component pointer for each individual entity</param>
        /// <param name="count">the number of entities</param>
        /// <param name="serializer">the IGhostSerialized instance used to serialize the buffer content</param>
        public static void CopyBuffersToSnapshot(IntPtr stateData, IntPtr snapshotData, int snapshotOffset,
            int snapshotStride, IntPtr componentData, int componentStride, int count, TSerializer serializer)
        {
            ref readonly var serializerState = ref GhostComponentSerializer.TypeCastReadonly<GhostSerializerState>(stateData);
            for (int i = 0; i < count; ++i)
            {
                serializer.CopyToSnapshot(serializerState, snapshotData + snapshotOffset + snapshotStride*i, componentData + componentStride*i);
            }
        }

        /// <summary>
        /// Copy the dynamic buffers content from the snapshot, using the <paramref name="serializer"/> strategy.
        /// </summary>
        /// <param name="stateData">a pointer to the <see cref="GhostSerializerState"/> struct </param>
        /// <param name="snapshotData">the snapshot buffer</param>
        /// <param name="snapshotOffset">the current offset in the snapshot buffer</param>
        /// <param name="snapshotStride">the stride to apply to snapshot pointer for each individual entity</param>
        /// <param name="componentData">a pointer to the chunk component data </param>
        /// <param name="componentStride">the stride to apply to component pointer for each individual entity</param>
        /// <param name="count">the number of entities</param>
        /// <param name="serializer">the IGhostSerialized instance used to serialize the buffer content</param>
        public static void CopyBuffersFromSnapshot(IntPtr stateData, IntPtr snapshotData, int snapshotOffset,
            int snapshotStride, IntPtr componentData, int componentStride, int count, TSerializer serializer)
        {
            var deserializerState = GhostComponentSerializer.TypeCast<GhostDeserializerState>(stateData);
            ref var snapshotInterpolationData = ref GhostComponentSerializer.TypeCast<SnapshotData.DataAtTick>(snapshotData);
            deserializerState.SnapshotTick = snapshotInterpolationData.Tick;
            for (int i = 0; i < count; ++i)
            {
                //For buffers the function iterate over the element in the buffers not entities.
                var snapshotBefore = snapshotInterpolationData.SnapshotBefore + snapshotOffset +snapshotStride * i;
                serializer.CopyFromSnapshot(deserializerState, componentData + componentStride*i,
                    snapshotInterpolationData.InterpolationFactor, snapshotInterpolationData.InterpolationFactor,
                    snapshotBefore, snapshotBefore);
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckDynamicDataRange(int dynamicSnapshotDataOffset, int maskSize, int len, int dynamicDataSize, int dynamicSnapshotMaxOffset)
        {
            if ((dynamicSnapshotDataOffset + maskSize + len*dynamicDataSize) > dynamicSnapshotMaxOffset)
                throw new InvalidOperationException("writing snapshot dyanmicdata outside of memory history buffer memory boundary");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckDynamicMaskOffset(int offset, int sizeInBytes)
        {
            if (offset > sizeInBytes*8)
                throw new InvalidOperationException("writing dynamic mask bits outside out of bound");
        }

        const int IntSize = 4;
        const int BaselinesPerEntity = 4;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SerializeOneBuffer(
            int ent, IntPtr snapshotData,
            int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int changeMaskBits,
            IntPtr baselines,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel, IntPtr entityStartBit,
            IntPtr snapshotDynamicDataPtr, IntPtr dynamicSizePerEntity,
            int len, ref int dynamicSnapshotDataOffset, int dynamicDataSize, int maskSize,
            TSerializer serializer)
        {
            int PtrSize = UnsafeUtility.SizeOf<IntPtr>();
            var baseline0Ptr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize*ent*BaselinesPerEntity);
            var baselineDynamicDataPtr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize*(ent*BaselinesPerEntity+3));
            var changeMaskPtr = snapshotData + sizeof(int) + ent * snapshotStride;
            ref var startuint = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize*2*ent);
            startuint = writer.Length/IntSize;

            DefaultBufferSerialization.SerializeBufferToStream(
                serializer,
                baseline0Ptr, snapshotOffset,
                changeMaskPtr, maskOffsetInBits, changeMaskBits,
                snapshotDynamicDataPtr, baselineDynamicDataPtr, len, dynamicSnapshotDataOffset,
                dynamicDataSize, maskSize, ref writer, compressionModel);

            var dynamicSize = GhostComponentSerializer.SnapshotSizeAligned(maskSize + dynamicDataSize * len);
            GhostComponentSerializer.TypeCast<int>(dynamicSizePerEntity, ent*IntSize) += dynamicSize;
            dynamicSnapshotDataOffset += dynamicSize;
            ref var sbit = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize*2*ent+IntSize);
            sbit = writer.LengthInBits - startuint*32;
            var missing = 32-writer.LengthInBits&31;
            if (missing < 32)
                writer.WriteRawBits(0, missing);
        }
    }
}

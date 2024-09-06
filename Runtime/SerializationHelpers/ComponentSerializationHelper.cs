using System;
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
    public static unsafe class ComponentSerializationHelper<TComponentType, TSnapshot, TSerializer>
        where TComponentType : unmanaged
        where TSnapshot : unmanaged
        where TSerializer : unmanaged, IGhostSerializer
    {
        const int IntSize = 4;
        const int BaselinesPerEntity = 4;
        private static void SerializeEntities(TSerializer serializer, IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int count, IntPtr baselines, ref DataStreamWriter writer,
            in StreamCompressionModel compressionModel, IntPtr entityStartBit)
        {
            var PtrSize = UnsafeUtility.SizeOf<IntPtr>();
            for (int ent = 0; ent < count; ++ent)
            {
                ref var startuint = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize * 2 * ent);
                startuint = writer.Length / IntSize;

                // Calculate the baseline
                TSnapshot baseline = default;
                var baseline0Ptr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize * ent * BaselinesPerEntity);
                if (baseline0Ptr != IntPtr.Zero)
                {
                    baseline = GhostComponentSerializer.TypeCast<TSnapshot>(baseline0Ptr, snapshotOffset);
                    var baseline2Ptr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize * (ent * BaselinesPerEntity + 2));
                    if (baseline2Ptr != IntPtr.Zero)
                    {
                        var baseline1Ptr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize * (ent * BaselinesPerEntity + 1));
                        var predictor = new GhostDeltaPredictor(
                            new NetworkTick { SerializedData = GhostComponentSerializer.TypeCast<uint>(snapshotData + snapshotStride * ent) },
                            new NetworkTick { SerializedData = GhostComponentSerializer.TypeCast<uint>(baseline0Ptr) },
                            new NetworkTick { SerializedData = GhostComponentSerializer.TypeCast<uint>(baseline1Ptr) },
                            new NetworkTick { SerializedData = GhostComponentSerializer.TypeCast<uint>(baseline2Ptr) });
                        serializer.PredictDelta(GhostComponentSerializer.IntPtrCast(ref baseline), baseline1Ptr + snapshotOffset,
                            baseline2Ptr + snapshotOffset, ref predictor);
                    }
                }

                var snapshotPtr = snapshotData + snapshotOffset + snapshotStride * ent;
                var baselinePtr = GhostComponentSerializer.IntPtrCast(ref baseline);
                serializer.CalculateChangeMask(snapshotPtr, baselinePtr, snapshotData + IntSize + snapshotStride * ent, maskOffsetInBits);
                serializer.Serialize(snapshotPtr, baselinePtr, snapshotData + IntSize + snapshotStride * ent, maskOffsetInBits, ref writer, compressionModel);
                ref var sbit = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize * 2 * ent + IntSize);
                sbit = writer.LengthInBits - startuint * 32;
                var missing = 32 - writer.LengthInBits & 31;
                if (missing < 32)
                    writer.WriteRawBits(0, missing);
            }
        }

        /// <summary>
        /// For internal use by source generator, write pre-serialized components data to the <paramref name="writer"/>
        /// stream.
        /// </summary>
        /// <param name="serializer">the IGhostSerialized instance used to serialize the buffer content</param>
        /// <param name="snapshotData">the snapshot buffer</param>
        /// <param name="snapshotOffset">the current offset in the snapshot buffer</param>
        /// <param name="snapshotStride">the stride to apply to each individual entity</param>
        /// <param name="maskOffsetInBits">the offset in the changemask bit array</param>
        /// <param name="count">the number of entities</param>
        /// <param name="baselines">the baselines for each entity</param>
        /// <param name="writer">the output data stream</param>
        /// <param name="compressionModel">the compression model used to compressed the stream</param>
        /// <param name="entityStartBit">an array of start/end offset in the data stream, that denote for each individual component where their compressed data is stored in the data stream.</param>
        public static void PostSerializeComponents(TSerializer serializer,
            IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits,
            int count, IntPtr baselines, ref DataStreamWriter writer,
            ref StreamCompressionModel compressionModel,
            IntPtr entityStartBit)
        {
            SerializeEntities(serializer,snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, count, baselines,
                ref writer, compressionModel, entityStartBit);
        }

        /// <summary>
        /// For internal use by source generator, copy the component data to the snapshot,
        /// calculate the change masks, and write the delta compressed snapshot data to the <paramref name="writer"/>
        /// stream.
        /// </summary>
        /// <param name="serializer">the IGhostSerialized instance used to serialize the buffer content</param>
        /// <param name="stateData">a pointer to the <see cref="GhostSerializerState"/> struct </param>
        /// <param name="snapshotData">the snapshot buffer</param>
        /// <param name="snapshotOffset">the current offset in the snapshot buffer</param>
        /// <param name="snapshotStride">the stride to apply to each individual entity</param>
        /// <param name="maskOffsetInBits">the offset in the changemask bit array</param>
        /// <param name="componentData">a pointer to the chunk component data </param>
        /// <param name="count">the number of entities</param>
        /// <param name="baselines">the baselines for each entity</param>
        /// <param name="writer">the output data stream</param>
        /// <param name="compressionModel">the compression model used to compressed the stream</param>
        /// <param name="entityStartBit">an array of start/end offset in the data stream, that denote for each individual component where their compressed data is stored in the data stream.</param>
        public static void SerializeComponents(TSerializer serializer,
            IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, IntPtr componentData, int count, IntPtr baselines, ref DataStreamWriter writer,
            StreamCompressionModel compressionModel, IntPtr entityStartBit)
        {
            ref var serializerState = ref GhostComponentSerializer.TypeCast<GhostSerializerState>(stateData);
            var IntPtrSize = UnsafeUtility.SizeOf<IntPtr>();
            for (int ent = 0; ent < count; ++ent)
            {
                IntPtr curCompData = GhostComponentSerializer.TypeCast<IntPtr>(componentData, IntPtrSize * ent);
                var snapshot = snapshotData + snapshotOffset + snapshotStride * ent;
                if (curCompData != IntPtr.Zero)
                {
                    serializer.CopyToSnapshot(serializerState, snapshot, curCompData);
                }
                else
                {
                    *(TSnapshot*)snapshot = default;
                }
            }

            SerializeEntities(serializer,snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, count, baselines,
                ref writer, compressionModel, entityStartBit);
        }

        /// <summary>
        /// Copy component data to the snapshot buffer using the <paramref name="serializer"/> strategy.
        /// </summary>
        /// <param name="stateData">a pointer to the <see cref="GhostSerializerState"/> struct </param>
        /// <param name="snapshotData">the snapshot buffer</param>
        /// <param name="snapshotOffset">the current offset in the snapshot buffer</param>
        /// <param name="snapshotStride">the stride to apply to snapshot pointer for each individual entity</param>
        /// <param name="componentData">a pointer to the chunk component data </param>
        /// <param name="componentStride">the stride to apply to component pointer for each individual entity</param>
        /// <param name="count">the number of entities</param>
        /// <param name="serializer">the IGhostSerialized instance used to serialize the buffer content</param>
        public static void CopyComponentsToSnapshot(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            IntPtr componentData, int componentStride, int count, TSerializer serializer)
        {
            ref var serializerState = ref GhostComponentSerializer.TypeCast<GhostSerializerState>(stateData);
            for (int i = 0; i < count; ++i)
            {
                var snapshot = snapshotData + snapshotOffset + snapshotStride * i;
                var component = componentData + componentStride * i;
                serializer.CopyToSnapshot(serializerState, snapshot, component);
            }
        }

        /// <summary>
        /// Copy the component data from the snapshot buffer using the <paramref name="serializer"/> strategy.
        /// </summary>
        /// <param name="stateData">a pointer to the <see cref="GhostSerializerState"/> struct </param>
        /// <param name="snapshotData">the snapshot buffer</param>
        /// <param name="snapshotOffset">the current offset in the snapshot buffer</param>
        /// <param name="snapshotStride">the stride to apply to snapshot pointer for each individual entity</param>
        /// <param name="componentData">a pointer to the chunk component data </param>
        /// <param name="componentStride">the stride to apply to component pointer for each individual entity</param>
        /// <param name="count">the number of entities</param>
        /// <param name="serializer">the IGhostSerialized instance used to serialize the buffer content</param>
        public static void CopyComponentsFromSnapshot(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            IntPtr componentData, int componentStride, int count, TSerializer serializer)
        {
            var deserializerState = GhostComponentSerializer.TypeCast<GhostDeserializerState>(stateData);
            for (int i = 0; i < count; ++i)
            {
                ref var snapshotInterpolationData = ref GhostComponentSerializer.TypeCast<SnapshotData.DataAtTick>(snapshotData, snapshotStride * i);
                //Compute the required owner mask for the components and buffers by retrievieng the ghost owner id from the data for the current tick.
                if((deserializerState.SendToOwner & snapshotInterpolationData.RequiredOwnerSendMask) == 0)
                    continue;

                deserializerState.SnapshotTick = snapshotInterpolationData.Tick;
                var snapshotBefore = snapshotInterpolationData.SnapshotBefore + snapshotOffset;
                var snapshotAfter = snapshotInterpolationData.SnapshotAfter + snapshotOffset;
                serializer.CopyFromSnapshot(deserializerState, componentData + componentStride * i,
                    snapshotInterpolationData.InterpolationFactor,
                    snapshotInterpolationData.InterpolationFactor, snapshotBefore, snapshotAfter);
            }
        }
    }
}

using System;
using System.Diagnostics;
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
    /// <typeparam name="TSerializer">A concrete type that implement the <see cref="IGhostSerializer{TComponent,TSnapshot}"/> interface.</typeparam>
    [BurstCompile]
    public static class BufferSerializationHelper<TComponentType, TSnapshot, TSerializer>
        where TComponentType: unmanaged
        where TSnapshot: unmanaged
        where TSerializer: unmanaged, IGhostSerializer<TComponentType, TSnapshot>
    {
        /// <summary>
        /// Setup all the <see cref="GhostComponentSerializer.State"/> data and function pointers.
        /// </summary>
        /// <param name="state"></param>
        /// <param name="systemState"></param>
        /// <returns>if the <param name="state"></param>/> has been initialised.</returns>
        public static bool SetupFunctionPointers(ref GhostComponentSerializer.State state,
            ref SystemState systemState)
        {
            // Optimization: Creating burst functions is expensive.
            // We don't need to do it in literally any other words as they're never called.
            if ((systemState.WorldUnmanaged.Flags & WorldFlags.GameServer) != WorldFlags.GameServer
                && (systemState.WorldUnmanaged.Flags & WorldFlags.GameClient) != WorldFlags.GameClient
                && (systemState.WorldUnmanaged.Flags & WorldFlags.GameThinClient) != WorldFlags.GameThinClient)
                return false;

            if(state.SnapshotSize == 0)
            {
                ZeroSizeComponentSerializationHelper.SetupFunctionPointers(ref state);
                return true;
            }

            state.PostSerializeBuffer = new PortableFunctionPointer<GhostComponentSerializer.PostSerializeBufferDelegate>(
                PostSerializeBuffer);
            state.SerializeBuffer = new PortableFunctionPointer<GhostComponentSerializer.SerializeBufferDelegate>(
                SerializeBuffer);
            state.CopyFromSnapshot = new PortableFunctionPointer<GhostComponentSerializer.CopyToFromSnapshotDelegate>(
                CopyBufferFromSnapshot);
            state.CopyToSnapshot = new PortableFunctionPointer<GhostComponentSerializer.CopyToFromSnapshotDelegate>(
                CopyBufferToSnapshot);
            state.RestoreFromBackup = new PortableFunctionPointer<GhostComponentSerializer.RestoreFromBackupDelegate>(
                RestoreFromBackup);
            state.PredictDelta = new PortableFunctionPointer<GhostComponentSerializer.PredictDeltaDelegate>(
                PredictDelta);
            state.Deserialize = new PortableFunctionPointer<GhostComponentSerializer.DeserializeDelegate>(
                Deserialize);
#if UNITY_EDITOR || NETCODE_DEBUG
            state.ReportPredictionErrors = new PortableFunctionPointer<GhostComponentSerializer.ReportPredictionErrorsDelegate>(
                ReportPredictionErrors);
#endif
            return true;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.RestoreFromBackupDelegate))]
        private static void RestoreFromBackup([NoAlias]IntPtr componentData, [NoAlias]IntPtr backupData)
        {
            default(TSerializer).RestoreFromBackupGenerated(
                ref GhostComponentSerializer.TypeCast<TComponentType>(componentData),
                GhostComponentSerializer.TypeCastReadonly<TComponentType>(backupData));
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.PredictDeltaDelegate))]
        private static void PredictDelta([NoAlias]IntPtr snapshotData,
            [NoAlias]IntPtr baseline1Data, [NoAlias]IntPtr baseline2Data, ref GhostDeltaPredictor predictor)
        {
            ref var snapshot = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotData);
            ref readonly var baseline1 = ref GhostComponentSerializer.TypeCastReadonly<TSnapshot>(baseline1Data);
            ref readonly var baseline2 = ref GhostComponentSerializer.TypeCastReadonly<TSnapshot>(baseline2Data);
            default(TSerializer).PredictDeltaGenerated(ref snapshot, baseline1, baseline2, ref predictor);
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.DeserializeDelegate))]
        private static void Deserialize([NoAlias]IntPtr snapshotData, [NoAlias]IntPtr baselineData, ref DataStreamReader reader, ref StreamCompressionModel compressionModel, [NoAlias]IntPtr changeMaskData, int startOffset)
        {
            ref var snapshot = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotData);
            ref readonly var baseline = ref GhostComponentSerializer.TypeCastReadonly<TSnapshot>(baselineData);
            default(TSerializer).DeserializeGenerated(ref reader, compressionModel, changeMaskData, startOffset, ref snapshot, baseline);
        }

#if UNITY_EDITOR || NETCODE_DEBUG
        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.ReportPredictionErrorsDelegate))]
        private static void ReportPredictionErrors([NoAlias]IntPtr componentData, [NoAlias]IntPtr backupData, [NoAlias]IntPtr errorsList, int errorsCount)
        {
            ref readonly var component = ref GhostComponentSerializer.TypeCastReadonly<TComponentType>(componentData);
            ref readonly var backup = ref GhostComponentSerializer.TypeCastReadonly<TComponentType>(backupData);
            default(TSerializer).ReportPredictionErrorsGenerated(component, backup, errorsList, errorsCount);
        }
#endif
        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.PostSerializeBufferDelegate))]
        private static void PostSerializeBuffer([NoAlias]IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int changeMaskBits, int count, [NoAlias]IntPtr baselines, ref DataStreamWriter writer, ref StreamCompressionModel compressionModel,
            [NoAlias]IntPtr entityStartBit, [NoAlias]IntPtr snapshotDynamicDataPtr, [NoAlias]IntPtr dynamicSizePerEntity, int dynamicSnapshotMaxOffset)
        {
            int dynamicDataSize = UnsafeUtility.SizeOf<TSnapshot>();
            for (int i = 0; i < count; ++i)
            {
                // Get the elements count and the buffer content offset inside the dynamic data history buffer from the pre-serialized snapshot
                int len = GhostComponentSerializer.TypeCast<int>(snapshotData + snapshotStride*i, snapshotOffset);
                int dynamicSnapshotDataOffset = GhostComponentSerializer.TypeCast<int>(snapshotData + snapshotStride*i, snapshotOffset+4);
                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(changeMaskBits, len);
                CheckDynamicDataRange(dynamicSnapshotDataOffset, maskSize, len, dynamicDataSize, dynamicSnapshotMaxOffset);
                SerializeOneBuffer(i, snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, changeMaskBits, baselines, ref writer,
                    compressionModel, entityStartBit, snapshotDynamicDataPtr, dynamicSizePerEntity, len, ref dynamicSnapshotDataOffset, dynamicDataSize, maskSize);
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.SerializeBufferDelegate))]
        private static void SerializeBuffer([NoAlias]IntPtr stateData,
            [NoAlias]IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int changeMaskBits,
            [NoAlias]IntPtr componentData, [NoAlias]IntPtr componentDataLen, int count, [NoAlias]IntPtr baselines,
            ref DataStreamWriter writer, ref StreamCompressionModel compressionModel,
            [NoAlias]IntPtr entityStartBit, [NoAlias]IntPtr snapshotDynamicDataPtr, ref int dynamicSnapshotDataOffset,
            [NoAlias]IntPtr dynamicSizePerEntity, int dynamicSnapshotMaxOffset)
        {
            int dynamicDataSize = UnsafeUtility.SizeOf<TSnapshot>();
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
                    CopyBufferToSnapshot(stateData, snapshotDynamicDataPtr + maskSize, dynamicSnapshotDataOffset, dynamicDataSize, curCompData, UnsafeUtility.SizeOf<TComponentType>(), len);
                }
                SerializeOneBuffer(i,
                    snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, changeMaskBits, baselines,
                    ref writer, compressionModel, entityStartBit, snapshotDynamicDataPtr,
                    dynamicSizePerEntity, len,
                    ref dynamicSnapshotDataOffset, dynamicDataSize, maskSize);
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.CopyToFromSnapshotDelegate))]
        private static void CopyBufferToSnapshot([NoAlias]IntPtr stateData, [NoAlias]IntPtr snapshotData, int snapshotOffset, int snapshotStride, [NoAlias]IntPtr componentData, int componentStride, int count)
        {
            var serializer = default(TSerializer);
            for (int i = 0; i < count; ++i)
            {
                ref var snapshot = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotData, snapshotOffset + snapshotStride*i);
                ref var component = ref GhostComponentSerializer.TypeCast<TComponentType>(componentData, componentStride*i);
                ref var serializerState = ref GhostComponentSerializer.TypeCast<GhostSerializerState>(stateData);
                serializer.CopyToSnapshotGenerated(serializerState, ref snapshot, component);
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.CopyToFromSnapshotDelegate))]
        private static void CopyBufferFromSnapshot([NoAlias]IntPtr stateData, [NoAlias]IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            [NoAlias]IntPtr componentData, int componentStride, int count)
        {
            var deserializerState = GhostComponentSerializer.TypeCast<GhostDeserializerState>(stateData);
            var serializer = default(TSerializer);
            ref var snapshotInterpolationData = ref GhostComponentSerializer.TypeCast<SnapshotData.DataAtTick>(snapshotData);
            deserializerState.SnapshotTick = snapshotInterpolationData.Tick;
            for (int i = 0; i < count; ++i)
            {
                //For buffers the function iterate over the element in the buffers not entities.
                ref var snapshotBefore = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotInterpolationData.SnapshotBefore, snapshotOffset + snapshotStride*i);
                ref var component = ref GhostComponentSerializer.TypeCast<TComponentType>(componentData, componentStride*i);
                serializer.CopyFromSnapshotGenerated(deserializerState, ref component,
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

        [BurstCompile]
        private static void SerializeOneBuffer(int ent,
            [NoAlias]IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, int changeMaskBits,
            [NoAlias]IntPtr baselines,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel, [NoAlias]IntPtr entityStartBit,
            [NoAlias]IntPtr snapshotDynamicDataPtr, [NoAlias]IntPtr dynamicSizePerEntity,
            int len, ref int dynamicSnapshotDataOffset, int dynamicDataSize, int maskSize)
        {
            int PtrSize = UnsafeUtility.SizeOf<IntPtr>();
            const int IntSize = 4;
            const int BaselinesPerEntity = 4;
            var serializer = default(TSerializer);
            ref var startuint = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize*2*ent);
            startuint = writer.Length/IntSize;

            int baseLen = 0;
            int baseOffset = 0;
            var baseline0Ptr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize*ent*BaselinesPerEntity);
            if (baseline0Ptr != IntPtr.Zero)
            {
                baseLen = (int)GhostComponentSerializer.TypeCast<uint>(baseline0Ptr, snapshotOffset);
                baseOffset = (int)GhostComponentSerializer.TypeCast<uint>(baseline0Ptr, snapshotOffset+IntSize);
            }
            var baselineDynamicDataPtr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize*(ent*BaselinesPerEntity+3));

            // Calculate change masks for dynamic data
            var dynamicMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(changeMaskBits * len);
            var dynamicMaskBitsPtr = snapshotDynamicDataPtr + dynamicSnapshotDataOffset;

            var dynamicMaskOffset = 0;
            var offset = dynamicSnapshotDataOffset;
            var bOffset = baseOffset;
            if (len == baseLen)
            {
                for (int j = 0; j < len; ++j)
                {
                    CheckDynamicMaskOffset(dynamicMaskOffset, maskSize);
                    serializer.CalculateChangeMaskGenerated(
                        GhostComponentSerializer.TypeCastReadonly<TSnapshot>(snapshotDynamicDataPtr, maskSize + offset),
                        GhostComponentSerializer.TypeCastReadonly<TSnapshot>(baselineDynamicDataPtr, maskSize + bOffset),
                        dynamicMaskBitsPtr, dynamicMaskOffset);
                    offset += dynamicDataSize;
                    bOffset += dynamicDataSize;
                    dynamicMaskOffset += changeMaskBits;
                }
                // Calculate any change mask and set the dynamic snapshot mask
                uint anyChangeMask = 0;

                //Cleanup the remaining bits for the changemasks
                var changeMaskLenInBits = changeMaskBits * len;
                var remaining = (changeMaskBits * len)&31;
                if(remaining > 0)
                    GhostComponentSerializer.CopyToChangeMask(snapshotDynamicDataPtr + dynamicSnapshotDataOffset, 0, changeMaskLenInBits, 32-remaining);
                for (int mi = 0; mi < dynamicMaskUints; ++mi)
                {
                    uint changeMaskUint = GhostComponentSerializer.TypeCast<uint>(snapshotDynamicDataPtr + dynamicSnapshotDataOffset, mi*IntSize);
                    anyChangeMask |= (changeMaskUint!=0)?1u:0;
                }
                GhostComponentSerializer.CopyToChangeMask(snapshotData + IntSize + snapshotStride*ent, anyChangeMask, maskOffsetInBits, 2);
                if (anyChangeMask != 0)
                {
                    // Write the bits to the data stream
                    for (int mi = 0; mi < dynamicMaskUints; ++mi)
                    {
                        uint changeMaskUint = GhostComponentSerializer.TypeCast<uint>(snapshotDynamicDataPtr + dynamicSnapshotDataOffset, mi*IntSize);
                        uint changeBaseMaskUint = GhostComponentSerializer.TypeCast<uint>(baselineDynamicDataPtr + baseOffset, mi*IntSize);
                        writer.WritePackedUIntDelta(changeMaskUint, changeBaseMaskUint, compressionModel);
                    }
                }
            }
            else
            {
                // Clear the dynamic change mask to all 1
                // var remaining = changeMaskBits * len;
                // while (remaining > 32)
                // {
                //     GhostComponentSerializer.CopyToChangeMask(dynamicMaskBitsPtr, ~0u, dynamicMaskOffset, 32);
                //     dynamicMaskOffset += 32;
                //     remaining -= 32;
                // }
                // if (remaining > 0)
                //     GhostComponentSerializer.CopyToChangeMask(dynamicMaskBitsPtr, (1u<<remaining)-1, dynamicMaskOffset, remaining);
                // // FIXME: setting the bits as above is more correct, but requires changes to the receive system making it incompatible with the v1 serializer
                for (int j = 0; j < maskSize; ++j)
                    GhostComponentSerializer.TypeCast<byte>(dynamicMaskBitsPtr, j) = 0xff;
                // Set the dynamic snapshot mask
                GhostComponentSerializer.CopyToChangeMask(snapshotData + IntSize + snapshotStride*ent, 3, maskOffsetInBits, 2);

                baselineDynamicDataPtr = IntPtr.Zero;
                writer.WritePackedUIntDelta((uint)len, (uint)baseLen, compressionModel);
            }
            //Serialize the elements contents
            dynamicMaskOffset = 0;
            offset = dynamicSnapshotDataOffset;
            bOffset = baseOffset;
            TSnapshot baselineData = default;
            for (int j = 0; j < len; ++j)
            {
                if (baselineDynamicDataPtr != IntPtr.Zero)
                    baselineData = GhostComponentSerializer.TypeCastReadonly<TSnapshot>(baselineDynamicDataPtr, maskSize + bOffset);
                serializer.SerializeGenerated(
                    GhostComponentSerializer.TypeCastReadonly<TSnapshot>(snapshotDynamicDataPtr, maskSize + offset),
                    baselineData, dynamicMaskBitsPtr, dynamicMaskOffset, ref writer, compressionModel);
                offset += dynamicDataSize;
                bOffset += dynamicDataSize;
                dynamicMaskOffset += changeMaskBits;
            }
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

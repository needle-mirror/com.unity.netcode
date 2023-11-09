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
    /// <typeparam name="TSerializer">A concrete type that implement the <see cref="IGhostSerializer{TComponent,TSnapshot}"/> interface.</typeparam>
    [BurstCompile]
    public static unsafe class ComponentSerializationHelper<TComponentType, TSnapshot, TSerializer>
        where TComponentType : unmanaged
        where TSnapshot : unmanaged
        where TSerializer : unmanaged, IGhostSerializer<TComponentType, TSnapshot>
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

            if (state.SnapshotSize == 0)
            {
                ZeroSizeComponentSerializationHelper.SetupFunctionPointers(ref state);
                return true;
            }
            state.PostSerialize = new PortableFunctionPointer<GhostComponentSerializer.PostSerializeDelegate>(
                PostSerialize);
            state.Serialize = new PortableFunctionPointer<GhostComponentSerializer.SerializeDelegate>(
                Serialize);
            state.SerializeChild = new PortableFunctionPointer<GhostComponentSerializer.SerializeChildDelegate>(
                SerializeChild);
            state.CopyFromSnapshot = new PortableFunctionPointer<GhostComponentSerializer.CopyToFromSnapshotDelegate>(
                CopyComponentFromSnapshot);
            state.CopyToSnapshot = new PortableFunctionPointer<GhostComponentSerializer.CopyToFromSnapshotDelegate>(
                CopyToSnapshot);
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

        const int IntSize = 4;
        const int BaselinesPerEntity = 4;
        private static void SerializeEntities([NoAlias] IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int count, [NoAlias] IntPtr baselines, ref DataStreamWriter writer,
            in StreamCompressionModel compressionModel, [NoAlias] IntPtr entityStartBit)
        {
            var PtrSize = UnsafeUtility.SizeOf<IntPtr>();
            var serializer = default(TSerializer);
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
                        PredictDelta(GhostComponentSerializer.IntPtrCast(ref baseline), baseline1Ptr + snapshotOffset,
                            baseline2Ptr + snapshotOffset, ref predictor);
                    }
                }

                ref var snapshot = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotData, snapshotOffset + snapshotStride * ent);
                serializer.CalculateChangeMaskGenerated(snapshot, baseline, snapshotData + IntSize + snapshotStride * ent, maskOffsetInBits);
                serializer.SerializeGenerated(snapshot, baseline, snapshotData + IntSize + snapshotStride * ent, maskOffsetInBits, ref writer, compressionModel);
                ref var sbit = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize * 2 * ent + IntSize);
                sbit = writer.LengthInBits - startuint * 32;
                var missing = 32 - writer.LengthInBits & 31;
                if (missing < 32)
                    writer.WriteRawBits(0, missing);
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.PostSerializeDelegate))]
        private static void PostSerialize([NoAlias] IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits,
            int count, [NoAlias] IntPtr baselines, ref DataStreamWriter writer,
            ref StreamCompressionModel compressionModel,
            [NoAlias] IntPtr entityStartBit)
        {
            SerializeEntities(snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, count, baselines,
                ref writer, compressionModel, entityStartBit);
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.SerializeDelegate))]
        private static void Serialize([NoAlias] IntPtr stateData,
            [NoAlias] IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits,
            [NoAlias] IntPtr componentData, int componentStride, int count, [NoAlias] IntPtr baselines,
            ref DataStreamWriter writer, ref StreamCompressionModel compressionModel, [NoAlias] IntPtr entityStartBit)
        {
            ref var serializerState = ref GhostComponentSerializer.TypeCast<GhostSerializerState>(stateData);
            var serializer = default(TSerializer);
            var IntPtrSize = UnsafeUtility.SizeOf<IntPtr>();
            for (int ent = 0; ent < count; ++ent)
            {
                IntPtr curCompData = GhostComponentSerializer.TypeCast<IntPtr>(componentData, IntPtrSize * ent);
                ref var snapshot = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotData, snapshotOffset + snapshotStride * ent);
                if (curCompData != IntPtr.Zero)
                {
                    ref var component = ref GhostComponentSerializer.TypeCast<TComponentType>(curCompData);
                    serializer.CopyToSnapshotGenerated(serializerState, ref snapshot, component);
                }
                else
                {
                    snapshot = default;
                }
            }

            SerializeEntities(snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, count, baselines,
                ref writer, compressionModel, entityStartBit);
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.SerializeChildDelegate))]
        private static void SerializeChild([NoAlias] IntPtr stateData,
            [NoAlias] IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits,
            [NoAlias] IntPtr componentData, int count, [NoAlias] IntPtr baselines,
            ref DataStreamWriter writer, ref StreamCompressionModel compressionModel, [NoAlias] IntPtr entityStartBit)
        {
            ref var serializerState = ref GhostComponentSerializer.TypeCast<GhostSerializerState>(stateData);
            var IntPtrSize = UnsafeUtility.SizeOf<IntPtr>();
            var serializer = default(TSerializer);
            for (int i = 0; i < count; ++i)
            {
                IntPtr curCompData = GhostComponentSerializer.TypeCast<IntPtr>(componentData, IntPtrSize * i);
                ref var snapshot = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotData, snapshotOffset + snapshotStride * i);
                if (curCompData != IntPtr.Zero)
                {
                    ref var component = ref GhostComponentSerializer.TypeCast<TComponentType>(curCompData);
                    serializer.CopyToSnapshotGenerated(serializerState, ref snapshot, component);
                }
                else
                {
                    snapshot = default;
                }
            }

            SerializeEntities(snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, count, baselines,
                ref writer, compressionModel, entityStartBit);
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.CopyToFromSnapshotDelegate))]
        private static void CopyToSnapshot([NoAlias] IntPtr stateData, [NoAlias] IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            [NoAlias] IntPtr componentData, int componentStride, int count)
        {
            var serializer = default(TSerializer);
            for (int i = 0; i < count; ++i)
            {
                ref var snapshot = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotData, snapshotOffset + snapshotStride * i);
                ref var component = ref GhostComponentSerializer.TypeCast<TComponentType>(componentData, componentStride * i);
                ref var serializerState = ref GhostComponentSerializer.TypeCast<GhostSerializerState>(stateData);
                serializer.CopyToSnapshotGenerated(serializerState, ref snapshot, component);
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.CopyToFromSnapshotDelegate))]
        private static void CopyComponentFromSnapshot([NoAlias] IntPtr stateData, [NoAlias] IntPtr snapshotData,
            int snapshotOffset, int snapshotStride,
            [NoAlias] IntPtr componentData, int componentStride, int count)
        {
            var deserializerState = GhostComponentSerializer.TypeCast<GhostDeserializerState>(stateData);
            var serializer = default(TSerializer);
            for (int i = 0; i < count; ++i)
            {
                ref var snapshotInterpolationData = ref GhostComponentSerializer.TypeCast<SnapshotData.DataAtTick>(snapshotData, snapshotStride * i);
                //Compute the required owner mask for the components and buffers by retrievieng the ghost owner id from the data for the current tick.
                if((deserializerState.SendToOwner & snapshotInterpolationData.RequiredOwnerSendMask) == 0)
                    continue;

                deserializerState.SnapshotTick = snapshotInterpolationData.Tick;
                ref var component = ref GhostComponentSerializer.TypeCast<TComponentType>(componentData, componentStride * i);
                ref var snapshotBefore = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotInterpolationData.SnapshotBefore, snapshotOffset);
                ref var snapshotAfter = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotInterpolationData.SnapshotAfter, snapshotOffset);
                serializer.CopyFromSnapshotGenerated(deserializerState, ref component, snapshotInterpolationData.InterpolationFactor,
                    snapshotInterpolationData.InterpolationFactor, snapshotBefore, snapshotAfter);
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.RestoreFromBackupDelegate))]
        private static void RestoreFromBackup([NoAlias] IntPtr componentData, [NoAlias] IntPtr backupData)
        {
            default(TSerializer).RestoreFromBackupGenerated(ref GhostComponentSerializer.TypeCast<TComponentType>(componentData),
                GhostComponentSerializer.TypeCastReadonly<TComponentType>(backupData));
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.PredictDeltaDelegate))]
        private static void PredictDelta([NoAlias] IntPtr snapshotData,
            [NoAlias] IntPtr baseline1Data, [NoAlias] IntPtr baseline2Data, ref GhostDeltaPredictor predictor)
        {
            ref var snapshot = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotData);
            ref readonly var baseline1 = ref GhostComponentSerializer.TypeCastReadonly<TSnapshot>(baseline1Data);
            ref readonly var baseline2 = ref GhostComponentSerializer.TypeCastReadonly<TSnapshot>(baseline2Data);
            default(TSerializer).PredictDeltaGenerated(ref snapshot, baseline1, baseline2, ref predictor);
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.DeserializeDelegate))]
        private static void Deserialize([NoAlias] IntPtr snapshotData, [NoAlias] IntPtr baselineData,
            ref DataStreamReader reader, ref StreamCompressionModel compressionModel,
            [NoAlias] IntPtr changeMaskData, int startOffset)
        {
            ref var snapshot = ref GhostComponentSerializer.TypeCast<TSnapshot>(snapshotData);
            ref readonly var baseline = ref GhostComponentSerializer.TypeCastReadonly<TSnapshot>(baselineData);
            default(TSerializer).DeserializeGenerated(ref reader, compressionModel, changeMaskData, startOffset, ref snapshot, baseline);
        }

#if UNITY_EDITOR || NETCODE_DEBUG
        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostComponentSerializer.ReportPredictionErrorsDelegate))]
        private static void ReportPredictionErrors([NoAlias] IntPtr componentData, [NoAlias] IntPtr backupData,
            [NoAlias] IntPtr errorsList, int errorsCount)
        {
            ref readonly var component = ref GhostComponentSerializer.TypeCastReadonly<TComponentType>(componentData);
            ref readonly var backup = ref GhostComponentSerializer.TypeCastReadonly<TComponentType>(backupData);
            default(TSerializer).ReportPredictionErrorsGenerated(component, backup, errorsList, errorsCount);
        }
#endif
    }
}

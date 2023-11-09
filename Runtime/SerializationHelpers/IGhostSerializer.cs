using System;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine.Scripting;

namespace Unity.NetCode
{
    /// <summary>
    /// Interface implemented by all the component/buffer serialiser. For internal use only.
    /// </summary>
    /// <typeparam name="TSnapshot">The snapshot struct type that will contains the component data.</typeparam>
    /// <typeparam name="TComponent">The component type that this interface serialize.</typeparam>
    [RequireImplementors]
    public interface IGhostSerializer<TComponent, TSnapshot>
        where TSnapshot: unmanaged
        where TComponent: unmanaged
    {
        /// <summary>
        /// Calculate the predicted baseline.
        /// </summary>
        /// <param name="snapshot"></param>
        /// <param name="baseline1"></param>
        /// <param name="baseline2"></param>
        /// <param name="predictor"></param>
        [RequiredMember]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void PredictDeltaGenerated(ref TSnapshot snapshot, in TSnapshot baseline1, in TSnapshot baseline2, ref GhostDeltaPredictor predictor);

        /// <summary>
        /// Compute the change mask for the snapshot in respect to the given baseline
        /// </summary>
        /// <param name="snapshot"></param>
        /// <param name="baseline"></param>
        /// <param name="changeMaskData"></param>
        /// <param name="startOffset"></param>
        [RequiredMember]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CalculateChangeMaskGenerated(in TSnapshot snapshot, in TSnapshot baseline, IntPtr changeMaskData, int startOffset){}

        /// <summary>
        /// Copy/Convert the data form the snapshot to the component. Support interpolation and extrapolation.
        /// </summary>
        /// <param name="serializerState"></param>
        /// <param name="component"></param>
        /// <param name="interpolationFactor"></param>
        /// <param name="snapshotInterpolationFactorRaw"></param>
        /// <param name="snapshotBefore"></param>
        /// <param name="snapshotAfter"></param>
        [RequiredMember]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CopyFromSnapshotGenerated(in GhostDeserializerState serializerState, ref TComponent component,
            float interpolationFactor, float snapshotInterpolationFactorRaw, in TSnapshot snapshotBefore,
            in TSnapshot snapshotAfter);

        /// <summary>
        /// Copy/Convert the component data to the snapshot.
        /// </summary>
        /// <param name="serializerState"></param>
        /// <param name="snapshot"></param>
        /// <param name="component"></param>
        [RequiredMember]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CopyToSnapshotGenerated(in GhostSerializerState serializerState, ref TSnapshot snapshot,
            in TComponent component);

        /// <summary>
        /// Serialise the snapshot dato to the <param name="writer"></param> based on the calculated changemask.
        /// </summary>
        /// <param name="snapshot"></param>
        /// <param name="baseline"></param>
        /// <param name="changeMaskData"></param>
        /// <param name="startOffset"></param>
        /// <param name="writer"></param>
        /// <param name="compressionModel"></param>
        [RequiredMember]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SerializeGenerated(in TSnapshot snapshot, in TSnapshot baseline,
            [ReadOnly] IntPtr changeMaskData, int startOffset, ref DataStreamWriter writer,
            in StreamCompressionModel compressionModel);

        /// <summary>
        /// Read the data from the <param name="reader"></param> stream into the snapshot data.
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="compressionModel"></param>
        /// <param name="changeMask"></param>
        /// <param name="startOffset"></param>
        /// <param name="snapshot"></param>
        /// <param name="baseline"></param>
        [RequiredMember]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void DeserializeGenerated(ref DataStreamReader reader, in StreamCompressionModel compressionModel,
            IntPtr changeMask,
            int startOffset, ref TSnapshot snapshot, in TSnapshot baseline);

        /// <summary>
        /// Restore the component data from the prediction backup buffer. Only serialised fields are restored.
        /// </summary>
        /// <param name="component"></param>
        /// <param name="backup"></param>
        [RequiredMember]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RestoreFromBackupGenerated(ref TComponent component, in TComponent backup);

#if UNITY_EDITOR || NETCODE_DEBUG
        /// <summary>
        /// Calculate the prediction error for this component.
        /// </summary>
        /// <param name="component"></param>
        /// <param name="backup"></param>
        /// <param name="errorsList"></param>
        /// <param name="errorsCount"></param>
        [RequiredMember]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ReportPredictionErrorsGenerated(in TComponent component, in TComponent backup, IntPtr errorsList,
            int errorsCount);
#endif
    }
}

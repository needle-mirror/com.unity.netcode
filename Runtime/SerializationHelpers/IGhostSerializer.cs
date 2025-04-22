using System;
using Unity.Burst;
using Unity.Collections;
using UnityEngine.Scripting;

namespace Unity.NetCode
{
    /// <summary>
    /// Interface that expose a raw, unsafe interface to copy all the component ghost fields to
    /// the snapshot buffer. It is mostly for internal use by code-gen and should not be used direcly nor implemented
    /// by user code.
    /// </summary>
    public interface IGhostSerializer
    {
        /// <summary>
        /// The number of bits necessary for change mask
        /// </summary>
        public int ChangeMaskSizeInBits { get; }

        /// <summary>
        /// True if the serialized component has some serialized fields.
        /// </summary>
        public bool HasGhostFields { get; }

        /// <summary>
        /// The size of the serialized data in the snapshot buffer.
        /// </summary>
        public int SizeInSnapshot { get; }

        /// <summary>
        /// Copy/Convert the component data to the snapshot.
        /// </summary>
        /// <param name="serializerState">Serializer state</param>
        /// <param name="snapshot">Snapshot pointer</param>
        /// <param name="component">Component</param>
        void CopyToSnapshot(in GhostSerializerState serializerState, [NoAlias]IntPtr snapshot, [ReadOnly][NoAlias]IntPtr component);

        /// <summary>
        /// Copy/Convert the snapshot to component. Perform interpolation if necessary.
        /// </summary>
        /// <param name="serializerState">Serializer state</param>
        /// <param name="component">Component</param>
        /// <param name="snapshotInterpolationFactor">Interpolation factor</param>
        /// <param name="snapshotInterpolationFactorRaw">Interpolation factor</param>
        /// <param name="snapshotBefore">Snapshot before</param>
        /// <param name="snapshotAfter">Snapshot after</param>
        public void CopyFromSnapshot(in GhostDeserializerState serializerState, [NoAlias] IntPtr component,
            float snapshotInterpolationFactor, float snapshotInterpolationFactorRaw,
            [NoAlias] [ReadOnly] IntPtr snapshotBefore, [NoAlias] [ReadOnly] IntPtr snapshotAfter);

        /// <summary>
        /// Compute the change mask for the snapshot in respect to the given baseline
        /// </summary>
        /// <param name="snapshot">Snapshot pointer</param>
        /// <param name="baseline">Snapshot baseline</param>
        /// <param name="changeMaskData">Change mask data</param>
        /// <param name="startOffset">Start offset</param>
        void CalculateChangeMask([NoAlias][ReadOnly]IntPtr snapshot, [NoAlias][ReadOnly]IntPtr baseline, [NoAlias]IntPtr changeMaskData, int startOffset);

        /// <summary>
        /// Serialise the snapshot data to the <paramref name="writer"/> and calculate the current changemask.
        /// </summary>
        /// <param name="snapshot">Snapshot pointer</param>
        /// <param name="baseline">Snapshot baseline</param>
        /// <param name="changeMaskData">Change mask data</param>
        /// <param name="startOffset">Start offset</param>
        /// <param name="writer">Datastream writer</param>
        /// <param name="compressionModel">Compression model</param>
        void SerializeCombined([ReadOnly][NoAlias] IntPtr snapshot, [ReadOnly][NoAlias] IntPtr baseline,
            [NoAlias][ReadOnly]IntPtr changeMaskData, int startOffset,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel);

        /// <summary>
        /// Serialise the snapshot dato to the <paramref name="writer"/> and calculate the current changemask.
        /// </summary>
        /// <param name="snapshot">Snapshot pointer</param>
        /// <param name="baseline0">Snapshot baseline</param>
        /// <param name="baseline1">Snapshot baseline</param>
        /// <param name="baseline2">Snapshot baseline</param>
        /// <param name="predictor">Delta predicot</param>
        /// <param name="changeMaskData">Change mask data</param>
        /// <param name="startOffset">Start offset</param>
        /// <param name="writer">Datastream writer</param>
        /// <param name="compressionModel">Compression model</param>
        void SerializeWithPredictedBaseline([ReadOnly] [NoAlias] IntPtr snapshot,
            [ReadOnly] [NoAlias] IntPtr baseline0,
            [ReadOnly] [NoAlias] IntPtr baseline1,
            [ReadOnly] [NoAlias] IntPtr baseline2,
            ref GhostDeltaPredictor predictor,
            [NoAlias] [ReadOnly] IntPtr changeMaskData, int startOffset,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel);

        /// <summary>
        /// Serialise the snapshot data to the <paramref name="writer"/> based on the calculated changemask.
        /// Expect the changemask bits be all already set.
        /// </summary>
        /// <param name="snapshot">Snapshot pointer</param>
        /// <param name="baseline">Snapshot baseline</param>
        /// <param name="changeMaskData">Change mask data</param>
        /// <param name="startOffset">start offset</param>
        /// <param name="writer">data stream writer</param>
        /// <param name="compressionModel">Compression model</param>
        void Serialize([ReadOnly][NoAlias] IntPtr snapshot, [ReadOnly][NoAlias] IntPtr baseline,
            [NoAlias][ReadOnly]IntPtr changeMaskData, int startOffset,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel);

        /// <summary>
        /// Calculate the predicted snapshot from the two baseline
        /// </summary>
        /// <param name="snapshotData">Predicted snapshot data</param>
        /// <param name="baseline1Data">Snapshot baseline</param>
        /// <param name="baseline2Data">Snapshot baseline</param>
        /// <param name="predictor">Delta predictor</param>
        void PredictDelta([NoAlias] IntPtr snapshotData, [NoAlias] IntPtr baseline1Data, [NoAlias] IntPtr baseline2Data, ref GhostDeltaPredictor predictor);

        /// <summary>
        /// Read the data from the <paramref name="reader"/> stream into the snapshot data.
        /// </summary>
        /// <param name="reader">Data stream reader</param>
        /// <param name="compressionModel">compression model</param>
        /// <param name="changeMask">change mask</param>
        /// <param name="startOffset">start offset</param>
        /// <param name="snapshot">Snapshot pointer</param>
        /// <param name="baseline">Snapshot baseline</param>
        void Deserialize(ref DataStreamReader reader, in StreamCompressionModel compressionModel,
            IntPtr changeMask,
            int startOffset, [NoAlias]IntPtr snapshot, [NoAlias][ReadOnly]IntPtr baseline);

        /// <summary>
        /// Restore the component data from the prediction backup buffer. Only serialised fields are restored.
        /// </summary>
        /// <param name="component">Component</param>
        /// <param name="backup">Backup buffer</param>
        void RestoreFromBackup([NoAlias]IntPtr component, [NoAlias][ReadOnly]IntPtr backup);

#if UNITY_EDITOR || NETCODE_DEBUG
        /// <summary>
        /// Calculate the prediction error for this component.
        /// </summary>
        /// <param name="component">Component</param>
        /// <param name="backup">Backup buffer</param>
        /// <param name="errorsList">Error list pointer</param>
        /// <param name="errorsCount">Number of errors</param>
        void ReportPredictionErrors([NoAlias][ReadOnly]IntPtr component, [NoAlias][ReadOnly]IntPtr backup, IntPtr errorsList,
            int errorsCount);
#endif
    }

    /// <summary>
    /// Interface implemented by all the component/buffer serialiser. For internal use only.
    /// </summary>
    /// <typeparam name="TSnapshot">The snapshot struct type that will contains the component data.</typeparam>
    /// <typeparam name="TComponent">The component type that this interface serialize.</typeparam>
    [RequireImplementors]
    [Obsolete("The IGhostSerializer<TComponent, TSnapshot> has been deprecated. Please use the IGhostComponentSerializer instead")]
    public interface IGhostSerializer<TComponent, TSnapshot>
        where TSnapshot: unmanaged
        where TComponent: unmanaged
    {
        /// <summary>
        /// Calculate the predicted baseline.
        /// </summary>
        /// <param name="snapshot">Snapshot reference</param>
        /// <param name="baseline1">Snapshot baseline</param>
        /// <param name="baseline2">Snapshot baseline</param>
        /// <param name="predictor">Delta predictor</param>
        void PredictDeltaGenerated(ref TSnapshot snapshot, in TSnapshot baseline1, in TSnapshot baseline2, ref GhostDeltaPredictor predictor);

        /// <summary>
        /// Compute the change mask for the snapshot in respect to the given baseline
        /// </summary>
        /// <param name="snapshot">Snapshot reference</param>
        /// <param name="baseline">Snapshot baseline</param>
        /// <param name="changeMaskData">Change mask data</param>
        /// <param name="startOffset">Start offset</param>
        void CalculateChangeMaskGenerated(in TSnapshot snapshot, in TSnapshot baseline, IntPtr changeMaskData, int startOffset){}

        /// <summary>
        /// Copy/Convert the data form the snapshot to the component. Support interpolation and extrapolation.
        /// </summary>
        /// <param name="serializerState">Serializer state</param>
        /// <param name="component">Component</param>
        /// <param name="interpolationFactor">Interpolation factor</param>
        /// <param name="snapshotInterpolationFactorRaw">Snapshot interpolation factor</param>
        /// <param name="snapshotBefore">Snapshot before</param>
        /// <param name="snapshotAfter">Snapshot after</param>
        void CopyFromSnapshotGenerated(in GhostDeserializerState serializerState, ref TComponent component,
            float interpolationFactor, float snapshotInterpolationFactorRaw, in TSnapshot snapshotBefore,
            in TSnapshot snapshotAfter);

        /// <summary>
        /// Copy/Convert the component data to the snapshot.
        /// </summary>
        /// <param name="serializerState">Serializer state</param>
        /// <param name="snapshot">Snapshot reference</param>
        /// <param name="component">Component</param>
        void CopyToSnapshotGenerated(in GhostSerializerState serializerState, ref TSnapshot snapshot,
            in TComponent component);

        /// <summary>
        /// Serialise the snapshot dato to the <paramref name="writer"/> based on the calculated changemask.
        /// </summary>
        /// <param name="snapshot">Snapshot reference</param>
        /// <param name="baseline">Snapshot baseline</param>
        /// <param name="changeMaskData">Change mask data</param>
        /// <param name="startOffset">Start offset</param>
        /// <param name="writer">Datastream writer</param>
        /// <param name="compressionModel">Compression model</param>
        void SerializeGenerated(in TSnapshot snapshot, in TSnapshot baseline,
            [ReadOnly][NoAlias]IntPtr changeMaskData, int startOffset, ref DataStreamWriter writer,
            in StreamCompressionModel compressionModel);

        /// <summary>
        /// Serialise the snapshot dato to the <paramref name="writer"/> based on the calculated changemask.
        /// </summary>
        /// <param name="snapshot">Snapshot reference</param>
        /// <param name="baseline">Snapshot baseline</param>
        /// <param name="changeMaskData">Change mask data</param>
        /// <param name="startOffset">Start offset</param>
        /// <param name="writer">Datastream writer</param>
        /// <param name="compressionModel">Compression model</param>
        void SerializeCombinedGenerated(in TSnapshot snapshot, in TSnapshot baseline,
            [NoAlias][ReadOnly]IntPtr changeMaskData, int startOffset,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel);

        /// <summary>
        /// Read the data from the <paramref name="reader"/> stream into the snapshot data.
        /// </summary>
        /// <param name="reader">Data stream reader</param>
        /// <param name="compressionModel">Compression model</param>
        /// <param name="changeMask">Change mask</param>
        /// <param name="startOffset">Starting offset</param>
        /// <param name="snapshot">Snapshot reference</param>
        /// <param name="baseline">Snapshot baseline</param>
        void DeserializeGenerated(ref DataStreamReader reader, in StreamCompressionModel compressionModel,
            IntPtr changeMask,
            int startOffset, ref TSnapshot snapshot, in TSnapshot baseline);

        /// <summary>
        /// Restore the component data from the prediction backup buffer. Only serialised fields are restored.
        /// </summary>
        /// <param name="component">Component</param>
        /// <param name="backup">Backup buffer</param>
        void RestoreFromBackupGenerated(ref TComponent component, in TComponent backup);

#if UNITY_EDITOR || NETCODE_DEBUG
        /// <summary>
        /// Calculate the prediction error for this component.
        /// </summary>
        /// <param name="component">Component</param>
        /// <param name="backup">Backup buffer</param>
        /// <param name="errorsList">Data for errors</param>
        /// <param name="errorsCount">Error count</param>
        void ReportPredictionErrorsGenerated(in TComponent component, in TComponent backup, IntPtr errorsList,
            int errorsCount);
#endif
    }
}

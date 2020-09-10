namespace Generated
{
    public struct GhostSnapshotData
    {
        public unsafe void CopyFromSnapshot(ref GhostDeserializerState deserializerState, ref Snapshot snapshotBefore, ref Snapshot snapshotAfter, float snapshotInterpolationFactor, ref IComponentData component)
        {
            if (true)
            {
                #region __GHOST_COPY_FROM_SNAPSHOT__
                component.__GHOST_FIELD_REFERENCE__ = new float3(snapshotBefore.__GHOST_FIELD_NAME___x * __GHOST_DEQUANTIZE_SCALE__, snapshotBefore.__GHOST_FIELD_NAME___y * __GHOST_DEQUANTIZE_SCALE__, snapshotBefore.__GHOST_FIELD_NAME___z * __GHOST_DEQUANTIZE_SCALE__);
                #endregion

                #region __GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE__
                component.__GHOST_FIELD_REFERENCE__ = math.lerp(
                    new float3(snapshotBefore.__GHOST_FIELD_NAME___x * __GHOST_DEQUANTIZE_SCALE__, snapshotBefore.__GHOST_FIELD_NAME___y * __GHOST_DEQUANTIZE_SCALE__, snapshotBefore.__GHOST_FIELD_NAME___z * __GHOST_DEQUANTIZE_SCALE__),
                    new float3(snapshotAfter.__GHOST_FIELD_NAME___x * __GHOST_DEQUANTIZE_SCALE__, snapshotAfter.__GHOST_FIELD_NAME___y * __GHOST_DEQUANTIZE_SCALE__, snapshotAfter.__GHOST_FIELD_NAME___z * __GHOST_DEQUANTIZE_SCALE__),
                    snapshotInterpolationFactor);
                #endregion
            }
        }
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static void ReportPredictionErrors(ref IComponentData component, in IComponentData backup, ref UnsafeList<float> errors, ref int errorIndex)
        {
            #region __GHOST_REPORT_PREDICTION_ERROR__
            errors[errorIndex] = math.max(errors[errorIndex], math.distance(component.__GHOST_FIELD_REFERENCE__, backup.__GHOST_FIELD_REFERENCE__));
            ++errorIndex;
            #endregion
        }
        private static int GetPredictionErrorNames(ref FixedString512 names, ref int nameCount)
        {
            #region __GHOST_GET_PREDICTION_ERROR_NAME__
            if (nameCount != 0)
                names.Append(new FixedString32(","));
            names.Append(new FixedString64("__GHOST_FIELD_REFERENCE__"));
            ++nameCount;
            #endregion
        }
        #endif
    }
}
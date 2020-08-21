namespace Generated
{
    public struct GhostSnapshotData
    {
        public unsafe void CopyToSnapshot(ref Snapshot snapshot, ref IComponentData component)
        {
            if (true)
            {
                #region __GHOST_COPY_TO_SNAPSHOT__
                snapshot.__GHOST_FIELD_NAME__ = component.__GHOST_FIELD_REFERENCE__?1u:0;
                #endregion
            }

        }
        public unsafe void CopyFromSnapshot(ref Snapshot snapshot, ref IComponentData component)
        {
            if (true)
            {
                #region __GHOST_COPY_FROM_SNAPSHOT__
                component.__GHOST_FIELD_REFERENCE__ = snapshotBefore.__GHOST_FIELD_NAME__ != 0;
                #endregion
            }
        }
        public unsafe void RestoreFromBackup(ref IComponentData component, in IComponentData backup)
        {
            #region __GHOST_RESTORE_FROM_BACKUP__
            component.__GHOST_FIELD_REFERENCE__ = backup.__GHOST_FIELD_REFERENCE__;
            #endregion
        }

        public void SerializeRpc(ref DataStreamWriter writer, in IComponentData data)
        {
            #region __RPC_WRITE__
            writer.WriteUInt(data.__RPC_FIELD_NAME__ ? 1u : 0);
            #endregion
        }

        public void DeserializeRpc(ref DataStreamReader reader, ref IComponentData data)
        {
            #region __RPC_READ__
            data.__RPC_FIELD_NAME__ = (reader.ReadUInt() != 0) ? true : false;
            #endregion
        }
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static void ReportPredictionErrors(ref IComponentData component, in IComponentData backup, ref UnsafeList<float> errors, ref int errorIndex)
        {
            #region __GHOST_REPORT_PREDICTION_ERROR__
            errors[errorIndex] = math.max(errors[errorIndex], (component.__GHOST_FIELD_REFERENCE__ != backup.__GHOST_FIELD_REFERENCE__) ? 1 : 0);
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
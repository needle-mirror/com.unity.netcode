namespace Generated
{
    public struct GhostSnapshotData
    {
        struct Snapshot
        {
            #region __GHOST_FIELD__
            public FixedString64 __GHOST_FIELD_NAME__;
            #endregion
        }

        public void Serialize(ref Snapshot snapshot, ref Snapshot baseline, ref DataStreamWriter writer, ref NetworkCompressionModel compressionModel, uint changeMask)
        {
            #region __GHOST_WRITE__
            if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
                writer.WritePackedFixedString64Delta(snapshot.__GHOST_FIELD_NAME__, baseline.__GHOST_FIELD_NAME__, compressionModel);
            #endregion
        }

        public void Deserialize(ref Snapshot snapshot, ref Snapshot baseline, ref DataStreamReader reader, ref NetworkCompressionModel compressionModel, uint changeMask)
        {
            #region __GHOST_READ__
            if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
                snapshot.__GHOST_FIELD_NAME__ = reader.ReadPackedFixedString64Delta(baseline.__GHOST_FIELD_NAME__, compressionModel);
            else
                snapshot.__GHOST_FIELD_NAME__ = baseline.__GHOST_FIELD_NAME__;
            #endregion
        }

        public void SerializeRpc(ref DataStreamWriter writer, in IComponentData data)
        {
            #region __RPC_WRITE__
            writer.WriteFixedString64(data.__RPC_FIELD_NAME__);
            #endregion
        }

        public void DeserializeRpc(ref DataStreamReader reader, ref IComponentData data)
        {
            #region __RPC_READ__
            data.__RPC_FIELD_NAME__ = reader.ReadFixedString64();
            #endregion
        }
    }
}
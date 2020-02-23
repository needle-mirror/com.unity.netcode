public struct GhostSnapshotData
{
    #region __GHOST_FIELD__
    private NativeString64 __GHOST_FIELD_NAME__;
    #endregion

    #region __GHOST_FIELD_GET_SET__
    public NativeString64 Get__GHOST_FIELD_NAME__(GhostDeserializerState deserializerState)
    {
        return __GHOST_FIELD_NAME__;
    }
    public NativeString64 Get__GHOST_FIELD_NAME__()
    {
        return __GHOST_FIELD_NAME__;
    }
    public void Set__GHOST_FIELD_NAME__(NativeString64 val, GhostSerializerState serializerState)
    {
        __GHOST_FIELD_NAME__ = val;
    }
    public void Set__GHOST_FIELD_NAME__(NativeString64 val)
    {
        __GHOST_FIELD_NAME__ = val;
    }
    #endregion

    public void PredictDelta(uint tick, ref GhostSnapshotData baseline1, ref GhostSnapshotData baseline2)
    {
        var predictor = new GhostDeltaPredictor(tick, this.tick, baseline1.tick, baseline2.tick);
        #region __GHOST_PREDICT__
        #endregion
    }

    public void Serialize(int networkId, ref GhostSnapshotData baseline, ref DataStreamWriter writer, NetworkCompressionModel compressionModel)
    {
        #region __GHOST_CALCULATE_CHANGE_MASK_ZERO__
        changeMask__GHOST_MASK_BATCH__ = __GHOST_FIELD_NAME__.Equals(baseline.__GHOST_FIELD_NAME__) ? 0 : 1u;
        #endregion
        #region __GHOST_CALCULATE_CHANGE_MASK__
        changeMask__GHOST_MASK_BATCH__ |= __GHOST_FIELD_NAME__.Equals(baseline.__GHOST_FIELD_NAME__) ? 0 : (1u<<__GHOST_MASK_INDEX__);
        #endregion
        #region __GHOST_WRITE__
        if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
            writer.WritePackedStringDelta(__GHOST_FIELD_NAME__, baseline.__GHOST_FIELD_NAME__, compressionModel);
        #endregion
    }

    public void Deserialize(uint tick, ref GhostSnapshotData baseline, ref DataStreamReader reader,
        NetworkCompressionModel compressionModel)
    {
        #region __GHOST_READ__
        if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
            __GHOST_FIELD_NAME__ = reader.ReadPackedStringDelta(baseline.__GHOST_FIELD_NAME__, compressionModel);
        else
            __GHOST_FIELD_NAME__ = baseline.__GHOST_FIELD_NAME__;
        #endregion
    }
    public void Interpolate(ref GhostSnapshotData target, float factor)
    {
        #region __GHOST_INTERPOLATE__
        #endregion
    }
}

public struct GhostSnapshotData
{
    #region __GHOST_FIELD__
    private float __GHOST_FIELD_NAME__X;
    private float __GHOST_FIELD_NAME__Y;
    private float __GHOST_FIELD_NAME__Z;
    private float __GHOST_FIELD_NAME__W;
    #endregion

    #region __GHOST_FIELD_GET_SET__
    public float4 Get__GHOST_FIELD_NAME__(GhostDeserializerState deserializerState)
    {
        return Get__GHOST_FIELD_NAME__();
    }
    public float4 Get__GHOST_FIELD_NAME__()
    {
        return new float4(__GHOST_FIELD_NAME__X, __GHOST_FIELD_NAME__Y, __GHOST_FIELD_NAME__Z, __GHOST_FIELD_NAME__W);
    }
    public void Set__GHOST_FIELD_NAME__(float4 val, GhostSerializerState serializerState)
    {
        Set__GHOST_FIELD_NAME__(val);
    }
    public void Set__GHOST_FIELD_NAME__(float4 val)
    {
        __GHOST_FIELD_NAME__X = val.x;
        __GHOST_FIELD_NAME__Y = val.y;
        __GHOST_FIELD_NAME__Z = val.z;
        __GHOST_FIELD_NAME__W = val.w;
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
        changeMask__GHOST_MASK_BATCH__ = (__GHOST_FIELD_NAME__X != baseline.__GHOST_FIELD_NAME__X ||
                                          __GHOST_FIELD_NAME__Y != baseline.__GHOST_FIELD_NAME__Y ||
                                          __GHOST_FIELD_NAME__Z != baseline.__GHOST_FIELD_NAME__Z ||
                                          __GHOST_FIELD_NAME__W != baseline.__GHOST_FIELD_NAME__W) ? 1u : 0;
        #endregion
        #region __GHOST_CALCULATE_CHANGE_MASK__
        changeMask__GHOST_MASK_BATCH__ |= (__GHOST_FIELD_NAME__X != baseline.__GHOST_FIELD_NAME__X ||
                                           __GHOST_FIELD_NAME__Y != baseline.__GHOST_FIELD_NAME__Y ||
                                           __GHOST_FIELD_NAME__Z != baseline.__GHOST_FIELD_NAME__Z ||
                                           __GHOST_FIELD_NAME__W != baseline.__GHOST_FIELD_NAME__W) ? (1u<<__GHOST_MASK_INDEX__) : 0;
        #endregion
        #region __GHOST_WRITE__
        if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
        {
            writer.WritePackedFloatDelta(__GHOST_FIELD_NAME__X, baseline.__GHOST_FIELD_NAME__X, compressionModel);
            writer.WritePackedFloatDelta(__GHOST_FIELD_NAME__Y, baseline.__GHOST_FIELD_NAME__Y, compressionModel);
            writer.WritePackedFloatDelta(__GHOST_FIELD_NAME__Z, baseline.__GHOST_FIELD_NAME__Z, compressionModel);
            writer.WritePackedFloatDelta(__GHOST_FIELD_NAME__W, baseline.__GHOST_FIELD_NAME__W, compressionModel);
        }
        #endregion
    }

    public void Deserialize(uint tick, ref GhostSnapshotData baseline, ref DataStreamReader reader,
        NetworkCompressionModel compressionModel)
    {
        #region __GHOST_READ__
        if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
        {
            __GHOST_FIELD_NAME__X = reader.ReadPackedFloatDelta(baseline.__GHOST_FIELD_NAME__X, compressionModel);
            __GHOST_FIELD_NAME__Y = reader.ReadPackedFloatDelta(baseline.__GHOST_FIELD_NAME__Y, compressionModel);
            __GHOST_FIELD_NAME__Z = reader.ReadPackedFloatDelta(baseline.__GHOST_FIELD_NAME__Z, compressionModel);
            __GHOST_FIELD_NAME__W = reader.ReadPackedFloatDelta(baseline.__GHOST_FIELD_NAME__W, compressionModel);
        }
        else
        {
            __GHOST_FIELD_NAME__X = baseline.__GHOST_FIELD_NAME__X;
            __GHOST_FIELD_NAME__Y = baseline.__GHOST_FIELD_NAME__Y;
            __GHOST_FIELD_NAME__Z = baseline.__GHOST_FIELD_NAME__Z;
            __GHOST_FIELD_NAME__W = baseline.__GHOST_FIELD_NAME__W;
        }
        #endregion
    }
    public void Interpolate(ref GhostSnapshotData target, float factor)
    {
        #region __GHOST_INTERPOLATE__
        Set__GHOST_FIELD_NAME__(math.lerp(Get__GHOST_FIELD_NAME__(), target.Get__GHOST_FIELD_NAME__(), factor));
        #endregion
    }
}

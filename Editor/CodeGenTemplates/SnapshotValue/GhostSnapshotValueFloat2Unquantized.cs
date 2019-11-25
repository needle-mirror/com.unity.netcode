public struct GhostSnapshotData
{
    #region __GHOST_FIELD__
    private float __GHOST_FIELD_NAME__X;
    private float __GHOST_FIELD_NAME__Y;
    #endregion

    #region __GHOST_FIELD_GET_SET__
    public float2 Get__GHOST_FIELD_NAME__(GhostDeserializerState deserializerState)
    {
        return Get__GHOST_FIELD_NAME__();
    }
    public float2 Get__GHOST_FIELD_NAME__()
    {
        return new float2(__GHOST_FIELD_NAME__X, __GHOST_FIELD_NAME__Y);
    }
    public void Set__GHOST_FIELD_NAME__(float2 val, GhostSerializerState serializerState)
    {
        Set__GHOST_FIELD_NAME__(val);
    }
    public void Set__GHOST_FIELD_NAME__(float2 val)
    {
        __GHOST_FIELD_NAME__X = val.x;
        __GHOST_FIELD_NAME__Y = val.y;
    }
    #endregion

    public void PredictDelta(uint tick, ref GhostSnapshotData baseline1, ref GhostSnapshotData baseline2)
    {
        var predictor = new GhostDeltaPredictor(tick, this.tick, baseline1.tick, baseline2.tick);
        #region __GHOST_PREDICT__
        #endregion
    }

    public void Serialize(int networkId, ref GhostSnapshotData baseline, DataStreamWriter writer, NetworkCompressionModel compressionModel)
    {
        #region __GHOST_CALCULATE_CHANGE_MASK_ZERO__
        changeMask__GHOST_MASK_BATCH__ = (__GHOST_FIELD_NAME__X != baseline.__GHOST_FIELD_NAME__X ||
                                          __GHOST_FIELD_NAME__Y != baseline.__GHOST_FIELD_NAME__Y) ? 1u : 0;
        #endregion
        #region __GHOST_CALCULATE_CHANGE_MASK__
        changeMask__GHOST_MASK_BATCH__ |= (__GHOST_FIELD_NAME__X != baseline.__GHOST_FIELD_NAME__X ||
                                           __GHOST_FIELD_NAME__Y != baseline.__GHOST_FIELD_NAME__Y) ? (1u<<__GHOST_MASK_INDEX__) : 0;
        #endregion
        #region __GHOST_WRITE__
        if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
        {
            writer.WritePackedFloatDelta(__GHOST_FIELD_NAME__X, baseline.__GHOST_FIELD_NAME__X, compressionModel);
            writer.WritePackedFloatDelta(__GHOST_FIELD_NAME__Y, baseline.__GHOST_FIELD_NAME__Y, compressionModel);
        }
        #endregion
    }

    public void Deserialize(uint tick, ref GhostSnapshotData baseline, DataStreamReader reader, ref DataStreamReader.Context ctx,
        NetworkCompressionModel compressionModel)
    {
        #region __GHOST_READ__
        if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
        {
            __GHOST_FIELD_NAME__X = reader.ReadPackedFloatDelta(ref ctx, baseline.__GHOST_FIELD_NAME__X, compressionModel);
            __GHOST_FIELD_NAME__Y = reader.ReadPackedFloatDelta(ref ctx, baseline.__GHOST_FIELD_NAME__Y, compressionModel);
        }
        else
        {
            __GHOST_FIELD_NAME__X = baseline.__GHOST_FIELD_NAME__X;
            __GHOST_FIELD_NAME__Y = baseline.__GHOST_FIELD_NAME__Y;
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

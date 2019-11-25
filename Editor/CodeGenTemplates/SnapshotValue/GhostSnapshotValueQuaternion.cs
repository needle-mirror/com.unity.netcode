public struct GhostSnapshotData
{
    #region __GHOST_FIELD__
    private int __GHOST_FIELD_NAME__X;
    private int __GHOST_FIELD_NAME__Y;
    private int __GHOST_FIELD_NAME__Z;
    private int __GHOST_FIELD_NAME__W;
    #endregion

    #region __GHOST_FIELD_GET_SET__
    public quaternion Get__GHOST_FIELD_NAME__(GhostDeserializerState deserializerState)
    {
        return Get__GHOST_FIELD_NAME__();
    }
    public quaternion Get__GHOST_FIELD_NAME__()
    {
        return new quaternion(__GHOST_FIELD_NAME__X * __GHOST_DEQUANTIZE_SCALE__, __GHOST_FIELD_NAME__Y * __GHOST_DEQUANTIZE_SCALE__, __GHOST_FIELD_NAME__Z * __GHOST_DEQUANTIZE_SCALE__, __GHOST_FIELD_NAME__W * __GHOST_DEQUANTIZE_SCALE__);
    }
    public void Set__GHOST_FIELD_NAME__(quaternion q, GhostSerializerState serializerState)
    {
        Set__GHOST_FIELD_NAME__(q);
    }
    public void Set__GHOST_FIELD_NAME__(quaternion q)
    {
        __GHOST_FIELD_NAME__X = (int)(q.value.x * __GHOST_QUANTIZE_SCALE__);
        __GHOST_FIELD_NAME__Y = (int)(q.value.y * __GHOST_QUANTIZE_SCALE__);
        __GHOST_FIELD_NAME__Z = (int)(q.value.z * __GHOST_QUANTIZE_SCALE__);
        __GHOST_FIELD_NAME__W = (int)(q.value.w * __GHOST_QUANTIZE_SCALE__);
    }
    #endregion

    public void PredictDelta(uint tick, ref GhostSnapshotData baseline1, ref GhostSnapshotData baseline2)
    {
        var predictor = new GhostDeltaPredictor(tick, this.tick, baseline1.tick, baseline2.tick);
        #region __GHOST_PREDICT__
        __GHOST_FIELD_NAME__X = predictor.PredictInt(__GHOST_FIELD_NAME__X, baseline1.__GHOST_FIELD_NAME__X, baseline2.__GHOST_FIELD_NAME__X);
        __GHOST_FIELD_NAME__Y = predictor.PredictInt(__GHOST_FIELD_NAME__Y, baseline1.__GHOST_FIELD_NAME__Y, baseline2.__GHOST_FIELD_NAME__Y);
        __GHOST_FIELD_NAME__Z = predictor.PredictInt(__GHOST_FIELD_NAME__Z, baseline1.__GHOST_FIELD_NAME__Z, baseline2.__GHOST_FIELD_NAME__Z);
        __GHOST_FIELD_NAME__W = predictor.PredictInt(__GHOST_FIELD_NAME__W, baseline1.__GHOST_FIELD_NAME__W, baseline2.__GHOST_FIELD_NAME__W);
        #endregion
    }

    public void Serialize(int networkId, ref GhostSnapshotData baseline, DataStreamWriter writer, NetworkCompressionModel compressionModel)
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
            writer.WritePackedIntDelta(__GHOST_FIELD_NAME__X, baseline.__GHOST_FIELD_NAME__X, compressionModel);
            writer.WritePackedIntDelta(__GHOST_FIELD_NAME__Y, baseline.__GHOST_FIELD_NAME__Y, compressionModel);
            writer.WritePackedIntDelta(__GHOST_FIELD_NAME__Z, baseline.__GHOST_FIELD_NAME__Z, compressionModel);
            writer.WritePackedIntDelta(__GHOST_FIELD_NAME__W, baseline.__GHOST_FIELD_NAME__W, compressionModel);
        }
        #endregion
    }

    public void Deserialize(uint tick, ref GhostSnapshotData baseline, DataStreamReader reader, ref DataStreamReader.Context ctx,
        NetworkCompressionModel compressionModel)
    {
        #region __GHOST_READ__
        if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
        {
            __GHOST_FIELD_NAME__X = reader.ReadPackedIntDelta(ref ctx, baseline.__GHOST_FIELD_NAME__X, compressionModel);
            __GHOST_FIELD_NAME__Y = reader.ReadPackedIntDelta(ref ctx, baseline.__GHOST_FIELD_NAME__Y, compressionModel);
            __GHOST_FIELD_NAME__Z = reader.ReadPackedIntDelta(ref ctx, baseline.__GHOST_FIELD_NAME__Z, compressionModel);
            __GHOST_FIELD_NAME__W = reader.ReadPackedIntDelta(ref ctx, baseline.__GHOST_FIELD_NAME__W, compressionModel);
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
        Set__GHOST_FIELD_NAME__(math.slerp(Get__GHOST_FIELD_NAME__(), target.Get__GHOST_FIELD_NAME__(), factor));
        #endregion
    }
}

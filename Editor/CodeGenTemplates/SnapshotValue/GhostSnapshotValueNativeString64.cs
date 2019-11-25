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

    public void Serialize(int networkId, ref GhostSnapshotData baseline, DataStreamWriter writer, NetworkCompressionModel compressionModel)
    {
        #region __GHOST_CALCULATE_CHANGE_MASK_ZERO__
        changeMask__GHOST_MASK_BATCH__ = __GHOST_FIELD_NAME__.Equals(baseline.__GHOST_FIELD_NAME__) ? 0 : 1u;
        #endregion
        #region __GHOST_CALCULATE_CHANGE_MASK__
        changeMask__GHOST_MASK_BATCH__ |= __GHOST_FIELD_NAME__.Equals(baseline.__GHOST_FIELD_NAME__) ? 0 : (1u<<__GHOST_MASK_INDEX__);
        #endregion
        #region __GHOST_WRITE__
        if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
        {
            writer.WritePackedUIntDelta(__GHOST_FIELD_NAME__.LengthInBytes, baseline.__GHOST_FIELD_NAME__.LengthInBytes, compressionModel);
            var __GHOST_FIELD_NAME__BaselineLength = (ushort)math.min((uint)__GHOST_FIELD_NAME__.LengthInBytes, baseline.__GHOST_FIELD_NAME__.LengthInBytes);
            for (int sb = 0; sb < __GHOST_FIELD_NAME__BaselineLength; ++sb)
            {
                unsafe
                {
                    fixed (byte* b1 = &__GHOST_FIELD_NAME__.buffer.byte0000)
                    fixed (byte* b2 = &baseline.__GHOST_FIELD_NAME__.buffer.byte0000)
                    {
                        writer.WritePackedUIntDelta(b1[sb], b2[sb], compressionModel);
                    }
                }
            }
            for (int sb = __GHOST_FIELD_NAME__BaselineLength; sb < __GHOST_FIELD_NAME__.LengthInBytes; ++sb)
            {
                unsafe
                {
                    fixed (byte* b = &__GHOST_FIELD_NAME__.buffer.byte0000)
                    {
                        writer.WritePackedUIntDelta(b[sb], 0, compressionModel);
                    }
                }
            }
        }
        #endregion
    }

    public void Deserialize(uint tick, ref GhostSnapshotData baseline, DataStreamReader reader, ref DataStreamReader.Context ctx,
        NetworkCompressionModel compressionModel)
    {
        #region __GHOST_READ__
        if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
        {
            __GHOST_FIELD_NAME__.LengthInBytes = (ushort)reader.ReadPackedUIntDelta(ref ctx, (uint)baseline.__GHOST_FIELD_NAME__.LengthInBytes, compressionModel);
            var __GHOST_FIELD_NAME__BaselineLength = (ushort)math.min((uint)__GHOST_FIELD_NAME__.LengthInBytes, baseline.__GHOST_FIELD_NAME__.LengthInBytes);
            for (int sb = 0; sb < __GHOST_FIELD_NAME__BaselineLength; ++sb)
            {
                unsafe
                {
                    fixed (byte* b1 = &__GHOST_FIELD_NAME__.buffer.byte0000)
                    fixed (byte* b2 = &baseline.__GHOST_FIELD_NAME__.buffer.byte0000)
                    {
                        b1[sb] = (byte)reader.ReadPackedUIntDelta(ref ctx, b2[sb], compressionModel);
                    }
                }
            }
            for (int sb = __GHOST_FIELD_NAME__BaselineLength; sb < __GHOST_FIELD_NAME__.LengthInBytes; ++sb)
            {
                unsafe
                {
                    fixed (byte* b = &__GHOST_FIELD_NAME__.buffer.byte0000)
                    {
                        b[sb] = (byte)reader.ReadPackedUIntDelta(ref ctx, 0, compressionModel);
                    }
                }
            }
        }
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

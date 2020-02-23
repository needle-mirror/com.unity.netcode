using Unity.Networking.Transport;
using Unity.NetCode;
#region __GHOST_USING_STATEMENT__
using __GHOST_USING__;
#endregion

#region __END_HEADER__
#endregion
public struct __GHOST_NAME__SnapshotData : ISnapshotData<__GHOST_NAME__SnapshotData>
{
    public uint tick;
    #region __GHOST_FIELD__
    #endregion
    #region __GHOST_CHANGE_MASK__
    uint changeMask__GHOST_MASK_BATCH__;
    #endregion

    public uint Tick => tick;
    #region __GHOST_FIELD_GET_SET__
    #endregion

    public void PredictDelta(uint tick, ref __GHOST_NAME__SnapshotData baseline1, ref __GHOST_NAME__SnapshotData baseline2)
    {
        var predictor = new GhostDeltaPredictor(tick, this.tick, baseline1.tick, baseline2.tick);
        #region __GHOST_PREDICT__
        #endregion
    }

    public void Serialize(int networkId, ref __GHOST_NAME__SnapshotData baseline, ref DataStreamWriter writer, NetworkCompressionModel compressionModel)
    {
        #region __GHOST_CALCULATE_CHANGE_MASK__
        #endregion
        #region __GHOST_WRITE_CHANGE_MASK__
        writer.WritePackedUIntDelta(changeMask__GHOST_MASK_BATCH__, baseline.changeMask__GHOST_MASK_BATCH__, compressionModel);
        #endregion
        #region __GHOST_WRITE_IS_PREDICTED__
        bool isPredicted = Get__GHOST_OWNER_FIELD__() == networkId;
        writer.WritePackedUInt(isPredicted?1u:0, compressionModel);
        #endregion
        #region __GHOST_WRITE__
        #endregion
        #region __GHOST_BEGIN_WRITE_PREDICTED__
        if (isPredicted)
        {
            #endregion
            #region __GHOST_WRITE_PREDICTED__
            #endregion
            #region __GHOST_END_WRITE_PREDICTED__
        }
        #endregion
        #region __GHOST_BEGIN_WRITE_INTERPOLATED__
        if (!isPredicted)
        {
            #endregion
            #region __GHOST_WRITE_INTERPOLATED__
            #endregion
            #region __GHOST_END_WRITE_INTERPOLATED__
        }
        #endregion
    }

    public void Deserialize(uint tick, ref __GHOST_NAME__SnapshotData baseline, ref DataStreamReader reader,
        NetworkCompressionModel compressionModel)
    {
        this.tick = tick;
        #region __GHOST_READ_CHANGE_MASK__
        changeMask__GHOST_MASK_BATCH__ = reader.ReadPackedUIntDelta(baseline.changeMask__GHOST_MASK_BATCH__, compressionModel);
        #endregion
        #region __GHOST_READ_IS_PREDICTED__
        bool isPredicted = reader.ReadPackedUInt(compressionModel)!=0;
        #endregion
        #region __GHOST_READ__
        #endregion
        #region __GHOST_BEGIN_READ_PREDICTED__
        if (isPredicted)
        {
            #endregion
            #region __GHOST_READ_PREDICTED__
            #endregion
            #region __GHOST_END_READ_PREDICTED__
        }
        #endregion
        #region __GHOST_BEGIN_READ_INTERPOLATED__
        if (!isPredicted)
        {
            #endregion
            #region __GHOST_READ_INTERPOLATED__
            #endregion
            #region __GHOST_END_READ_INTERPOLATED__
        }
        #endregion
    }
    public void Interpolate(ref __GHOST_NAME__SnapshotData target, float factor)
    {
        #region __GHOST_INTERPOLATE__
        #endregion
    }
}

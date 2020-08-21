#region __GHOST_IMPORTS__
#endregion
namespace Generated
{
    public struct GhostSnapshotData
    {
        struct Snapshot
        {
            #region __GHOST_FIELD__
            public int __GHOST_FIELD_NAME__;
            public uint __GHOST_FIELD_NAME__SpawnTick;
            #endregion
        }

        public void PredictDelta(uint tick, ref GhostSnapshotData baseline1, ref GhostSnapshotData baseline2)
        {
            var predictor = new GhostDeltaPredictor(tick, this.tick, baseline1.tick, baseline2.tick);
            #region __GHOST_PREDICT__
            snapshot.__GHOST_FIELD_NAME__ = predictor.PredictInt(snapshot.__GHOST_FIELD_NAME__, baseline1.__GHOST_FIELD_NAME__, baseline2.__GHOST_FIELD_NAME__);
            snapshot.__GHOST_FIELD_NAME__SpawnTick = (uint)predictor.PredictInt((int)snapshot.__GHOST_FIELD_NAME__SpawnTick, (int)baseline1.__GHOST_FIELD_NAME__SpawnTick, (int)baseline2.__GHOST_FIELD_NAME__);
            #endregion
        }
        public unsafe void CopyToSnapshot(ref GhostSerializerState serializerState, ref Snapshot snapshot, ref IComponentData component)
        {
            if (true)
            {
                #region __GHOST_COPY_TO_SNAPSHOT__
                snapshot.__GHOST_FIELD_NAME__ = 0;
                snapshot.__GHOST_FIELD_NAME__SpawnTick = 0;
                if (serializerState.GhostFromEntity.HasComponent(component.__GHOST_FIELD_REFERENCE__))
                {
                    var ghostComponent = serializerState.GhostFromEntity[component.__GHOST_FIELD_REFERENCE__];
                    snapshot.__GHOST_FIELD_NAME__ = ghostComponent.ghostId;
                    snapshot.__GHOST_FIELD_NAME__SpawnTick = ghostComponent.spawnTick;
                }
                #endregion
            }
        }
        public unsafe void CopyFromSnapshot(ref GhostDeserializerState deserializerState, ref Snapshot snapshotBefore, ref Snapshot snapshotAfter, float snapshotInterpolationFactor, ref IComponentData component)
        {
            if (true)
            {
                #region __GHOST_COPY_FROM_SNAPSHOT__
                component.__GHOST_FIELD_REFERENCE__ = Entity.Null;
                if (snapshotBefore.__GHOST_FIELD_NAME__ != 0 && deserializerState.GhostMap.TryGetValue(new SpawnedGhost{ghostId = snapshotBefore.__GHOST_FIELD_NAME__, spawnTick = snapshotBefore.__GHOST_FIELD_NAME__SpawnTick}, out var ghostEnt))
                    component.__GHOST_FIELD_REFERENCE__ = ghostEnt;
                #endregion
            }
        }
        public void Serialize(int networkId, ref GhostSnapshotData baseline, ref DataStreamWriter writer, NetworkCompressionModel compressionModel)
        {
            #region __GHOST_WRITE__
            if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
            {
                writer.WritePackedIntDelta(snapshot.__GHOST_FIELD_NAME__, baseline.__GHOST_FIELD_NAME__, compressionModel);
                writer.WritePackedUIntDelta(snapshot.__GHOST_FIELD_NAME__SpawnTick, baseline.__GHOST_FIELD_NAME__SpawnTick, compressionModel);
            }
            #endregion
        }

        public void Deserialize(uint tick, ref GhostSnapshotData baseline, ref DataStreamReader reader,
            NetworkCompressionModel compressionModel)
        {
            #region __GHOST_READ__
            if ((changeMask__GHOST_MASK_BATCH__ & (1 << __GHOST_MASK_INDEX__)) != 0)
            {
                snapshot.__GHOST_FIELD_NAME__ = reader.ReadPackedIntDelta(baseline.__GHOST_FIELD_NAME__, compressionModel);
                snapshot.__GHOST_FIELD_NAME__SpawnTick = reader.ReadPackedUIntDelta(baseline.__GHOST_FIELD_NAME__SpawnTick, compressionModel);
            }
            else
            {
                snapshot.__GHOST_FIELD_NAME__ = baseline.__GHOST_FIELD_NAME__;
                snapshot.__GHOST_FIELD_NAME__SpawnTick = baseline.__GHOST_FIELD_NAME__SpawnTick;
            }
            #endregion
        }
        public void CalculateChangeMask(ref Snapshot snapshot, ref Snapshot baseline, uint changeMask)
        {
            #region __GHOST_CALCULATE_CHANGE_MASK_ZERO__
            changeMask = (snapshot.__GHOST_FIELD_NAME__ != baseline.__GHOST_FIELD_NAME__ || snapshot.__GHOST_FIELD_NAME__SpawnTick != baseline.__GHOST_FIELD_NAME__SpawnTick) ? 1u : 0;
            #endregion
            #region __GHOST_CALCULATE_CHANGE_MASK__
            changeMask |= (snapshot.__GHOST_FIELD_NAME__ != baseline.__GHOST_FIELD_NAME__ || snapshot.__GHOST_FIELD_NAME__SpawnTick != baseline.__GHOST_FIELD_NAME__SpawnTick) ? (1u<<__GHOST_MASK_INDEX__) : 0;
            #endregion
        }
        public unsafe void RestoreFromBackup(ref IComponentData component, in IComponentData backup)
        {
            #region __GHOST_RESTORE_FROM_BACKUP__
            component.__GHOST_FIELD_REFERENCE__ = backup.__GHOST_FIELD_REFERENCE__;
            #endregion
        }
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static void ReportPredictionErrors(ref IComponentData component, in IComponentData backup, ref UnsafeList<float> errors, ref int errorIndex)
        {
            #region __GHOST_REPORT_PREDICTION_ERROR__
            #endregion
        }
        private static int GetPredictionErrorNames(ref FixedString512 names, ref int nameCount)
        {
            #region __GHOST_GET_PREDICTION_ERROR_NAME__
            #endregion
        }
        #endif
    }
}

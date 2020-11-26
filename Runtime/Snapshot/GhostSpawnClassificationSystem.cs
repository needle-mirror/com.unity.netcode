using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// GhostSPawnQueueComponent is used to identify the singleton component which contains the GhostSpawnBuffer.
    /// </summary>
    public struct GhostSpawnQueueComponent : IComponentData
    {

    }

    /// <summary>
    /// The GhostSpawnBuffer is the data for a GhostSpawnQueueComponent singleton. It contains a list of ghosts which
    /// will be spawned by the GhostSpawnSystem at the beginning of next frame. It is populated by the
    /// GhostReceiveSystem and there needs to be a classification system updating after the GhostReceiveSystem which
    /// sets the SpawnType so the spawn system knows how to spawn the ghost.
    /// A classification system should only modify the SpawnType and PredictedSpawnEntity fields of this struct.
    /// </summary>
    public struct GhostSpawnBuffer : IBufferElementData
    {
        public enum Type
        {
            Unknown,
            Interpolated,
            Predicted
        }
        public Type SpawnType;
        public int GhostType;
        public int GhostID;
        public int DataOffset;
        /// <summary>
        /// The size of the initial dynamic buffers data associated with the entity.
        /// </summary>
        public uint DynamicDataSize;
        /// <summary>
        /// The tick this ghost was spawned on the client. This is mainly used to determine the first tick we have data
        /// for so we can avoid spawning it before we have any data for the ghost.
        /// </summary>
        internal uint ClientSpawnTick;
        /// <summary>
        /// The tick this ghost was spawned on the server. For any predicted spawning this is the tick that should
        /// match since you are interested in when the server spawned the ghost, not when the server first sent the
        /// ghost to the client. Using this also means you are not considering ghosts becoming relevant as spawning.
        /// </summary>
        public uint ServerSpawnTick;
        public Entity PredictedSpawnEntity;
    }
    /// <summary>
    /// The default GhostSpawnClassificationSystem will set the SpawnType to the default specified in the
    /// GhostAuthoringComponent, unless some other classification has already set the SpawnType. This system
    /// will also check ghost owner to set the spawn type correctly for owner predicted ghosts.
    /// For predictive spawning you usually add a system after GhostSpawnClassificationSystem which only looks at
    /// items with SpawnType set to Predicted and set the PredictedSpawnEntity if you find a matching entity.
    /// The reason to put predictive spawn systems after the default is so the owner predicted logic has run.
    /// </summary>
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    public class GhostSpawnClassificationSystem : SystemBase
    {
        protected override void OnCreate()
        {
            var ent = EntityManager.CreateEntity();
            EntityManager.AddComponentData(ent, default(GhostSpawnQueueComponent));
            EntityManager.AddBuffer<GhostSpawnBuffer>(ent);
            EntityManager.AddBuffer<SnapshotDataBuffer>(ent);
            RequireSingletonForUpdate<NetworkIdComponent>();
            RequireSingletonForUpdate<GhostCollection>();
        }
        protected unsafe override void OnUpdate()
        {
            var ghostCollectionSingleton = GetSingletonEntity<GhostCollection>();
            var ghostTypesFromEntity = GetBufferFromEntity<GhostCollectionPrefabSerializer>(true);
            var networkId = GetSingleton<NetworkIdComponent>().Value;
            Dependency = Entities
                .WithAll<GhostSpawnQueueComponent>()
                .WithReadOnly(ghostTypesFromEntity)
                .ForEach((DynamicBuffer<GhostSpawnBuffer> ghosts, DynamicBuffer<SnapshotDataBuffer> data) =>
            {
                var ghostTypes = ghostTypesFromEntity[ghostCollectionSingleton];
                for (int i = 0; i < ghosts.Length; ++i)
                {
                    var ghost = ghosts[i];
                    if (ghost.SpawnType == GhostSpawnBuffer.Type.Unknown)
                    {
                        ghost.SpawnType = ghostTypes[ghost.GhostType].FallbackPredictionMode;
                        if (ghostTypes[ghost.GhostType].PredictionOwnerOffset != 0 && ghostTypes[ghost.GhostType].OwnerPredicted != 0)
                        {
                            // Prediciton mode is where the owner i is stored in the snapshot data
                            var dataPtr = (byte*)data.GetUnsafePtr();
                            dataPtr += ghost.DataOffset;
                            if (*(int*)(dataPtr+ghostTypes[ghost.GhostType].PredictionOwnerOffset) == networkId)
                                ghost.SpawnType = GhostSpawnBuffer.Type.Predicted;
                        }
                        ghosts[i] = ghost;
                    }
                }
            }).Schedule(Dependency);
        }
    }
}
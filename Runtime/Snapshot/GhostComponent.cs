using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Component signaling an entity which is replicated over the network
    /// </summary>
    public struct GhostComponent : IComponentData
    {
        public int ghostId;
    }

    /// <summary>
    /// Component on client signaling that an entity is predicted instead of interpolated
    /// </summary>
    public struct PredictedGhostComponent : IComponentData
    {
        public uint AppliedTick;
        public uint PredictionStartTick;
    }

    /// <summary>
    /// Component used to request predictive spawn of a ghost. Create an entity with this
    /// tag and an ISnapshotData of the ghost type you want to create. The ghost type must
    /// support predictive spawning to use this.
    /// </summary>
    public struct PredictedGhostSpawnRequestComponent : IComponentData
    {
    }
}

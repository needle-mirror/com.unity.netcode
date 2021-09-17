using Unity.Entities;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostCollectionSystem))]
    [UpdateBefore(typeof(GhostPredictionSystemGroup))]
    public class PrespawnGhostSystemGroup : ComponentSystemGroup
    {
    }
}

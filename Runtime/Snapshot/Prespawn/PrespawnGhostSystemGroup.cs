using Unity.Entities;
using UnityEngine.Scripting.APIUpdating;

namespace Unity.Netcode
{
    /// <summary>
    /// The PrespawnGhostSystemGroup contains all the systems related to pre-spawned ghost.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostCollectionSystem))]
    [MovedFrom(true, "Unity.NetCode")]
    public partial class PrespawnGhostSystemGroup : ComponentSystemGroup
    {
    }
}

using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Parent group of all systems that need to process ghost entities after they are spawned.
    /// The group execute before <see cref="NetworkReceiveSystemGroup"/> to guarantee that when a new snasphot is received
    /// from server, all new ghosts has been spawned and ready to receive new data.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ThinClientSimulation, WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst=true)]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    [UpdateBefore(typeof(NetworkReceiveSystemGroup))]
    public partial class GhostSpawnSystemGroup : ComponentSystemGroup
    {
    }
}

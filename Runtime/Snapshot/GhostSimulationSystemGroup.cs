using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    [UpdateBefore(typeof(TransformSystemGroup))]
    public class GhostSimulationSystemGroup : ComponentSystemGroup
    {
        public GhostSimulationSystemGroup()
        {
            UseLegacySortOrder = false;
        }
    }

    [UpdateInGroup(typeof(ClientSimulationSystemGroup), OrderFirst=true)]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    public class GhostSpawnSystemGroup : ComponentSystemGroup
    {
        public GhostSpawnSystemGroup()
        {
            UseLegacySortOrder = false;
        }
    }
}

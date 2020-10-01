using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    public interface IGhostMappingSystem
    {
        JobHandle LastGhostMapWriter { get; set; }
        NativeHashMap<SpawnedGhost, Entity> SpawnedGhostEntityMap { get; }
    }
    [UpdateInWorld(UpdateInWorld.TargetWorld.ClientAndServer)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    public class GhostSimulationSystemGroup : ComponentSystemGroup
    {
        private IGhostMappingSystem ghostMappingSystem;
        protected override void OnCreate()
        {
            //Client and server retrieve depedencies and ghost mapping from different systems. They are implementing
            //the IGhostMappingSystem interface and the GhostSimulationGroup just like as mediato to dispatch the
            //call to the right system.
            base.OnCreate();
            if (World.GetExistingSystem<GhostSendSystem>() != null)
            {
                ghostMappingSystem = World.GetExistingSystem<GhostSendSystem>();
            }
            else if (World.GetExistingSystem<GhostReceiveSystem>() != null)
            {
                ghostMappingSystem = World.GetExistingSystem<GhostReceiveSystem>();
            }
            else
            {
                throw new InvalidOperationException("Neither GhostSendSystem or GhostReceiveSystem are present in the world");
            }
        }

        public JobHandle LastGhostMapWriter
        {
            get { return ghostMappingSystem.LastGhostMapWriter; }
            set { ghostMappingSystem.LastGhostMapWriter = value; }
        }

        public NativeHashMap<SpawnedGhost, Entity> SpawnedGhostEntityMap
        {
            get { return ghostMappingSystem.SpawnedGhostEntityMap; }
        }
    }

    [UpdateInGroup(typeof(ClientSimulationSystemGroup), OrderFirst=true)]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    [UpdateBefore(typeof(NetworkReceiveSystemGroup))]
    public class GhostSpawnSystemGroup : ComponentSystemGroup
    {
    }
}

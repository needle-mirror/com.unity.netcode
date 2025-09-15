using Unity.Burst;
using Unity.Entities;

namespace Unity.NetCode
{
    [BurstCompile]
    [WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)]
    [WithChangeFilter(typeof(GhostOwner), typeof(GhostOwnerIsLocal))]
    internal partial struct UpdateGhostOwnerIsLocal : IJobEntity
    {
        public int localNetworkId;
        public void Execute(in GhostOwner ghostOwner, EnabledRefRW<GhostOwnerIsLocal> isLocalEnabledRef) => isLocalEnabledRef.ValueRW = ghostOwner.NetworkId == localNetworkId;
    }

    [BurstCompile]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostUpdateSystem))] // so ownership is up to date
    [UpdateBefore(typeof(GhostInputSystemGroup))] // so input gathering has up to date input owner info when gathering input
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial struct GhostOwnerUpdateSystem : ISystem
    {
        public void OnCreate(ref SystemState state) { }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.TryGetSingletonEntity<LocalConnection>(out var connectionEntity))
            {
                var job = new UpdateGhostOwnerIsLocal() { localNetworkId = state.EntityManager.GetComponentData<NetworkId>(connectionEntity).Value };
                state.Dependency = job.ScheduleParallel(state.Dependency);
            }
        }
    }
}

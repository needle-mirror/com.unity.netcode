using Unity.Burst;
using Unity.Entities;
using Unity.Transforms;

namespace Unity.NetCode
{
    [BurstCompile]
    internal static class BurstedComponentAccess
    {
        // TODO-release@potentialOptim burst more access
        // TODO-release@potentialOptim there's new overhead with CoreCLR for managed to unmanaged calls. This might be slower now than just simply calling EntityManager directly
        [BurstCompile]
        internal static void StaticGetOwnerNetworkIdBursted(in EntityManager em, in Entity entity, out int ownerNetworkId)
        {
            ownerNetworkId = em.GetComponentData<GhostOwner>(entity).NetworkId;
        }

        [BurstCompile]
        internal static void StaticSetOwnerNetworkIdBursted(in EntityManager em, in Entity entity, in NetworkId ownerNetworkId)
        {
            em.SetComponentData(entity, new GhostOwner { NetworkId = ownerNetworkId.Value });
        }

        [BurstCompile]
        internal static void StaticGetGhostInstanceBursted(in EntityManager em, in Entity entity, out GhostInstance ghostInstance)
        {
            ghostInstance = em.GetComponentData<GhostInstance>(entity);
        }

        [BurstCompile]
        internal static void StaticGetLocalTransform(in EntityManager em, in Entity entity, out LocalTransform ghostInstance)
        {
            ghostInstance = em.GetComponentData<LocalTransform>(entity);
        }
    }
}

using Unity.Entities;
using Unity.Mathematics;

namespace Unity.NetCode
{
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    public struct CurrentSimulatedPosition : IComponentData
    {
        public float3 Value;
    }

    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    public struct PreviousSimulatedPosition : IComponentData
    {
        public float3 Value;
    }

    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    public struct CurrentSimulatedRotation : IComponentData
    {
        public quaternion Value;
    }

    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    public struct PreviousSimulatedRotation : IComponentData
    {
        public quaternion Value;
    }
}

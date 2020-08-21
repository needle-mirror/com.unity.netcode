using Unity.Entities;

namespace Unity.NetCode
{
    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    [GenerateAuthoringComponent]
    public struct CommandDataInterpolationDelay : IComponentData
    {
        public uint Delay;
    }
}
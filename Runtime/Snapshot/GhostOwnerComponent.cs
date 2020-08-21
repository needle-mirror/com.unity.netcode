using Unity.Entities;

namespace Unity.NetCode
{
    public struct GhostOwnerComponent : IComponentData
    {
        [GhostField]
        public int NetworkId;
    }
}
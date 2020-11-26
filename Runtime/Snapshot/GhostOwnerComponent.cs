using Unity.Entities;

namespace Unity.NetCode
{
    [DontSupportPrefabOverrides]
    public struct GhostOwnerComponent : IComponentData
    {
        [GhostField]
        public int NetworkId;
    }
}
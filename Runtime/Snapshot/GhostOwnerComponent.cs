using Unity.Entities;

namespace Unity.NetCode
{
    [DontSupportPrefabOverrides]
    [DontSupportVariation]
    public struct GhostOwnerComponent : IComponentData
    {
        [GhostField] public int NetworkId;
    }
}
using Unity.Entities;

namespace Unity.NetCode
{
    public struct AutoCommandTarget : IComponentData
    {
        [GhostField] public bool Enabled;
    }
}

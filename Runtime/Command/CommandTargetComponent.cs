using Unity.Entities;

namespace Unity.NetCode
{
    public struct CommandTargetComponent : IComponentData
    {
        public Entity targetEntity;
    }
}

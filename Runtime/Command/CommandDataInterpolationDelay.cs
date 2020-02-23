using Unity.Entities;

namespace Unity.NetCode
{
    [GenerateAuthoringComponent]
    public struct CommandDataInterpolationDelay : IComponentData
    {
        public uint Delay;
    }
}
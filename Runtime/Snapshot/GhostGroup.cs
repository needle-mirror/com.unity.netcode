using Unity.Entities;

namespace Unity.NetCode
{
    [GenerateAuthoringComponent]
    public struct GhostGroup : IBufferElementData
    {
        public Entity Value;
    };
}
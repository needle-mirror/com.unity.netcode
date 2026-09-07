using Unity.Entities;

namespace Unity.Netcode.Tests
{
    internal struct SomeData : IComponentData
    {
        [GhostField] public int Value;
    }

    internal struct SomeDataElement : IBufferElementData
    {
        [GhostField] public int Value;
    }

    internal struct NonGhostTestData : IComponentData
    {
        public int Value;
    }
}

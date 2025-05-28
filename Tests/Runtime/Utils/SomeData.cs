using Unity.Entities;

namespace Unity.NetCode.Tests
{
    internal struct SomeData : IComponentData
    {
        [GhostField] public int Value;
    }

    internal struct SomeDataElement : IBufferElementData
    {
        [GhostField] public int Value;
    }
}


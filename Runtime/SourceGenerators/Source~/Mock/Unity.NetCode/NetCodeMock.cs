using Unity.Entities;

namespace Unity.Netcode;

public class NetCodeMock { }

public class PredictedSimulationSystemGroup : ComponentSystemGroup
{

}

public struct TestComponent : IComponentData
{
    public int Value;
}

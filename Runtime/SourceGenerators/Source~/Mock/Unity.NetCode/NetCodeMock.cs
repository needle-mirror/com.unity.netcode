using Unity.Entities;

namespace Unity.NetCode;

public class NetCodeMock { }

public class PredictedSimulationSystemGroup : ComponentSystemGroup
{

}

public struct TestComponent : IComponentData
{
    public int Value;
}

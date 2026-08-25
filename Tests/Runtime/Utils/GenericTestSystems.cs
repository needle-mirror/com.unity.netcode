using Unity.Entities;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    internal abstract partial class BaseCallbackSystem : SystemBase
    {
        public delegate void OnUpdateDelegate(World world);
        public OnUpdateDelegate OnUpdateCallback;

        protected override void OnUpdate()
        {
            OnUpdateCallback?.Invoke(this.World);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    internal partial class BeforePredictionSystem : BaseCallbackSystem
    {
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    internal partial class AfterPredictionSystem : BaseCallbackSystem
    {

    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class UpdateInPredictionSystem : BaseCallbackSystem
    {

    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(UpdateNetworkTimeSystem))]
    internal partial class BeforeSimulationSystemGroup : BaseCallbackSystem
    {

    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    internal partial class AfterSimulationSystemGroup : BaseCallbackSystem
    {

    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(GameObjectPhysicsSimulateSystem))]
    internal partial class AfterSimulateFixedPredictionCallbackSystem : BaseCallbackSystem
    {

    }
}


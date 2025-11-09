#if ENABLE_UNITY_NETCODE_PHYSICS
using Unity.Core;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;

namespace DocumentationCodeSamples
{
    partial class physics
    {
        #region MultiplePhysicsWorlds
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
        public partial class VisualizationPhysicsSystemGroup : CustomPhysicsSystemGroup
        {
            public VisualizationPhysicsSystemGroup() : base(1, true)
            {}
        }
        #endregion

        [DisableAutoCreation]
        #region DisablePhysicsInitialization
        [UpdateInGroup(typeof(InitializationSystemGroup))]
        [CreateAfter(typeof(PredictedPhysicsConfigSystem))]
        public partial struct DisablePhysicsInitializationIfNotConnect : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.World.GetExistingSystemManaged< PredictedPhysicsConfigSystem >().Enabled = false;
            }
        }
        #endregion

        [DisableAutoCreation]
        #region ForcePhysicsInitialization
        [UpdateInGroup(typeof(InitializationSystemGroup))]
        public partial class ForcePhysicsInitializationIfNotConnect : SystemBase
        {
            protected override void OnUpdate()
            {
                if (SystemAPI.GetSingleton<SimulationSingleton>().Type == SimulationType.NoPhysics)
                {
                    //Force a single update of physics just to ensure we have some stuff setup
                    World.PushTime(new TimeData(0.0, 0f));
                    World.GetExistingSystem<PhysicsSystemGroup>().Update(World.Unmanaged);
                    World.PopTime();
                    Enabled = false;
                }
            }
        }
        #endregion
    }
}
#endif

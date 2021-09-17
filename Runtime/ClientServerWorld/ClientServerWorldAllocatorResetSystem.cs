using Unity.Entities;

namespace Unity.NetCode
{
    [UpdateInWorld(TargetWorld.ClientAndServer)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(BeginSimulationEntityCommandBufferSystem))]
    public struct ClientServerWorldAllocatorResetSystem : ISystem
    {
        //This is just used to use internal member we don't have access to.
        private WorldUpdateAllocatorResetSystem m_resetWorlSystem;
        public void OnCreate(ref SystemState state)
        {
            m_resetWorlSystem = new WorldUpdateAllocatorResetSystem();
        }
        public void OnDestroy(ref SystemState state)
        {
        }
        public void OnUpdate(ref SystemState state)
        {
            m_resetWorlSystem.OnUpdate(ref state);
        }
    }
}

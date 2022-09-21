using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Reset the world allocator at the beginning of the frame.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(BeginSimulationEntityCommandBufferSystem))]
    internal struct ClientServerWorldAllocatorResetSystem : ISystem
    {
        //This is just used to use internal member we don't have access to.
        private WorldUpdateAllocatorResetSystem m_resetWorlSystem;
        /// <summary>
        /// ISystem interface implemention. Create a new instance of the <see cref="WorldUpdateAllocatorResetSystem"/>
        /// </summary>
        /// <param name="state"></param>
        public void OnCreate(ref SystemState state)
        {
            m_resetWorlSystem = new WorldUpdateAllocatorResetSystem();
        }
        /// <summary>
        /// ISystem interface implemention.
        /// </summary>
        /// <param name="state"></param>
        public void OnDestroy(ref SystemState state)
        {
        }
        /// <summary>
        /// ISystem interface implemention. Reset the allocator.
        /// </summary>
        /// <param name="state"></param>
        public void OnUpdate(ref SystemState state)
        {
            m_resetWorlSystem.OnUpdate(ref state);
        }
    }
}

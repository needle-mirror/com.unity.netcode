using Unity.Entities;
using Unity.Jobs;
using Unity.Physics.Systems;

namespace Unity.NetCode
{
    [UpdateBefore(typeof(BuildPhysicsWorld))]
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    [UpdateInWorld(UpdateInWorld.TargetWorld.ClientAndServer)]
    /// <summary>
    /// System to make sure physics runs after ghost update.
    /// This system only exists when Unity Physics is instaled.
    /// </summary>
    public class PhysicsNetCodeOrderingSystem : SystemBase
    {
        PhysicsWorldHistory m_PhysicsWorldHistory;
        protected override void OnCreate()
        {
            m_PhysicsWorldHistory = World.GetOrCreateSystem<PhysicsWorldHistory>();
        }
        protected override void OnUpdate()
        {
            m_PhysicsWorldHistory.LastPhysicsJobHandle.Complete();
        }
    }
}

using System;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// System responsible to track all the <see cref="GhostBehaviour"/> component and run their
    /// <see cref="GhostBehaviour.PredictedPhysicsUpdate"/> method inside the <see cref="PredictedSimulationSystemGroup"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(GameObjectPhysicsSimulateSystem))]
    partial class GhostBehaviourFixedPredictionUpdate : BaseNetcodeUpdateCaller
    {
        protected override void InitQueryForGhosts()
        {
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<PredictedGhost, GhostGameObjectLink, GhostBehaviour.GhostBehaviourTracking, GhostRigidbodyData>();
            if (this.World.IsClient())
                //Server can use a faster query (no checks for simulate)
                builder = builder.WithAll<Simulate>();
            m_GhostsToRunOn = GetEntityQuery(builder);
        }

        protected override bool HasUpdate(in GhostBehaviourTypeInfo typeInfo)
        {
            return typeInfo.HasNetworkedFixedUpdate;
        }

        protected override void RunMethodOnBehaviour(GhostBehaviour behaviour, float deltaTime)
        {
            behaviour.PredictedPhysicsUpdate(deltaTime);
        }
    }
}

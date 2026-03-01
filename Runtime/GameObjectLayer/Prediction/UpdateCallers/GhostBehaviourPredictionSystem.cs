#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Profiling;
using UnityEngine.Profiling;

namespace Unity.NetCode
{
    /// <summary>
    /// System responsible to track all the <see cref="GhostBehaviour"/> component and run their
    /// <see cref="GhostBehaviour.PredictionUpdate"/> method inside the <see cref="PredictedSimulationSystemGroup"/>.
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    internal
#endif
    partial class GhostBehaviourPredictionSystem : BaseNetcodeUpdateCaller
    {

        protected override void InitQueryForGhosts()
        {
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<PredictedGhost, GhostGameObjectLink, GhostBehaviour.GhostBehaviourTracking>();
            if (this.World.IsClient())
                //Server can use a faster query (no checks for simulate)
                builder = builder.WithAll<Simulate>();
            m_GhostsToRunOn = GetEntityQuery(builder);
        }

        protected override bool HasUpdate(in GhostBehaviourTypeInfo typeInfo)
        {
            return typeInfo.HasPredictionUpdate;
        }
        protected override void RunMethodOnBehaviour(GhostBehaviour behaviour, float deltaTime)
        {
            behaviour.PredictionUpdate(deltaTime);
        }
    }
}
#endif

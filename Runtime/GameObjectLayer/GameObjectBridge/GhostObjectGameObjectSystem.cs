
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// System group where it's safe to access and destroy a Netcode tracked GameObject from a system. Underlying entity should be spawned, ghost fields should be up to date with snapshot values
    /// TODO-release is this actually still needed?
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.LocalSimulation,
        WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup), OrderLast = true)]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    partial class GhostGameObjectSystemGroup : ComponentSystemGroup { }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
    /// <summary>
    /// Simple system that gives users proper messages if they try to do things that aren't supported.
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial struct GhostGameObjectCleanupSystem : ISystem
    {
        EntityQuery m_InvalidGhostsQuery;
        public void OnCreate(ref SystemState state)
        {
            m_InvalidGhostsQuery = new EntityQueryBuilder(Allocator.Temp).WithAll<GhostGameObjectLink>().WithNone<GhostInstance>().Build(ref state);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!m_InvalidGhostsQuery.IsEmpty)
            {
                // TODO-release we should support this once we have entities integration that manage this link for us
                var invalidEntities = m_InvalidGhostsQuery.ToEntityArray(Allocator.Temp);
                var firstEntity = invalidEntities[0];

                Debug.LogError($"Destroying a ghost entity (first entity with this issue: {state.EntityManager.GetName(firstEntity)}) that has a GameObject attached is not supported. Please destroy the GameObject itself. There are {invalidEntities.Length} entities with this issue.");
            }
        }
    }
#endif
}


using System;
using Unity.Entities;

// TODO-next empty shell for now, upcoming PRs will refactor this to support PredictionUpdate in monobehaviours
namespace Unity.NetCode
{
    /// <summary>
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    partial class GhostBehaviourPredictionSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
}

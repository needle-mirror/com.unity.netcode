using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    // replicated to all
    internal partial class BehaviourAllData : GhostBehaviour
    {
        public GhostField<int> data;
        public GhostComponentRef<BridgedIntGhostComponentReplicatedToAll> bridgeData;
    }

    [System.Serializable]
    internal struct BridgedIntGhostComponentReplicatedToAll : IComponentData
    {
        [GhostField] public int data;
    }
}

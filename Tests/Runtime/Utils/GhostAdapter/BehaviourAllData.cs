#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
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

    public struct BridgedIntGhostComponentReplicatedToAll : IComponentData
    {
        [GhostField] public int data;
    }
}

#endif

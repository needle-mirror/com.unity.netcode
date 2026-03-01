#if !UNITY_DISABLE_MANAGED_COMPONENTS
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using UnityEngine;

namespace Unity.NetCode.Tests
{
    [DefaultExecutionOrder(200)]
    internal class GhostBehaviourB : GhostBehaviourWithPriority
    {
    }
}
#endif
#endif

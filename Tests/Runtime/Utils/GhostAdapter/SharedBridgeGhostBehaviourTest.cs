#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
namespace Unity.NetCode.Tests
{
    internal partial class SharedBridgeGhostBehaviourTest : GhostBehaviour
    {
        public GhostComponentRef<SomeBridgedValue> bridge;
    }
}
#endif

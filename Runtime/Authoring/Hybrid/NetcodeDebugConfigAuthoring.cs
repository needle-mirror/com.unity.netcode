using Unity.Entities;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Add this component to a gameobject present in a sub-scene to configure the <see cref="NetDebug"/> logging level and
    /// enable packet dumps.
    /// </summary>
    [HelpURL(Authoring.HelpURLs.NetCodeDebugConfigAuthoring)]
    [AddComponentMenu("Multiplayer/NetCode Debug Config Authoring", 5)]
    public class NetcodeDebugConfigAuthoring : MonoBehaviour
    {
        /// <summary>
        /// The current debug level used by netcode.
        /// </summary>
        public NetDebug.LogLevelType LogLevel = NetDebug.LogLevelType.Notify;
        /// <summary>
        /// Enable/Disable per connection packet dumps. When enabled, for each connection a file is created containing all the packet sent
        /// (for the server) or received (for the client).
        /// The packet dump use quite a lot of resources and should be used mostly (if not only) for debugging replication issues.
        /// </summary>
        public bool DumpPackets;
    }

    [BakingVersion("cmarastoni", 1)]
    class NetCodeDebugConfigAuthoringBaker : Baker<NetcodeDebugConfigAuthoring>
    {
        public override void Bake(NetcodeDebugConfigAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new NetcodeDebugConfig
            {
                LogLevel = authoring.LogLevel,
                DumpPackets = authoring.DumpPackets
            });
        }
    }
}

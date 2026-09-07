#if !NETCODE_NO_OBSOLETE_HELPER
using System;
using System.ComponentModel;

namespace Unity.NetCode
{
    /// <summary>Deprecated. Use <c>Unity.Netcode.NetcodePhysicsConfig</c> instead.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("NetCode capital C letter changed to lower case. (UnityUpgradable) -> Unity.Netcode.NetcodePhysicsConfig", error: true)]
    public sealed class NetCodePhysicsConfig
    {

    }
}
#endif

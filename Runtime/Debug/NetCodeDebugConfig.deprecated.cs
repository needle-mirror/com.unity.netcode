#if !NETCODE_NO_OBSOLETE_HELPER
using System;
using System.ComponentModel;

namespace Unity.NetCode
{
    /// <summary>Deprecated. Use <c>Unity.Netcode.NetcodeDebugConfig</c> instead.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("NetCode capital C letter changed to lower case. (UnityUpgradable) -> Unity.Netcode.NetcodeDebugConfig", error: true)]
    public struct NetCodeDebugConfig
    {

    }
}
#endif

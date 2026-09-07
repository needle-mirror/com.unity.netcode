#if !NETCODE_NO_OBSOLETE_HELPER
using System;
using System.ComponentModel;

namespace Unity.NetCode.Hybrid
{
    /// <summary>Deprecated. Use <c>Unity.Netcode.Hybrid.NetcodeClientAndServerSettings</c> instead.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("NetCode capital C letter changed to lower case. (UnityUpgradable) -> Unity.Netcode.Hybrid.NetcodeClientAndServerSettings", error: true)]
    public class NetCodeClientAndServerSettings
    {

    }
}
#endif

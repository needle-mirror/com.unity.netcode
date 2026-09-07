#if !NETCODE_NO_OBSOLETE_HELPER

using System;
using System.ComponentModel;

namespace Unity.NetCode.Hybrid
{
    /// <summary>Deprecated. Use <c>Unity.Netcode.Hybrid.NetcodeClientSettings</c> instead.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("NetCode capital C letter changed to lower case. (UnityUpgradable) -> Unity.Netcode.Hybrid.NetcodeClientSettings", error: true)]
    public class NetCodeClientSettings
    {

    }

    /// <summary>Deprecated. Use <c>Unity.Netcode.Hybrid.NetcodeClientTarget</c> instead.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("NetCode capital C letter changed to lower case. (UnityUpgradable) -> Unity.Netcode.Hybrid.NetcodeClientTarget", error: true)]
    public enum NetCodeClientTarget
    {
        /// <summary>Deprecated. Use <c>Unity.Netcode.Hybrid.NetcodeClientTarget.Client</c> instead.</summary>
        Client = 0,
        /// <summary>Deprecated. Use <c>Unity.Netcode.Hybrid.NetcodeClientTarget.ClientAndServer</c> instead.</summary>
        ClientAndServer = 1
    }
}
#endif

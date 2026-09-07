#if !NETCODE_NO_OBSOLETE_HELPER
using System;
using System.ComponentModel;

namespace Unity.NetCode
{
    /// <summary>Deprecated. Use <c>Unity.Netcode.NetcodeConfig</c> instead.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("(UnityUpgradable) -> Unity.Netcode.NetcodeConfig", error: true)]
    public class NetCodeConfig { }
}
#endif

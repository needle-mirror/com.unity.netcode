#if !NETCODE_NO_OBSOLETE_HELPER
using System;
using System.ComponentModel;

namespace Unity.NetCode
{
    /// <summary>Deprecated. Use <c>Unity.Netcode.NetcodeDisableCommandCodeGenAttribute</c> instead.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("NetCode capital C letter changed to lower case. (UnityUpgradable) -> Unity.Netcode.NetcodeDisableCommandCodeGenAttribute", error: true)]
    public class NetCodeDisableCommandCodeGenAttribute
    {

    }
}
#endif

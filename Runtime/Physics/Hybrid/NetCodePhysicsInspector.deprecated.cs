#if !NETCODE_NO_OBSOLETE_HELPER
using System;
using System.ComponentModel;

namespace Unity.NetCode.Editor
{
    /// <summary>Deprecated. Use <c>Unity.Netcode.Editor.NetcodePhysicsInspector</c> instead.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("NetCode capital C letter changed to lower case. (UnityUpgradable) -> Unity.Netcode.Editor.NetcodePhysicsInspector", error: true)]
    public sealed class NetCodePhysicsInspector
    {

    }
}
#endif

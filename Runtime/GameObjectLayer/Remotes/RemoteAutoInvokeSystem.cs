using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Netcode.LowLevel.Unsafe;

namespace Unity.Netcode
{
    /// <summary>
    /// Parent group of all code-generated systems that register automatic Remote invocation <see cref="IRemote"/>,
    /// For internal use only, don't add systems to this group.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation,
        WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [CreateAfter(typeof(GhostComponentSerializerCollectionSystemGroup))]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#else
    internal
#endif // NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    partial class Internal_RemoteAutoInvokeSystemGroup : ComponentSystemGroup
    {
    }
}

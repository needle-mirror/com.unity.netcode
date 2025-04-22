# Network protocol checks

When a client connects to a server, they exchange a
protocol ([NetworkProtocolVersion](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkProtocolVersion.html))
that contains the netcode version, game version, RPC collection, and serialized component collections. This is a
preventative measure to stop incompatible versions of games from connecting to each other, which can lead to undefined behavior.

The RPC collection is hash calculated from all RPCs that are compiled in all loaded assemblies, based on their type and
their members. Similarly, serialized components are based on all the ghost components that are compiled in all loaded
assemblies and picked up by Netcode for Entities. Using the types and the type members of RPCs and serialized
components, a hash is calculated which ends up as part of the protocol.

By default, Netcode for Entities requires these exchanged protocol hashes to be deterministic (fully identical) to
prevent mid-match exceptions and enable bandwidth optimizations.

However, this process can frequently flag potentially compatible builds as incompatible during
development due to the strictness of this requirement (by producing false positive hits when testing). For example, when
testing a standalone player against an in-Editor world, the Editor may have some test assemblies loaded (which may
contain RPC types, ghost component types, or runtime ghost types) that aren't included in the build, which causes a hash mismatch, and therefore a disconnection.
This strict protocol version check can therefore [be disabled](#disabling-the-check).

When this protocol version error occurs, each peer will disconnect itself from the remote
via `NetworkStreamDisconnectReason.BadProtocolVersion`, which user code can read, and use to signal to the player that
their build is incompatible with the target remote peer. In development builds, the package will also output error logs
denoting the full and sorted lists of RPC and ghost types loaded on the local peer. Cross reference these logs against
the ones raised on the remote peer to troubleshoot type mismatches.

## Disabling the check

To disable the check, set [`RpcCollection.DynamicAssemblyList`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.RpcCollection.html#Unity_NetCode_RpcCollection_DynamicAssemblyList)
to true like this:

```csharp
[BurstCompile] // BurstCompile is optional
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
[CreateAfter(typeof(RpcSystem))]
public partial struct SetRpcSystemDynamicAssemblyListSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        SystemAPI.GetSingletonRW<RpcCollection>().ValueRW.DynamicAssemblyList = true;
        state.Enabled = false;
    }
}
```

Because this modifies the `RpcCollection` (which is itself instantiated by the `RpcSystem`), this flag needs to be set before `RpcSystem.OnUpdate` has run, but after `RpcSystem.OnCreate` has run. This flag must also match on both the client and the server before beginning communication, because the netcode package changes its RPC encoding based off the flag's value (including for the `NetworkProtocolVersion` RPC itself). Attempting to connect to a world with a different flag value than your own will lead to a similar (but less explicit) forceful disconnect error.

> [!NOTE]
> Enabling this flag adds six bytes to each RPC sent because it sends the full RPC hash instead of a ushort index into a guaranteed deterministic lookup. This means that netcode will throw a mid-game runtime error if it receives a ghost or RPC with an unknown type hash, and only then forcibly disconnect. This can result in a client being kicked hours into a game session, if they suddenly receive a ghost or RPC that they do not know about (rather than having this data validated during the connection attempt handshake).

## Additional resources

- [`NetworkProtocolVersion` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkProtocolVersion.html)

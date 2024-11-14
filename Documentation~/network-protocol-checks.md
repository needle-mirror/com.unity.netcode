# Network protocol checks

Understand network protocol checks in Netcode for Entities and how to disable them if required.

When a client connects to a server, they exchange a handshake protocol ([`NetworkProtocolVersion`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkProtocolVersion.html)) that contains the Netcode for Entities version, game version, and a hash representing the remote procedure call (RPC) and serialized component collections present on the server/client. This protocol check is a preventative measure to stop incompatible versions of games from connecting to each other, which can lead to undefined behavior.

## Calculating hashes

The RPC collection is a hash calculated from all RPCs that are compiled into (present in) all loaded assemblies, based on their type and their members. Similarly, the hash of serialized components is based on all the ghost components that are compiled into all loaded assemblies and picked up by Netcode for Entities. Using the types and the type members of RPCs and serialized components, two hashes are calculated, which are then shared as part of the protocol.

## Protocol validation

By default, Netcode for Entities requires the exchanged protocol hashes to be deterministic (fully identical) to prevent mis-match exceptions and enable bandwidth optimizations. However, due to the strictness of the determinism requirement, the protocol check can frequently flag potentially compatible builds as incompatible during development (by producing false positive hits when testing).

For example, when testing a standalone Player against an in-Editor world, the Unity Editor might have some test assemblies loaded (which might contain RPC types, ghost component types, or runtime ghost types) that aren't included in the build. This causes a hash mismatch, and therefore a disconnection. To avoid these issues, the strict protocol version check can [be disabled](#disabling-strict-protocol-checks).

When a protocol version error occurs, the client disconnects itself from the remote via `NetworkStreamDisconnectReason.BadProtocolVersion`, which user code can read and use to signal to the player that their build is incompatible with the target server. In development builds, Netcode for Entities also outputs error logs that contain the full and sorted lists of RPCs and ghost types loaded on the local client. You can cross reference these logs against the logs from the server to troubleshoot type mismatches.

## Disabling strict protocol checks

To disable strict protocol checking, set [`RpcCollection.DynamicAssemblyList`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.RpcCollection.html#Unity_NetCode_RpcCollection_DynamicAssemblyList)
to true as in the following example:

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

Because this change modifies the `RpcCollection` (which is itself instantiated by the `RpcSystem`), this flag needs to be set before `RpcSystem.OnUpdate` has run, but after `RpcSystem.OnCreate` has run (which is why the `CreateAfter` attribute is used). This flag must also match on both the client and the server before beginning communication, because Netcode for Entities changes its RPC encoding based on the flag's value, including for the `NetworkProtocolVersion` RPC itself. Attempting to connect to a world with a different flag value than your own will lead to a similar (but less explicit) forceful disconnect error.

> [!NOTE]
> Enabling this flag adds six bytes to each RPC sent because it sends the full RPC hash instead of a `ushort` index into a guaranteed deterministic lookup. This can result in Netcode for Entities throwing a mid-game runtime error if it receives a ghost or RPC with an unknown type hash, and only then forcibly disconnecting. This can cause clients to be kicked hours into a game session if they receive a ghost or RPC that they're not aware of (rather than having this data validated during the connection attempt handshake).

## Additional resources

- [`NetworkProtocolVersion` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkProtocolVersion.html)
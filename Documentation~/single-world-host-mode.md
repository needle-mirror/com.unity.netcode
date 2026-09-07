# Single-world host mode

Configure a host that combines the client and server roles into a single world to reduce the host's CPU and memory overhead.

A single-world host is primarily a server with some limited client systems running on it to render the local player's view. The host player's process contains one [world](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-worlds.html) that acts as both client and server. Unlike [binary-worlds host mode](binary-worlds-host-mode.md), there's no client-to-server IPC connection: the host runs a single simulation, owns the authoritative ghost state, and renders the local player directly from that state. The host opens a listening driver so that remote players can connect, and a fake connection entity (a `LocalConnection` singleton with a `NetworkId` component and no `NetworkStreamConnection`) represents the host's own player for convenience.

Because the host doesn't serialize and deserialize its own state, single-world host mode avoids the extra work a binary-world host performs for its local player, such as snapshot serialization, deserialization, rollback, and re-simulation. This makes it a good fit when the host's CPU and memory budget is tight. The trade-off is that the host no longer matches a dedicated server's code paths, and several features behave differently or aren't supported. For a setup that keeps parity with a dedicated server, refer to [Binary-worlds host mode](binary-worlds-host-mode.md).

> [!NOTE]
> Connecting players to a client-hosted server requires the use of a relay server. Refer to [Connect using a relay server](networking-using-relay.md) for more details.

## Select single-world host mode

Use the **Host World Mode Selection** dropdown on the active [`NetcodeConfig`](xref:Unity.Netcode.NetcodeConfig) asset to select single-world host mode. This is the default value for new projects.

## Bootstrap a single-world host manually

When you need more control over when worlds are created, for example to delay world creation until a menu transition, derive from [`ClientServerBootstrap`](xref:Unity.Netcode.ClientServerBootstrap) and create the host world yourself.

The following example overrides the default bootstrap so that no networked worlds are created on startup, then creates a single-world host on demand when the player chooses to host a game:

[!code-cs[blobs](../Tests/Editor/DocCodeSamples/single-host-world.cs#SingleWorldHostBootstrap)]

After the world exists, configure it to listen so that remote clients can connect.

## How the host's single world works

In single-world host mode, the host's world runs both server and client systems against one set of entities:

- The world runs the server simulation and owns the authoritative ghosts.
- Client systems run in the same world, so the visible ghosts are the authoritative ghosts rendered directly, not predicted or interpolated copies.
- Remote clients connect to the host through the network driver and receive snapshots just as they do from any server.

Because client and server systems share a world, `IsClient` and `IsServer` are both true on the host. Client systems can access other players' inputs, so filter inputs appropriately, for example with `GhostOwnerIsLocal`, when you only want to act on the local player.

## When to use single-world host mode

Use single-world host mode when:

- The host's CPU and memory budget is tight and you want to avoid the overhead of running two worlds.
- You don't need the host to exercise the same serialization and prediction code paths as a dedicated server.
- You don't rely on features that single-world host mode doesn't support, such as prediction switching or partial ticks.

Use [binary-worlds host mode](binary-worlds-host-mode.md) instead when you want parity with a dedicated server deployment, or when you plan to ship a dedicated server build alongside the client-hosted build.

## Limitations and considerations

Single-world host mode behaves differently from binary-worlds host mode in several ways. Consider the following before you choose it or migrate to it:

- **Prediction switching isn't supported**, so you can't use it in single-world host mode.
- **Partial ticks aren't supported on hosts.** Instead, interpolate your ghosts between full ticks on the host. You can enable one simulation tick's worth of interpolation smoothing per ghost type with the **SingleWorldHostInterpolationSmoothing** dropdown, which replaces the role of partial ticks on host worlds.
- **All ghosts are authoritative**, so you must handle interpolation differently. For transforms, you can use the **SingleWorldHostInterpolationSmoothing** setting mentioned above. For non-transform values, such as a health bar, you can smooth the visual representation by adding a component that lerps between the current and previous tick's values, or by using a custom interpolation system.
- **Relevancy can't be enabled for the host connection.** The host world must keep all server ghost entities in memory to serve other connections, so disable rendering for distant ghosts manually rather than relying on relevancy. Relevancy still applies to other connections.
- **Testing with lag requires an external client.** Because the host doesn't serialize or deserialize its own state, you can't add artificial latency to the host's local objects.
- **RPCs can use a fast path.** Custom serialization can take advantage of it with `IsPassthroughRPC` and `GetPassthroughActionData`.
- **The host only sends one snapshot per frame.** This means that catch-up ticks are handled differently than on a server-only world. If the host simulation falls behind and runs multiple catch-up ticks in the same frame, it only sends one snapshot at the end of the frame, rather than one per catch-up tick. Remote clients still run all the catch-up ticks and receive the final snapshot, but they won't receive intermediate snapshots for each catch-up tick. This also puts quite a high CPU load on the host while running catch-up ticks.
- **The host's client logic now has other player inputs locally.** Filter inputs using `GhostOwnerIsLocal`, which behaves differently in single-world host mode compared to binary-worlds host mode.
  - In single-world host mode, `GhostOwnerIsLocal` is true for the host player's connection and false for all remote clients' connections, so it can be used to filter for the host player's input.
  - In binary-worlds host mode, `GhostOwnerIsLocal` is true for all clients, including the host, so it can't be used to filter for the host player's input specifically.
- **The host's own connection is a fake connection entity.** Because the host has no transport connection to itself, Netcode for Entities creates a local connection entity with a `NetworkId` and a `LocalConnection` tag, but no `NetworkStreamConnection`. The host world also contains a real `NetworkStreamConnection` entity for each remote client that connects. To find the host's own connection, query for the `LocalConnection` singleton, or for entities that have a `NetworkId` but no `NetworkStreamConnection`. Don't destroy connection entities yourself.

## Additional resources

- [Network topologies](network-topologies.md)
- [Binary-worlds host mode](binary-worlds-host-mode.md)
- [Client and server worlds networking model](client-server-worlds.md)

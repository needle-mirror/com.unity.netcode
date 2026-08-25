# Binary-worlds host mode

Configure a host that runs separate client and server worlds connected through intra-process communication (IPC).

Binary-worlds host mode is the default client-hosted setup in Netcode for Entities. In this mode, the host player's process contains two distinct [worlds](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-worlds.html): a server world that runs server systems and owns authoritative ghost state, and a client world that runs client systems and renders the local player. The two worlds exchange snapshots and commands through a local IPC connection, exactly as a remote client would, except that no data travels across the network. Remote players connect to the host's server world using a regular network transport.

Because the host runs a full client and a full server, gameplay code paths are identical to a dedicated server deployment. This makes binary-worlds host mode the recommended choice when you want to keep the option of moving to a dedicated server later, or when you want local testing to exercise the same serialization and prediction code that runs in production. For an alternative that combines both roles into a single world for lower CPU and memory overhead, refer to [Single-world host mode](single-world-host-mode.md).

> [!NOTE]
> Connecting players to a client-hosted server requires the use of a relay server. Refer to [Connect using a relay server](networking-using-relay.md) for more details.

## Select binary-worlds host mode

Use the **Host World Mode Selection** dropdown on the active [`NetCodeConfig`](xref:Unity.NetCode.NetCodeConfig) asset to select binary-worlds host mode. This is the default value for new projects.

## Bootstrap a binary world host manually

When you need more control over when worlds are created, for example to delay world creation until a menu transition, derive from [`ClientServerBootstrap`](xref:Unity.NetCode.ClientServerBootstrap) and create the client and server worlds yourself. This is the same pattern as a standard [client-server](client-server.md) setup.

The following example overrides the default bootstrap so that no networked worlds are created on startup, then creates a binary host on demand when the player chooses to host a game:

[!code-cs[blobs](../Tests/Editor/DocCodeSamples/binary-host-mode.cs#BinaryHostBootstrap)]

After both worlds exist, configure the server world to listen and the client world to connect to the server's IPC endpoint.

## How the host's worlds communicate

In binary-worlds host mode, the host's client world connects to the host's server world using an IPC endpoint exposed by the server world:

- The server world serializes ghost snapshots and sends them through the IPC driver.
- The client world deserializes those snapshots, runs prediction and interpolation, and serializes input commands back to the server.
- Remote clients connect to the same server world through their network driver and receive the same snapshot stream.

Because the host's client uses the same code path as remote clients, any bug that only affects clients, such as a missing `[GhostField]` attribute or incorrect prediction logic, surfaces on the host as well as on remote clients. This is one of the main reasons to prefer binary-worlds host mode during development.

## When to use binary-worlds host mode

Use binary-worlds host mode when:

- You want local play tests to exercise the same serialization, prediction, and interpolation code paths as a dedicated server deployment.
- You plan to ship, or might later ship, a dedicated server build alongside the client-hosted build.
- You want to maintain a clear distinction between client and server worlds, which makes it easier to reason about which systems run where.
- You're using features that aren't supported in single-world host mode, such as prediction switching, partial ticks, or relevancy filtering on the host connection.

Use a [single-world host](single-world-host-mode.md) instead when the host's CPU and memory budget is tight and you don't need the parity with a dedicated server.

## Add host migration

Binary-worlds host mode supports host migration, which lets a session continue after the host disconnects by promoting another client to host. To add host migration to a binary-world host setup, refer to [Host migration](host-migration/host-migration.md).

## Additional resources

- [Network topologies](network-topologies.md)
- [Single-world host](single-world-host-mode.md)

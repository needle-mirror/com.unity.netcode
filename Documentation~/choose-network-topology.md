# Choose a network topology

Compare the network topologies that Netcode for Entities supports so you can choose the one that fits your game's hosting, performance, and scaling needs.

Netcode for Entities uses a server-authoritative model with client-side prediction, so every session has one authoritative server and one or more clients. The network topology determines where that server runs: on dedicated hardware in a client-server topology, or on one of the players' own machines in a client-hosted topology. Your choice affects hosting cost, how much you trust the host, and how easily you can move to a dedicated server later.

Both topologies build on the same client and server worlds. For background on how those worlds communicate, refer to [Client and server worlds networking model](client-server-worlds.md).

## Client-server topology

In a client-server topology, the authoritative server runs on dedicated hardware that you provision and operate, and players connect to it remotely. You distribute client builds to your players and host one or more server builds yourself, often as dedicated game server (DGS) builds.

Because the server is impartial and isn't tied to any player, this topology suits games that need to scale to many concurrent players, resist cheating, or keep the session running when individual players leave. The trade-off is that you're responsible for provisioning, operating, and paying for server infrastructure.

Use a client-server topology when:

- You're building a competitive game where an impartial, authoritative server is important for fairness and cheat resistance.
- You need sessions to stay available independently of any single player's connection.
- You're prepared to host and operate dedicated server infrastructure.

## Client-hosted topology

In a client-hosted topology, one player's machine runs the authoritative server and also acts as a client for that player. Other players connect to the host remotely, as they would to a dedicated server. This removes the need to provision and pay for dedicated server hardware, at the cost of placing the authoritative simulation on a player-controlled machine. Connecting remote players to a client-hosted server requires a relay server. Refer to [Connect using a relay server](networking-using-relay.md).

Netcode for Entities supports two client-hosted setups that differ in how the host runs its client and server logic: binary-worlds host mode and single-world host mode. To add the ability to transfer the host role to another player when the host leaves, refer to [Host migration](host-migration/host-migration.md).

### Binary-worlds host mode

In binary-worlds host mode, the host's process contains a full client world and a full server world connected through a local intra-process communication (IPC) connection, so the host runs the same code paths as a dedicated server deployment.

This parity makes binary-worlds host mode a good fit when you want local play tests to exercise the same serialization and prediction code as production, or when you plan to ship, or might later ship, a dedicated server build alongside the client-hosted build. It also supports features that single-world host mode doesn't, such as host-side prediction switching, partial ticks, and relevancy on the host connection. For more information, refer to [Binary-worlds host mode](binary-worlds-host-mode.md).

### Single-world host mode

Single-world host mode is the default client-hosted setup. It combines the client and server roles into a single world. Because the host doesn't serialize and deserialize its own state, it avoids the extra work a binary-worlds host mode performs for its local player, which lowers the host's CPU and memory overhead.

This makes single-world host mode a good fit when the host's performance budget is tight and you don't need parity with a dedicated server. In exchange, several features behave differently or aren't supported, including prediction switching, partial ticks, and relevancy on the host connection. For more information, refer to [Single-world host mode](single-world-host-mode.md).

## Choose a topology

The following table summarizes the recommended use cases for each topology:

| **Topology**                        | **Where the server runs**                                   | **Recommended for**                                                                                          |
| :---------------------------------- | :---------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------- |
| Client-server                       | Dedicated hardware you provision and operate.               | Games that require consistent performance and uptime, such as competitive or co-op games that need to scale, resist cheating, or keep sessions available when players leave.        |
| Client-hosted (binary-worlds host mode)    | In a server world on a player's machine, separate to that player's client world. | Client-hosted games that want parity with a dedicated server, or might later ship a dedicated server build.  |
| Client-hosted (single-world host mode)   | In a combined client and server world on a player's machine.           | Client-hosted games where the host's CPU and memory budget is tight and dedicated-server parity isn't needed. |

You're not locked into a single topology for the lifetime of a project. Because binary-worlds host mode mirrors a dedicated server's code paths, you can develop against a client-hosted setup and move to a client-server deployment later with fewer changes.

## Additional resources

- [Network topologies](network-topologies.md)
- [Client-hosted](client-hosted.md)
- [Client and server worlds networking model](client-server-worlds.md)
- [Connect using a relay server](networking-using-relay.md)

# Client-hosted

Run the server on one player's machine, which acts as both the host and a client for the other players in the session.

In a client-hosted topology, one player's process owns the authoritative server simulation and also renders that player as a local client. Other players connect to the host remotely, as they would to a dedicated server. This removes the need to provision and pay for dedicated server hardware, at the cost of placing the authoritative simulation on a player-controlled machine.

Netcode for Entities supports two client-hosted setups that differ in how the host runs its client and server logic. Binary-worlds host mode keeps a full client world and a full server world in the host's process, while single-world host mode combines both roles into one world to reduce CPU and memory overhead. Review the following topics to compare the two and choose the setup that fits your game.

> [!NOTE]
> Single-world host mode is experimental. To enable it, add the `NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST` scripting define symbol to your project.

| **Topic**                                       | **Description**                                                                                                                                              |
| :---------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **[Binary-worlds host mode](binary-worlds-host-mode.md)**     | Configure a host that runs separate client and server worlds connected through intra-process communication (IPC), which keeps gameplay code paths identical to a dedicated server deployment. |
| **[Single-world host mode](single-world-host-mode.md)**   | Configure a host that combines the client and server roles into a single world to reduce the host's CPU and memory overhead.                                     |
| **[Connect using a relay server](networking-using-relay.md)** | Configure the `NetworkDriverStore` so that players can connect to the host through Unity Relay when using a client-hosted setup.                                  |

## Additional resources

- [Network topologies](network-topologies.md)
- [Host migration](host-migration/host-migration.md)

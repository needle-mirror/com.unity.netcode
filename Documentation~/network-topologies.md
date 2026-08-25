# Network topologies

Choose how the authoritative server and connected clients are arranged to suit your game's hosting, latency, and scaling needs.

Netcode for Entities uses a server-authoritative model with client-side prediction, so every session has one authoritative server and one or more clients. The network topology determines where that server runs: on dedicated hardware that clients connect to remotely, or on one of the players' own machines that also acts as a client.

Your choice of topology affects hosting cost, the level of trust you place in the host, and how easily you can move to a dedicated server later. Review the following topics to compare the available options and decide which one fits your game.

| **Topic**                                       | **Description**                                                                                                                                              |
| :---------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **[Choose a network topology](choose-network-topology.md)** | Compare the supported network topologies and their recommended use cases to decide which one fits your game's hosting, performance, and scaling needs. |
| **[Client-server](client-server.md)**           | Run the authoritative simulation on a dedicated server that clients connect to remotely, which suits games that need to scale, resist cheating, or stay impartial between players. |
| **[Client-hosted](client-hosted.md)**           | Run the server on one player's machine, which acts as both host and client for the other players who connect to the session.                                  |

## Additional resources

- [Client and server worlds networking model](client-server-worlds.md)
- [Connect using a relay server](networking-using-relay.md)

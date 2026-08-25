# Client-server

Set up a topology where the authoritative server runs separately from every client, on hardware that you provision and operate.

In a client-server topology, the server is the authoritative source of truth for the game state, and each client connects to it over the network. You distribute client builds to your players and host the server build yourself, typically as a dedicated game server (DGS) build that runs in a server-only [world](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-worlds.html). For details about how these worlds are structured and updated, refer to [Client and server worlds networking model](client-server-worlds.md).

Because the server is impartial and isn't tied to any player, this topology suits games that need to scale to many concurrent players, resist cheating, or keep a session running independently of any single player's connection. The trade-off is that you're responsible for provisioning, operating, and paying for the server infrastructure. For a topology where one player hosts the session instead, refer to [Client-hosted](client-hosted.md).

## Select the client-server roles

How you select the client-server roles depends on whether you're in the Unity Editor or in a build:

- In the Editor, use the **PlayMode Tools** window (**Window** > **Multiplayer** > **PlayMode Tools**) to set the **PlayMode Type**. Select **Client** to create only a client world, **Server** to create only a server world, or **Client & Server** to create one of each. For more information, refer to [PlayMode Tool](testing/playmode-tool.md). Set **Server Emulation** to **Dedicated Server**.
- In a build, the role is fixed by the build target through the `UNITY_SERVER` and `UNITY_CLIENT` defines, which you control in your Project Settings (**Edit** > **Project Settings** > **Multiplayer** > **Build**). A dedicated server build is a server-only build that can only listen for incoming connections, and a client build is a client-only build that can only connect to a server.

## Bootstrap client and server worlds manually

The default [`ClientServerBootstrap`](xref:Unity.NetCode.ClientServerBootstrap) creates the appropriate worlds automatically when your game starts. When you need more control over when worlds are created, for example to delay creation until a player chooses to host or join from a menu, derive from `ClientServerBootstrap` and create the worlds yourself.

The following example overrides the default bootstrap so that no networked worlds are created on startup, then creates a server or client world on demand:

[!code-cs[blobs](../Tests/Editor/DocCodeSamples/client-server.cs#ClientServerSetup)]

After the worlds exist, configure the server world to listen for incoming connections and the client world to connect to the server's address and port. For more information about establishing the connection, refer to [Connecting server and clients](network-connection.md).

Alternatively, you can let the bootstrap connect the worlds for you. If you set `ClientServerBootstrap.AutoConnectPort` to a valid port, any server world that's created listens on that port, and any client world that's created connects to `ClientServerBootstrap.DefaultConnectAddress` on that port.

## How clients and the server communicate

In a client-server topology, the server world is authoritative and each client world connects to it through the network driver:

- The server serializes ghost snapshots and sends them to every connected client.
- Each client deserializes those snapshots, runs prediction and interpolation, and serializes input commands back to the server.
- The server applies the inputs it receives, advances the authoritative simulation, and replicates the results to all clients in the next snapshot.

Because clients only ever receive the server's authoritative state, a single misbehaving or malicious client can't change the simulation for other players, which is what makes this topology resilient to cheating.

## When to use a client-server topology

Use a client-server topology when:

- You're building a competitive game where an impartial, authoritative server is important for fairness and cheat resistance.
- You need sessions to stay available independently of any single player's connection.
- You expect to scale to many concurrent players.
- You're prepared to provision, operate, and pay for dedicated server infrastructure.
- You want to control the quality and performance of the server hardware.

Use a [client-hosted](client-hosted.md) topology instead when you don't want to host dedicated server infrastructure and you're willing to run the authoritative server on a player's machine.

## Additional resources

- [Network topologies](network-topologies.md)
- [Client-hosted](client-hosted.md)
- [Client and server worlds networking model](client-server-worlds.md)
- [Connecting server and clients](network-connection.md)

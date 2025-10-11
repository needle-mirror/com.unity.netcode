# Understanding how NetworkStreamInGame works

After the client has completed the initial handshake with the server and it successfully connected, **they can only exchange RPC messages.**

- No `ghost snapshots` are sent to client
- No `input commands` are sent to server
- Time is not synced (the client is not ahead of the server, nor it does know its current simulated tick)

To enable/disable sendind/receiving commands and ghost snapshot for a given connection it is necessary to add/remove the `NetworkStreamInGame` component to it.

> FUNDAMENTAL: the component should be added on both end: client and server. When the client and the server add the component may be not necessarily in sync
> and can occurs at different point in time.

Once the connection is in `Game` mode, the client and server start exchanging new type of messages: `Commands` and `Ghost Snapshots`.

| Role   | Message Type | direction | frequency                   | in game | not in game |
|--------|--------------|-----------|-----------------------------|---------|-------------|
| CLIENT | Commands     | out       | fixed, simulation tick rate | yes     | no          |
|        | Snapshot     | in        | fixed, network tick rate    | yes     | no          |
|        | RPC          | in/out    | one per frame (variable)    | yes     | yes         |
| SERVER | Snapshot     | out       | fixed, networ tick rate     | yes     | no          |
|        | Commands     | out       | fixed, simulation tick rate | yes     | no          |
|        | RPC          | in/out    | simulation tick rate        | yes     | yes         |

The most common and desired behavior is that client and server add the `NetworkStreamInGame` "in-sync": that it both client and server connection are either `in-game` or not.
However, because of latency and the fact that enabling/disabling streaming typically involve sending an RPC, the connection may be in a different "state"
on the client and server at any given point in time: i.e can be enabled on the client but not on the server or viceversa.

In particular:
- when the client connection does not have the `NetworkStreamInGame` component, all received snapshots are silently discarded. No commands are sent.
- when the server connection does not have the `NetworkStreamInGame` component, all received commands are silently discarded. No snapshot is sent.

There are multiple way to mark a connection in game

### Without message exchange
The `NetworkStreamInGame` is added to the connection right after the client and server are connected, on each side respectively.

> REMARK This is the simplest possible flow, and work fine for most situation. However it can have some downside when it comes to the prefab handling.
That because, based on the timing, the client (especially the local client for self-hosted mode) can start receiving messages from the server before he actually
loaded the scenes.

A decent compromise for this is to wait adding the component on the client until the sub-scene are loaded (i.e requiring some entity).
Any snapshot received by the server is going to be discared, so nothing will really happen.

### Client initiated
The `NetworkStreamInGame` is added first by the client (locally) and the server notified to start streaming the ghost data via RPC.
This is usually a robust flow, because the client has all the context necessary.

### Server initiated
The `NetworkStreamInGame` change is server authoritative: the server tell the client when enable or disable the streaming.
This flow may be not the optimal choice, because he need the server to know when the client is ready to do so. It is a legit
choice to have the server instructing the client to **remove** the component.

### Mixed
- the client to tell the server start sending ghost data (when the client is ready). The client will have the `NetworkStreamInGame` already set, ready to receive data.
- the server to tell the client to remove the `NetworkStreamInGame`, effectively stopping receiving ghost data. The server already remove the component at that time, sparing bandwidth.


## State reset when switching between in-game and not-in-game.

Although the main purpose of adding the `NetworkStreamInGame` is to enable ghost and command transmission, the presence (and absence) of the component
on the connections affect how the Server and Client works.

Many Server and Client systems react to the absence or presence of the `NetworkStreamInGame` effectively operating in two distinct modes:
when there are connections "in-game" and when there are "not".

### Entering the "In-Game" mode
- The GhostCollectionSystem start processing prefabs
- The Pre-spawned ghosts are processed and initialized
- The NetworkTimeSystem start syncing

### Exiting the "In-Game" mode
- The GhostCollectionSystem reset all the data structure
- The Pre-spawned ghosts systems internal reset
- The NetworkTimeSystem date is reset
- All other internal state are also reset to the default (mostly on the client only)

As a result, most of the systems internal state are reset to their default. When the "in-game" mode is entered again,
all the Netcode systems react like it was the first time.

> Rationale: The are multiple reasons behind this choice, mostly historical but some are still relevant:
> - No work should be done when not necessary.
> - Facilitate level switching
> - Avoid desync of pre-spawned ghosts
> - A point of reset for the client, without need to close the connection.

## Use cases
- Level switching / reset
- Network Drivers migration
- Others .. ? TODO add more.


## The Snapshot format


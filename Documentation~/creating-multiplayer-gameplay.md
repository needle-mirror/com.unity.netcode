# Creating multiplayer gameplay

Create multiplayer gameplay in Netcode for Entities.

| **Topic**                       | **Description**                  |
| :------------------------------ | :------------------------------- |
| **[Connecting server and clients](network-connection.md)** | Netcode for Entities uses the [Unity Transport package](https://docs.unity3d.com/Packages/com.unity.transport@latest) to manage connections. It stores each connection as an entity (named 'NetworkConnection [nid]'), and each connection entity has a [NetworkStreamConnection](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamConnection.html) component with the `Transport` handle for the connection. |
| **[Communicating with RPCs](rpcs.md)** | Netcode for Entities uses a limited form of RPCs to handle events. A job on the sending side can issue RPCs, and the RPCs then execute on a job on the receiving side. |
| **[Synchronizing states and inputs](synchronization.md)** | Describes how netcode synchronizes ghost states and inputs/commands, lists our supported types, and denotes how to mark-up fields and components to be replicated via netcode's eventual consistency model. |
| **[Time synchronization](time-synchronization.md)**| Netcode uses a server authoritative model, which means that the server executes a fixed time step based on how much time has passed since the last update. As such, the client needs to match the server time at all times for the model to work. |
| **[Interpolation](interpolation.md)**| Networked games are subject to [latency, jitter and packet loss](https://docs-multiplayer.unity3d.com/netcode/current/learn/lagandpacketloss/) which can in turn impact player's gameplay by making networked objects seem unstable and jittery. A common solution to this is to use buffered [interpolation](https://docs-multiplayer.unity3d.com/netcode/current/learn/clientside_interpolation/), which involves intentionally delaying the playback of received snapshots, and then interpolating between them, which creates a grace period (or window) for snapshot packets to arrive. |
| **[Prediction](prediction.md)**| Use prediction to manage latency in your game. |
| **[Physics](physics.md)**| The Netcode package has some integration with Unity Physics which makes it easier to use physics in a networked game. The integration handles interpolated ghosts with physics, and support for predicted ghosts with physics. |
| **[Ghost types templates](ghost-types-templates.md)**| Use ghost types templates to serialize ghosts. |

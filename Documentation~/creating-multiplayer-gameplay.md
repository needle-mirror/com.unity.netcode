# Create multiplayer gameplay

Create multiplayer gameplay in Netcode for Entities.

| **Topic**                       | **Description**                  |
| :------------------------------ | :------------------------------- |
| **[Connecting server and clients](network-connection.md)** | Netcode for Entities uses the [Unity Transport package](https://docs.unity3d.com/Packages/com.unity.transport@latest) to manage connections. It stores each connection as an entity (named 'NetworkConnection [nid]'), and each connection entity has a [NetworkStreamConnection](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamConnection.html) component with the `Transport` handle for the connection. |
| **[Synchronizing states and inputs](synchronization.md)** | Describes how netcode synchronizes ghost states and inputs/commands, lists our supported types, and denotes how to mark-up fields and components to be replicated via netcode's eventual consistency model. |
| **[Time synchronization](time-synchronization.md)**| Netcode uses a server authoritative model, which means that the server executes a fixed time step based on how much time has passed since the last update. As such, the client needs to match the server time at all times for the model to work. |
| **[Interpolation and extrapolation](interpolation.md)**| Use interpolation and extrapolation in your game to minimize the effects of adverse network conditions on gameplay. |
| **[Prediction](prediction.md)**| Use prediction to manage latency in your game. |
| **[Physics](physics.md)**| The Netcode package has some integration with Unity Physics which makes it easier to use physics in a networked game. The integration handles interpolated ghosts with physics, and support for predicted ghosts with physics. |
| **[Host migration](host-migration/host-migration.md)** | Use host migration to transfer the host role to a client in the same session when the current host leaves. |

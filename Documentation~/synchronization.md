# Synchronizing states and inputs

Use ghosts, commands, and RPCs to synchronize states and inputs between server and clients in your multiplayer game.

| **Topic**                                       | **Description**                               |
|:------------------------------------------------|:----------------------------------------------|
| **[Synchronization with ghosts](ghosts.md)** | Use ghosts to synchronize and replicate states between server and clients in a consistent and customizable way.|
| **[Communication with RPCs](rpcs.md)** | Use remote procedure calls (RPCs) to communicate high-level game flow events and send one-off, non-predicted commands from the client to the server. |
| **[Handling inputs with the command stream](command-stream.md)** | Clients send a continuous command stream to the server when [`NetworkStreamConnection`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamConnection.html) is tagged as in-game. This stream includes all inputs and acknowledgements of the last received snapshot. |

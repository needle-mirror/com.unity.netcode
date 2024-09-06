# Synchronizing states and inputs

| **Topic**                                       | **Description**                               |
|:------------------------------------------------|:----------------------------------------------|
| **[Ghost snapshots and synchronization](ghost-snapshots.md)** | A ghost is a networked object that the server simulates. During every frame, the server sends a snapshot of the current state of all ghosts to the client. |
| **[Ghost spawning](ghost-spawning.md)** | A ghost is spawned by instantiating it on the server, as all ghosts on the server are replicated to all clients automatically. |
| **[Commands](command-stream.md)** | The client sends a continuous command stream to the server when the `NetworkStreamConnection` is tagged to be "in-game". This stream includes all inputs and acknowledgements of the last received snapshot. |

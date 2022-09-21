# Network connection
## NetCode + Unity Transport

The network connection uses the [Unity Transport package](https://docs.unity3d.com/Packages/com.unity.transport@latest) and stores each connection as an entity. Each connection entity has a [NetworkStreamConnection](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamConnection.html) component with the `Transport` handle for the connection. The connection also has a `NetworkStreamDisconnected` component for one frame, after it disconnects and before the entity is destroyed.

To request disconnect, add a `NetworkStreamRequestDisconnect` component to the entity. Direct disconnection through the driver is not supported. Your game can mark a connection as being in-game, with the `NetworkStreamInGame` component. Your game must do this; it is never done automatically.

> [!NOTE]
> Before the component is added to the connection, the client doesn’t send commands, nor does the server send snapshots.

To store commands in the correct buffer when not using auto command target, each connection has a `CommandTargetComponent` which must point to the entity where the received commands need to be stored. Your game is responsible for keeping this entity reference up to date.

Each connection has three incoming buffers for each type of stream, command, RPC, and snapshot. There is also an outgoing buffer for RPCs. Snapshots and commands are gathered and sent in their respective send systems. When the client receives a snapshot it is available in the incoming snapshot buffer. The same method is used for the command stream and the RPC stream.

When your game starts, it must tell the netcode to manually start listening for a connection on the server, or connect to a server from the client. This isn’t done automatically because a default has not been set. To establish a connection, you must get the `NetworkStreamReceiveSystem` from the client World for Connect, and the server World for Listen, and then call either `Connect` or `Listen` on it.

## Network simulation
Unity Transport provides a `SimulatorUtility`, which is available (and configurable) in the NetCode package. Access it via `Multiplayer > PlayMode Tools`. Non-zero values for delay, jitter or packet loss will enable network simulation.

> [!NOTE]
> These simulator settings are applied on a per-packet basis (i.e. each way).

> [!NOTE]
> Enabling network simulation will force the Unity Transport's network interface to be a full UDP socket. Otherwise, we'll use IPC (Inter-Process Communication). See `DefaultDriverConstructor.cs`.

We strongly recommend that you frequently test your gameplay with the simulator enabled, as it more closely resembles real-world conditions.

Network simulation can be enabled (**in development builds only! DotsRuntime is also not supported!**) via the command line argument `--loadNetworkSimulatorJsonFile [optionalJsonFilePath]`. 
Alternatively, `--createNetworkSimulatorJsonFile [optionalJsonFilePath]` can be passed if you want the file to be auto-generated (in the case that it's not found). 
It expects a json file containing `SimulatorUtility.Parameters`.
Passing in either parameter will **always** enable a simulator profile, as we fallback to using the `DefaultSimulatorProfile` if the file is not found (or generated).

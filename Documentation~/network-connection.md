# Connecting server and clients

Netcode for Entities uses the [Unity Transport package](https://docs.unity3d.com/Packages/com.unity.transport@latest) to manage connections and stores each connection as an entity. Each connection entity has a [NetworkStreamConnection](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamConnection.html) component with the `Transport` handle for the connection. When the connection is closed, either because the server disconnected the user or the client requested to disconnect, the entity is destroyed.

To target which entity should receive the player commands, when not using the [`AutoCommandTarget` feature](command-stream.md#automatically-handling-commands-autocommandtarget) or for having more manual control, each connection has a [CommandTarget](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.CommandTarget.html) which must point to the entity where the received commands need to be stored. Your game is responsible for keeping this entity reference up to date.

Your game can mark a connection as being in-game with the `NetworkStreamInGame` component. Your game must do this; it's never done automatically. Before the `NetworkStreamInGame` component is added to the connection, the client does not send commands, nor does the server send snapshots.

To request to disconnect, add a `NetworkStreamRequestDisconnect` component to the entity. Direct disconnection through the driver is not supported.

### Incoming buffers

Each connection can have up to three incoming buffers, one for each type of stream: commands, RPCs, and snapshots (client only).

- [IncomingRpcDataStreamBuffer](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.IncomingRpcDataStreamBuffer.html)
- [IncomingCommandDataStreamBuffer](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.IncomingCommandDataStreamBuffer.html)
- [IncomingSnapshotDataStreamBuffer](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.IncomingSnapshotDataStreamBuffer.html)

When a client receives a snapshot from the server, the message is queued into the buffer and processed later by the [`GhostReceiveSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.IncomingSnapshotDataStreamBuffer.html).
RPCs and commands follow the same principle. The messages are gathered first by the [`NetworkStreamReceiveSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamReceiveSystem.html) and then consumed by the respective RPC and command receive system.

> [!NOTE]
> Server connection does not have an IncomingSnapshotDataStreamBuffer.

### Outgoing buffers

Each connection can have up to two outgoing buffers: one for RPCs and one for commands (client only).

- [OutgoingRpcDataStreamBuffer](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.OutgoingRpcDataStreamBuffer.html)
- [OutgoingCommandDataStreamBuffer](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.OutgoingCommandDataStreamBuffer.html)

When commands are produced, they're first queued into the outgoing buffer, which is flushed by the client at regular intervals (every new tick). RPC messages follow the sample principle: they're gathered initially by their respective send system that encodes them into the buffer. Then, the [RpcSystem](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.OutgoingCommandDataStreamBuffer.html) flushes the RPCs in the queue (by coalescing multiple messages into one maximum transmission unit (MTU)) at regular intervals.

## Connection flow

When your game starts, Netcode for Entities doesn't automatically connect the client to the server, nor makes the server start listening to a specific port. By default, `ClientServerBoostrap` only creates the client and server worlds. It's up to developer to decide how and when the server and client open their communication channel.

There are a number of different options:

- [Manually start listening for a connection on the server, or connect to a server from the client using the `NetworkStreamDriver`.](#manually-listen-or-connect)
- [Automatically connect and listen by using the `AutoConnectPort` (and relative `DefaultConnectAddress`).](#using-the-autoconnectport)
- [Create a `NetworkStreamRequestConnect` and/or `NetworkStreamRequestListen` request in the client and/or server world respectively.](#controlling-the-connection-flow-using-networkstreamrequest)

> [!NOTE]
> Regardless of how you choose to connect to the server, we strongly recommend ensuring `Application.runInBackground` is `true` while connected.
> You can do so by setting `Application.runInBackground = true;` directly, or setting it project-wide via **Project Settings** > **Player** > **Resolution and Presentation**.
> If you don't, your multiplayer game will stall (and likely disconnect) if and when the application loses focus (for example, by the player tabbing out), as netcode will be unable to tick.
> The server should likely always have this enabled.
> We provide error warnings for both via `WarnAboutApplicationRunInBackground`.

### Manually listen or connect

To establish a connection, you must get the [NetworkStreamDriver](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamDriver.html) singleton (present on both client and server worlds) and then call either `Connect` or `Listen` on it.

Refer to the [DOTS samples repository](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/master/NetcodeSamples/Assets/Samples/HelloNetcode/1_Basics/01_BootstrapAndFrontend/Frontend/Frontend.cs#L80) for example code that covers manually listening and connecting.

### Using the `AutoConnectPort`

The `ClientServerBootstrap` [`AutoConnectPort` field](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html#Unity_NetCode_ClientServerBootstrap_AutoConnectPort) contains two special properties that can be used to instruct the server and client to automatically listen and connect respectively when initially set up.

- [DefaultConnectAddress](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html#Unity_NetCode_ClientServerBootstrap_DefaultConnectAddress)
- [DefaultListenAddress](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html#Unity_NetCode_ClientServerBootstrap_DefaultListenAddress)

To set up the `AutoConnectPort`, you need to create a custom [bootstrap](client-server-worlds.md#bootstrap) and set a value other than 0 for the `AutoConnectPort` before creating your worlds. For example:

```c#
public class AutoConnectBootstrap : ClientServerBootstrap
{
    public override bool Initialize(string defaultWorldName)
    {
        // This will enable auto connect.
        AutoConnectPort = 7979;
        // Create the default client and server worlds, depending on build type in a player or the PlayMode Tools in the editor
        CreateDefaultClientServerWorlds();
        return true;
    }
}
```

The server starts listening at the wildcard address (`DefaultListenAddress`:`AutoConnectPort`). The `DefaultConnectAddress` is by default set to `NetworkEndpoint.AnyIpv4`. The client starts connecting to server address (`DefaultConnectAddress`:`AutoConnectPort`). The `DefaultConnectAddress` is by default set to `NetworkEndpoint.Loopback`.

> [!NOTE]
> In the Editor, the [PlayMode tool](playmode-tool.md) allows you to override both the `AutoConnectAddress` and `AutoConnectPort` fields. However, when `AutoConnectPort` is set to 0, the PlayMode Tool's override functionality won't be used. The intent is then you need to manually trigger connection.

### Controlling the connection flow using `NetworkStreamRequest`

Instead of invoking and calling methods on the [NetworkStreamDriver](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamDriver.html) you can instead create:

- A [NetworkStreamRequestConnect](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamRequestConnect.html) singleton to request a connection to the desired server address/port.
- A [NetworkStreamRequestListen](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamRequestListen.html) singleton to make the server start listening at the desired address/port.

```csharp
//On the client world, create a new entity with a NetworkStreamRequestConnect. It will be consumed by NetworkStreamReceiveSystem later.
var connectRequest = clientWorld.EntityManager.CreateEntity(typeof(NetworkStreamRequestConnect));
EntityManager.SetComponentData(connectRequest, new NetworkStreamRequestConnect { Endpoint = serverEndPoint });

//On the server world, create a new entity with a NetworkStreamRequestConnect. It will be consumed by NetworkStreamReceiveSystem later.
var listenRequest = serverWorld.EntityManager.CreateEntity(typeof(NetworkStreamRequestListen));
EntityManager.SetComponentData(listenRequest, new NetworkStreamRequestListen { Endpoint = serverEndPoint });

```

The request will be then consumed at runtime by the [NetworkStreamReceiveSystem](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamReceiveSystem.html).

> [!NOTE]
> If you encounter runtime errors, open the PlayMode Tools window and re-enter Play Mode.
> If worlds exist, then bootstrapping (see above) is creating the worlds automatically.
> If the server world is already listening, and/or the client world already connecting, then auto-connection (see above) is already enabled. You will therefore need to modify your bootstrap to disable auto-connection to support manual connection workflows.

### Network simulator

Unity Transport provides a [SimulatorUtility](playmode-tool.md#networksimulator), which is available (and configurable) in the Netcode for Entities package. Access it via **Multiplayer** > **PlayMode Tools** in the Editor.

We strongly recommend that you frequently test your gameplay with the simulator enabled, as it more closely resembles real-world conditions.

## Listening for client connection events

There is a `public NativeArray<NetCodeConnectionEvent>.ReadOnly ConnectionEventsForTick` collection (via the `NetworkStreamDriver` singleton), allowing you to iterate over (and thus react to) client connection events on the client and server.

These events only persist for a single `SimulationSystemGroup` tick, and are reset during `NetworkStreamConnectSystem` and `NetworkStreamListenSystem` respectively. If your system runs _after_ these aforementioned system's jobs execute, you'll receive notifications on the same tick that they were raised. However, if you query this collection _before_ this system's jobs execute, you'll be iterating over the previous tick's values.

```csharp
// Example System:
[UpdateAfter(typeof(NetworkReceiveSystemGroup))]
[BurstCompile]
public partial struct NetCodeConnectionEventListener : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var connectionEventsForClient = SystemAPI.GetSingleton<NetworkStreamDriver>().ConnectionEventsForTick;
        foreach (var evt in connectionEventsForClient)
        {
            UnityEngine.Debug.Log($"[{state.WorldUnmanaged.Name}] {evt.ToFixedString()}!");
        }
    }
}
```

> [!NOTE]
> Because the server runs on a fixed delta-time, the `SimulationSystemGroup` may tick any number of times (including zero times) on each render frame.
> Because of this, `ConnectionEventsForTick` is only valid to be read in a system running inside the `SimulationSystemGroup`.
> For example, trying to access it outside the `SimulationSystemGroup` can lead to a) either **_only_** seeing events for the current tick (meaning you miss events for previous ticks) or b) receiving events multiple times, if the simulation doesn't tick on this render frame.
> Therefore, do not access `ConnectionEventsForTick` inside the `InitializationSystemGroup`, nor inside the `PresentationSystemGroup`, nor inside any `MonoBehaviour` Unity method (non-exhaustive list!).

### NetCodeConnectionEvents on the client

| Connection status | Invocation rules                                                                                                                                                                                                                                                                                                                            |
|-------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Unknown`         | Never raised.                                                                                                                                                                                                                                                                                                                               |
| `Connecting`      | Raised once for your own client, once the `NetworkStreamReceiveSystem` registers your `Connect` call (which may be one frame after you call `Connect`).                                                                                                                                                                                     |
| `Handshake`       | Raised once for your own client, once your client has entered the internal transport driver `Connected` state.<br/>_The client must now wait for Netcode's own automatic handshake process to complete (see `NetworkProtocolVersion` and `RequestProtocolVersionHandshake`), which typically takes only a few ticks (it's ping dependent)._ |
| `Approval`        | Raised once for your own client, but only when [Connection Approval](#connection-approval) is enabled. Appears after successfully handshaking with the server.<br/>_Therefore, note that enabling `Approval` will cause clients to take a few frames longer to connect to the server._                                                      |
| `Connected`       | Raised once for your own client, once the server sends you your `NetworkId`.                                                                                                                                                                                                                                                                |
| `Disconnected`    | Raised once for your own client, once you disconnect from/timeout from/are disconnected by the server. The `DisconnectReason` will be set.                                                                                                                                                                                                  |

> [!NOTE]
> Clients do **_not_** receive events for other clients. Any events raised in a client world will only be for its own client connection.

> [!NOTE]
> The `Handshake` and `Approval` steps can fail, and thus have a timeout of `ClientServerTickRate.HandshakeApprovalTimeoutMS` (default: 5000ms).

### NetCodeConnectionEvents on the server

| Connection Status | Invocation Rules                                                                                                                                                                                                                                                                                          |
|-------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Unknown`         | Never raised.                                                                                                                                                                                                                                                                                             |
| `Connecting`      | Never raised on the server, because the server doesn't know when a client begins to connect.                                                                                                                                                                                                              |
| `Handshake`       | Raised once for every client, and entered as soon as the server's listening driver accepts your transport connection. The server will be in this state until `NetworkProtocolVersion` information has been to be exchanged.<br/>Note: As of 1.3, handshaking is no longer instantaneous, and can timeout. |
| `Approval`        | Raised once for every client, but only when approval flow is enabled. Appears after netcode's internal handshaking process succeeds (i.e. on the tick the client would be considered `Connected`, if not requiring approval). See `NetworkStreamDriver.RequireConnectionApproval`.                        |
| `Connected`       | Raised once for every accepted client, on the frame the server accepts the connection (i.e. assigns said client a `NetworkId`, after `Handshake`, and after `Approval` (if enabled)).                                                                                                                     |
| `Disconnected`    | Raised once for every accepted client which then disconnects, on the frame we receive the disconnect event or state. The `DisconnectReason` will be set.                                                                                                                                                  |

> [!NOTE]
> The server does not raise any events when it successfully `Binds`, nor when it begins to `Listen`. Use existing APIs to query these statuses.

## Connection approval

You can optionally require connection approval for every client connection on the server. Approval should be used to validate connections attempting to connect with this server, for the purposes of player convenience (whitelists & blacklists, password protected servers etc) and validation (user must pass a secret token - received by the matchmaking response - to ensure that only matchmade players may join this server).

When connection approval is enabled, the following changes apply:
* Clients can only send `IApprovalRpcCommand` RPCs to the server for processing during the `Handshake` and `Approval` phases.
* All clients move from the `Handshake` state to the `Approval` state, rather than directly to `Connected`.
* The server must manually approve each connection by adding the `ConnectionApproved` component to its connection entity.
* The `NetworkId` is only assigned after connection approval succeeds. If approval is denied, the client is disconnected.
* The approval process has a timeout (see [ClientServerTickRate.HandshakeApprovalTimeoutMS](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.HandshakeApprovalTimeoutMS.html)), and therefore, may do so.

To reiterate: During the `Handshake` and `Approval` phases, a client may send multiple RPCs, as long as each is of the `IApprovalRpcCommand` RPC type. These RPC payloads can contain authentication tokens, player identities, or anything required to verify that the client is allowed to continue. To approve a connection, the server needs to add a `ConnectionApproved` component to the network connection entity and the connection flow will continue.

The `NetworkStreamDriver` has a `RequireConnectionApproval` field which must be set to true on both client and server for proper connection flow.

Enabling connection approval is done like this:

```csharp
if (isServer)
{
    using var drvQuery = server.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.RequireConnectionApproval = true;
drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(ep);
}
else
{
    using var drvQuery = client.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
    drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.RequireConnectionApproval = true;
    drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(client.EntityManager, ep);
}
```

And connection approval handling could be set up like this:

```csharp
// The approval RPC, here it contains a hypothetical payload the server will validate
public struct ApprovalFlow : IApprovalRpcCommand
{
    public FixedString512Bytes Payload;
}

// This is used to indicate we've already sent an approval RPC and don't need to do so again
public struct ApprovalStarted : IComponentData
{
}

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
public partial struct ClientConnectionApprovalSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<RpcCollection>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);
        // Check connections which have not yet fully connected and send connection approval message
        foreach (var (connection, entity) in SystemAPI.Query<RefRW<NetworkStreamConnection>>().WithNone<NetworkId>().WithNone<ApprovalStarted>().WithEntityAccess())
        {
            var sendApprovalMsg = ecb.CreateEntity();
            ecb.AddComponent(sendApprovalMsg, new ApprovalFlow { Payload = "ABC" });
            ecb.AddComponent<SendRpcCommandRequest>(sendApprovalMsg);

            ecb.AddComponent<ApprovalStarted>(entity);
        }
        ecb.Playback(state.EntityManager);
    }
}

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerConnectionApprovalSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);
        // Check connections which have not yet fully connected and send connection approval message
        foreach (var (receiveRpc, approvalMsg, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>,RefRW<ApprovalFlow>>().WithEntityAccess())
        {
            var connectionEntity = receiveRpc.ValueRO.SourceConnection;
            if (approvalMsg.ValueRO.Payload.Equals("ABC"))
            {
                ecb.AddComponent<ConnectionApproved>(connectionEntity);

                // Destroy RPC message
                ecb.DestroyEntity(entity);
            }
            else
            {
                // Failed approval messages should be disconnected
                ecb.AddComponent<NetworkStreamRequestDisconnect>(connectionEntity);
            }
        }
        ecb.Playback(state.EntityManager);
    }
}
```

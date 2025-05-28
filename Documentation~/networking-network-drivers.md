# Netcode for Entities multi-driver architecture

Netcode for Entities has a multi-driver architecture, allowing you to use multiple [`NetworkDriver`s](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.NetworkDriver.html), stored in the [`NetworkDriverStore`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkDriverStore.html), at the same time.

`NetworkDriver` configuration is designed to be customizable and is implemented using a delegate/strategy pattern. Netcode for Entities provides a [default strategy implementation](#default-driver-setup) that can be changed by creating your own custom strategy class that implements the [`INetworkStreamDriverConstructor`](https://docs.unity3d.com/Packages/com.unity.netcode.adapter.utp@latest?subfolder=/api/Unity.Netcode.INetworkStreamDriverConstructor.html) interface.

The most common use cases for implementing a custom initialization strategy, or [resetting the `NetworkDriverStore`](#resetting-the-networkdriverstore-setup), are usually:

- Using [Unity Relay](networking-using-relay.md) for self-hosting and letting remote players connect to a local machine.
- Listening to all interfaces on the server-side (to support connecting web and standalone).
- Configuring the driver to use DTLS, as shown in the [secure connection sample](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/2_Intermediate/08_SecureConnection).

> [!NOTE]
> When using web as a platform, Relay must be used to start listening for incoming
> connections. Relay is not required to connect your game to a deployed server using WebSocket.

## `NetworkDriverStore`

The [`NetworkDriverStore`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkDriverStore.html) struct stores `NetworkDriver` instances and, by default, is automatically configured by [`NetworkStreamReceiveSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamReceiveSystem.html) at world creation time.

`NetworkDriverStore` allows up to three drivers to be used, each using a different [`INetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.INetworkInterface.html). While it's possible to listen or connect to different addresses at the same time, the `NetworkDriverStore` interface limits the options to the most common use cases that Netcode for Entities is designed for:

- The server can listen from multiple `NetworkDriver`s, commonly listening to the same server port.
- The client is primarily designed to use only a single `NetworkDriver` and connection.

## Default driver setup

Netcode for Entities provides a default `NetworkDriver` setup, implemented by the [`IPCAndSocketDriverConstructor`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IPCAndSocketDriverConstructor.html). The driver setup is different for each world type and depends on the [PlayMode Tool](playmode-tool.md) settings.

### Default server configuration

For the server world, the `NetworkDriverStore` contains multiple drivers to listen for incoming connections:

| Slot   | Interface                                                            | Description                                                                      |
|--------|----------------------------------------------------------------------|----------------------------------------------------------------------------------|
| Slot 1 | [`IPCNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.IPCNetworkInterface.html)                                                  | Used to connect local client instances directly to the server for self-hosting.   |
| Slot 2 | [`UDPNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.UDPNetworkInterface.html) (standalone)<br/>[`WebsocketNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.WebSocketNetworkInterface.html) (web) | For accepting external connections.                                               |

### Default client configuration

For the client world, the `NetworkDriverStore` always uses a single `NetworkDriver`, but the interface used depends on the [PlayMode Tool](playmode-tool.md) settings.

| Mode          | Network Emulator |                                                                        |
|---------------|------------------|------------------------------------------------------------------------|
| Client        | On/Off           | [`UDPNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.UDPNetworkInterface.html) (standalone)<br/>[`WebsocketNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.WebSocketNetworkInterface.html) (web)   |
| Client/Server | Off              | [`IPCNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.IPCNetworkInterface.html)                                                    |
| Client/Server | On               | [`UDPNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.UDPNetworkInterface.html) (standalone)<br/>[`WebsocketNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.WebSocketNetworkInterface.html) (web)   |                                                   |

When a game runs in Client/Server mode, the client can connect to the local server in two different ways, depending on whether the Network Emulator is turned on or off.

#### Self-hosting scenario

When the Network Emulator is turned off, Netcode for Entities assumes a self-hosting scenario and the `IPCNetworkInteface` is used
to connect to the server. When using this interface, the client optimizes its prediction loop to ensure that:

- At most one prediction tick is done every frame.
- Packet loss, jitter, and round trip time (RTT) are assumed to be 0 (for time synchronization purposes).

#### Client connection emulation

When the Network Emulator is turned on, Netcode for Entities sets up the client driver to use the `WebsocketNetworkInterface`, to simulate a client
connecting to a remote server (even though the server is locally located on the same machine). This modality is used mostly for testing
network conditions.

## Customize network driver creation

You can customize how the `NetworkDriverStore` is set up at world creation by creating a class implementing the `INetworkStreamDriverConstructor`.

The `DefaultDriverBuilder` class provides a set of utility methods that can be used to help create and initialize the drivers required by Netcode for Entities.

```csharp

public class MyCustomDriverConstructor : INetworkStreamDriverConstructor

    public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        var settings = DefaultDriverBuilder.GetClientNetworkSettings();
        #if !UNITY_WEBGL
        DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
        #else
        DefaultDriverBuilder.RegisterClientWebSocketDriver(world, ref driverStore, netDebug, settings);
        #endif
    }

        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            var settings = DefaultDriverBuilder.GetNetworkServerSettings();
#if !UNITY_WEBGL
            DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, relaySettings);
#else
            DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, relaySettings);
#endif
        }
    }
}
```

To use a custom strategy, you need to assign a new instance of your class to the `NetworkStreamReceiveSystem.DriverConstructor` static property before the worlds are created.

```csharp
var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
//The try/finally pattern can be used to avoid any exceptions resetting back to the old default.
try
{
    NetworkStreamReceiveSystem.DriverConstructor = new MyCustomDriverConstructor();
    var server = ClientServerBootstrap.CreateServerWorld("ServerWorld");
    var client = ClientServerBootstrap.CreateClientWorld("ClientWorld");
}
finally
{
    NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;
}
```


> [!NOTE]
> The `DefaultDriverConstructor.RegisterServerDriver` and `DefaultDriverConstructor.RegisterClientDriver` methods implement
> the default strategy for both server and client. For the latter in particular, the Network Emulator used has can connect
> via IPC or Socket. When creating a custom driver constructor, it's recommended to always use the interface-specific builder methods (i.e. `RegisterServerUdpDriver`)
> to control exactly the interfaces you want.

## Reset the `NetworkDriverStore` setup

You can change the current `NetworkDriverStore` drivers setup after world creation using the `NetworkStreamDriver.ResetDriverStore` method.

A new `NetworkDriverStore` instance must be created, manually configured, and passed as an argument to the reset method.

> [!NOTE]
> Resetting can't be done if there are live connections. Drivers can be reset only when no `NetworkStreamConnection` exists in the world.

Resetting the `NetworkDriverStore` instead of using a custom driver constructor can be useful or preferred in some cases. One
of the most common use cases is when connecting or listening requires an asynchronous setup, such as when [using relay](configure-drivers-for-relay.md).

Another common scenario is when using [thin clients](thin-clients.md), if asynchronous connection logic is required and thus not supported natively by the Play Mode Tool (because it requires a synchronous connection setup).

### Reset the driver store

There are multiple ways to reset the driver store:

- You can use an `INetworkDriverConstructor` instance to delegate the creation of the drivers.

```csharp
var driverStore = new NetworkDriverStore();
var clientWorld = ClientServerBootstrap.ClientWorld;
var netDebug = ClientServerBootstrap.ClientWorld.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
// you can use any constructor to initialize the store
NetworkStreamReceiveSystem.DriverConstructor.CreateClientDriver(m_ClientWorld, ref driverStore, netDebug);
var networkStreamDriver = ClientServerBootstrap.ClientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
networkStreamDriver.ResetDriverStore(ClientServerBootstrap.ServerWorld, ref driverStore);
```

- You can manually populate them directly.

```csharp
var driverStore = new NetworkDriverStore();
var netDebug = SystemAPI.Query<NetDebug>();
var settings = DefaultDriverBuilder.GetServerNetworkSettings();
var netDebug = ClientServerBootstrap.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
//register some drivers
DefaultDriverBuilder.RegisterServerIpcDriver(ClientServerBootstrap.ServerWorld, ref driverStore, netDebug, settings);
DefaultDriverBuilder.RegisterServerUpdDriver(ClientServerBootstrap.ServerWorld, ref driverStore, netDebug, settings);
//reset
var networkStreamDriver = ClientServerBootstrap.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
networkStreamDriver.ResetDriverStore(ClientServerBootstrap.ServerWorld, ref driverStore);
```

## Additional resources

* [Use Unity Relay with Netcode for Entities](networking-using-relay.md)

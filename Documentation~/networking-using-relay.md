# Use Unity Relay with Netcode for Entities

Using Unity Relay is required to connect players when using a self-hosted architecture with no dedicated server deployment.

Netcode for Entities' default driver setup isn't configured to connect using Relay out of the box, and it's the user's responsibility to configure the `NetworkDriverStore` appropriately in this case.

This page assumes familiarity with Relay and that necessary data has been already obtained. Please refer to the [Relay documentation](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction) for more detailed information and code examples.

## Configure `NetworkDriverStore` to use Relay

There are two possible strategies for configuring the `NetworkDriverStore` to use Relay:

* By using a [custom driver constructor](networking-network-drivers.md#customize-network-driver-creation).
    * The Netcode for Entities [Relay sample](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/1_Basics/01b_RelaySupport) show how to use this setup.
* By [resetting the driver store](networking-network-drivers.md#reset-the-NetworkDriverStore-setup) after world creation.

For the former, the set up and connection to the Relay service, the allocation, and the relative join-code must be obtained before the worlds are created.

It's recommended that you establish and set up all your services connections and perform any other service-related operations that don't require a live connection before creating the client and server worlds, for the following reasons:

* It makes your workflows more contextual.
* In case of errors, there's less world creation and disposal to do.

However, your scenario may be different, and there is no strict limitation imposed. Client and server worlds can be created and disposed at any time.

## Set up the driver using a custom `INetworkDriverConstructor`

You can create a very simple driver constructor that initializes the `NetworkSettings` using the Relay data and passes it to the `NetworkStreamReceiveSystem.DriverConstructur`.

The following example shows how to set up the driver to support both a local IPC connection (for self-hosting) or Relay (connecting to either a remote or local server via Relay).

```csharp
/// <summary>
/// Register client and server using Relay server settings.
/// For the client, if the Relay settings are not set and the modality is `Client/Server`, it will
/// try to setup the driver using IPCNetworkInterface.
/// </summary>
public class RelayDriverConstructor : INetworkStreamDriverConstructor
{
    RelayServerData m_RelayClientData;
    RelayServerData m_RelayServerData;

    public RelayDriverConstructor(RelayServerData serverData, RelayServerData clientData)
    {
        m_RelayServerData = serverData;
        m_RelayClientData = clientData;
    }

    /// <summary>
    /// This method will ensure that we register different driver types based on the Relay settings
    /// settings.
    /// <para>
    /// Mode          |  Relay Settings
    /// Client/Server |  Valid -> use Relay to connect to local server
    ///                  Invalid -> use IPC to connect to local server
    /// Client        |  Always use Relay. Expect data to be valid, or exceptions are thrown by Transport.
    /// <para>
    /// <para>
    /// For WebGL, WebSocket is always preferred for client in the Editor, to closely emulate the player behaviour.
    /// </para>
    /// </summary>
    public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        var settings = DefaultDriverBuilder.GetNetworkClientSettings();
        //if the Relay data is not valid, connect via local IPC
        if(ClientServerBootstrap.RequestedPlayType == ClientServerBootstrap.PlayType.ClientAndServer &&
           !m_RelayClientData.Endpoint.IsValid)
        {
            DefaultDriverBuilder.RegisterClientIpcDriver(world, ref driverStore, netDebug, settings);
        }
        else
        {
            settings.WithRelayParameters(ref m_RelayClientData);
#if !UNITY_WEBGL
            DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
#else
            DefaultDriverBuilder.RegisterClientWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
        }
    }

    public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        //The first driver is the IPC for internal client/server connection if necessary.
        // IPC can't use Relay and needs to be set up without Relay data.
        var ipcSettings = DefaultDriverBuilder.GetNetworkServerSettings();
        DefaultDriverBuilder.RegisterServerIpcDriver(world, ref driverStore, netDebug, ipcSettings);
        var relaySettings = DefaultDriverBuilder.GetNetworkServerSettings();
        //The other driver (still the same port) is going to listen using Relay for external connections.
        relaySettings.WithRelayParameters(ref m_RelayServerData);
#if !UNITY_WEBGL
        DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, relaySettings);
#else
        DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, relaySettings);
#endif
    }
}
```

## Set up the driver using `NetworkStreamDriverReset`

Resetting the driver store is very similar to the previous example. The only difference is that the initialization can be performed after world creation.

```csharp
public void SetupClientWorld(World world, in RelayData relay)
{
    //we assume here we want to forcibly use Relay
    var settings = DefaultDriverBuilder.GetNetworkClientSettings();
    settings.WithRelayParameters(ref m_RelayClientData);
    var netDebug = world.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
    var driverStore = new NetworkDriverStore();
    DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
    var networkStreamDriver = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
    networkStreamDriver.ResetDriverStore(world, ref driverStore);
}

public void SetupServerWorld(World world, in RelayData relay)
{
    var driverStore = new NetworkDriverStore();
    var netDebug = world.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
    var ipcSettings = DefaultDriverBuilder.GetNetworkServerSettings();
    DefaultDriverBuilder.RegisterServerIpcDriver(world, ref driverStore, netDebug, ipcSettings);
    var relaySettings = DefaultDriverBuilder.GetNetworkServerSettings();
    //The other driver (still the same port) is going to listen using relay for external conections.
    relaySettings.WithRelayParameters(ref m_RelayServerData);
    DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, relaySettings);
#else
    DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, relaySettings);
#endif
    var networkStreamDriver = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
    networkStreamDriver.ResetDriverStore(world, ref driverStore);
}

```

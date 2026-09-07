# Connect using a relay server

Configure the `NetworkDriverStore` so that players can connect through Unity Relay when using a client-hosted setup.

When you use a [client-hosted](client-hosted.md) architecture with no dedicated server deployment, players can't connect to the host directly, so they connect through a relay server instead. Unity Relay provides this connection, but the default Netcode for Entities driver setup isn't configured to use it out of the box. To enable Relay, configure the `NetworkDriverStore` yourself.

You can configure the `NetworkDriverStore` either before or after the client and server worlds are created. It's recommended that you establish your service connections and perform any service-related operations that don't require a live connection before creating the client and server worlds, because it makes your workflows more contextual and leaves less world creation and disposal to undo if an error occurs. This isn't a strict requirement: you can create and dispose of client and server worlds at any time.

This page assumes that you're familiar with Unity Relay. For background information and code examples, refer to the [Relay documentation](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction).

## Prerequisites

Before you configure the `NetworkDriverStore`, complete the following:

- Set up and connect to the Relay service, create an allocation, and obtain the Relay server data and the corresponding join code. For instructions, refer to the [Relay documentation](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction).

## Choose how to configure the driver

There are two strategies for configuring the `NetworkDriverStore` to use Relay, which differ only in when you apply the configuration:

- Use a custom driver constructor to configure Relay before the worlds are created. Use this strategy when you obtain the Relay allocation and join code before world creation.
- Reset the driver store to configure Relay after the worlds are created. Use this strategy when your Relay data isn't available until after the worlds exist.

### Set up the driver with a custom INetworkDriverConstructor

Use an [`INetworkStreamDriverConstructor`](xref:Unity.Netcode.INetworkStreamDriverConstructor) to initialize the `NetworkSettings` with the Relay data before the worlds are created. For more information about custom driver constructors, refer to [Customize network driver creation](networking-network-drivers.md#customize-network-driver-creation).

To configure Relay before the client and server worlds are created:

1. Set up the Relay service and obtain the Relay server data for the client and server.
2. Create a driver constructor that initializes the `NetworkSettings` with the Relay data and passes it to `NetworkStreamReceiveSystem.DriverConstructor`. The following example supports both a local IPC connection for self-hosting and a Relay connection for remote or local clients:

    [!code-cs[blobs](../Tests/Editor/DocCodeSamples/networking-using-relay.cs#RelayConstructor)]

3. Assign the constructor to `NetworkStreamReceiveSystem.DriverConstructor` before the bootstrap creates the worlds.

After the worlds are created, the host listens for remote clients through Relay, and clients connect through Relay or through a local IPC connection when self-hosting. For a complete example, refer to the [Relay sample](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/1_Basics/01b_RelaySupport).

### Set up the driver by resetting the NetworkDriverStore

Reset the driver store to apply the same Relay configuration after the worlds already exist. This strategy is almost identical to using a custom driver constructor; the difference is that you perform the initialization after world creation. For more information, refer to [Reset the `NetworkDriverStore` setup](networking-network-drivers.md#reset-the-networkdriverstore-setup).

To configure Relay after the worlds are created:

1. Create a new `NetworkDriverStore` and register the client or server drivers with the Relay server data.
2. Call `NetworkStreamDriver.ResetDriverStore` on the relevant world to apply the new driver store. The following example resets the driver store for the client and server worlds:

    [!code-cs[blobs](../Tests/Editor/DocCodeSamples/networking-using-relay.cs#SetupWorlds)]

After you reset the driver store, the world uses the Relay drivers for subsequent connections.

## Additional resources

- [Netcode for Entities multi-driver architecture](networking-network-drivers.md)
- [Client-hosted](client-hosted.md)
- [Relay documentation](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction)
- [Relay sample](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/1_Basics/01b_RelaySupport)

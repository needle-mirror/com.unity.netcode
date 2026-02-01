# Use Unity Relay with Netcode for Entities

Using Unity Relay is required to connect players when using a self-hosted architecture with no dedicated server deployment.

Netcode for Entities' default driver setup isn't configured to connect using Relay out of the box, and it's the user's responsibility to configure the `NetworkDriverStore` appropriately in this case.

This page assumes familiarity with Relay and that necessary data has been already obtained. Please refer to the [Relay documentation](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction) for more detailed information and code examples.

## Configure `NetworkDriverStore` to use Relay

There are two possible strategies for configuring the `NetworkDriverStore` to use Relay:

* By using a [custom driver constructor](networking-network-drivers.md#customize-network-driver-creation).
    * The Netcode for Entities [Relay sample](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/1_Basics/01b_RelaySupport) show how to use this setup.
* By [resetting the driver store](networking-network-drivers.md#reset-the-networkdriverstore-setup) after world creation.

For the former, the set up and connection to the Relay service, the allocation, and the relative join-code must be obtained before the worlds are created.

It's recommended that you establish and set up all your services connections and perform any other service-related operations that don't require a live connection before creating the client and server worlds, for the following reasons:

* It makes your workflows more contextual.
* In case of errors, there's less world creation and disposal to do.

However, your scenario may be different, and there is no strict limitation imposed. Client and server worlds can be created and disposed at any time.

## Set up the driver using a custom `INetworkDriverConstructor`

You can create a very simple driver constructor that initializes the `NetworkSettings` using the Relay data and passes it to the `NetworkStreamReceiveSystem.DriverConstructur`.

The following example shows how to set up the driver to support both a local IPC connection (for self-hosting) or Relay (connecting to either a remote or local server via Relay).

[!code-cs[blobs](../Tests/Editor/DocCodeSamples/networking-using-relay.cs#RelayConstructor)]

## Set up the driver using `NetworkStreamDriverReset`

Resetting the driver store is very similar to the previous example. The only difference is that the initialization can be performed after world creation.

[!code-cs[blobs](../Tests/Editor/DocCodeSamples/networking-using-relay.cs#SetupWorlds)]

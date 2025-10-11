# Thin clients

Use thin clients to test and debug in the Editor by running simplified simulated clients alongside your normal client and server worlds.

Thin clients are heavily stripped down and should run as little logic as possible so that they don't put a heavy load on the CPU while testing. Each additional thin client adds extra work to be computed each frame.

## Using thin clients

Only systems that have been explicitly marked with the [`WorldSystemFilterFlags.ThinClientSimulation`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.WorldSystemFilterFlags.html) flag will run on thin client worlds. No rendering is done for thin client data, so they are invisible to the presentation.

In some cases, you might need to check if your system logic should be running for thin clients, and then early out or cancel processing. The `World.IsThinClient()` extension methods can be used in these cases, and note that `World.IsClient` returns true for both thin and full clients.

## Thin client workflow recommendations

Thin clients can be used in a variety of ways to help test multiplayer games. The following use cases are recommended:

* Use thin clients to quickly test client flows such as team assignment, spawn locations, and leaderboards.
* Use thin clients to create builds with large numbers of simulated players, allowing stress and soak testing of your game servers. For example, you can add a configuration option to automatically create `n` thin client worlds (alongside your normal client world) and have each thin client "follow the leader" and automatically attempt to join the same IP address and port as your main client world. Thus, you can use your existing UI flows to get these thin clients into the stress test target server.
* Use thin clients controlled by a second input source. Multiplayer games often have complex PvP interactions, and therefore you often wish to have an AI perform a specific action while your client is interacting with it. For example: crouch, go prone, jump, run diagonally backwards, reload, enable shield, activate ability, and so on. Hooking thin client controls up to keyboard commands allows you to test these situations without requiring a play-test (or a second developer). You can also use thin clients to mirror inputs of the tester, with similarly good results.

## Set up inputs for thin clients

Thin clients don't work with [`AutoCommandTarget`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.AutoCommandTarget.html) by default, because `AutoCommandTarget` requires the same ghost to exist on both the client and the server, and thin clients don't create ghosts. So you need to set up the [`CommandTarget`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.CommandTarget.html) component on the connection entity yourself.

[`IInputComponentData`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IInputComponentData.html) is the primary input API. It automatically handles writing inputs (from your input struct) directly to the replicated dynamic buffer. When a ghost entity that contains an `IInputCommandData` composed struct is baked, an [`ICommandData`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ICommandData.html) dynamic buffer is automatically added to the entity. However, this baking process is not available on thin clients, because thin clients don't create ghosts entities.

`ICommandData` is also supported with thin clients ([details here](../command-stream.md)), but you'll need to perform the same thin client set up work described below as with `IInputComponentData`.

Therefore, to support sending input from a thin client, you must do the following:

1. Create an entity containing your `IInputCommmandData` (or `ICommandData`) component, as well as the code-generated `YourNamespace.YouCommandNameInputBufferData` dynamic buffer. This may appear to throw a missing assembly definition error in your IDE, but it will work.
2. Set up the `CommandTarget` component to point to this entity. In a `[WorldSystemFilter(WorldSystemFilterFlags.ThinClientSimulation)]` system:
```c#
    var myDummyGhostCharacterControllerEntity = entityManager.CreateEntity(typeof(MyNamespace.MyInputComponent), typeof(InputBufferData<MyNamespace.MyInputComponent>));
    var myConnectionEntity = SystemAPI.GetSingletonEntity<NetworkId>();
    entityManager.SetComponentData(myConnectionEntity, new CommandTarget { targetEntity = myDummyGhostCharacterControllerEntity }); // This tells Netcode for Entities which entity it should be sending inputs for.
```
3. On the server (where you spawn the actual character controller ghost for the thin client, which will be replicated to all proper clients), you only need to setup the `CommandTarget` for thin clients (assuming your player ghosts all use `AutoCommandTarget`. If you're not using `AutoCommandTarget`, you probably already perform this action for all clients already).
```c#
    entityManager.SetComponentData(thinClientConnectionEntity, new CommandTarget { targetEntity = thinClientsCharacterControllerGhostEntity });
```

## Thin client samples

- [NetcodeSamples > HelloNetcode > ThinClient](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/2_Intermediate/06_ThinClients)
- [NetcodeSamples > Asteroids](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/f22bb949b3865c68d5fc588a6e8d032096dc788a/NetcodeSamples/Assets/Samples/Asteroids/Client/Systems/InputSystem.cs#L66)

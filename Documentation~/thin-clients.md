# Testing with thin clients

Thin clients are a tool to help test and debug in the Editor by running simulated dummy clients alongside your normal client and server worlds.

These clients are heavily stripped down, and should run as little logic as possible (so they don't put a heavy load on the CPU while testing).
Each thin client added adds a little bit of extra work to be computed each frame.

Only systems which have explicitly been set up to run on thin client worlds will run, marked with the `WorldSystemFilterFlags.ThinClientSimulation` flag on the `WorldSystemFilter` attribute.
No rendering is done for thin client data, so they are invisible to the presentation.

In some cases, you might need to check if your system logic should be running for thin clients, and then early out or cancel processing.
The `World.IsThinClient()` extension methods can be used in these cases, and note that `World.IsClient` returns true for both thin and full clients.

## Thin client workflow recommendations

Thin clients can be used in a variety of ways to help test multiplayer games. We recommend the following:

* Thin clients allow you to quickly test client flows, things like team assignment, spawn locations, leaderboards, UI etc.
* Thin clients created in built players, allowing stress and soak testing of your game servers. For example, you may wish to add a configuration option to automatically create `n` thin client worlds (alongside your normal client world). Have each thin client "follow the leader" and automatically attempt to join the same IP address and port as your main client world. Thus, you can use your existing UI flows (matchmaking, lobby, relay etc.) to get these thin clients into the stress test target server.
* Thin clients controlled by a second input source. Multiplayer games often have complex PvP interactions, and therefore you often wish to have an AI perform a specific action while your client is interacting with it. Examples: crouch, go prone, jump, run diagonally backwards, reload, enable shield, activate ability etc. Hooking thin client controls up to keyboard commands allows you to test these situations without requiring a play-test (or a second dev). You can also hookup thin clients to have mirrored inputs of the tester, with similarly good results.

## Thin client samples

- [NetcodeSamples > HelloNetcode > ThinClient](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/2_Intermediate/06_ThinClients)
- [NetcodeSamples > Asteroids](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/f22bb949b3865c68d5fc588a6e8d032096dc788a/NetcodeSamples/Assets/Samples/Asteroids/Client/Systems/InputSystem.cs#L66)

## Setting up inputs for thin clients

Thin clients don't work out of the box with `AutoCommandTarget`, because `AutoCommandTarget` requires the same ghost to exist on both the client and the server, and thin clients don't create ghosts. So you need to set up the `CommandTarget` component on the connection entity yourself.

`IInputComponentData` is the newest input API. It automatically handles writing out inputs (from your input struct) directly to the replicated dynamic buffer.
Additionally, when we bake the ghost entity - and said entity contains an `IInputCommandData` composed struct - we automatically add an underlying `ICommandData` dynamic buffer to the entity.
However, this baking process is not available on thin clients, as thin clients do not create ghosts entities.

`ICommandData` is also supported with thin clients ([details here](command-stream.md)), but note that you'll need to perform the same thin client hookup work (below) that you do with `IInputComponentData`.

Therefore, to support sending input from a thin client, you must do the following:

1. Create an entity containing your `IInputCommmandData` (or `ICommandData`) component, as well as the code-generated `YourNamespace.YouCommandNameInputBufferData` dynamic buffer. **This may appear to throw a missing assembly definition error in your IDE, but it will work.**
1. Set up the `CommandTarget` component to point to this entity. Therefore, in a `[WorldSystemFilter(WorldSystemFilterFlags.ThinClientSimulation)]` system:
```c#
    var myDummyGhostCharacterControllerEntity = entityManager.CreateEntity(typeof(MyNamespace.MyInputComponent), typeof(InputBufferData<MyNamespace.MyInputComponent>));
    var myConnectionEntity = SystemAPI.GetSingletonEntity<NetworkId>();
    entityManager.SetComponentData(myConnectionEntity, new CommandTarget { targetEntity = myDummyGhostCharacterControllerEntity }); // This tells the netcode package which entity it should be sending inputs for.
```
1. On the server (where you spawn the actual character controller ghost for the thin client, which will be replicated to all proper clients), you **_only_** need to setup the `CommandTarget` for thin clients (as presumably your player ghosts all use `AutoCommandTarget`. If you're **_not_** using `AutoCommandTarget`, you probably already perform this action for all clients already).
```c#
    entityManager.SetComponentData(thinClientConnectionEntity, new CommandTarget { targetEntity = thinClientsCharacterControllerGhostEntity });
```

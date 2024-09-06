# Client and server worlds networking model

The Netcode for Entities package has a separation between client and server logic, and splits logic into multiple worlds (the "client world", and the "server world").
It does this using concepts laid out in the [hierarchical update system](https://docs.unity3d.com/Packages/com.unity.entities@1.0/manual/systems-update-order.html) of Unity’s Entity Component System (ECS).

## Declaring in which world the system should update

By default, systems are created into (and updated in) the `SimulationSystemGroup`, and created for both client and server worlds. If you want to override that behavior (for example, to have your system
created and run only on the client world), there are two different ways to do it.

### Targeting specific system groups

By specifying that your system belongs in a specific system group (that is present only in the desired world), your system will automatically **not** be created in worlds where this system group is not present.
In other words: Systems in a system group inherit system group world filtering. For example:
```csharp
[UpdateInGroup(typeof(GhostInputSystemGroup))]
public class MyInputSystem : SystemBase
{
  ...
}
```
Because the `GhostInputSystemGroup` exists only for client worlds, the `MyInputSystem` will **only** be present on the client world (caveat: this includes both `Client` and `Thin Client` worlds).

> [!NOTE]
> Systems that update in the `PresentationSystemGroup` are only added to the client world, since the `PresentationSystemGroup` is not created for `Server` and `Thin Client` worlds.


### Use WorldSystemFilter

When more granularity is necessary (or you just want to be more explicit about which world type(s) the system belongs to), you should use the
[WorldSystemFilter](https://docs.unity3d.com/Packages/com.unity.entities@latest/index.html?subfolder=/api/Unity.Entities.WorldSystemFilter.html) attribute.

When an entity `World` is created, users tag it with specific [WorldFlags](https://docs.unity3d.com/Packages/com.unity.entities@latest/index.html?subfolder=/api/Unity.Entities.WorldFlags.html),
that can then be used by the Entities package to distinguish them (for example, to apply filtering and update logic).

By using the `WorldSystemFilter`, you can declare (at compile time) which world types your system belongs to:
- `LocalSimulation`: a world that does not run any Netcode systems, and that's not used to run the multiplayer simulation.
- `ServerSimulation`: a world used to run the server simulation.
- `ClientSimulation`: a world used to run the client simulation.
- `ThinClientSimulation`: a world used to run the thin client simulation.

```csharp
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public class MySystem : SystemBase
{
  ...
}
```
In the example above, we declared that the `MySystem` system should **only** be present for worlds that can be used for running the client simulation. That is, the world that has the `WorldFlags.GameClient` set. `WorldSystemFilterFlags.Default` is used when this attribute is not present.

## Bootstrap

When the Netcode for Entities package is added to your project, a new default [bootstrap](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html) is added to the project.

The default bootstrap creates the client and server worlds automatically at startup:
```c#
        public virtual bool Initialize(string defaultWorldName)
        {
            CreateDefaultClientServerWorlds();
            return true;
        }
```

It populates them with the systems defined by the `[WorldSystemFilter(...)]` attributes you have set. This is useful when you're working in the Editor, and you enter Play Mode with your game scene opened. However, in a standalone game - where you typically want to use some sort of frontend menu - you might want to delay world creation, or choose which Netcode worlds to spawn.

For example, Consider a "Hosting a Client Hosted Server" flow vs a "Connect as a client to a Dedicated Server via Matchmaking" flow.
In the former case, you want to add (and connect via IPC to) an in-proc server world. In the latter, you only want to create a client world.

It's possible to create your own bootstrap class and customize your game flow by creating a class that extends `ClientServerBootstrap` (such as `MyGameSpecificBootstrap`), and overriding the default `Initialize` method implementation.
In your derived class, you can mostly re-use the provided helper methods, which let you create `client`, `server`, `thin-client` and `local simulation` worlds. For more details, refer to [ClientServerBootstrap methods](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html).

The following code example shows how to override the default bootstrap to prevent automatic creation of the client server worlds:

```c#
public class MyGameSpecificBootstrap : ClientServerBootstrap
{
    public override bool Initialize(string defaultWorldName)
    {
        //Create only a local simulation world without any multiplayer and netcode system in it.
        CreateLocalWorld(defaultWorldName);
        return true;
    }

}
```

Then, when you're ready to create the various Netcode worlds, call:

```c#
void OnPlayButtonClicked()
{
    // Typically this:
    var clientWorld = ClientServerBoostrap.CreateClientWorld();
    // And/Or this:
    var serverWorld = ClientServerBoostrap.CreateServerWorld();

    // And/Or something like this, for soak testing:
    const int numThinClientWorldsForStressTest = 10;
    for(int i = 0; i < numThinClientWorldsForStressTest; i++)
        ClientServerBoostrap.CreateThinClientWorld();

    // Or the following, which creates worlds smartly based on:
    // - The Playmode Tool setting specified in the editor.
    // - The current build type, if used in a player.
    ClientServerBootstrap.CreateDefaultClientServerWorlds();
}
```

There are NetcodeSamples showcasing how to manage scene and sub-scene loading with this world creation setup, as well as proper Netcode world disposal (when leaving the gameplay loop).

## Fixed and dynamic time-step

When using Netcode for Entities, the server always updates **at a fixed time-step**. The package also limits the maximum number of fixed-step iterations per frame, to ensure that the server doesn't end up in a state where it takes several seconds to simulate a single frame.

It's therefore important to understand that the fixed update does not use the [standard Unity update frequency](https://docs.unity3d.com/Manual/class-TimeManager.html).

### Configuring the server fixed update loop

The [ClientServerTickRate](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html) singleton component (in the server world) controls this tick-rate.

By using the `ClientServerTickRate`, you can control different aspects of the server simulation loop. For example:
- The `SimulationTickRate` lets you configure the number of simulation ticks per second.
- The `NetworkTickRate` lets you configure how frequently the server sends snapshots to the clients (by default the `NetworkTickRate` is identical to the `SimulationTickRate`).

**The default number of simulation ticks is 60**.

If the server updates at a lower rate than the simulation tick rate, it will perform multiple ticks in the same frame. For example, if the last server update took 50ms (instead of the usual 16ms), the server will need to catch up and will do ~3 simulation steps on the next frame (16ms * 3 ≈ 50ms).

This behavior can lead to what is known as 'the spiral of death' (or 'death spiral'): the server update becomes slower and slower (because it's executing more steps per update, to catch up), causing it to become even further behind (creating more problems).
The `ClientServerTickRate` allows you to customize how the server runs in this particular situation (that is, when the server can't maintain the desired tick-rate).

By setting the [MaxSimulationStepsPerFrame](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#ClientServerTickRate_MaxSimulationStepsPerFrame)
you can control how many simulation steps the server can run in a single frame. <br/>
By using the [MaxSimulationStepBatchSize](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#MaxSimulationStepBatchSize)
you can instruct the server loop to `batch` together multiple ticks into a single step, but with a multiplier on the delta time. For example, instead of running two steps, you can run only one (but with double the delta time).

> [!NOTE]
> This batching only works under specific conditions, and has its own nuances and considerations. Ensure that your game doesn't assume that one simulation step is equivalent to one tick (nor should you hardcode deltaTime).
> This type of situation can happen when your server is having performance issues. This produces mispredictions, since the simulation granularity won't be the same on both client and server side.

Finally, you can configure how the server should consume the idle time to target the desired frame rate.
The [TargetFrameRateMode](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#TargetFrameRateMode) controls how the server should keep the tick rate. Available values are:
* `BusyWait` to run at maximum speed
* `Sleep` for `Application.TargetFrameRate` to reduce CPU load
* `Auto` to use `Sleep` on headless servers and `BusyWait` otherwise


### Configuring the client update loop

The client updates at a dynamic time step, with the exception of prediction code (which always runs at the same fixed time step as the server, attempting to maintain a somewhat deterministic relationship between the two simulations).
The prediction runs in the [PredictedSimulationSystemGroup](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html), which applies this unique fixed time step for prediction.

**The `ClientServerTickRate` configuration is sent (by the server, to the client) during the initial connection handshake. The client prediction loop runs at the exact same `SimulationTickRate` as the server (as mentioned).**

## Standalone builds

Netcode exposes build configuration options inside **ProjectSettings** > **Entities** > **Build**.

[Please refer to the Project Settings page for details.](project-settings.md)

## World migration

Sometimes you want to destroy the world you're in and spin up another world without losing the connection state. You can use `DriverMigrationSystem` for this, which allows you to store and load Transport-related information so a smooth world transition can be made.

```
public World MigrateWorld(World sourceWorld)
{
    DriverMigrationSystem migrationSystem = default;
    foreach (var world in World.All)
    {
        if ((migrationSystem = world.GetExistingSystem<DriverMigrationSystem>()) != null)
            break;
    }

    var ticket = migrationSystem.StoreWorld(sourceWorld);
    sourceWorld.Dispose();

    var newWorld = migrationSystem.LoadWorld(ticket);

    // NOTE: LoadWorld must be executed before you populate your world with the systems it needs!
    // This is because LoadWorld creates a `MigrationTicket` Component that the NetworkStreamReceiveSystem needs in order to be able to Load
    // the correct Driver.

    return ClientServerBootstrap.CreateServerWorld(DefaultWorld, newWorld.Name, newWorld);
}
```

## Thin clients

Thin clients are a tool to help test and debug in the Editor by running simulated dummy clients alongside your normal client and server worlds.
See the _Playmode Tools_ section above for how to configure them.

These clients are heavily stripped down, and should run as little logic as possible (so they don't put a heavy load on the CPU while testing).
Each thin client added adds a little bit of extra work to be computed each frame.

Only systems which have explicitly been set up to run on thin client worlds will run, marked with the `WorldSystemFilterFlags.ThinClientSimulation` flag on the `WorldSystemFilter` attribute.
No rendering is done for thin client data, so they are invisible to the presentation.

In some cases, you might need to check if your system logic should be running for thin clients, and then early out or cancel processing.
The `World.IsThinClient()` extension methods can be used in these cases.

### Thin client workflow recommendations

Thin clients can be used in a variety of ways to help test multiplayer games. We recommend the following:

* Thin clients allow you to quickly test client flows, things like team assignment, spawn locations, leaderboards, UI etc.
* Thin clients created in built players, allowing stress and soak testing of your game servers. For example, you may wish to add a configuration option to automatically create `n` thin client worlds (alongside your normal client world). Have each thin client "follow the leader" and automatically attempt to join the same IP address and port as your main client world. Thus, you can use your existing UI flows (matchmaking, lobby, relay etc.) to get these thin clients into the stress test target server.
* Thin clients controlled by a second input source. Multiplayer games often have complex PvP interactions, and therefore you often wish to have an AI perform a specific action while your client is interacting with it. Examples: crouch, go prone, jump, run diagonally backwards, reload, enable shield, activate ability etc. Hooking thin client controls up to keyboard commands allows you to test these situations without requiring a play-test (or a second dev). You can also hookup thin clients to have mirrored inputs of the tester, with similarly good results.

### Thin client samples

- [NetcodeSamples > HelloNetcode > ThinClient](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/2_Intermediate/06_ThinClients)
- [NetcodeSamples > Asteroids](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/f22bb949b3865c68d5fc588a6e8d032096dc788a/NetcodeSamples/Assets/Samples/Asteroids/Client/Systems/InputSystem.cs#L66)

### Setting up inputs for thin clients

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

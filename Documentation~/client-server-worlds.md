# Client and server worlds networking model

Understand the client and server networking model that the Netcode for Entities package uses.

Netcode for Entities separates client and server logic into two worlds, referred to as the client world and the server world respectively. The concept of [worlds](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-worlds.html) is inherited from Unity's Entity Component System (ECS), and refers to a collection of [entities](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-entities.html) and [systems](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-systems.html) arranged into [system groups](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/systems-update-order.html).

In addition to the standard client and server worlds, Netcode for Entities also supports [thin clients](thin-clients.md) which you can use to test your game during development.

## Configuring system creation and updates

By default, systems are created and updated in the [`SimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.SimulationSystemGroup.html) for both client and server worlds. If you want to override this behavior (for example, to have your system created and run only on the client world), there are two different methods available.

### Target specific system groups

When you specify a system group that your system belongs in, Unity automatically filters out your system in worlds where this system group isn't present. This means that systems in a system group inherit the world filter of said system group. For example:

```csharp
[UpdateInGroup(typeof(GhostInputSystemGroup))]
public class MyInputSystem : SystemBase
{
  ...
}
```

If you examine the `WorldSystemFilter` attribute on [`GhostInputSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostInputSystemGroup.html), you will find that this system group only exists for client, thin client, and local simulation (offline) worlds. It also has a `childDefaultFlags` argument which specifies the flags that child systems, such as the example `MyInputSystem`, inherit (and this argument doesn't contain thin client worlds). Therefore, `MyInputSystem` will be present on full client and local simulation worlds exclusively (unless a `WorldSystemFilter` is added to `MyInputSystem` overriding this default).

> [!NOTE]
> Systems that update in the [`PresentationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.PresentationSystemGroup.html) are only added to the client world because the `PresentationSystemGroup` isn't created for server and thin client worlds.

### Use WorldSystemFilter

Use the [`WorldSystemFilter`](https://docs.unity3d.com/Packages/com.unity.entities@latest/index.html?subfolder=/api/Unity.Entities.WorldSystemFilter.html) attribute to specify the world type(s) that the system belongs to in more detail.

When a world is created, you can tag it with specific [`WorldFlags`](https://docs.unity3d.com/Packages/com.unity.entities@latest/index.html?subfolder=/api/Unity.Entities.WorldFlags.html) that Netcode for Entities uses to distinguish between worlds (for example, to apply filtering and update logic).

Use `WorldSystemFilter` to declare (at compile time) which of the following world types your system belongs to:

- `LocalSimulation`: a world that doesn't run any Netcode systems, and that's not used to run the multiplayer simulation.
- `ServerSimulation`: a world used to run the server simulation.
- `ClientSimulation`: a world used to run the client simulation.
- `ThinClientSimulation`: a world used to run the thin client simulation.

In the following example, `MySystem` is defined such that it's only present for worlds that can be used to run the client simulation (any world that has the `WorldFlags.GameClient` set). `WorldSystemFilterFlags.Default` is used when this attribute isn't present and automatically inherits its filtering rules from its parent system group (in this case, that's the `SimulationSystemGroup`, because no `UpdateInGroup` attribute is specified).

```csharp
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public class MySystem : SystemBase
{
  ...
}
```

## Creating client and server worlds with bootstrapping

When you add Netcode for Entities to your project, the default [`ClientServerBootstrap` class](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html) is added to the project. This bootstrapping class configures and creates the server and client worlds at runtime when your game starts (or when entering Play mode in the Unity Editor).

The default bootstrap creates the client and server worlds automatically at startup:

```c#
        public virtual bool Initialize(string defaultWorldName)
        {
            CreateDefaultClientServerWorlds();
            return true;
        }
```

`ClientServerBootstrap` uses the same bootstrapping flows as defined by [Entities](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/index.html), which means that new worlds are populated using all the systems defined by the relevant world filtering set (such as `[WorldSystemFilter(...)]` attributes you have defined, `WorldSystemFilterFlags` rules your systems inherit, and other attributes like `DisableAutoCreation`). Netcode for Entities also injects many systems (and groups) automatically.

This automatic world creation is most useful when you're working in the Editor and enter Play mode with your game scene opened, because it allows immediate Editor iteration testing of your multiplayer game. However, in a standalone game where you typically want to use some sort of front-end menu, you might want to delay world creation, or choose which Netcode worlds to spawn.

For example, consider a "Hosting a client-hosted server" flow versus a "Connect as a client to a dedicated server via matchmaking" flow. In the first scenario, you want to add (and connect via IPC to) an in-process server world. In second scenario, you only want to create a client world. In these cases, you can choose to customize the bootstrapping flow.

### Customize the bootstrapping flow

You can create your own bootstrap class and customize your game flow by creating a class that extends `ClientServerBootstrap` (such as `MyGameSpecificBootstrap`), and overriding the default `Initialize` method implementation. In your derived class, you can reuse the provided helper methods, which let you create `client`, `server`, `thin-client` and `local simulation` worlds. For more details, refer to [`ClientServerBootstrap` methods](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html).

The following code example shows how to override the default bootstrap to prevent automatic creation of the client and server worlds:

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
    var clientWorld = ClientServerBootstrap.CreateClientWorld();
    // And/Or this:
    var serverWorld = ClientServerBootstrap.CreateServerWorld();

    // And/Or something like this, for soak testing:
    AutomaticThinClientWorldsUtility.NumThinClientsRequested = 10;
    AutomaticThinClientWorldsUtility.BootstrapThinClientWorlds();

    // Or the following, which creates worlds smartly based on:
    // - The Playmode Tool setting specified in the editor.
    // - The current build type, if used in a player.
    ClientServerBootstrap.CreateDefaultClientServerWorlds();
}
```

There are [Netcode samples](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/master/NetcodeSamples/README.md) showcasing how to manage scene and subscene loading with this world creation setup, as well as proper Netcode world disposal (when leaving the gameplay loop).

## Updating the client and server

When using Netcode for Entities, the server always updates on a fixed timestep to ensure a baseline level of determinism for client prediction (although it's not strict determinism), for physics stability, and for frame rate independence. The package also limits the maximum number of fixed-step iterations per frame to ensure that the server doesn't end up in a state where it takes several seconds to simulate a single frame.

Importantly, the fixed update doesn't use the [standard Unity update frequency](https://docs.unity3d.com/Manual/class-TimeManager.html), nor the physics system __Fixed Timestep__ frequency. It uses its own `ClientServerTickRate.SimulationTickRate` frequency (which` Unity.Physics` - if in use - must be an integer multiple of, refer to `ClientServerTickRate.PredictedFixedStepSimulationTickRatio`).

Clients, however, update at a dynamic timestep, except for [prediction code](intro-to-prediction.md), which always runs at the same fixed timestep as the server, attempting to maintain a deterministic relationship between the two simulations.

Refer to [partial ticks](intro-to-prediction.md#partial-ticks) to understand how prediction is handled for refresh rates that aren't in sync with full ticks.

### Configuring the server fixed update loop

The [`ClientServerTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html) singleton component (in the server world) controls the server tick-rate.

Using `ClientServerTickRate`, you can control different aspects of the server simulation loop. For example:

- `SimulationTickRate` configures the number of simulation ticks per second. The default number of simulation ticks is 60 per second.
- `NetworkTickRate` configures how frequently the server sends snapshots to the clients (by default, the `NetworkTickRate` is identical to the `SimulationTickRate`).

#### Avoiding performance issues

If the server updates at a lower rate than the simulation tick rate, it will perform multiple ticks in the same frame. For example, if the last server update took 50 ms (instead of the usual 16 ms), the server will need to catch up and will do ~3 simulation steps on the next frame (16 ms * 3 ≈ 50 ms).

This behavior can lead to compounding performance issues: the server update becomes slower and slower (because it's executing more steps per update, to catch up), causing it to become even further behind, creating more problems. `ClientServerTickRate` allows you to customize how the server behaves in this situation when the server can't maintain the desired tick-rate.

- Setting [`MaxSimulationStepsPerFrame`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#ClientServerTickRate_MaxSimulationStepsPerFrame) controls how many simulation steps the server can run in a single frame.
- Setting [`MaxSimulationStepBatchSize`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#MaxSimulationStepBatchSize) instructs the server loop to batch together multiple ticks into a single step, but with a multiplier on the delta time. For example, instead of running two steps, the server runs only one (but with double the delta time).

> [!NOTE]
> The batching enabled with `MaxSimulationStepBatchSize` only works under specific conditions and has its own nuances and considerations. Ensure that your game doesn't assume that one simulation step is equivalent to one tick and don't hard code `TimeData.DeltaTime`.
> This type of situation can happen when your server is having performance issues. This produces mispredictions because the simulation granularity won't be the same on both client and server side.

Finally, you can configure how the server consumes the idle time to target the desired frame rate. [`TargetFrameRateMode`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#TargetFrameRateMode) controls how the server maintains the tick rate. Available values are:

- `BusyWait` to run at maximum speed.
- `Sleep` for `Application.TargetFrameRate` to reduce CPU load.
- `Auto` to use `Sleep` on headless servers and `BusyWait` otherwise.

### Configuring the client update loop

Clients update at a dynamic timestep, with the exception of [prediction code](intro-to-prediction.md), which always runs at the same fixed timestep as the server in an attempt to maintain a deterministic relationship between the two simulations. Prediction runs in the [`PredictedSimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html), which applies this unique fixed timestep for prediction.

The `ClientServerTickRate` configuration is sent (by the server, to the client) during the initial connection handshake. The client prediction loop runs at the exact same `SimulationTickRate` as the server.

## World migration

If you want to destroy the world you're in and spin up another world without losing the connection state, you can use `DriverMigrationSystem`, which allows you to store and load transport-related information so a smooth world transition can be made.

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

## Additional resources

- [Entities overview](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/index.html)
- [Thin clients](thin-clients.md)
- [Introduction to prediction](intro-to-prediction.md)

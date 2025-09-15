# Client and server worlds networking model

Understand the client and server networking model that the Netcode for Entities package uses.

Netcode for Entities uses a client-server model and has a separation between client and server logic that is split into multiple worlds (the client world and server world <!-- or host world (Experimental) TODO -->). The concept of [worlds](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-worlds.html) is inherited from Unity's Entity Component System (ECS), and refers to a collection of [entities](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-entities.html) and [systems](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-systems.html) arranged into [system groups](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/systems-update-order.html).

In addition to the standard client and server worlds, Netcode for Entities also supports [thin clients](thin-clients.md) which you can use to test your game during development.

## Terminology

The words client and server can mean different things depending on the context. They can refer to the role a world is taking, or they can refer to the device a game is executing on.

- From a hosting perspective, server refers to the hardware or virtual machine that is running the server world for client devices to connect to.
- From a role perspective, server refers to the world that's running the authoritative simulation, and client refers to the world that's running the local simulation for a player.

A client device can have a server role, for example, which is referred to as a client-hosted server (or simply host).

<!--

TODO remove this comment once ready to be used by users

## Client, server, and host worlds

Netcode for Entities supports different configurations of worlds within the client-server model. A host world is a special type of server world that also runs client systems, allowing one of the players to act as a server. This is referred to as a client-hosted server.

[See Hosting vs Roles](hosting-vs-role.md) for the difference between the two.

| Configuration                                                          | Description                                                                                                                                                                                                                                                                                                                                                                                     |
|-----------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Client-only world connecting to a server-only world.                  | You distribute client builds to your players and host a server build yourself in dedicated servers.                                                                                                                                                                                                                                                                                 |
| Client-only world connecting to a client-hosted server world          | You distribute client-server builds to your players and one of the players acts as a server.<br/>The host player has a client world connecting through IPC to a server world.                                                                                                                                                                                                       |
| Client-only world connecting to a client-hosted single-world server   | You distribute client-server builds to your players, but instead of the host player creating two worlds, they create a single world acting as both a client and server world. This single-world host behaves as a server with client systems running on it as well. Visible ghosts are no longer predicted or interpolated, they are simply the authoritative ghosts being rendered. |

### Binary and single-world host modes

You can choose between the default binary host mode and single-world host mode using the NetcodeConfig's **Host World Mode Selection** dropdown.

> [!NOTE]
> Because this is an experimental feature, you also need to add the NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST define in your project to enable single-world host mode.

Each mode has its own advantages and disadvantages, as described below.

TODO-next format the following in a table

Single-world host mode advantages:

- Performance: a binary world host has multiple extra steps to spend CPU time on. A server world's `SimulationGroup`, `GhostSendSystem`'s serialization, a client world's deserialization, rolling back to the last snapshot, replaying 1+ ticks, 1 partial tick, serializing and sending inputs to the local server world. Whereas a single-world host only has one world to execute locally, with one simulation tick and spends no time sending/receiving data for its own player.
- Because there's no longer two worlds in the same process, static state is for only one world (a client world or a host world).

Binary host mode advantages:

- Client and server separation: `IsClient` and `IsServer` are always exclusive, making writing client and server code easier to think about. Client-only logic will behave the same whether executing on a client or on a client-hosted server.
- Easier testing: when you test locally with a split client world connecting through IPC to a local server world, you are already testing a client connecting to a server. The chances of client-only issues when testing a second client connecting to your host are lessened (although not zero, you should still test with builds or [Multiplayer Play Mode](https://docs.unity3d.com/Packages/com.unity.multiplayer.playmode@latest) clones). For example, it can be easy to forget adding a `[GhostField]` attribute on your `Entity somePlayer` or `int myHealth` fields. With single-world host, you need to always test the behavior of client-only separately.
- When using binary host mode, your game is closer to a dedicated server. It's easier to port to a dedicated server hosting model since your gameplay logic is already split between client and server worlds.

### Behavior differences and migration considerations

If you want to switch between binary host mode and single-world host mode mid-project, you need to be aware of the following differences in behavior between the two modes.

- Connection entity: a world where client systems execute (your host) can have multiple connections containing `NetworkId` and `NetworkStreamInGame`.
  - Single-world host has a fake connection entity, containing a singleton `LocalConnection` component, a `NetworkId` component, and no `NetworkStreamConnection` component.
- Inputs: client systems have access to other player's inputs on a host. You need to filter appropriately using `GhostOwnerIsLocal`.
- `GhostOwnerIsLocal` behaves differently between the two modes.
    - In a binary host mode setup, client worlds see `GhostOwnerIsLocal` active on locally owned ghosts (ghosts whose owner ID corresponds to the `LocalConnection`'s network ID). On a server world, the behaviour is undefined.
    - In a single-world setup, the host world sees `GhostOwnerIsLocal` active on its locally owned ghosts, just like for client worlds.
    - Make sure to strip your input components so they only appear on predicted ghosts if you want to run prediction code reading inputs server-side without having to rely on `GhostOwnerIsLocal`. Refer to `GhostComponent` for stripping configurations.
- Client-only logic executes in the same world as server systems when using single-world host mode.
- Relevancy and culling: as the single hosting world is the server role, it must have all server ghost entities loaded in memory to be able to properly perform server duties (for other connections). Therefore, host worlds cannot enable relevancy for the host connection (though relevancy can still be applied to other connections). Therefore, you need to manually disable rendering for far away ghosts in single-world host mode, you can't rely on relevancy.
- Prediction switching isn't supported on hosts, so can't be used in single-world host mode.
- When using single-world host mode, all ghosts are authoritative ghosts which means interpolation must be handled differently.
    - Instead of changing the authoritative value, it's recommended to just smooth the visual instead when interpolating. For example, use `LocalToWorld` for transforms.
    - Refer to the [Health sample](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/3_Advanced/01_HealthBars) for an example of an interpolated and replicated values (the player's health).
- You can use a fast path for RPCs in single-world host mode. Custom serialization should take advantage of that fast path with `IsPassthroughRPC` and `GetPassthroughActionData`.
- Partial Ticks: single-host world doesn't support partial ticks. You can instead interpolate your ghosts between full ticks on the host.
- Testing with lag: to test with lag, you need to test with an external client connecting to your host. Because the host doesn't serialize/deserialize its own state in single-host world mode, there's no way to add artificial latency on your local objects.
- Sending snapshots on catchup ticks: in a server-only world, the server can send snapshot packets for each individual catchup ticks if they all happen in the same frame. This isn't the case for single-world host, where the host only sends one snapshot per frame.

TODO-next see comment here https://github.cds.internal.unity3d.com/unity/dots/pull/14369/files/ee874d6192b2d76cf38a3aa733b54469b65b24fa#r831105 the above should be a table.

-->

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

For example, consider a "Hosting a client-hosted server" flow versus a "Connect as a client to a dedicated server via matchmaking" flow. In the first scenario, you may want to add (and connect via IPC to) an in-process server world. In the second scenario, you only want to create a client world. In these cases, you can choose to customize the bootstrapping flow.

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

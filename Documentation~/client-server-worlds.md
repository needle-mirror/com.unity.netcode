# Client and server worlds networking model

Understand how Netcode for Entities separates gameplay into client and server worlds, and how to control, create, and update those worlds.

Netcode for Entities uses a client-server model that separates client and server logic into distinct [worlds](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-worlds.html): a client world and a server world. The concept of worlds is inherited from Unity's Entity Component System (ECS), and refers to a collection of [entities](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-entities.html) and [systems](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-systems.html) arranged into [system groups](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/systems-update-order.html). A client-hosted game can also combine both roles into a single world, as described in [Single-world host](single-world-host-mode.md). In addition to the client and server worlds, Netcode for Entities supports [thin clients](testing/thin-clients.md) that you can use to test your game during development.

This page explains how to control which systems run in each world, how the default bootstrap creates the client and server worlds and how to customize that flow, how the client and server update loops differ, and how to migrate between worlds without losing the connection state. All the [network topologies](network-topologies.md) that Netcode for Entities supports use some variant of client and server worlds.

## Configure system creation and updates

By default, systems are created and updated in the [`SimulationSystemGroup`](xref:Unity.Entities.SimulationSystemGroup) for both client and server worlds. To override this behavior, for example to have your system created and run only on the client world, use one of the following two methods.

### Target specific system groups

When you specify a system group that your system belongs in, Unity automatically filters out your system in worlds where this system group isn't present. This means that systems in a system group inherit the world filter of that system group. For example:

[!code-cs[blobs](../Tests/Editor/DocCodeSamples/client-server-worlds.cs#SystemGroup)]

If you examine the `WorldSystemFilter` attribute on [`GhostInputSystemGroup`](xref:Unity.Netcode.GhostInputSystemGroup), you find that this system group only exists for client, thin client, and local simulation (offline) worlds. It also has a `childDefaultFlags` argument that specifies the flags that child systems, such as the example `MyInputSystem`, inherit (and this argument doesn't contain thin client worlds). Therefore, `MyInputSystem` is present on full client and local simulation worlds exclusively, unless a `WorldSystemFilter` added to `MyInputSystem` overrides this default.

> [!NOTE]
> Systems that update in the [`PresentationSystemGroup`](xref:Unity.Entities.PresentationSystemGroup) are only added to the client world, because the `PresentationSystemGroup` isn't created for server and thin client worlds.

### Filter systems by world type

Use the [`WorldSystemFilter`](xref:Unity.Entities.WorldSystemFilter) attribute to specify the world types that the system belongs to in more detail.

When a world is created, you can tag it with specific [`WorldFlags`](xref:Unity.Entities.WorldFlags) that Netcode for Entities uses to distinguish between worlds, for example to apply filtering and update logic.

Use `WorldSystemFilter` to declare, at compile time, which of the following world types your system belongs to:

- `LocalSimulation`: a world that doesn't run any Netcode systems, and that isn't used to run the multiplayer simulation.
- `ServerSimulation`: a world used to run the server simulation.
- `ClientSimulation`: a world used to run the client simulation.
- `ThinClientSimulation`: a world used to run the thin client simulation.

In the following example, `MySystem` is defined such that it's only present for worlds that can run the client simulation (any world that has the `WorldFlags.GameClient` set). `WorldSystemFilterFlags.Default` is used when this attribute isn't present, and automatically inherits its filtering rules from its parent system group (in this case, that's the `SimulationSystemGroup`, because no `UpdateInGroup` attribute is specified).

[!code-cs[blobs](../Tests/Editor/DocCodeSamples/client-server-worlds.cs#WorldSystemFilter)]

## Create client and server worlds with bootstrapping

When you add Netcode for Entities to your project, the default [`ClientServerBootstrap` class](xref:Unity.Netcode.ClientServerBootstrap) is added to the project. This bootstrapping class configures and creates the server and client worlds at runtime when your game starts, or when you enter Play mode in the Unity Editor. The default bootstrap creates the client and server worlds automatically at startup.

`ClientServerBootstrap` uses the same bootstrapping flows that [Entities](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/index.html) defines, which means that new worlds are populated using all the systems defined by the relevant world filtering set, such as the `[WorldSystemFilter(...)]` attributes you define, the `WorldSystemFilterFlags` rules your systems inherit, and other attributes like `DisableAutoCreation`. Netcode for Entities also injects many systems and groups automatically.

This automatic world creation is most useful when you work in the Editor and enter Play mode with your game scene open, because it allows immediate Editor iteration testing of your multiplayer game. However, in a standalone game where you typically want a front-end menu, you might want to delay world creation or choose which Netcode worlds to spawn.

For example, consider a "host a client-hosted server" flow compared to a "connect as a client to a dedicated server through matchmaking" flow. In the first scenario, you might want to add an in-process server world and connect to it through IPC. In the second scenario, you only want to create a client world. In these cases, you can customize the bootstrapping flow.

### Customize the bootstrapping flow

To customize your game flow, create a class that extends `ClientServerBootstrap`, such as `MyGameSpecificBootstrap`, and override the default `Initialize` method implementation. In your derived class, you can reuse the provided helper methods, which let you create client, server, thin client, and local simulation worlds. For more details, refer to [`ClientServerBootstrap` methods](xref:Unity.Netcode.ClientServerBootstrap).

The following code example shows how to override the default bootstrap to prevent automatic creation of the client and server worlds:

[!code-cs[blobs](../Tests/Editor/DocCodeSamples/client-server-worlds.cs#CustomBootstrap)]

Then, when you're ready to create the various Netcode worlds, call:

[!code-cs[blobs](../Tests/Editor/DocCodeSamples/client-server-worlds.cs#UsingCustomBootstrap)]

The [Netcode samples](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/master/NetcodeSamples/README.md) show how to manage scene and subscene loading with this world creation setup, as well as proper Netcode world disposal when leaving the gameplay loop.

## Update the client and server

The server always updates on a fixed timestep to provide a baseline level of determinism for client prediction (although it's not strict determinism), for physics stability, and for frame rate independence. The package also limits the maximum number of fixed-step iterations per frame to ensure that the server doesn't end up in a state where it takes several seconds to simulate a single frame.

The fixed update doesn't use the [standard Unity update frequency](https://docs.unity3d.com/Manual/class-TimeManager.html), nor the physics system **Fixed Timestep** frequency. It uses its own `ClientServerTickRate.SimulationTickRate` frequency. If `Unity.Physics` is in use, its timestep must be an integer multiple of this frequency. Refer to `ClientServerTickRate.PredictedFixedStepSimulationTickRatio`.

Clients update at a dynamic timestep, except for [prediction code](intro-to-prediction.md), which always runs at the same fixed timestep as the server to maintain a deterministic relationship between the two simulations. To understand how prediction is handled for refresh rates that aren't in sync with full ticks, refer to [partial ticks](intro-to-prediction.md#partial-ticks).

### Configure the server fixed update loop

The [`ClientServerTickRate`](xref:Unity.Netcode.ClientServerTickRate) singleton component, in the server world, controls the server tick rate. Use it to control different aspects of the server simulation loop. For example:

- `SimulationTickRate` configures the number of simulation ticks per second. The default is 60 ticks per second.
- `NetworkTickRate` configures how frequently the server sends snapshots to the clients. By default, the `NetworkTickRate` is identical to the `SimulationTickRate`.

If the server updates at a lower rate than the simulation tick rate, it performs multiple ticks in the same frame. For example, if the last server update took 50 ms instead of the usual 16 ms, the server needs to catch up and performs around three simulation steps on the next frame (16 ms * 3 ≈ 50 ms).

This behavior can lead to compounding performance issues: the server update becomes slower and slower, because it executes more steps per update to catch up, which causes it to fall even further behind. `ClientServerTickRate` lets you customize how the server behaves when it can't maintain the desired tick rate:

- [`MaxSimulationStepsPerFrame`](xref:Unity.Netcode.ClientServerTickRate.MaxSimulationStepsPerFrame) controls how many simulation steps the server can run in a single frame.
- [`MaxSimulationStepBatchSize`](xref:Unity.Netcode.ClientServerTickRate.MaxSimulationStepBatchSize) instructs the server loop to batch multiple ticks into a single step, with a multiplier on the delta time. For example, instead of running two steps, the server runs one with double the delta time.

> [!NOTE]
> The batching enabled with `MaxSimulationStepBatchSize` only works under specific conditions and has its own nuances and considerations. Ensure that your game doesn't assume that one simulation step is equivalent to one tick, and don't hard code `TimeData.DeltaTime`. Batching can happen when your server has performance issues, and it will most likely produce mispredictions because the simulation granularity isn't the same on the client and the server.

You can also configure how the server consumes idle time to target the desired frame rate. [`TargetFrameRateMode`](xref:Unity.Netcode.ClientServerTickRate.TargetFrameRateMode) controls how the server maintains the tick rate. The available values are:

- `BusyWait` to run at maximum speed.
- `Sleep` to use `Application.TargetFrameRate` and reduce CPU load.
- `Auto` to use `Sleep` on headless servers and `BusyWait` otherwise.

### Configure the client update loop

Clients update at a dynamic timestep, except for [prediction code](intro-to-prediction.md), which always runs at the same fixed timestep as the server to maintain a deterministic relationship between the two simulations. Prediction runs in the [`PredictedSimulationSystemGroup`](xref:Unity.Netcode.PredictedSimulationSystemGroup), which applies this fixed timestep for prediction.

The server sends the `ClientServerTickRate` configuration to the client during the initial connection handshake, so the client prediction loop runs at the exact same `SimulationTickRate` as the server.

## Migrate between worlds

To destroy the world you're in and create another world without losing the connection state, use `DriverMigrationSystem`, which lets you store and load transport-related information so that a smooth world transition can be made.

[!code-cs[blobs](../Tests/Editor/DocCodeSamples/client-server-worlds.cs#WorldMigration)]

## Additional resources

- [Entities overview](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/index.html)
- [Thin clients](testing/thin-clients.md)
- [Introduction to prediction](intro-to-prediction.md)

# Managing latency with prediction

You can use client-side prediction to mitigate the effects of latency on gameplay for your users. For an overview, refer to the [client prediction page](prediction-high-level-explanation.md).

This current page covers how to implement client prediction in your game.

There are also some [client prediction edge cases](prediction-details.md) you should be aware of.

## Prediction in Netcode for Entities

Prediction only runs for entities that have the [`PredictedGhost`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedGhost.html) and [`Simulate`](https://docs.unity3d.com/Packages/com.unity.entities@latest/index.html?subfolder=/api/Unity.Entities.Simulate.html) components. Unity adds the `PredictedGhost` component to all predicted ghosts on the client, and to all ghosts on the server. On the client, the component also contains some data it needs for the prediction, such as which snapshot has been applied to the ghost.

The prediction is based on a fixed time-step loop, controlled by the [`PredictedSimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@0latest/index.html?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html),
which runs on both client and server, and that usually contains the core part of the deterministic ghosts simulation.

The primary API elements involved in prediction are:

- The `Simulate` tag, which filters ghosts to simulate.
- The `PredictedSimulationSystemGroup` system group where your predicted simulation should run. Like `FixedUpdate`, this can run multiple times per frame.
- The `IInputComponentData` interface, which sends inputs associated with ticks.
- `HasOwner`, `AutoCommandTarget`, `SupportedGhostMode`, and `DefaultGhostMode` on the `GhostAuthoringComponent`. to set whether a ghost is predicted or not.
- The `NetworkTime` singleton:
  - `NetworkTime.ServerTick` for the latest simulation tick (both client-predicted tick and server-side tick). In front of the last received snapshot tick by an amount depending on lag.
  - `NetworkTime.InterpolationTick` for the current client-side interpolation tick. Generally behind the last received snapshot tick.
  - Both of the above have a `XXFraction` field available.
  - So `IT | | Snapshot | | | | | | | | | | ST`

## Client-side `PredictedSimulationSystemGroup`

When the prediction runs, the `PredictedSimulationSystemGroup` sets the correct time for the current prediction tick in the ECS `TimeData` struct. It also sets the `ServerTick` in the [`NetworkTime`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTime.html) singleton to the tick being predicted.

> [!NOTE]
> The rollback and prediction resimulation can add a substantial overhead to each frame.
> Example: For a 300ms connection, expect ~22 frames of re-simulation. In other words, physics and all other systems in the `PredictedSimulationSystemGroup` will tick ~22 times in a single frame.
> You can test this by setting a high simulated ping in the `PlayMode Tools Window`.
> See the [Optimizations](optimizations.md) page for further details.

## Simulate tag

Netcode for Entities supports partial snapshots. If your world state update can't be contained in a single packet, Netcode streams the state over multiple ticks, with each snapshot only containing a subset of your entities. Because the prediction loop runs from the oldest tick applied to any entity, and some entities might already have newer data, **you must check whether each entity needs to be simulated or not**. Since the simulation tags are enabled both client and server side, you can reuse the same code in both cases.

There are two ways to perform this check, the second one being included for legacy reasons.

### Check which entities to predict using the `Simulate` tag component (PREFERRED)

The client uses the `Simulate` tag, present on all entities in world, to specify whether a ghost entity should be predicted or not.

- At the beginning of the prediction loop, the `Simulate` tag is disabled for all `Predicted` ghosts.
- For each prediction tick, the `Simulate` tag is enabled for all the entities that should be simulated for that tick.
- At the end of the prediction loop, all predicted ghost entities' `Simulate` components are guaranteed to be enabled.

In game systems that run in the `PredictedSimulationSystemGroup` (or any of its sub-groups) you should add the following to your queries: EntitiesForEach (deprecated) and an idiomatic foreach `.WithAll&lt;Simulate&gt;>` condition. This automatically gives the job (or function) the correct set of entities you need to work on.

For example:

```c#

Entities
    .WithAll<PredictedGhost, Simulate>()
    .ForEach(ref Translation trannslation)
{                 
      ///Your update logic here
}
```

### Check which entities to predict using the `PredictedGhost.ShouldPredict` helper method (LEGACY)

This is a legacy method of performing these checks and is not recommended, although it is still supported. You can call the static method  [`PredictedGhost.ShouldPredict`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedGhost.html#Unity_NetCode_PredictedGhost_ShouldPredict_System_UInt32_) before updating an entity. In this case the method or job that updates the entity should look something like this:

```c#

var serverTick = GetSingleton<NetworkTime>().ServerTick;
Entities
    .WithAll<PredictedGhost, Simulate>()
    .ForEach(ref Translation trannslation)
{                 
      if!(PredictedGhost.ShouldPredict(serverTick))
           return;

      ///Your update logic here
}
```

If an entity didn't receive any new data from the network since the last prediction ran, and it ended with simulating a full tick (which is not always true when you use a dynamic time-step), the prediction continues from where it finished last time, rather than applying the network data.

## Server simulation

On the server, the prediction loop always runs exactly once, and doesn't update the `TimeData` struct (because it's already correct). The simulation on the server is not a predicted one, but the official authoritative version of the game simulation. The `ServerTick` in the `NetworkTime` singleton also has the correct value, so the same code can run on both the client and server.

Thus, the `PredictedGhost.ShouldPredict` always returns true when called on the server, and the `Simulate` component is also always enabled.

> [!NOTE]
> For predicted gameplay systems, you can write the code*once, and it will work on both client and server (without needing to make a distinction about where it's running).

## Remote player prediction

### Remote player prediction with `IInputComponentData`

If inputs are configured to be serialized to other players (refer to [GhostSnapshots](ghost-snapshots.md#icommanddata-and-iinputcomponentdata-serialization)), then it's possible to use client-side prediction for the remote players using the remote player's inputs, the same way you would predict the local player.

When a new snapshot is received by the client, the `PredictedSimulationSystemGroup` runs from the oldest tick applied to any entity, to the tick the prediction is targeting. What needs to be predicted can vary by entity, and you must always check if the entity needs to update/apply the input for a specific tick by only processing entities with the `Simulate` component.

Your input data for the current simulated tick will be updated automatically by Netcode for you.

```c#
    protected override void OnUpdate()
    {
        Entities
            .WithAll<PredictedGhost, Simulate>()
            .ForEach((Entity entity, ref Translation translation, in MyInput input) =>
        {                 
              ///Your update logic here
        }).Run();
    }   
```

### (Legacy) commands

If using the legacy commands, you need to check or retrieve the input buffer yourself.

```c#
    protected override void OnUpdate()
    {
        var tick = GetSingleton<NetworkTime>().ServerTick;
        Entities
            .WithAll<Simulate>()
            .ForEach((Entity entity, ref Translation translation, in DynamicBuffer<MyInput> inputBuffer) =>
            {
                if (!inputBuffer.GetDataAtTick(tick, out var input))
                    return;

                //your move logic
            }).Run();
    }
```

## Prediction smoothing

Prediction errors can occur for a number of reasons: variations in logic between clients and the server, packet drops, quantization errors, and so on.
For predicted entities the net effect is that when rollling back and resimulating from the latest available snapshot, there can be a significant difference between the recomputed values and the originally predicted values.

The `GhostPredictionSmoothingSystem` system provides a way of reconciling and reducing these errors over time, to make the transitions between the two states smoother. For each component, you can configure how to manage these errors by registering a `Smoothing Action Function` on the `GhostPredictionSmoothing` singleton which will smooth the error out over time.

```c#
    public delegate void SmoothingActionDelegate(void* currentData, void* previousData, void* userData);
    //pass null as user data
    GhostPredictionSmoothing.RegisterSmoothingAction<Translation>(EntityManager, MySmoothingAction);
    //will pass as user data a pointer to a MySmoothingActionParams chunk component
    GhostPredictionSmoothing.RegisterSmoothingAction<Translation, MySmoothingActionParams>(EntityManager, DefaultTranslateSmoothingAction.Action);
```
The user data must be a chunk-component present in the entity. A default implementation for smoothing out any Translation prediction errors is provided by the package.

```c#
world.GetSingleton<GhostPredictionSmoothing>().RegisterSmootingAction<Translation>(EntityManager, CustomSmoothing.Action);

[BurstCompile]
public unsafe class CustomSmoothing
{
    public static readonly PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>
        Action =
            new PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>(SmoothingAction);

    [BurstCompile(DisableDirectCall = true)]
    private static void SmoothingAction(void* currentData, void* previousData, void* userData)
    {
        ref var trans = ref UnsafeUtility.AsRef<Translation>(currentData);
        ref var backup = ref UnsafeUtility.AsRef<Translation>(previousData);

        var dist = math.distance(trans.Value, backup.Value);
        //UnityEngine.Debug.Log($"Custom smoothing, diff {trans.Value - backup.Value}, dist {dist}");
        if (dist > 0)
            trans.Value = backup.Value + (trans.Value - backup.Value) / dist;
    }
}
```

## Prediction switching

In a typical multiplayer game, you often want to only predict ghosts (via `GhostMode.Predicted`) that the client is directly interacting with (because prediction CPU intensive). Examples include:

- Your own character controller (typically `GhostMode.OwnerPredicted`).
- Dynamic objects your character controller is colliding with (like crates, balls, platforms, and vehicles).
- Interactive items that your client is triggering (like weapons), and any related entities (like projectiles).

For the majority of the ghosts in your world, you want them to be interpolated (via `GhostMode.Interpolated`). Netcode supports opting into prediction on a per-client, per-ghost basis, based on some criteria (for example, predict all ghosts inside this radius of my clients' character controller).
This feature is called prediction switching.

### The client singleton

The `GhostPredictionSwitchingQueues` client singleton provides two queues that you can subscribe ghosts to:

- `ConvertToPredictedQueue`: Take an interpolated ghost and make it predicted (via `GhostPredictionSwitchingSystem.ConvertGhostToPredicted`).
- `ConvertToInterpolatedQueue`: Take a predicted ghost and make it interpolated (via `GhostPredictionSwitchingSystem.ConvertGhostToInterpolated`).

The `GhostPredictionSwitchingSystem` converts these ghosts for you automatically (changing a ghost's `GhostMode` live).
In practice, this is represented as either adding (or removing) the `PredictedGhost`.

### Prediction switching queue rules

- The entity must be a ghost.
- The ghost type (prefab) must have its `Supported Ghost Modes` set to `All` (via the [`GhostAuthoringComponent`](ghost-snapshots.md#authoring-ghosts)).
- Its `CurrentGhostMode` must not be set to `OwnerPredicted`. `OwnerPredicted` ghosts already switch prediction based on ownership.
- If switching to `Predicted`, the ghost must currently be `Interpolated` (and vice versa).
- The ghost must not currently be switching prediction (see the transitions section below, and the `SwitchPredictionSmoothing` component).

> [!NOTE]
> These rules are guarded in the switching system, and thus an invalid queue entry will be harmlessly ignored (with an error/warning log).

### Timeline issues with prediction switching

Prediction switching moves ghosts from one relative [timeline](interpolation.md#timelines) to another, which can cause visual issues during the transition and cause ghosts to teleport forward or back by more than 2 x Ping ms.

- Predicted ghosts run on the same timeline as the client (roughly your ping _ahead_ of the server).
- Interpolated ghosts run on a timeline behind the server (roughly your ping _behind_ the server).

#### The `SwitchPredictionSmoothing` component and prediction switching transitions

This timeline jump can be mitigated using prediction switching smoothing with the transient component `SwitchPredictionSmoothing` and the system that acts upon it, `SwitchPredictionSmoothingSystem`. This smoothing uses linear interpolation to automatically transition between the `Position` and `Rotation` values of your entity `Transform`, over a user-specified period of time defined when adding the entity to a queue (using `ConvertPredictionEntry.TransitionDurationSeconds`).

The smoothing process isn't perfect, and fast-moving objects that change direction frequently may still experience visual artifacts. Best practice is to set the `TransitionDurationSeconds` value high enough to avoid teleporting, but low enough to minimize the frequency of sudden changes in direction

### Component modification with prediction switching

An additional complication involved in prediction switching is that you may have removed specific components from the predicted or interpolated versions of a ghost (via the `GhostAuthoringInspectionComponent` and/or variants). As a result, whenever a ghost switches prediction at runtime, you need to add or remove these components to stay in sync with your rules (using the `AddRemoveComponents` method).

> [!NOTE]
> This happens automatically, but you should be aware that when re-adding components, the component value is reset to the value baked at authoring time.

### Example code

```c#
// Fetch the singleton as RW as we're modifying singleton collection data.
ref var ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;

// Converts ghost entityA to Predicted, instantly (i.e. as soon as the `GhostPredictionSwitchingSystem` runs). If this entity is moving, it will teleport.
ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
{
    TargetEntity = entityA,
    TransitionDurationSeconds = 0f,
});

// Converts ghost entityB to Interpolated, over 1 second.
// A lerp is applied to the Transform (both Position and Rotation) automatically, smoothing (and somewhat hiding) the change in timelines.
ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
{
    TargetEntity = entityA,
    TransitionDurationSeconds = 1f,
});
```

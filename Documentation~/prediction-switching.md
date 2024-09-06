# Prediction switching

In a typical multiplayer game, you often want to only predict ghosts (via `GhostMode.Predicted`) that the client is directly interacting with (because prediction CPU intensive). Examples include:

- Your own character controller (typically `GhostMode.OwnerPredicted`).
- Dynamic objects your character controller is colliding with (like crates, balls, platforms, and vehicles).
- Interactive items that your client is triggering (like weapons), and any related entities (like projectiles).

For the majority of the ghosts in your world, you want them to be interpolated (via `GhostMode.Interpolated`). Netcode supports opting into prediction on a per-client, per-ghost basis, based on some criteria (for example, predict all ghosts inside this radius of my clients' character controller).
This feature is called prediction switching.

## The client singleton

The `GhostPredictionSwitchingQueues` client singleton provides two queues that you can subscribe ghosts to:

- `ConvertToPredictedQueue`: Take an interpolated ghost and make it predicted (via `GhostPredictionSwitchingSystem.ConvertGhostToPredicted`).
- `ConvertToInterpolatedQueue`: Take a predicted ghost and make it interpolated (via `GhostPredictionSwitchingSystem.ConvertGhostToInterpolated`).

The `GhostPredictionSwitchingSystem` converts these ghosts for you automatically (changing a ghost's `GhostMode` live).
In practice, this is represented as either adding (or removing) the `PredictedGhost`.

## Prediction switching queue rules

- The entity must be a ghost.
- The ghost type (prefab) must have its `Supported Ghost Modes` set to `All` (via the [`GhostAuthoringComponent`](ghost-snapshots.md#authoring-ghosts)).
- Its `CurrentGhostMode` must not be set to `OwnerPredicted`. `OwnerPredicted` ghosts already switch prediction based on ownership.
- If switching to `Predicted`, the ghost must currently be `Interpolated` (and vice versa).
- The ghost must not currently be switching prediction (see the transitions section below, and the `SwitchPredictionSmoothing` component).

> [!NOTE]
> These rules are guarded in the switching system, and thus an invalid queue entry will be harmlessly ignored (with an error/warning log).

## Timeline issues with prediction switching

Prediction switching moves ghosts from one relative [timeline](interpolation.md#timelines) to another, which can cause visual issues during the transition and cause ghosts to teleport forward or back by more than 2 x Ping ms.

- Predicted ghosts run on the same timeline as the client (roughly your ping _ahead_ of the server).
- Interpolated ghosts run on a timeline behind the server (roughly your ping _behind_ the server).

### The `SwitchPredictionSmoothing` component and prediction switching transitions

This timeline jump can be mitigated using prediction switching smoothing with the transient component `SwitchPredictionSmoothing` and the system that acts upon it, `SwitchPredictionSmoothingSystem`. This smoothing uses linear interpolation to automatically transition between the `Position` and `Rotation` values of your entity `Transform`, over a user-specified period of time defined when adding the entity to a queue (using `ConvertPredictionEntry.TransitionDurationSeconds`).

The smoothing process isn't perfect, and fast-moving objects that change direction frequently may still experience visual artifacts. Best practice is to set the `TransitionDurationSeconds` value high enough to avoid teleporting, but low enough to minimize the frequency of sudden changes in direction

## Component modification with prediction switching

An additional complication involved in prediction switching is that you may have removed specific components from the predicted or interpolated versions of a ghost (via the `GhostAuthoringInspectionComponent` and/or variants). As a result, whenever a ghost switches prediction at runtime, you need to add or remove these components to stay in sync with your rules (using the `AddRemoveComponents` method).

> [!NOTE]
> This happens automatically, but you should be aware that when re-adding components, the component value is reset to the value baked at authoring time.

## Example code

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

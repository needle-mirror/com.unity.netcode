# Reduce prediction overhead

Reduce prediction CPU overhead to improve the performance of your game.

* [Physics scheduling](#physics-scheduling)
* [Prediction switching](#prediction-switching)
* [Using `MaxSendRate` to reduce client prediction costs](#using-maxsendrate-to-reduce-client-prediction-costs)
* [Using `ForcedInputLatencyTicks`](#using-forcedinputlatencyticks)
* [Limit resimulation after structural changes](#limit-resimulation-after-structural-changes)

## Physics scheduling

When using [physics](../physics.md) in your game, the `PhysicsSimulationGroup` runs inside the `PredictedFixedStepSimulationSystemGroup`, and you may encounter scheduling overhead when running at a high ping (such as when re-simulating 20+ frames). You can reduce this scheduling overhead by forcing the majority of physics work onto the main thread. Add a [`Physics Step`](https://docs.unity3d.com/Packages/com.unity.physics@latest?subfolder=/manual/component-step.html) singleton to your scene, and set __Multi Threaded__ to `false`.

## Prediction switching

The cost of prediction increases with each predicted ghost. To optimize these costs, you can opt out of predicting a ghost given some set of criteria (such as distance to your client's character controller).

Refer to the [prediction switching page](../prediction-switching.md) for more details.

## Using `MaxSendRate` to reduce client prediction costs

Predicted ghosts are particularly impacted by the [`GhostAuthoringComponent.MaxSendRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html#Unity_NetCode_GhostAuthoringComponent_MaxSendRate) setting, because predicted ghosts are only rolled back and re-simulated after being received in a snapshot.

Reducing the frequency with which a ghost chunk is added to the snapshot indirectly reduces the predicted ghost re-simulation rate, saving client CPU cycles overall. However, it can cause larger client misprediction errors, which leads to larger corrections that may be more visible to players.

> [!NOTE]
> Ghost group children do not support `MaxSendRate` (nor Relevancy, Importance, Static-Optimization etc.) until they've left the group, refer to the [ghost groups page](../ghost-groups.md) for more details.

## Using `ForcedInputLatencyTicks`

[`ClientTickRate.ForcedInputLatencyTicks`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.ForcedInputLatencyTicks.html) reduces the number of client prediction steps needed to be performed each frame, on average,
at the considerable expense of increased input latency (which will make the game feel less responsive to players).

It has two other benefits:

* Higher values reduce the likelihood (and severity) of prediction errors, especially on good connections.
* When using a constant Forced Input Latency value, the consistency of the delay can be learned by players, as the implicit "weight" of the character controller, and therefore may be imperceptible. This is especially true if non-simulation (such as UI, animations, or FX) is able to react on the same frame as a new input is polled, and is similarly true on mobile and console platforms, which typically have higher built-in latencies.

> [!NOTE]
> To handle Forced Input Latency correctly in input command code, see `NetworkTime.InputTargetTick`, which should be used instead of `ServerTick` in some cases.

## Limit resimulation after structural changes

By default, `RollbackPredictionOnStructuralChanges` is set to true, and predicted ghosts on the client are resimulated whenever they are subject to a structural change, such as adding or removing a replicated component. This ensures that prediction for the ghost remains as accurate as possible. The resimulation happens from the most recently received server snapshot up to the current client-predicted tick, and can be costly, especially when physics are involved (because the whole world must be rebuilt).

You can disable this resimulation on a per-prefab basis by unchecking the `RollbackPredictionOnStructuralChanges` toggle in the `GhostAuthoringComponent` inspector. When `RollbackPredictionOnStructuralChanges` is set to false, the `GhostUpdateSystem` reuses the existing prediction history, which saves a lot of CPU processing at the cost of potential prediction inaccuracies.

In general, setting `RollbackPredictionOnStructuralChanges` to false can be a good performance optimization, especially if your game doesn't require highly accurate prediction. However, it can cause a race condition when [removing and re-adding components](#removing-and-re-adding-components).

### Removing and re-adding components

If you remove and re-add a replicated component to a ghost during runtime, then having `RollbackPredictionOnStructuralChanges` set to false can cause inconsistencies in outcomes.

When a new update for the ghost is received, the snapshot data contains the last value from the server. However, if the component is missing at that time, then the value of the component won't be restored. If the component is re-added later, because the entity is not rolled back and re-predicted, then the current state of the re-added component will remain default (all zeros). By comparison, if `RollbackPredictionOnStructuralChanges` is enabled, then the entity will be repredicted and the value of the re-added component will be restored correctly.

## Additional resources

* [Prediction](../intro-to-prediction.md)
* [Prediction switching](../prediction-switching.md)
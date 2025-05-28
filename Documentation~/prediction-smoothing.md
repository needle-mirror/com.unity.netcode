# Prediction smoothing

Prediction errors can occur for a number of reasons: variations in logic between clients and the server, packet drops, quantization errors, and so on. For predicted entities the net effect is that when rolling back and resimulating from the latest available snapshot, there can be a significant difference between the recomputed values and the originally predicted values. The `GhostPredictionSmoothingSystem` system provides a way of reconciling and reducing these errors over time to make the transitions between the two states smoother.

For each component, you can configure how to manage these errors by registering a `Smoothing Action Function` on the `GhostPredictionSmoothing` singleton which smooths the error out over time.

```c#
    public delegate void SmoothingActionDelegate(void* currentData, void* previousData, void* userData);
    //pass null as user data
    GhostPredictionSmoothing.RegisterSmoothingAction<Translation>(EntityManager, MySmoothingAction);
    //will pass as user data a pointer to a MySmoothingActionParams chunk component
    GhostPredictionSmoothing.RegisterSmoothingAction<Translation, MySmoothingActionParams>(EntityManager, DefaultTranslateSmoothingAction.Action);
```
The user data must be a chunk-component present in the entity. A default implementation for smoothing out any translation prediction errors is provided by the package.

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

## Smoothing frequency

The `PredictionSmoothingSystem` doesn't correct prediction errors every single frame (at the rendering frame rate). The registered corrective actions are invoked only when a client reconciles its state because a new snapshot (containing predicted ghosts) is received from the server.

During the resimulation, when the current server tick equals the last predicted full tick, the smoothing corrections
are applied to all ghosts.

> [!NOTE]
> The only requirement for the smoothing action to run is that the client rewinds and resimulates its state
> (which requires predicted ghost state updates). The correction applied to the individual entities is not correlated to the
> fact that a state update has been received for them in the last packet.

##  Limitations and known issues

* The quality of the correction depends on the frequency with which predicted ghost data is received.
   * Jittery connections and lag spikes can affect the prediction correction.
   * Large numbers of replicated ghosts (either predicted or replicated), or in general predicted ghost updates being received for a certain amount of time, also affect the frequency of the correction.
* Structural changes (such as adding or removing a component on a predicted ghost entity) may prevent corrections from being applied. Entities that have changed chunks or that are still in the same chunk but have been moved since the last prediction history backup will not have their prediction smoothing applied.
* Because prediction smoothing is based on function pointer callbacks, there may not be enough context or flexibility inside the smoothing function implementation to apply the logic you may need. The `Smoothing Action` delegates are supposed to be simple and stateless.

## Additional resources

* [Introduction to prediction](intro-to-prediction.md)
* [Managing latency with prediction](prediction-n4e.md)

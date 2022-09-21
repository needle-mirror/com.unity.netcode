# Prediction

Prediction in a multiplayer games means that the client is running the same simulation as the server for the local player. The purpose of running the simulation on the client is so it can predictively apply inputs to the local player right away to reduce the input latency.

Prediction should only run for entities which have the [PredictedGhostComponent](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedGhostComponent.html). Unity adds this component to all predicted ghosts on the client and to all ghosts on the server. On the client, the component also contains some data it needs for the prediction - such as which snapshot has been applied to the ghost.

The prediction is based on a [PredictedSimulationSystemGroup](https://docs.unity3d.com/Packages/com.unity.netcode@0latest/index.html?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html) which always runs at a fixed timestep to get the same results on the client and server.

## Client

The basic flow on the client is:
* NetCode applies the latest snapshot it received from the server to all predicted entities.
* While applying the snapshots, NetCode also finds the oldest snapshot it applied to any entity.
* Once NetCode applies the snapshots, the [PredictedSimulationSystemGroup](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html) runs from the oldest tick applied to any entity, to the tick the prediction is targeting.
* When the prediction runs, the `PredictedSimulationSystemGroup` sets the correct time for the current prediction tick in the ECS TimeData struct. It also sets the `ServerTick` in the [NetworkTime](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTime.html) singleton to the tick being predicted.

Because the prediction loop runs from the oldest tick applied to any entity, and some entities might already have newer data, you must check whether each entity needs to be simulated or not. To perform these checks, either add `.WithAll<Simulate>()` or call the static method  [PredictedGhostComponent.ShouldPredict](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedGhostComponent.html#Unity_NetCode_PredictedGhostComponent_ShouldPredict_System_UInt32_) before updating an entity. If it returns `false` the update should not run for that entity.

If an entity did not receive any new data from the network since the last prediction ran, and it ended with simulating a full tick (which is not always true when you use a dynamic timestep), the prediction continues from where it finished last time, rather than applying the network data.

## Server

On the server the prediction loop always runs exactly once, and does not update the TimeData struct because it is already correct. The `ServerTick` in the `NetworkTime` singleton also has the correct value, so the exact same code can be run on both the client and server.

## Remote Players Prediction
If commands are configured to be serialized to the other players (see [GhostSnapshots](ghost-snapshots.md#icommandData-serialization)) it is possible to use client-side prediction for the remote players using the remote players commands, the same way you do for the local player.
When a new snapshot is received by client, the `PredictedSimulationSystemGroup` runs from the oldest tick applied to any entity, to the tick the prediction is targeting.  It might vary depending on the entity what need to be predicted and you must always check if the entity need to update/apply the input for a specific tick by only processing entities with
the `Simulate` component.

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
# Prediction Smoothing
Prediction errors are always presents for many reason: slightly different logic in between clients and server, packet drops, quantization errors etc.
For predicted entities the net effect is that when we rollback and predict again from the latest available snapshot, more or large delta in between the recomputed values and the current predicted one can be present.
The __GhostPredictionSmoothingSystem__ system provide a way to reconcile and reduce these errors over time, making the transitions smoother.
For each component it is possible to configure how to reconcile and reducing these errors over time by registering a `Smoothing Action Function` on the __GhostPredictionSmoothing__ singleton.
```c#
    public delegate void SmoothingActionDelegate(void* currentData, void* previousData, void* userData);
    //pass null as user data
    GhostPredictionSmoothing.RegisterSmoothingAction<Translation>(EntityManager, MySmoothingAction);
    //will pass as user data a pointer to a MySmoothingActionParams chunk component
    GhostPredictionSmoothing.RegisterSmoothingAction<Translation, MySmoothingActionParams>(EntityManager, DefaultTranslateSmoothingAction.Action);
```
The user data must be a chunk-component present in the entity. A default implementation for smoothing out Translation prediction error is provided by the package.

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

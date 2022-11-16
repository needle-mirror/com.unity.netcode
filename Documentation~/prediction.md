# Prediction

Prediction in a multiplayer games means that the client is running the same simulation as the server for the local player. The purpose of running the simulation on the client is so it can predictively apply inputs to the local player right away to reduce the input latency.

Prediction should only run for entities which have the [PredictedGhostComponent](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedGhostComponent.html). 
Unity adds this component to all predicted ghosts on the client and to all ghosts on the server. On the client, the component also contains some data it needs for the prediction - such as which snapshot has been applied to the ghost.

The prediction is based on a fixed timestep loop, controlled by the [PredictedSimulationSystemGroup](https://docs.unity3d.com/Packages/com.unity.netcode@0latest/index.html?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html), 
which runs on both client and server, and that usually contains the core part of the deterministic ghosts simulation.

## Client

The basic flow on the client is:
* Netcode applies the latest snapshot it received from the server to all predicted entities.
* While applying the snapshots, Netcode also finds the oldest snapshot it applied to any entity.
* Once Netcode applies the snapshots, the [PredictedSimulationSystemGroup](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html) runs from the oldest tick applied to any entity, to the tick the prediction is targeting.
* When the prediction runs, the `PredictedSimulationSystemGroup` sets the correct time for the current prediction tick in the ECS TimeData struct. It also sets the `ServerTick` in the [NetworkTime](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTime.html) singleton to the tick being predicted.

Because the prediction loop runs from the oldest tick applied to any entity, and some entities might already have newer data, **you must check whether each entity needs to be simulated or not**. There are two distinct wayw
to do this check:

### Check which entities to predict using the Simulate tag component (PREFERRED)
The client use the `Simulate` tag, present on all entities in world, to set when a ghost entity should be predicted or not.
- At the beginning of the prediction loop, the `Simulate` tag is disabled the simulation of all `Predicted` ghosts.
- For each prediction tick, the `Simulate` tag is enabled for all the entities that should be simulate for that tick.
- At the end of the prediction loop, all predicted ghost entities `Simulate` components are guarantee to be enabled.

In your systems that run in the `PredictedSimulationSystemGroup` (or any of its sub-groups) you should add to your queries, EntitiesForEach (deprecated) and idiomatic foreach a `.WithAll&lt;Simulate&gt;>` condition.  This will automatically give to the job (or function) the correct set of entities you need to work on.

For example:

```c#

Entities
    .WithAll<PredictedGhostComponent, Simulate>()
    .ForEach(ref Translation trannslation)
{                 
      ///Your update logic here
}
```

### Check which entities to predict using the PredictedGhostComponent.ShouldPredict helper method
The old way To perform these checks, calling the static method  [PredictedGhostComponent.ShouldPredict](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedGhostComponent.html#Unity_NetCode_PredictedGhostComponent_ShouldPredict_System_UInt32_) before updating an entity
is still supported. In this case the method/job that update the entity should looks something like this:

```c#

var serverTick = GetSingleton<NetworkTime>().ServerTick;
Entities
    .WithAll<PredictedGhostComponent, Simulate>()
    .ForEach(ref Translation trannslation)
{                 
      if!(PredictedGhostComponent.ShouldPredict(serverTick))
           return;
                  
      ///Your update logic here
}
```

If an entity did not receive any new data from the network since the last prediction ran, and it ended with simulating a full tick (which is not always true when you use a dynamic timestep), the prediction continues from where it finished last time, rather than applying the network data.

## Server

On the server the prediction loop always runs exactly once, and does not update the TimeData struct because it is already correct.  
The `ServerTick` in the `NetworkTime` singleton also has the correct value, so the exact same code can be run on both the client and server.

The `PredictedGhostComponent.ShouldPredict` always return true when called on the server. The `Simulate` component is also always enabled. You can write the same code for the system that run in prediction, without
making any distinction if it runs on the server or the client.

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

### Remote player prediction with the new IInputComponentData
By using the new `IInputComponentData`, you don't need to check or retrieve the input buffer anymore. Your input data for
the current simulated tick will provide for you. 

```c#
    protected override void OnUpdate()
    {
        Entities
            .WithAll<PredictedGhostComponent, Simulate>()
            .ForEach((Entity entity, ref Translation translation, in MyInput input) =>
        {                 
              ///Your update logic here
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

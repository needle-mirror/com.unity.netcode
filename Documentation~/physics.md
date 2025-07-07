# Physics

Netcode for Entities has some integration with [Unity Physics](https://docs.unity3d.com/Packages/com.unity.physics@latest?subfolder=/manual/index.html) to make it easier to use physics in a networked game. The integration handles interpolated ghosts with physics, and support for predicted ghosts with physics.

The integration works without any configuration, but assumes that all dynamic physics objects are ghosts, so either fully simulated by the server (interpolated ghosts), or by both with the client also simulating forward (at predicted/server tick) and server correcting prediction errors (predicted ghosts), the two types can be mixed together. To run the physics simulation only locally for certain objects some setup is required.

Physics Ghost Setup Checklist:

| GhostAuthoringComponent | Rigidbody             | Static | In a separate physics world | ------------ |
|-------------------------|-----------------------|--------|-----------------------------|-------------:|
| yes                     | yes                   | both   | no                          |        valid |
| no                      | yes                   | no     | yes                         |        valid |
| not required            | no                    | yes    | both                        |        valid |
| yes                     | both                  | yes    | both                        |        valid |
| no                      | on a child of a ghost | no     | both                        |        error |
| no                      | yes                   | no     | no                          |        error |

**Important**: For physics to run at all, Netcode currently requires at least a single predicted ghost to exist in your scene. With this, the prediction update loop will run and tick the physics loop.

## Interpolated ghosts

For interpolated ghosts it's important that the physics simulation only runs on the server. On the client, the ghost's position and rotation are controlled by the snapshots from the server and the client shouldn't run the physics simulation for interpolated ghosts.

To make sure this is true, Netcode disables the `Simulate` component data tag on clients on appropriate ghost entities at the beginning on each frame. This makes the physics object `kinematic` and they won't be moved by the physics simulation.

In particular:

- The `PhysicsVelocity` is ignored (set to zero).
- The `Translation` and `Rotation` are preserved.

## Predicted ghosts and physics simulation

Predicted physics means that the physics simulation runs in the prediction loop (possibly multiple times per update from the tick of the last received snapshot update) on the client, as well as running normally on the server.

During initialization, Netcode moves the `PhysicsSystemGroup` and all `FixedStepSimulationSystemGroup` systems into the `PredictedFixedStepSimulationSystemGroup`. This group is the predicted version of `FixedStepSimulationSystemGroup`, so everything here will be called multiple times up to the required number of predicted ticks. These groups are then only updated when there is actually a dynamic predicted physics ghost present in the world.

All predicted ghosts with physics components run this kind of simulation when they are dynamic. Like with interpolated ghosts, the `Simulate` tag will be enabled/disabled as appropriate at the beginning of each predicted frame, but this time multiple simulation steps might be needed.

Since the physics simulation can be quite CPU intensive, it can spiral out of control when it needs to run multiple times. Needing to predict multiple simulation frames could then result in needing to run multiple ticks in one frame as the fixed timesteps falls behind the simulation tick rate, making the situation worse. On the server it can be beneficial to enable simulation batching in the [`ClientServerTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html) component, see the `MaxSimulationStepBatchSize` and `MaxSimulationStepsPerFrame` options. On clients there are options for prediction batching exposed in the [`ClientTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientTickRate.html), see `MaxPredictionStepBatchSizeFirstTimeTick` and `MaxPredictionStepBatchSizeRepeatedTick`. However, this WILL increase the chance of mispredictions.

By default, the current [quantization](compression.md#quantization) level is set to 1000 for transform and velocity. This is enough in most cases, but does create discrepancies in simulation which can create visible corrections or jitter. [Increasing quantization](ghost-snapshots.md#ghost-component-variants) for physics ghosts will result in more precise simulations at the cost of more bandwidth consumption.

### Using lag compensation predicted collision worlds

When using predicted physics the client will see his predicted physics objects at a slightly different view as the _correct_ authoritative view seen by the server, since it is forward predicting where objects will be at the current server tick. When interacting with such physics objects there is a lag compensation system available so the server can _look up_ what collision world the client saw at a particular tick (to for example better account for if he hit a particular collider). This is enabled via the `EnableLagCompensation` tick in the [`NetCodePhysicsConfig`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetCodePhysicsConfig.html) component. Then you can use the [`PhysicsWorldHistorySingleton`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PhysicsWorldHistorySingleton.html) to query for the collision world at a particular tick.

## Requirements for predicted physics

Because the `PhysicsSystemGroup` is moved to the `PredictedFixedStepSimulationSystemGroup` at the beginning of the first world update, the `PhysicsSystemGroup` only updates when the `PredictedFixedStepSimulationSystemGroup` does.

The `PredictedFixedStepSimulationSystemGroup` is part of the `PredictedSimulationSystemGroup`, and the `PredictedSimulationSystemGroup` requires that:

- A singleton `NetworkStreamInGame` exist in the world. This is a mandatory condition.
- Predicted ghosts must exist in the world. This is a mandatory condition.

Further requirements are imposed by the `RateManager` assigned to the `PredictedFixedStepSimulationSystemGroup` and the `PhysicsSystemGroup` by Netcode for Entities:

- The system must run at fixed tick rate.
- Physics doesn't run for [partial ticks](intro-to-prediction.md#partial-ticks), unless configured to run at a tick rate faster than the `ClientServerTickRate.SimulationTickRate`.
- Netcode for Entities further imposes that `Kinematic` entities must exist (they have a `PhysicsVelocity` component) or lag compensation must be enabled.

If any of these conditions is false at any given time, the `PhysicsSystemGroup` will not update.

### Mitigating entities and lag compensation requirements

There may be some situations where you want physics simulation to run, even if there are no predicted entities in a given scene. For example, when ray casting against the ground in a scene with only interpolated ghosts present.

In these cases, you can configure the behavior of `ClientTickRate.PredictionLoopUpdateMode` and `NetCodePhysicsConfig.PhysicGroupRunMode` to make physics simulations run despite not meeting the [requirements](#requirements-for-predicted-physics) described above.

| `ClientTickRate.PredictionLoopUpdateMode` | `NetCodePhysicsConfig.PhysicGroupRunMode`     |                                                                          |
|-----------------------------------------|---------------------------------------------|--------------------------------------------------------------------------|
| `RequirePredictedGhost` (default setting) | `LagCompensationEnabledOrKinematicGhosts` (*) | Fixed tick rate and predicted ghosts and (kinematic or lag compensation) |
|                                         | `LagCompensationEnabledOrAnyPhysicsEntities`  | Fixed tick rate and predicted ghosts and (lag compensation or colliders)  |
|                                         | `AlwaysRun`                                   | Fixed tick rate and predicted ghosts                                     |                                                     |
| `AlwaysRun`                              | `LagCompensationEnabledOrKinematicGhosts`     | Fixed tick rate and (kinematic or lag compensation)                      |
|                                         | `LagCompensationEnabledOrAnyPhysicsEntities`  | Fixed tick rate and (lag compensation or colliders)                      |
|                                         | `AlwaysRun`                                   | Fixed tick rate and                                                      |                                            |

## Client-only physics simulation with multiple physics worlds

Predicted simulation works by default and all general physics objects in the world should be replicated ghosts. To enable client-only physics simulation for visual effects, particles, and any other physics interactions that don't need to be replicated, you need to create another physics world.

By default, the main physics world at index 0 is the predicted physics world, and you can create the client-only physics world by implementing a custom physics system group and providing it with a new physics world index.

### Set up the multi-physics world

#### Authoring setup

1. Add the `NetcodePhysicsConfig` component to your sub-scene.
2. Set __Client Non Ghost World__ to a value other than 0.
3. Add a [`PhysicsWorldIndex`](https://docs.unity3d.com/Packages/com.unity.physics@latest?subfolder=/api/Unity.Physics.PhysicsWorldIndex.html) component to each physics GameObject you want to simulate in your client-only physics world.
4. Set the world index value to the same value you set in step 2.

As part of the entity baking process, a `PhysicsWorldIndex` shared component is added to all physics entities, indicating which physics world simulation the entity should be part of.

#### Code setup

Create a secondary physics world group that simulates a specific `PhysicsWorldIndex`:

```csharp
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
public partial class VisualizationPhysicsSystemGroup : CustomPhysicsSystemGroup
{
    //This should match the value you set into the `Client Non Ghost World` property.
    public const int WorldToSimulate = 1;
    public VisualizationPhysicsSystemGroup() : base(WorldToSimulate, true)
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            //Any other things you need.
        }
    }
}
```

The arguments passed to the custom class constructor are the world index and a Boolean indicating if it should share static colliders with the main physics world.

A physics simulation set up this way runs inside the `FixedUpdateSimulationGroup` as usual. Refer to the [Unity Physics documentation](https://docs.unity3d.com/Packages/com.unity.physics@latest/index.html?subfolder=/manual/) for more information about `CustomPhysicsSystemGroup`.

The two simulations can use different fixed-time steps and are not required to be in sync, meaning that in the same frame it is possible to have both, or only one of them to be executed independently.
However, as mentioned in the previous section, for the predicted simulation **when a rollback occurs the simulation may runs multiple times in the same frame, one the for each rollback tick**. The client only simulation of course just runs once as normally.

> [!NOTE]
> It is the responsibility of the user to setup their prefab properly to make them run in the correct physics world. This can be achieved with the `PhysicsWorldIndexAuthoring` component, provided with the Unity Physics package, which allows setting the physics world index for rigid bodies. For more information, please refer to the [Unity Physics documentation](https://docs.unity3d.com/Packages/com.unity.physics@latest/index.html?subfolder=/manual/).

### `CustomPhysicsSystemGroup` update requirement

Having multiple physics worlds requires the `PhysicsSystemGroup` to run at least once before any `CustomPhysicsSystemGroup` is able to update.

This constraint is necessary because the `Unity.Physics.SimulationSingleton.Type` property must have been set to be different than `SimulationType.NoPhysics`. The simulation type is updated by an internal physics system.

Refer to [requirements for predicted physics](#requirements-for-predicted-physics) for more information.

### Interaction between predicted and client-only physics entities

There are situations when you would like to make the ghosts interact with physics object that are present only on the client (ex: debris). However, them being a part of a different simulation islands they can't interact with each-other.
The Physics package provides for that use-case a specific workflow using `Custom Physics Proxy` entities.

For each physics entity present in the predicted world where you would like to interact with the client-only world, you need to add the `CustomPhysicsProxyAuthoring` component. The baking process will then automatically create a proxy entity with the necessary physics components (PhysicsBody, PhysicsMass, PhysicsVelocity) along with a [`CustomPhysicsProxyDriver`](https://docs.unity3d.com/Packages/com.unity.physics@latest/index.html?subfolder=/api/Unity.Physics.CustomPhysicsProxyDriver.html) which is the link to the root ghost entity. It will make a copy of the ghosts collider as well and configure the proxy physics body as kinematic. The simulated ghost entity in the predicted world will then be used to _drive_ the proxy by copying the necessary component data and setup the physics velocity to let the proxy move and interact with the other physics entities in the  client-only world.

The ghost proxy position and rotation and are automatically handled by [`SyncCustomPhysicsProxySystem`](https://docs.unity3d.com/Packages/com.unity.physics@latest/index.html?subfolder=/api/Unity.Physics.Systems.SyncCustomPhysicsProxySystem.html) system.
By default the kinematic physics entity is moved using kinematic velocity, by altering the PhysicsVelocity component. It is possible to change the default behavior for the prefab by setting the
`GenerateGhostPhysicsProxy.DriveMode` component property.
Furthermore, it is possible to change that behavior dynamically at runtime by setting the `PhysicsProxyGhostDriver.driveMode` property to the desired mode.

## Custom client physics

There may be situations in which you want to customize how physics is simulated on clients, such as:

- Not having predicted physics on the client.
- Ensuring that physics simulation runs before a connection exists.
- Ensuring that physics simulation runs before the `NetworkStreamInGame` is added to the connection.

For example, if you have:

- A lobby that isn't connected to the server yet.
- A connection that exists, but will never enable streaming ghosts and so never run the `PredictedSimulationSystemGroup`.
- A simulation that only runs on the server.

In these cases, you can manually [disable predicted physics](#disable-predicted-physics) on the client.

### Disable predicted physics

When you don't want or need to have the physics simulation run inside the `PredictedSimulationSystemGroup`, you can force this by manually disabling the `PredictedPhysicsConfigSystem` system.

```csharp
[UpdateInGroup(typeof(InitializationSystemGroup))]
[CreateAfter(typeof(PredictedPhysicsConfigSystem))]
public partial struct DisablePhysicsInitializationIfNotConnect : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.World.GetExistingSystemManaged< PredictedPhysicsConfigSystem >().Enabled = false;
    }
}
```

Because the system doesn't run, the `PhysicsSystemGroup` continues to update inside the `FixedStepSimulationSystemGroup` as usual.

### Enabling multi-physics worlds without connection or predicted ghosts

If disabling the `PredictedPhysicsConfigSystem` is not an option and you still need to run physics before you start receiving ghost data, you can instead use a [multiple physics worlds setup](#multiple-physics-worlds).

Because the `Unity.Physics.SimulationSingleton` must be initialized for physics to run, you can forcibly run the `PhysicsSystemGroup` once at the beginning of the frame.

```csharp
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial class ForcePhysicsInitializationIfNotConnect : SystemBase
{
    protected override void OnUpdate()
    {
        if (SystemAPI.GetSingleton<SimulationSingleton>().Type == SimulationType.NoPhysics)
        {
            //Force a single update of physics just to ensure we have some stuff setup
            World.PushTime(new TimeData(0.0, 0f));
            World.GetExistingSystem<PhysicsSystemGroup>().Update(World.Unmanaged);
            World.PopTime();
            Enabled = false;
        }
    }
}
```

The custom physics world will now update at fixed rate on the client, even if not connected with the server or in-game at all.

## Limitations

As mentioned on this page there are some limitations you must be aware of to use physics and Netcode together.

- Physics simulation will not use partial ticks on the client, you must use physics interpolation if you want physics to update more frequently than it is simulating.
- The Unity.Physics debug systems does not work correctly in presence of multiple world (only the default physics world is displayed).

## Additional resources

* [Unity Physics](https://docs.unity3d.com/Packages/com.unity.physics@latest?subfolder=/manual/index.html)

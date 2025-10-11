# Lag Compensation (aka Ghost Rewind)

`Lag Compensation` is the mechanism used by the server to validate:
- instant hits (i.e ray-cast weapons),
- collisions (i.e landing on a interpolated object, validate projectile motions, ball collisions etc)
- in general inspect the past state
of entities to compensate for the client latency and interpolation.

The current implementation only focus on compensate and rewind the physics state, to deal with collision checks and raycast,
mostly focusing on the "hit-validation" case.

## Core Design Priciples
1. The state recording should be fast
2. The state rewind should be fast
3. The cost of rewind should scale well with the number of player. Ideally sub-linearly or at most linearly.
4. The rewind should not affect the current state of the world.
5. We should have the same code-path for running the lag-compensated queries for client and server if possible.
6. Enabling and using the lag-compensation should be optional (pay what you need)


## Recording the physics state

Both client and server record the state of the `Physic Collision World` by cloning every full tick (when the physics run)
the `Broadphase` AABB tree. The cloning occurs for both the static and dynamic objects.

The server keep up to 32 ticks of history (configurable) while the client, optionally, can hold up to 1 historical information
(the last physics state).

The recording/cloning of the state is done after the `PhysicsGroup` update, to ensure the recorded data is in sync with the last
calculated broadphase.

The history is kept short to avoid lag-compensate client too far in the past. That help to reduce and tune lag artifact like:
- behind the corner kill
- peeker advantage

The history is stored inside an internal `RawHistoryBuffer` struct, that is a collection of `CollisionWorld` entries and `NetworkTick`.
This raw buffer is encapsulate by the `CollisionHistoryBuffer`, that is the high-level (with safety) interface to access the
recorded history.

The `CollisionHistoryBuffer` exposed a way to retrive a safe reference to a `CollisionWorld` state for a given tick in the past, i.e
`GetCollisionWorldFromTick`.

Once the old `CollisionWorld` is retrieved, it can be used as usual to raycast or check collisions.

> Possible nuance: The collision world being a copy, can have references to destroyed entities. The colliders are kept alive by deep-copy and cloning the blob,
but the entity that the broadphase is referring to may have been disposed by the time of the check.

This approach fit very well point 2, 3, 4, and 5. Both client and server can always use the `CollisionWorld` in the past (for the former),
and the current state (for the latter).

In term of speed and memory cost the solution may be acceptable (the blob data is cloned only if necessary). However, for big static world,
cloning the broadphase tree may be still sub-optimal.

## Server rewind requirements

### Requirement 1: Aiming to the past

The lag-compensation require that the client is shooting or aiming at things in the past. That it, lag-compensation does not work well with predicted entities
on the client (i.e stabbings or close combat melee).

### Requirement2: single interpolation timeline

The server can reconcile the lag view of the client up to 32 ticks in the past. Technically, it would be possible to have multiple interpolation
timelines for interpolated ghosts (i.e based on distance) and the server should be still able to figure out the approximate location
on the ghost on the client for a past tick.

However, Netcode for Entities is specifically designed to require a single interpolation timeline for interpolate ghosts only. The reason being:
- Simple to understand and reason about
- Less edgy sub-cases to solve
- Robustness and well know model.

## Serwer rewind logic

The client at a given frame, render/present the interpolated ghosts state at `InterpolationTick.InterpolatedTickFraction`.

While it possible to estimate that server side, it is much more reliable (but also theoritically open to cheat) to send
the delta in between the command `Tick` and  current `InterpolationTick` (including the fractional part) to the server.

This delta represent the how the interpolated ghost are lagging behind in respect the predicted timeline, and it is
normally referred (in Netcode for Entities) as `Interpolation Delay`.

### The ideal Interpolation Delay

The interpolation delay is used by the Server to "reconstruct" what the client was **rendering** at the tick the command was issued.

Why the rendering state ?
- The users are aiming at something on the screen.
- When the client process tick T, what it see on the screen is the previous frame simulated state not the current frame.
- There can be a further delay in the rendered frame because of triple buffering or more queuing (querable but may be complex).
> Usually in competitive scenario, where Lag Compensation are is used the most, graphics card everything is configured to have no
extra latency. So while it is a case to consider, can be seen as a niche one.
- The interpolated ghosts are rendered using the previous frame value of the `InterpolationTick` and `InterpolationTickFraction`,
not the current one calculated for this frame.

The calculated `Interpolation Delay` by the client should be a closed estimated of the delta in between the current command
tick and the `InterpolationTick.InterpolationTickFraction` used for the simulated frame shown on screen.

### Current implementation

In the current implementation, the client calculate the `Interpolation Delay` as delta in between the current
`NetworkTime.ServerTick` and `NetworkTime.InterpolationTick`, adjusted by the delta in between the
`ServerTickFraction` and `InterpolationTickFraction`.

> Remark: The adjustment ia actually non-sensical anymore (it was at a certain point because of the correlation in between
> interpolated and predicted time).

```csharp
var interpolationDelay = networkTime.ServerTick.TickSince(networkTime.InterpolationTick);
var fractionalDelta = networkTime.ServertTickFraction - networkTime.InterpolationTickFraction;
//Logic should be: If the InterpolationTickFraction is closer to the beginning of the tick, prefer using the previous tick
//collision world. If the InterpolationTickFraction it closer to the end, prefer the next tick.
if(fractionDelta >= 1.0) //this is possible only if: ServerTickFraction == 1 and  InterpolationTickFraction == 0
    ++interpolationDelay;
else if(fractionData < 0)
    --interpolationDelay;
```

The client only send an integral number of ticks, no fractional part.

> Rationale: we do that because of a limitation: the server does record the physics state at fixed interval. It is does not interpolate the "collision" world state in
> using fractional information.

When the commands are parsed by the `NetworkStreamReceivedSystem` the reported`InterpolationTick`is saved into the
`SnapshotAck.RemoteInterpolationDelay` property. The `RemoteInterpolationDelay` can be then used by the Server systems
to retrieve the `CollisionWorld` state at `ServerTick - RemoteInterpolationDelay`.

> Problems and Limitations: The `SnapshotAck.RemoteInterpolationDelay` is not a buffer. Is a field. As such, the latest value reported
> for the interpolation tick is always stored (it is not a smoothed average either).
> Being the client in the future and commands (on average) queue 2 ticks before they are used, the current value of the
> `RemoteInterpolationDelay` can be different in respect the value the client had at the time the action was processed.
> For slow moving object this is not a problem, but for small or fast object, that difference can lead to missing hits on the server.









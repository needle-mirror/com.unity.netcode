# Simulation Update

Client and Server worlds configure how **SimulationSystemGroup** and **PredictedSimulationSystemGroup** update
by using custom `RateManagers`.

The rate managers are the one responsible to dictate how many times the these group update (and how) for the
current application frame. Client and Server uses different a different `RateManager` for each group.

## Group affected by RateManager
| Group                                       | Client                             | Server                             |
|---------------------------------------------|------------------------------------|------------------------------------|
| SimulationSystemGroup                       | None                               | NetCodeServerRateManager           |
| PredictedSimulationSystemGroup              | NetCodeClientPredictionRateManager | NetCodeServerPredictionRateManager |
| PredictedFixedStepSimulationSystemGroup (*) | NetcodePredictionFixedRateManager  | NetcodePredictionFixedRateManager  |
| PhysicSystemGroup                           | NetcodePhysicsRateManager          | NetcodePhysicsRateManager          |

> `*` The NetcodePredictionFixedRateManager is not a rate manager but still control how and when the PredictedFixedStepSimulationSystemGroup.Update is invoked.
> The rationale about that design it is because we needed a way to invoke the Begin/End command buffer multiple times inside the PredictedFixedStepSimulationSystemGroup
> (so likely injecting them into it) but without breaking the system sorting. Unfortunately, because of limitation of the sorting algorithm used by the ComponetSystemGroup
> (and others) the best way to achieve the order we wanted and not scramble the order has been to change Update method such that
> it manually updating the CommandBufferSystems before and after the internal systems update.

> PredictedFixedStepSimulationSystemGroup is allowed to run faster than the SimulationTickRate but now slower.
> The reason is because that group run the physics update and usually Physics simulation at rate lower than 60hz ro 30hz
> can become unstable.


## On Using custom allocator for groups with fixed rate

It is necessary to use a custom allocation for dealing with temp and temp-job allocation because for fixed-update loop the
system group is not run every frame. However, the engine perform a check every single time player loop run to verify that allocation older than 4 frames has been not leaked.
That was causing a lot of "false-positive" leaks reported. The use of the custom allocation policy ensure that the group actually properly reset the dual-allocator.

## Spiral of death: tick batching

When the server simulation take longer than expected, the accumulated elapsed time may be larger than one single tick. In that case,
the server will perform multiple tick per frame, until all the elapsed time as been consumed.

By allowing multiple tick per frame a more consistent simulation result can be achieved, but can trigger the so called `spiral-of-death`:
in order to catch-up, the fixed loop run multiple time, incurring in even larger cpu execution time, causing  even more catch-up needs, up to certain
upper-bound.

To partial help solving this problem the we can batch tick together: **we run a single step but with larger delta time.**

Both server and client can use `Tick-Batching`:

- Server split the time into long steps and short steps. Each long step can be at most N Tick long.
- Client split the time into blocks that has the same inputs. That make prediction code client side work better.

Pros:
- Can reduce the CPU cost of prediction (client) and recover (server).

Cons:
- introduce differences in computation in between client and server (and so misprediction).
- hard to understand how to use it correctly.
- nuances with spawning and inputs, most of the time confused as "bug" but that in reality are "by-design".

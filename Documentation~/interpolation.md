# Interpolation

Networked games are subject to [jitter](https://docs-multiplayer.unity3d.com/netcode/current/learn/lagandpacketloss/) which can in turn impact player's gameplay by making objects seem unstable and jittery. A common solution to jitter is to use buffered [interpolation](https://docs-multiplayer.unity3d.com/netcode/current/learn/clientside_interpolation/), which involves intentionally delaying ticks to allow snapshots to arrive and then interpolating between them. Netcode's interpolation is **not** from current state to target state, but between two known snapshots. If the client renders at the same rate as the simulation rate, then the client is always rendering uninterpolated (but still buffered) snapshots.

This page is about ghosts in interpolated mode. Jitter affects predicted ghosts as well, but [prediction](prediction-high-level-explanation.md) solves this on its own.

## Terminology: Interpolation vs Extrapolation

**Linear interpolation** describes the process of smoothly traversing between two known values.

**Waypoint pathing** describes a specific form of movement (playback), whereby an entity linearly interpolates between marked points **A, B, C** by traveling first from **A to B**, then from **B to C**.

**Buffering** describes the process of adding an intentional time delay between receiving data and acting on it. Buffering creates an opportunity for delayed packets to arrive before their data is needed. Larger buffer windows produce more correct playback (under realistic network conditions), but at the cost of additional latency.
In Netcode for Entities, all of the above are used for interpolated ghosts, where each waypoint node is a received snapshot. The more snapshots received, the more accurate the interpolated ghost playback will be. This is set using `ClientTickRate.InterpolationTimeMS`.

**Extrapolation** is an unclamped interpolation. If Netcode hasn't received the destination snapshot value in time, extrapolation causes your value to continue in the same direction, at the same rate. Extrapolation is a basic form of estimation, and is often wrong, but can be preferable to having no estimation at all. Note that extrapolation still has a limit and doesn't continue forever.

The term `Dead Reckoning` has been used in similar context as extrapolation, but can also mean using more complex logic to guess a trajectory. Netcode does not use dead reckoning.

>[!NOTE]
>In Unity's Netcode context, `Extrapolation` is distinct from [client prediction](prediction.md): extrapolation is a simple linear mathematical operation which is applied to interpolated ghosts when snapshot data hasn't arrived by the current `interpolationTime`, whereas client prediction involves complex simulation of gameplay code adjusting to the client's latency in an attempt to mirror the server's own gameplay simulation. In other words, an interpolated ghost can be extrapolated, but not a predicted ghost. Extrapolation and prediction run on different [timelines](#timelines).

## Timelines

Any given client has two timelines at the same time: the [predicted](prediction.md) timeline which runs in your game's 'present', and the interpolated timeline, which shows late (due to network latency) server values. See [Time Synchronization](time-synchronization.md) for more details.

Server side, there is only one timeline: the 'present' timeline.

In total, there are three timelines:

- The server's present timeline (`NetworkTime.ServerTick`)
- The client's predicted timeline (`NetworkTime.ServerTick`)
- The client's interpolated timeline (`NetworkTime.InterpolationTick`).

![Timelines.jpg](images/PredictionSteps/Timelines.jpg)

## Interpolation Tick Fraction

`NetworkTime.InterpolationTickFraction` contains the fraction that the client is currently interpolating to get to the target `InterpolationTick`. For example, with an `InterpolationTick` of 11 and a fraction of 0.5f, this means that the client is currently interpolating between ticks 10 and 11 and is halfway to tick 11. This is **not** tick 11.5f. In other words, `InterpolationTick` is the **target** tick and `InterpolationTickFraction` is the **progress** to get to the target tick.

When `InterpolationTickFraction` is 1.0f, the client is at the target tick. If there are no partial ticks, then `InterpolationTickFraction` will always be 1.0f. The same applies to prediction with `ServerTick` and `ServerTickFraction`.

![TickFraction.jpg](images/TickFraction.jpg)

## Interpolation in Netcode

Netcode has a number of features to ensure that interpolation remains smooth and consistent. Refer to the following pages for more information:

- [Ghost Mode](ghost-snapshots.md#authoring-ghosts): Denotes which timeline (interpolated or predicted) a given ghost type should be on, by default.
- [Smoothing GhostFields](ghost-snapshots.md#authoring-component-serialization)
- [GhostComponentAttribute Predicted vs Interpolated](ghost-snapshots.md#using-the-ghostcomponentattribute): Adds filtering options depending on the ghost's current mode.
- [Prediction Switching](prediction.md#prediction-switching): Convert a ghost from the predicted timeline to the interpolated timeline (and vice versa), with additional options for smoothing during the transition period (which is a form of interpolation).
- [CommandDataInterpolationDelay](entities-list.md#commanddata): Optional component, added server-side, to help with server rewind (lag compensation).
- [Spawns for Interpolated Ghosts](ghost-spawning.md#different-type-of-spawning): Ensures interpolated ghosts are spawned on the appropriate interpolation tick, rather than spawning on the tick the snapshot arrives.
- [Physics](physics.md#interpolated-ghosts)
- [Interpolated Timeline Details](time-synchronization.md)
- [Prediction Smoothing](prediction.md#prediction-smoothing): While not used on interpolated ghosts, the smoothing applied to corrections on mis-predicted GhostField values is a form of interpolation.

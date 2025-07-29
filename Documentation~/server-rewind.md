# Server-side rewind

Set up server-side rewind in your game to limit the impact of latency on multiplayer gameplay.

Server-side rewind is a technique that allows the server to rewind its game state to an earlier point in time to validate incoming information from clients with high latency. It's also referred to as lag compensation.

You can use server-side rewind in conjunction with [prediction](intro-to-prediction.md) to further minimize the effects of latency on your game.

## Introduction to server-side rewind

When interacting with objects on the client, such as shooting at an enemy, players are seeing the [interpolated](interpolation.md) version of those objects. Players themselves, however, are usually [predicted](intro-to-prediction.md). The difference between the predicted and interpolated [timelines](interpolation.md#timelines) can often be hundreds of milliseconds, depending on latency, and without intervention this can cause serious gameplay issues such as having to lead shots. Players can also be difficult to predict, especially when they're trying to avoid being hit, so client-side prediction alone can often fail to produce a consistent experience.

Server-side rewind is the recommended method for dealing with this potential discrepancy. By keeping a history of the game state tick by tick, the server can validate incoming client inputs from different timelines against the relevant historical tick, effectively rewinding its state to match the client's for the purposes of validation. Once the input is validated, the server updates its current state with any necessary changes (such as health decreasing, players dying, and so on) and imposes those changes onto clients.

You can also selectively disable server-side rewind in certain game scenarios, such as when a player is invincible or using an evasive ability.

## Implement server-side rewind

To implement server-side rewind in your project, you need to fetch the collision history from the `PhysicsWorldHistorySingleton` component, which stores the history of the server's physics state, and use the delay value available from [`CommandDataInterpolationDelay`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.CommandDataInterpolationDelay.html) to find out how far back in time the server should rewind to validate the client's input. Both client and server can use the same logic when calculating collisions, but the client calculates its inputs with no delay.

The following code example shows an example implementation of server-side rewind logic. For the full context of this example implementation, refer to the [`ShootingSystem` sample](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/master/NetcodeSamples/Assets/Samples/HelloNetcode/2_Intermediate/03_HitScanWeapon/ShootingSystem.cs).


```csharp
var collisionHistory = SystemAPI.GetSingleton<PhysicsWorldHistorySingleton>();
var physicsWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld;
var networkTime = SystemAPI.GetSingleton<NetworkTime>();
var ghostComponentFromEntity = SystemAPI.GetComponentLookup<GhostInstance>();
var localToWorldFromEntity = SystemAPI.GetComponentLookup<LocalToWorld>();
var lagCompensationEnabledFromEntity = SystemAPI.GetComponentLookup<LagCompensationEnabled>();
var predictingTick = networkTime.ServerTick;
// Do not perform hit-scan when rolling back, only when simulating the latest tick
if (!networkTime.IsFirstTimeFullyPredictingTick)
    return;

foreach (var (character, interpolationDelay, hitComponent) in SystemAPI.Query<CharacterAspect, RefRO<CommandDataInterpolationDelay>, RefRW<Hit>>().WithAll<Simulate>())
{
    if (character.Input.SecondaryFire.IsSet)
    {
        hitComponent.ValueRW.Victim = character.Self;
        hitComponent.ValueRW.Tick = predictingTick;
        continue;
    }
    if (!character.Input.PrimaryFire.IsSet)
    {
        continue;
    }

    // When we fetch the CollisionWorld for ServerTick T, we need to account for the fact that the user
    // raised this input sometime on the previous tick (render-frame, technically).
    const int additionalRenderDelay = 1;

    // Breakdown of timings:
    // - On the client, predicting ServerTick: 100 (for example)
    // - InterpolationDelay: 2 ticks
    // - Rendering Latency (assumption): 1 tick (likely more than 1 due to: double/triple buffering, pipelining, monitor refresh & draw latency)
    // - Client visually sees 97 (-1 for render latency, -2 for lag compensation)
    // - CommandDataInterpolationTick.Delay is a delta between CurrentCommand.Tick vs InterpolationTick, thus -2.
    //   I.e. InterpolationDelay is already accounted for.
    // - On the server, we process this input on ServerTick:100.
    // - CommandDataInterpolationTick.Delay:-2 = 98 (-2)
    // - So the server also needs to subtract the rendering delay to be consistent with what the client sees and queries against (97).
    var delay = lagCompensationEnabledFromEntity.HasComponent(character.Self)
        ? interpolationDelay.ValueRO.Delay + additionalRenderDelay
        : additionalRenderDelay;

    collisionHistory.GetCollisionWorldFromTick(predictingTick, delay, ref physicsWorld, out var collWorld, out var expectedTick, out var returnedTick);

    bool hit = collWorld.CastRay(rayInput, out var closestHit);
}
```

### Implementation considerations

When implementing server-side rewind in your game, consider limiting the server's state history to between 250-500 ms worth of backup, otherwise you risk players experiencing degraded gameplay such as being shot behind a wall (from their perspective) due to large discrepancies in timelines between low- and high-latency clients.

## Test server-side rewind

Test your implementation of server-side rewind by adding artificial latency and seeing how your clients and server behave. Testing at 50, 150, and 500 ms of latency is usually enough to cover most common network conditions. You can use the Netcode for Entities PlayMode Tool to [emulate network conditions](playmode-tool.md#emulate-client-network-conditions) in your project.

You can either test manually, or use bots to simulate client inputs and see if your server-side rewind is working as expected. In the context of the shooting example, this could involve adding test code that counts hits both client- and server-side, and then ensuring that those numbers remain similar and converge when shooting stops.

## Limitations

Netcode for Entities only saves backups of the server's physics state. If players have some other state influencing the result of an action, such as being invincible, you need to track the history of those states per tick as well (depending on your project's gameplay).

## Additional resources

* [Prediction](intro-to-prediction.md)

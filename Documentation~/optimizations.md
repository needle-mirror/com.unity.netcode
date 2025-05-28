# Optimizing your game

Netcode optimizations fall into two categories:

* The amount of CPU time spent on the client and the server (for example, CPU time being spent on serializing components as part of the GhostSendSystem)
* The size and amount of snapshots (which depends on how much and how often Netcode sends data across the network)

This page will describe different strategies for improving both.

## Optimization Mode

Optimization Mode is a setting available in the `Ghost Authoring Component` as described in the [Ghost Snapshots](ghost-snapshots.md) documentation. The setting changes how often Netcode resends the `GhostField` on a spawned entity. It has two modes: **Dynamic** and **Static**. For example, if you spawn objects that never move, you can set Optimization Mode to **Static** to ensure Netcode doesn't resync their transform.

When a GhostField change Netcode will send the changes regardless of this setting. We are optimizing the amount and the size of the snapshots sent.

* `Dynamic` optimization mode is the default mode and tells Netcode that the ghost will change often. It will optimize the ghost for having a small snapshot size when changing and when not changing.
* `Static` optimization mode tells Netcode that the ghost will change rarely. It will not optimize the ghost for having a small snapshot size when changing, but it will not send it at all when not changing.

### Limitations with static-optimized ghosts

* Static-optimized ghosts are forced to enable `UseSingleBaseline`.
* Static-optimization is not supported for ghosts involved in a [ghost group](ghost-groups.md) (neither the root, nor ghost group children), nor for ghosts containing any replicated child components. In both of these cases, ghosts will be treated as `Dynamic` at runtime.
* Ghosts that are both static-optimized and interpolated will not run `GhostField` extrapolation (i.e. `SmoothingAction.InterpolateAndExtrapolate` will be forced into `SmoothingAction.Interpolate`).

## Reducing netcode memory consumption
By default, Netcode for Entities stores up to 32 snapshot history buffer entries for each connection and ghost chunk pair, as defined by `GhostSystemConstants.SnapshotHistorySize:32`.
This allows future snapshots to delta-compress newer `GhostField` values against the latest acked of these 32 previously-sent snapshots.
The `const` value of 32 is best suited for ghosts sending at very high rates (such as 60Hz), providing roughly 500ms worth of history.

However, for MMO-scale games (where `MaxSendRate`s are often significantly lower), smaller snapshot history sizes may be preferable.
To change this `const`, define one of the following in your **Project Settings** > **Player** > **Scripting Define Symbols**:

* `NETCODE_SNAPSHOT_HISTORY_SIZE_16` is a good middle-ground between size-reduction (for static ghosts) and ack availability (for dynamic ghosts). Recommended for projects where the highest `GhostPrefabCreation.Config.MaxSendRate` is 30Hz, or where the `ClientServerTickRate.NetworkTickRate` is 30.
* `NETCODE_SNAPSHOT_HISTORY_SIZE_6` is best suited for larger scale projects, such as those with hundreds of dynamic ghosts, thousands of static ghosts, and where the player character controller is already sent at a significantly lower frequency due to congestion or widespread use of `GhostPrefabCreation.Config.MaxSendRate`.

> [!NOTE]
> Be aware that ghost chunks may not be sent to a specific connection if their entire snapshot history buffer fills up with 'in-flight' snapshots (un-acked snapshots - sent less than one round trip ago - containing this ghost chunk).
> See the `PacketDumpResult_SnapshotHistorySaturated` method for debugging.

## Reducing prediction CPU overhead

### Physics scheduling

Context: [Prediction](intro-to-prediction.md) and [Physics](physics.md).
As the `PhysicsSimulationGroup` is run inside the `PredictedFixedStepSimulationSystemGroup`, you may encounter scheduling overhead when running at a high ping (i.e. re-simulating 20+ frames).
You can reduce this scheduling overhead by forcing the majority of Physics work onto the main thread. Add a `PhysicsStep` singleton to your scene, and set `Multi Threaded` to `false`.
Of course, we are always exploring ways to reduce scheduling overhead._

### Prediction switching

The cost of prediction increases with each predicted ghost.
Thus, as an optimization, we can opt-out of predicting a ghost given some set of criteria (e.g. distance to your clients character controller).
See [Prediction Switching](prediction-switching.md) for details.

### Using `MaxSendRate` to reduce client prediction costs

Predicted ghosts are particularly impacted by the `GhostAuthoringComponent.MaxSendRate` setting, because we only rollback and re-simulate a predicted ghost after it is received in a snapshot.
Therefore, reducing the frequency by which a ghost chunk is added to the snapshot indirectly reduces predicted ghost re-simulation rate, saving client CPU cycles in aggregate.
However, it may cause larger client misprediction errors, which leads to larger corrections, which can be observed by players. As always, it is a trade-off.

> [!NOTE]
> Ghost group children do not support `MaxSendRate` (nor Relevancy, Importance, Static-Optimization etc.) until they've left the group, [read more here](ghost-groups.md).

## Executing expensive operations during off frames

On client-hosted servers, your game can be set at a tick rate of 30Hz and a frame rate of 60Hz (if your ClientServerTickRate.TargetFrameRateMode is set to BusyWait). Your host would execute 2 frames for every tick. In other words, your game would be less busy one frame out of two. This can be used to do extra operations during those "off frames".
To access whether a tick will execute during the frame, you can access the server world's rate manager to get that info.

> [!NOTE]
> A server world isn't idle during off frames and can time-slice its data sending to multiple connections if there's enough connections and enough off frames. For example, a server with 10 connections can send data to 5 connections one frame and the other 5 the next frame if its tick rate is low enough.


```cs
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial class DoExtraWorkSystem : SystemBase
{
    protected override void OnUpdate()
    {
        var serverRateManager = ClientServerBootstrap.ServerWorld.GetExistingSystemManaged&lt;SimulationSystemGroup&gt;().RateManager as NetcodeServerRateManager;
        if (!serverRateManager.WillUpdate())
            DoExtraWork(); // We know this frame will be less busy, we can do extra work
    }
}
```

## Serialization cost

When a `GhostField` changes, we serialize the data and synchronize it over the network. If a job has write access to a `GhostField`, Netcode will check if the data changed.
First, it serializes the data and compares it with the previous synchronized version of it. If the job did not change the `GhostField`, it discards the serialized result.
For this reason, it is important to ensure that no jobs have write access to data if it will not change (to remove this hidden CPU cost).

## Relevancy

"Ghost Relevancy" (a.k.a. "Ghost Filtering") is a server feature, allowing you to set conditions on whether or not a specific ghost entity is replicated to (i.e. spawned on) a specific client. You can use this to:
* Set a max replication distance (e.g. in an open world FPS), or replication filtering based on which gameplay zone they're in.
* Create a server-side, anti-cheat-capable "Fog of War" (preventing clients from knowing about entities that they should be unable to see).
* Only allow specific clients to be notified of a ghosts state (e.g. an item being dropped in a hidden information game).
* To create client-specific (i.e. "single client") entities (e.g. in MMO games, NPCs that are only visible to a player when they've completed some quest condition. You could even create an AI NPC companion only for a specific player or party, triggered by an escort mission start).
* Temporarily pause all replication for a client, while said client is in a specific state (e.g. post-death, when the respawn timer is long, and the user is unable to spectate).

Essentially: Use Relevancy to avoid replicating entities that the player can neither see, nor interact with.

> [!NOTE]
> Ghost group children do not support Relevancy (nor Importance, MaxSendRate, Static-Optimization etc.) until they've left the group, [read more here](ghost-groups.md).

The `GhostRelevancy` singleton component contains these controls:

The `GhostRelevancyMode` field chooses the behaviour of the entire Relevancy subsystem:
* **Disabled** - The default. No relevancy will be applied under any circumstances.
* **SetIsRelevant** - Only ghosts added to relevancy set (`GhostRelevancySet`, below) are considered "relevant to that client", and thus serialized for the specified connection (where possible, obviously, as eventual consistency and importance scaling rules still apply (see paragraphs below)).
_Note that applying this setting will cause **all** ghosts to default to **not be replicated** to **any** client. It's a useful default when it's rare or impossible for a player to be viewing the entire world._
* **SetIsIrrelevant** - Ghosts added to relevancy set (`GhostRelevancySet`, below) are considered "not-relevant to that client", and thus will be not serialized for the specified connection. In other words: Set this mode if you want to specifically ignore specific entities for a given client.

`GhostRelevancySet` is the map that stores a these (connection, ghost) pairs. The behaviour (of adding a (connection, ghost) item) is determined according to the above rule.
`GlobalRelevantQuery` is a global rule denoting that all ghost chunks matching this query are always considered relevant to all connections (unless you've added the ghosts in said chunk to the `GhostRelevancySet`). This is useful for creating general relevancy rules (e.g. "the entities in charge of tracking player scores are always relevant"). `GhostRelevancySet` takes precedence over this rule. See the [example](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/Asteroids/Authoring/Server/SetAlwaysRelevantSystem.cs) in Asteroids.
```c#
var relevancy = SystemAPI.GetSingletonRW<GhostRelevancy>();
relevancy.ValueRW.DefaultRelevancyQuery = GetEntityQuery(typeof(AsteroidScore));
```
> [!NOTE]
> If a ghost has been replicated to a client, then is set to **not be** relevant to said client, that client will be notified that this entity has been **destroyed**, and will do so. This misnomer can be confusing, as the entity being despawned does not imply the server entity was destroyed.
> Example: Despawning an enemy monster in a MOBA because it became hidden in the Fog of War should not trigger a death animation (nor S/VFX). Thus, use some other data to notify what kind of entity-destruction state your entity has entered (e.g. enabling an `IsDead`/`IsCorpse` component).

### Relevancy fast-path via importance scaling
You can merge the ghost relevancy calculation with the batched importance scaling function pointer (assuming relevancy can be expressed via the same data as importance scaling).
As shown in the [`GhostDistanceImportance.BatchScaleWithRelevancy` sample code](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostDistanceImportance.html#Unity_NetCode_GhostDistanceImportance_BatchScaleWithRelevancyFunctionPointer), enabling this fast-path requires the following steps:

1. Enabling relevancy via `SystemAPI.GetSingletonRW<GhostRelevancy>().ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;` (or `SetIsIrrelevant`).
2. Setting the `PrioChunk.isRelevant` flag for each chunk (this flag ignores the `SetIsRelevant` vs `SetIsIrrelevant` distinction, so setting `isRelevant = true` will cause the chunk to be relevant, regardless of which mode we're in).
```csharp
    ...
    data.priority = basePriority;
    data.isRelevant = distSq <= 16; // Any chunks greater than 4 tiles from the player will be irrelevant (unless explicitly added to the `GhostRelevancySet`).
```
When using this fast-path, there is no need to write ghost instances into the global `GhostRelevancySet` unless they would not be added via the ghost importance function `isRelevant` flag.
For example; a map marker ghost far outside the practical `BatchScaleWithRelevancy` radius, but that you still want to replicate.

> [!NOTE]
> `PrioChunk.isRelevant` has lower precedence than the per-entity `GhostRelevancySet`.

## Limiting snapshot size

* Use `GhostAuthoringComponent.MaxSendRate` to broadly reduce/clamp the resend rate of each of your ghost prefab types.
It is an effective tool to reduce total bandwidth consumption, particularly in cases where your snapshot is always filling up with large ghosts with high priorities.
_For example: A "LootItem" ghost prefab type can be told to only replicate - at most - on every tenth snapshot, by setting `MaxSendRate` to 10._

* The per-connection component `NetworkStreamSnapshotTargetSize` will stop serializing entities into a snapshot if/when the snapshot goes above the specified byte size (`Value`).
This is a way to try to enforce a (soft) limit on per-connection bandwidth consumption.
To apply this limit globally, set a non-zero value in `GhostSendSystemData.DefaultSnapshotPacketSize`.

> [!NOTE]
> Note that `MaxSendRate` is distinct from `Importance`: The former enforces a cap on the resend interval, whereas the latter informs the `GhostSendSystem` of which ghost chunks should be prioritized in the next snapshot.
> Therefore, `MaxSendRate` can be thought of as a gating mechanism (much like its predecessor; `MinSendImportance`).

> [!NOTE]
> Snapshots do have a minimum send size. This is because - per snapshot - we ensure that _some_ new and destroyed entities are replicated, and we ensure that at least one ghost has been replicated.

* `GhostSendSystemData.MaxSendChunks` can be used to limit the max number of chunks added to any given snapshot.

* `GhostSendSystemData.MaxIterateChunks` can be used to limit the total number of chunks the `GhostSendSystem` will iterate over & serialize when looking for ghosts to replicate.
Very useful when dealing with thousands of static ghosts.

* `GhostSendSystemData.MinSendImportance` can be used to prevent a chunks entities from being sent too frequently.
__As of 1.4, prefer `GhostAuthoringComponent.MaxSendRate` over this global.__
`GhostSendSystemData.FirstSendImportanceMultiplier` can be used to bump the priority of chunks containing new entities, to ensure they're replicated quickly, regardless of the above setting.

> [!NOTE]
> The above optimizations are applied on the per-chunk level, and they kick in **_after_** a chunks contents have been added to the snapshot. Thus, in practice, real send values will be higher.
> Example: `MaxSendEntities` is set to 100, but you have two chunks, each with 99 entities. Thus, you'd actually send 198 entities.

## Importance scaling

The server operates with a fixed bandwidth target, and sends a single snapshot packet of customizable size on every network tick.
It fills this packet with the ghosts with the highest importance, determined by a priority queue of ghost chunks (rebuilt each tick).
Therefore, importance is determined at the ghost chunk level, not on each instance individually.

Several factors determine the importance of each ghost chunk:

* You can specify the base `GhostAuthoringComponent.Importance` per ghost type.
* Which Netcode for Entities then multiplies by `ticksSinceLastSent` (note: not `ticksSinceLastAcked`), as well as other modifiers,
like `GhostSendSystemData.IrrelevantImportanceDownScale` and `GhostSendSystemData.FirstSendImportanceMultiplier`.
* You can also supply your own method to scale the **Importance** on a per-chunk, per-connection basis, via `GhostImportance.BatchScaleImportanceFunction`. For example, this allows you to [deprioritize far away ghosts, in favor of nearby ones](#distance-based-importance).
* `GhostAuthoringComponent.MaxSendRate` does not directly impact `Importance` values. Instead, it is a pre-pass, preventing a ghost chunk from being added to the priority queue at all, for this tick.

Once a packet is full, the server sends it, and all remaining ghost entities are simply not sent on this tick -
though they are now more likely to be in the next snapshot, thanks to `ticksSinceLastSent` scaling.

> [!NOTE]
> Ghost group children do not support `Importance` (nor Relevancy, `MaxSendRate`, Static-Optimization etc.) until they've left the group, [read more here](ghost-groups.md).

### Set-up required

Below is an example of how to set up the built-in distance-based importance scaling. If you want to use a custom importance implementation, you can reuse parts of the built-in solution or replace it with your own.

### `GhostImportance`

[`GhostImportance`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostImportance.html) is the configuration component for setting up importance scaling.
`GhostSendSystem` invokes the `BatchScaleImportanceFunction` only if the `GhostConnectionComponentType` and `GhostImportanceDataType` are created.

The fields you can set on this is:
- `BatchScaleImportanceFunction` allows you to write and assign a custom scaling function (to scale the importance, with chunk granularity).
- `GhostConnectionComponentType` is the type added per-connection, allowing you to store per-connection data needed in the scaling calculation.
- `GhostImportanceDataType` is an optional singleton component, allowing you to pass in any of your own static data necessary in the scaling calculation.
- `GhostImportancePerChunkDataType` is the shared component added per-chunk, storing any chunk-specific data used in the scaling calculation.

Flow: The function pointer is invoked by the `GhostSendSystem` for each chunk, and returns the importance scaling for the entities contained within this chunk. The signature of the method is of the delegate type `GhostImportance.ScaleImportanceDelegate`.
The parameters are `IntPtr`s, which point to instances of the three types of data described above.

You must add a `GhostConnectionComponentType` component to each connection to determine which tile the connection should prioritize.
As mentioned, this `GhostSendSystem` passes this per-connection information to the `BatchScaleImportanceFunction` function.

The `GhostImportanceDataType` is global, static, singleton data, which configures how chunks are constructed. It's optional, and `IntPtr.Zero` will be passed if it is not found.
**Importantly: This static data _must_ be added to the same entity that holds the `GhostImportance` singleton. You'll get an exception in the editor if this type is not found here.**
`GhostSendSystem` will fetch this singleton data, and pass it to the importance scaling function.

`GhostImportancePerChunkDataType` is added to each ghost, essentially forcing it into a specific chunk. The `GhostSendSystem` expects the type to be a shared component. This ensures that the elements in the same chunk will be grouped together by the entity system.
A user-created system is required to update each entity's chunk to regroup them (example below). It's important to think about how entity transfer between chunks actually works (i.e. the performance implications), as regularly changing an entities chunk will not be performant.

## Distance-based importance

The built-in form of importance scaling is distance-based (`GhostDistanceImportance.Scale`). The `GhostDistanceData` component describes the size and borders of the tiles entities are grouped into.

### An example set up for distance-based importance in Asteroids

The [Asteroids Sample](https://github.com/Unity-Technologies/multiplayer/tree/master/sampleproject/Assets/Samples/Asteroids) makes use of this default scaling implementation. The `LoadLevelSystem` sets up an entity to act as a singleton with `GhostDistanceData` and `GhostImportance` added:

```c#
    var gridSingleton = state.EntityManager.CreateSingleton(new GhostDistanceData
    {
        TileSize = new int3(tileSize, tileSize, 256),
        TileCenter = new int3(0, 0, 128),
        TileBorderWidth = new float3(1f, 1f, 1f),
    });
    state.EntityManager.AddComponentData(gridSingleton, new GhostImportance
    {
        ScaleImportanceFunction = GhostDistanceImportance.ScaleFunctionPointer,
        GhostConnectionComponentType = ComponentType.ReadOnly<GhostConnectionPosition>(),
        GhostImportanceDataType = ComponentType.ReadOnly<GhostDistanceData>(),
        GhostImportancePerChunkDataType = ComponentType.ReadOnly<GhostDistancePartitionShared>(),
    });
```
>[!NOTE]
> Again, you _must_ add both singleton components to the same entity.

The `GhostDistancePartitioningSystem` will then split all the ghosts in the World into chunks, based on the tile size above.
Thus, we use the Entities concept of chunks to create spatial partitions/buckets, allowing us to fast cull entire sets of entities based on distance to the connections character controller (or other notable object).

How? Via another user-definable component: `GhostConnectionPosition` can store the position of a players entity (`Ship.prefab` in Asteroids), which (as mentioned) is passed into the `Scale` function via the `GhostSendSystem`, allowing each connection to determine which tiles (i.e. chunks) that connection should prioritize.
In Asteroids, this component is added to the connection entity when the (steroids-specific) `RpcLevelLoaded` RPC is invoked:
```c#
    [BurstCompile(DisableDirectCall = true)]
    [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
    private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
    {
        var rpcData = default(RpcLevelLoaded);
        rpcData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);

        parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new PlayerStateComponentData());
        parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, default(NetworkStreamInGame));
        parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, default(GhostConnectionPosition)); // <-- Here.
    }
```

Which is then updated via the Asteroids server system `UpdateConnectionPositionSystemJob`:
```c#
        [BurstCompile]
        partial struct UpdateConnectionPositionSystemJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<LocalTransform> transformFromEntity;
            public void Execute(ref GhostConnectionPosition conPos, in CommandTarget target)
            {
                if (!transformFromEntity.HasComponent(target.targetEntity))
                    return;
                conPos = new GhostConnectionPosition
                {
                    Position = transformFromEntity[target.targetEntity].Position
                };
            }
        }
```

### Writing my own importance scaling function

Crucially: Every component and function used in this process is user-configurable.
You simply need to:
1. Define the above 3 components (a per-connection component, an optional singleton config component, and a per-chunk shared component), and set them in the `GhostImportance` singleton.
2. Define your own Scaling function, and again set it via the `GhostImportance` singleton.
3. Define your own version of a `GhostDistancePartitioningSystem` which moves your entities between chunks (via writing to the shared component).

## Avoid rollback predicted ghost on structural changes

When the client predict ghost (either using predicted or owner-predicted mode), in case of structural changes (add/remove component on a ghost), the entity
state is rollback to the `latest received` snapshot from the server and re-simulated up to the current client predicted server tick.

This behavior is due to the fact, in case of structural changes, the current prediction history backup for the entity is "invalidated", and we don't continue predicting
since the last fully simulated tick (backup). We do that to ensure that when component are re-added the state of the component is re-predicted (even though this is a fair assumption, it still an approximation, because the other entities are not doing the same),

This operation can be extremely costly, especially with physics (because of the build reworld step).
It is possible to disable this bahaviour on a per-prefab basis, by unackecking the `RollbackPredictionOnStructuralChanges` toggle in the GhostAuthoringComponent inspector.

When the `RollbackPredictionOnStructuralChanges` is set to false, the GhostUpdateSystem will try to reuse the current backup if possible, preserving a lot of CPU cycle. This is in general a very good optimization that you would like to enable by default.

However there are (at the moment) some race conditions and **differences in behaviour when a replicated component is removed from the ghost**. In that case, because the entity is not rollback, the value of this component when it is re-added to entity
may vary and, depending on the timing, different scenario can happen.

In particular, If a new update for this ghost is received, the snapshot data will contains the last value from the server. However, if the component is missing at that time, the value of the component will be not restored.
When later, ther component is re-added, because the entity is not rollback and re-predicted, the current state of the re-added component is still `default` (all zeros).
For comparison, if this optimization is off, because the entity is re-predicted, the value of the re-added component is restored correctly.

If you know that none of the replicated component are removed for your ghost at runtime (removing others does not cause problem), it is strongly suggested to enable this optimzation, by disabling the default behaviour.

## Reduce serialization/deserialization CPU cost (single-baseline vs three-baseline)

Sending and receiving ghost data involves expensive CPU read/write operations, which scales roughly linearly with the the number of ghosts serialized in a packet (in other words; the more the server can pack,
the larger the cost of serialization and deserialization).

The server serializes ghost data in "batches", and uses a **predictive-delta-compression** strategy for compressing the data:
the replicated fields are delta-encoded against a `predicted` value (similar to a linear extrapolation), extrapolated from the last
3 baselines (i.e. previously acked values).

The client applies the same strategy when decoding: it uses the same baselines (as communicated by the server), predicts the new value, and then decompresses against said predicted value.

This idea is very effective for ghost data that is predictable: E.g. timers, linear movements, linear increments/decrements and so on. The predictor very often matches exactly (or very closely) the current
state value, therefore the serialized delta value is either 0 or very close to it, allowing good bandwidth reductions.

However, there are some downsides to three-baseline predictive-delta-compression:
- it requires netcode to continue to send snapshots to the client about a ghost, even if said ghosts data has not changed at all.
- The CPU encoding cost (on the server) is slightly higher.
- The CPU decoding cost (on the client) is also slightly higher, particularly for GhostField's that infrequently change.

Therefore: Three-baselines is effective only for "predictable" i.e. "linearly changing" fields. In unpredictable cases, it does not save many bits.

It is possible to reduce some of this encoding cost on a per-archetype basis by toggling the `UseSingleBaseline` option in the [GhostAuhoringComponent](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html).
When set, it instructs the server to **always** use a single baseline for delta compression for this specific prefab type.

In case you would like to test the impact of using single baseline for all ghosts and without changing all the prefabs, you can use the [GhostSendSystemData.ForceSingleBaseline](https://docs.unity3d.com/Packages/com.unity.netcode@1.4/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_ForceSingleBaseline) property.
This options can be used during the development to help you understand/verify what it is the impact of using single baseline in your game, in both bandwidth and cpu.

Enabling this option positively affect CPU for both client and server, especially when the archetype has a large number of components and/or many fields which rarely change.
The more visible savings are on the client side, where deserialization time (see the `GhostReceiveSystem` profile marker in the profile) is usually reduced by a good ~50%.

Also note that using single-baseline enables a specific bandwidth optimization (and subsequent CPU saving on server): When any replicated entity has not changed for a certain period of time, single-baseline is able to stop re-sending the ghost chunk entirely,
where previously these were sent in all cases (as they were needed for three-baseline).

Therefore, the **UseSingleBaseline** option can lead to significant savings in two common scenarios;
* When a ghost prefab is suited for **OptimizationMode.Dynamic**, but has frequent periods of inactivity.
* When the majority of the component data changes on a ghost type do not follow linear, predictable patterns,
thus the three baselines cost is not justified.

> REMARKS: the **UseSingleBaseline** toggle works on a "per-prefab" basis, not on a "per-component" basis.
> In its ideal form, the baselines prediction should be applied on a "per-component" basis (that is; the individual component specify which algorithm to use). At the moment, this feature it is not supported.

> REMARKS: when ghosts are configure to use **Static Optimization** the prefab is always serialized using a single baseline.

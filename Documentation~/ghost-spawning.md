# Spawn ghosts

After creating ghost prefabs (and defining how they're [synchronized](ghost-snapshots.md#synchronizing-ghost-components-and-fields) between the client and server), [ghosts](ghost-snapshots.md#ghosts) can spawned by:

- Instantiating them on your server via server simulation logic. All entities instantiated from a ghost prefab by the server are automatically spawned and replicated to the client. The spawning and despawning is handled by the Netcode for Entities package.
- Instantiating ghosts prefabs configured as `Predicted` or `OwnerPredicted` on the client [using predicted spawning](#implement-predicted-spawning-for-player-spawned-objects).
- By adding instances of ghost prefabs inside a sub-scene (in-scene placed objects). See [pre-spawned](#pre-spawned-ghosts) for more details.

## Spawn ghosts on the server

The server can spawn replicated entities in two ways:

- By instantiating the prefab via the `EntityManager.Instantiate` method (or variants).
- By using [pre-spawned ghosts](#pre-spawned-ghosts).

In both cases, the server is allowed to spawn new ghosts at any point in time and systems:

- Before clients are connected.
- After clients are connected.
- In any place or groups inside `SimulationSystemGroup` (suggested and preferred).
- Inside the `InitializatioSystemGroup` (this group does not run at a fixed step so care may be necessary).

Because the server has authority, by default all replicated entities present on the server are automatically spawned on each client by the [snapshot replication](ghost-snapshots.md#snapshots) sub-system. Updates from the server version of each ghost are then sent to each client, as defined by the ghost's synchronization settings.

### Limit replicated entities on a per-client basis

There are situations where the default behavior of replicating all entities to all clients is not desirable. For example, in large virtual worlds, clients usually interact and view only a subset of the world (the area nearby the player location), and in team versus team battles certain replicated entities are team-specific.

On the server-side you can use [relevancy](optimizations.md#relevancy) to specify, on a per-client basis, which entities need to be replicated or not.

## Spawning ghosts on the client

You can spawn a ghost on clients in multiple different ways, as described in the [spawn types](#spawn-types) table below.

### Spawn types

| Type                                                  | Description                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
|-------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Delayed or interpolated spawning                      | [Interpolated](interpolation.md) ghosts don't use [prediction](prediction-n4e.md) and aren't immediately spawned when the client world starts. Otherwise, the ghost object would appear when the first snapshot arrives, even if its ghost data is only applicable for a later interpolation tick. _For example; a player ghost would appear to spawn, idle for a few ticks, and then begin to interpolate (as new data is finally received from the server)._<br/><br/>Instead, they are spawned on the __Interpolation Timeline__. This delay in spawning is governed by the interpolation timeline delay, which can be configured via [`ClientTickRate.InterpolationTimeNetTicks`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.InterpolationTimeNetTicks.html) (or [`ClientTickRate.InterpolationTimeMS`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.InterpolationTimeMS.html)). Interpolated ghosts spawn when the [`NetworkTime.InterpolationTick`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkTime.InterpolationTick.html) is greater or equal to a ghosts [`GhostInstance.spawnTick`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostInstance.spawnTick.html). Refer to [time synchronization](time-synchronization.md) for more information about interpolation delay and interpolation tick. |
| Predicted spawning for client-predicted player spawns | The spawned ghost is [predicted](prediction-n4e.md), and typically instantiated in response to inputs raised on the client. This usually applies to objects that the player spawns, like in-game bullets or rockets that the player fires. Refer to [implementing predicted spawning for player-spawned objects](#implementing-predicted-spawning-for-player-spawned-objects) for more information. Predictively spawning ghosts in this way removes round trip spawn delays and reduces perceived latency, improving gameplay quality. If/when the server authoritative snapshot data arrives for the ghost object, we first map our predicted spawn entity to the real ghost entity (in a process known as 'Ghost Classification'), and then the `GhostUpdateSystem` applies the data directly to the predicted ghost, and plays back the local inputs that have happened since that time. If the predictive spawn was created by the client in error, the prediction error is corrected by destroying the predicted ghost.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| Pre-spawned Ghost (i.e. Ghost Prespawns)              | All ghost prefabs dragged into a sub scene - at authoring time - are considered prespawns. These are typically level-specific gameplay entities like spawn points, destructible rocks, openable doors, loot chests, weapon pickups etc. [See details of pre-spawned ghosts](#pre-spawned-ghosts).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |

Netcode for Entities doesn't require a specific spawn message for client-side ghosts. When the client receives a new ghost ID from the server, it's treated as an implicit spawn and the ghost is assigned a [spawn type](#spawn-types) based on a set of classification systems.

Once you have the spawn type, the [`GhostSpawnSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnSystem.html) handles instantiating the new entity.

> [!NOTE]
> Ghost entities can only be spawned if the ghost prefabs are loaded in the world. Server and client need to agree on the prefabs they have and the server will only replicate to the client ghosts for which the client has the prefab.

## Implement predicted spawning for player-spawned objects

Like other aspects of [client prediction](intro-to-prediction.md#client-prediction), predicted spawns require the same logic to be run on both the client and server, to make sure that the two are as deterministic as possible.

The predicted spawning process on the client requires two steps:

1. Create the entity in a system that runs inside the prediction loop.
2. After the entity has been created, the entity must be first 'classified' as a predicted spawn and then
matched with the authoritative update received from the server.

### Spawn predicted ghosts on the client side

To spawn predicted ghosts on the client side, you need to add your spawn system to the [`PredictedSimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html) to make the client code instantiate the spawn under the same conditions that the server does (for example, after the player presses the shoot mouse button).

All ghost prefabs configured to be predicted upon spawn have the [`PredictedGhostSpawnRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictedGhostSpawnRequest.html) component already added to them, and are therefore treated as predicted spawns by default.

When your system (running in the client world) instantiates the ghost entity, it's already treated as a predicted spawn automatically, and the only change required to your system (to make it correct) is to add an early out for `networkTime.IsFirstTimeFullyPredictingTick`.

When the first snapshot update for this entity arrives on the client, the system detects that the received update is for an entity already spawned by the client and from that time on, all the updates are applied to it.

In the prediction system code, the [`NetworkTime.IsFirstTimeFullyPredictingTick`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTime.html) value needs to be checked to prevent the spawned object from being spawned multiple times as data is rolled back and re-simulated as part of the prediction loop.

```csharp
public void OnUpdate()
{
    // Other input like movement handled here or in another system...

    var networkTime = SystemAPI.GetSingleton<NetworkTime>();
    if (!networkTime.IsFirstTimeFullyPredictingTick)
        return;
    // Handle the input for instantiating a bullet for example here
    // ...
}
```

#### Conditions to check before spawning predicted ghosts

Clients shouldn't be allowed to spawn an entity until:

1. A singleton `NetworkStreamConnectionInGame` exists. This is a necessary requirement because otherwise the created entity will be disposed of automatically.
2. The `GhostCollectionPrefab` buffer has been initialized (length > 0) and:
    - A ghost prefab matching the spawned entity `GhostType` component exists in that buffer.
    - Alternatively, an entry in the `GhostCollection.GhostTypeToColletionIndex` hashmap is present (this is a faster check).

```csharp
[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
public partial class SpawnGhost : SystemBase
{
    protected override void OnUpdate()
    {
        var networkTime = SystemAPI.GetSingleton<NetworkTime>();
        //Only do that once. When the client re-simulate the same tick, the flag is false.
        if(!networkTime.IsFirstTimeFullyPredictedTick())
            return;
        var prefab = GetPrefabToSpawn();
        var typeToCollection = SystemApi.GetSingleton<GhostCollection>() GhostTypeToColletionIndex;
        var type = World.EntityManager.GetComponentData<GhostType>(prefab);
        //Can't spawn yet. The prefab is not registered.
        if(!typeToCollection.ContainsKey(type))
            return;
        //it is now valid to spawn. That does not means the ghost will be initialized properly yet
        //that can be still the case if the
    }
}
```

The `GhostCollection` data condition is critical for spawning predicted ghosts because the data is required by the
`PredictedGhostSpawningSystem` to initialize the new ghosts.

If the originating prefab isn't found inside the `GhostCollectionPrefab` during this initialization phase, exceptions are thrown to flag that the current predicted spawn can't be handled.

#### Specify rollback options for predicted spawned ghosts

When a ghost is predicted by the client (owner-predicted or predicted ghost modes), you can specify how predicted spawned ghosts handle [prediction and rollback](intro-to-prediction.md#rollback-and-replay) until the authoritative spawned ghost has been confirmed and received by the client.

By checking the **Rollback Predicted Spawned Ghost State** toggle in the Ghost Authoring component inspector, the unclassified spawned ghosts on the client roll back and resimulate their state starting from the spawn tick when a new snapshot (that contains the predicted ghost) is received from the server.

This can alleviate some misprediction errors caused by ghost-ghost interaction (refer to [prediction error and mitigation](prediction-details.md#predicted-spawn-interactions-with-other-predicted-ghosts)).

### Ghost classification and entity matching

The process of matching a predicted spawned ghost to its server-authoritative counterpart is referred to as classification. If classification fails, the locally predicted spawn is deleted after a grace period.

Netcode for Entities provides a default classification strategy to automatically handle all the client-spawned predicted objects by the client, which is implemented by the [`GhostSpawnClassificationSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnClassificationSystem.html).

The `GhostSpawnClassificationSystem` uses a tick-based check to match newly received ghosts with any of the
client-predicted spawned ones based on their types and spawning tick (within a five-tick window).

The default implementation has some limitations:

- It can only reliably match one predicted spawned ghost of a single type inside the five-tick window. For example, if you spawn multiple bullets in the same tick then only one of them can be matched, and the match may not be the correct one. The spawning tick value alone is not enough to resolve the individual bullet identity.
- If [tick batching](client-server-worlds.md#avoiding-performance-issues) is enabled and the server batches ticks together then inputs can be applied on a different tick than the one they were issued on, causing entities to be spawned at a slightly different tick than intended. These differences can impact the `GhostSpawnClassificationSystem` logic and cause it to fail to match ghosts correctly.

If you want more control or advanced logic for matching spawned ghosts, then you can [add your own classification system](#add-your-own-classification-system) to override the default behavior.

### Add your own classification system

You can create your own classification system to override the default client classification. Your custom classification system must:

- Update in the [`GhostSimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSimulationSystemGroup.html).
- Run after the [`GhostSpawnClassificationSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnClassificationSystem.html).

The classification system works by inspecting the ghosts that need to be spawned by retrieving the
[`GhostSpawnBuffer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnBuffer.html) on the singleton
[`GhostSpawnQueueComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostSpawnQueue.html) entity and changing their `SpawnType`.

Each entry in the `GhostSpawnQueueComponent` list should be compared to the entries in the [`PredictedGhostSpawn`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictedGhostSpawn.html) buffer on the singleton with a [`PredictedGhostSpawnList`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictedGhostSpawnList.html) component.
If the two entries have the same type and match, then the classification system should set the `PredictedSpawnEntity` property in the [`GhostSpawnBuffer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnBuffer.html) element and remove the entry from `PredictedGhostSpawn` buffer.

```csharp
public void Execute(DynamicBuffer<GhostSpawnBuffer> ghosts, DynamicBuffer<SnapshotDataBuffer> data)
{
    var predictedSpawnList = PredictedSpawnListLookup[spawnListEntity];
    for (int i = 0; i < ghosts.Length; ++i)
    {
        var newGhostSpawn = ghosts[i];
        if (newGhostSpawn.SpawnType != GhostSpawnBuffer.Type.Predicted || newGhostSpawn.HasClassifiedPredictedSpawn || newGhostSpawn.PredictedSpawnEntity != Entity.Null)
            continue;

        // Mark all the spawns of this type as classified even if not our own predicted spawns
        // otherwise spawns from other players might be picked up by the default classification system when
        // it runs.
        if (newGhostSpawn.GhostType == ghostType)
            newGhostSpawn.HasClassifiedPredictedSpawn = true;

        // Find new ghost spawns (from ghost snapshot) which match the predict spawned ghost type handled by
        // this classification system. You can use the SnapshotDataBufferLookup to inspect components in the
        // received snapshot in your matching function
        for (int j = 0; j < predictedSpawnList.Length; ++j)
        {
            if (newGhostSpawn.GhostType != predictedSpawnList[j].ghostType)
                continue;

            if (YOUR_FUZZY_MATCH(newGhostSpawn, predictedSpawnList[j]))
            {
                newGhostSpawn.PredictedSpawnEntity = predictedSpawnList[j].entity;
                predictedSpawnList[j] = predictedSpawnList[predictedSpawnList.Length - 1];
                predictedSpawnList.RemoveAt(predictedSpawnList.Length - 1);
                break;
            }
        }
        ghosts[i] = newGhostSpawn;
    }
}
```

Inside your classification system you can use the [`SnapshotDataBufferLookup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnQueue.html) to:

- Check for component presence in the ghost archetype.
- Retrieve from the snapshot data associated with the new ghost any replicated component type.

## Pre-spawned ghosts

Ghosts that are saved directly into subscenes are referred to as pre-spawned ghosts, and are spawned when the subscene is loaded on both client and server worlds. If a pre-spawned ghost hasn't changed on the server (from the values it had when originally baked into the subscene), then no snapshot updates are required to activate it on the client. In other words, it's treated as already acknowledged.
Therefore, even if there are a few changes, those changes are delta-compressed against the subscene baseline.

Pre-spawns are best suited for ghosts that are:

* **Persistent**: that share their lifetimes with the subscene itself. Destroying a pre-spawned ghost and replacing it with a new instance is inefficient, because new joiners will not only need to handle the destruction of the original pre-spawn (which they've already loaded), but then replicate the new spawn as a completely new ghost.
* **Statically optimized**: benefitting from the fact that most of their data will be delta-compressed to ~0 in the common case.

Good examples of pre-spawned ghosts in a game include trees you can chop down which regrow (which are disabled, not destroyed when chopped), and doors that can be opened and closed by players.

> [!NOTE]
> Regarding persistence: be careful using pre-spawns with [ghost relevancy](optimizations.md#relevancy), because all pre-spawned ghosts that are irrelevant to a client will need to be loaded on said client, then marked for deletion by the server (as irrelevant), and then deleted on said client (via events sent inside the snapshot).
> Irrelevant ghosts therefore have the same pitfalls as manually destroyed pre-spawns, and so it may be more efficient to treat pre-spawns as always relevant (and statically optimized too, where possible).
> Alternatively, you can convert these regularly irrelevant pre-spawns into runtime spawns, replacing their subscene entries with server-only spawners, as the client will therefore only receive the ghosts which are actually relevant to begin with.

To create a pre-spawned ghost, place a ghost prefab into a subscene in the Unity Editor. To do so:

1. Right click on the **Hierarchy** in the inspector and click **New Subscene**.
2. Drag an instance of a ghost prefab into the newly created subscene.

<img src="images/prespawn-ghost.png" alt="prespawn ghost" width="700">

### Pre-spawned ghost limitations

There are some limitations when creating pre-spawned ghosts:

- Pre-spawned ghosts must be an instance of a ghost prefab.
- Pre-spawned ghosts must be placed into a subscene (i.e. not directly into a scene), like all other authored/baked entities.
- Pre-spawned ghosts in the same scene can't have the exact same position and rotation as another pre-spawn (as their `LocalTransform` is used to deterministically sort them).
- Pre-spawned ghosts must always be placed on the main scene section (section 0).
- The `GhostAuthoringComponent` on the pre-spawned ghost cannot be configured differently than its ghost prefab source (as that data is handled on a ghost type basis, not a per-scene-instance basis), and is therefore marked as read-only. However, other authoring data can be modified, as expected.

### How pre-spawned ghosts work

At [baking time](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/baking-overview.html), each subscene assigns a [`PreSpawnedGhostIndex`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PreSpawnedGhostIndex.html) to the ghosts it contains, which are unique IDs for the ghosts within that subscene.
The IDs are assigned by sorting the ghosts using a deterministic hash, differentiated by the ghost type (or prefab ID) and the [`SceneGUID`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.SceneSectionData.SceneGUID.html) of the scene section.
If two or more ghosts of the same type are added to the same subscene section (a very common case), uniqueness is determined by adding the entity's `Position` and `Rotation` to its ID (which is why there is the limitation of not supporting two or more ghosts of the same type being pre-spawned at the same scene location).
All of this is done because pre-spawned ghosts can't be given unique, deterministic ghost IDs at bake/build time.

Each subscene has a resulting combined hash that contains all the ghosts' calculated hashes, which is extracted and used to:

- Group the pre-spawned ghosts on a per-subscene basis by assigning a [`SubSceneGhostComponentHash`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SubSceneGhostComponentHash.html) shared component to all the ghosts in the scene.
- Add to the first [`SceneSection`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.SceneSection.html) in the subscene a [`SubSceneWithPrespawnGhosts`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SubSceneWithPrespawnGhosts.html) component, which is used by the runtime to handle subscenes with pre-spawned ghosts.

>[!NOTE]
> Additionally, for safety reasons, each pre-spawn ghost is baked with the `Disabled` component, to hide it from user-land systems until it has been fully re-initialized at runtime (i.e. the scene is loaded, and the serialization baseline has been calculated).

At runtime, when a subscene has been loaded, it's processed by both client and server:

- For each pre-spawned ghost, a pre-spawn baseline is extracted and used to delta compress the ghost component when it's first sent (for bandwidth optimization).
- The server assigns a unique ghost ID range to each subscene, which is used to assign distinct ghost ID's to the newly instantiated pre-spawned ghosts based on their [`PreSpawnedGhostIndex`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PreSpawnedGhostIndex.html).
- The server replicates these assigned ID ranges for each subscene (identified by the hash assigned to the `SubSceneWithPrespawnGhosts` component) using an internal ghost entity named 'PrespawnSceneList'.
- Once the client has loaded the subscene and received the ghost range, it then:
  - Assigns - to each pre-spawned ghost - its server authoritative ghost ID.
  - Reports to the server that it's ready to stream the pre-spawned ghosts (via [RPC](rpcs.md)).

For both client and server, when a subscene has been processed (and ghost ID assigned), a `PrespawnsSceneInitialized` internal component is added to the main `SceneSection`.
The client automatically tracks when subscenes with pre-spawned ghosts are loaded or unloaded, and reports to the server to stop streaming pre-spawned ghosts associated with them.

All the pre-spawn ghost ID setup described in the previous paragraphs is done automatically, so nothing special needs to be done to keep pre-spawned ghosts synchronized between client and server.

> [!NOTE]
> If pre-spawned ghosts are moved before going in-game, or in general before the baseline is calculated properly, then data may be not replicated correctly (the snapshot delta compression will fail).
> Both server and client calculate a cyclic redundancy check (CRC) of the baseline and this hash is validated when clients connect. A mismatch will cause a disconnection. This is also why ghosts are `Disabled` when the scene is loaded.

#### Dynamically loading subscenes with pre-spawned ghosts

You can load a subscene at runtime with pre-spawned ghosts while you're already in-game and the pre-spawned ghosts are automatically handled and synchronized. You can also unload subscenes that contain pre-spawned ghosts on demand. Netcode for Entities handles it automatically, and the server stops reporting the pre-spawned ghosts for sections the client has unloaded.

## Additional resources

* [Ghosts and snapshots](ghost-snapshots.md)
* [Serializing and synchronizing with `GhostFieldAttribute`](ghostfield-synchronize.md)
* [`GhostSpawnSystem` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnSystem.html)
* [Introduction to prediction](intro-to-prediction.md)
* [`ClientPopulatePrespawnedGhostsSystem` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientPopulatePrespawnedGhostsSystem.html)
* [`ClientTrackLoadedPrespawnSections` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientTrackLoadedPrespawnSections.html)
* [`ServerPopulatePrespawnedGhostsSystem` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ServerPopulatePrespawnedGhostsSystem.html)
* [`ServerTrackLoadedPrespawnSections` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ServerTrackLoadedPrespawnSections.html)

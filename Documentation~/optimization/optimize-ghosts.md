# Ghost optimization

Optimize your ghosts to improve the performance of your game.

* [Importance scaling](#importance-scaling)
* [Ghost relevancy](#ghost-relevancy)
* [Preserialize ghosts](#preserialize-ghosts)
* [__Optimization Mode__](#optimization-mode)

## Importance scaling

The server operates with a fixed bandwidth target and sends a single snapshot packet of customizable size on every network tick.
It fills this packet with the ghosts with the highest importance, determined by a priority queue of ghost chunks (rebuilt each tick).
Therefore, importance is determined at the ghost chunk level, not on each instance individually.

Several factors determine the importance of each ghost chunk:

* You can specify the base [`GhostAuthoringComponent.Importance`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html#Unity_NetCode_GhostAuthoringComponent_Importance) per ghost type.
    * Netcode for Entities multiplies this base importance value by `ticksSinceLastSent` (not `ticksSinceLastAcked`), as well as other modifiers such as [`GhostSendSystemData.IrrelevantImportanceDownScale`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_IrrelevantImportanceDownScale) and [`GhostSendSystemData.FirstSendImportanceMultiplier`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_FirstSendImportanceMultiplier).
* You can also supply your own method to scale importance on a per-chunk, per-connection basis, using [`GhostImportance.BatchScaleImportanceFunction`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostImportance.html#Unity_NetCode_GhostImportance_BatchScaleImportanceFunction). For example, this allows you to [deprioritize far away ghosts, in favor of nearby ones](#distance-based-importance).
* [`GhostAuthoringComponent.MaxSendRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html#Unity_NetCode_GhostAuthoringComponent_MaxSendRate) doesn't directly impact importance values. It's a pre-pass that prevents a ghost chunk from being added to the priority queue at all (for this tick).

Once a packet has reached the bandwidth target, the server sends it. The remaining ghost entities aren't sent on this tick, but they are more likely to be in the next snapshot because of `ticksSinceLastSent` scaling.

> [!NOTE]
> Ghost group children do not support relevancy (nor importance, MaxSendRate, static-optimization etc.) until they've left the group, refer to the [ghost groups page](../ghost-groups.md) for more information.

### Set up ghost importance scaling

The following is an example of how to set up the built-in distance-based importance scaling in Netcode for Entities. If you want to use a custom importance implementation, you can reuse parts of the built-in solution or replace it with your own.

#### `GhostImportance`

[`GhostImportance`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostImportance.html) is the configuration component for setting up importance scaling. [`GhostSendSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystem.html) invokes the `BatchScaleImportanceFunction` only if the `GhostConnectionComponentType` and `GhostImportanceDataType` are created.

You can set the following fields on `GhostImportance`:

- [`BatchScaleImportanceFunction`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostImportance.html#Unity_NetCode_GhostImportance_BatchScaleImportanceFunction) allows you to write and assign a custom scaling function (to scale the importance, with chunk granularity).
- [`GhostConnectionComponentType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostImportance.html#Unity_NetCode_GhostImportance_GhostConnectionComponentType) is the type added per connection, allowing you to store per-connection data that's needed in the scaling calculation.
- [`GhostImportanceDataType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostImportance.html#Unity_NetCode_GhostImportance_GhostImportanceDataType) is an optional singleton component, allowing you to pass in any of your own static data necessary in the scaling calculation.
- [`GhostImportancePerChunkDataType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostImportance.html#Unity_NetCode_GhostImportance_GhostImportancePerChunkDataType) is the shared component added per chunk, storing any chunk-specific data used in the scaling calculation.

#### Order of operations

First, the function pointer is invoked by the `GhostSendSystem` for each chunk and returns the importance scaling for the entities contained within that chunk. The signature of the method is of the delegate type `GhostImportance.ScaleImportanceDelegate` and the parameters are `IntPtr`s, which point to instances of the three types of data described above.

You must add a `GhostConnectionComponentType` component to each connection to determine which tile the connection should prioritize. The `GhostSendSystem` then passes this per-connection information to the `BatchScaleImportanceFunction` function.

The `GhostImportanceDataType` is global, static, singleton data that configures how chunks are constructed. It's optional, and `IntPtr.Zero` is passed if it's not found. `GhostSendSystem` fetches this singleton data and passes it to the importance scaling function.

> [!NOTE]
> The `GhostImportanceDataType` must be added to the same entity as the `GhostImportance` singleton. If it isn't, an exception is thrown in the Editor.

`GhostImportancePerChunkDataType` is then added to each ghost, essentially forcing it into a specific chunk. The `GhostSendSystem` expects the type to be a shared component. This ensures that the elements in the same chunk are all grouped together by the entity system. A user-created system is required to update each entity's chunk to regroup them (example below). It's important to think about how entity transfer between chunks actually works (namely the performance implications) because regularly changing an entity's chunk is not efficient.

### Distance-based importance

The built-in form of importance scaling in Netcode for Entities is distance-based ([`GhostDistanceImportance.Scale`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostDistanceImportance.html)) and uses tiling to group entities into spatial chunks. The `GhostDistanceData` component describes the size and borders of the tiles entities are grouped into.

#### Distance-based importance in Asteroids

The [Asteroids sample project](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/Asteroids) uses Netcode for Entities' default scaling implementation. The `LoadLevelSystem` sets up an entity to act as a singleton with `GhostDistanceData` and `GhostImportance` added:

```c#
    var gridSingleton = state.EntityManager.CreateSingleton(new GhostDistanceData
    {
        TileSize = new int3(tileSize, tileSize, 256),
        TileCenter = new int3(0, 0, 128),
        TileBorderWidth = new float3(16f, 16f, 16f),
    });
    state.EntityManager.AddComponentData(gridSingleton, new GhostImportance
    {
        BatchScaleImportanceFunction = GhostDistanceImportance.ScaleFunctionPointer,
        GhostConnectionComponentType = ComponentType.ReadOnly<GhostConnectionPosition>(),
        GhostImportanceDataType = ComponentType.ReadOnly<GhostDistanceData>(),
        GhostImportancePerChunkDataType = ComponentType.ReadOnly<GhostDistancePartitionShared>(),
    });
```

>[!NOTE]
> Again, you must add both singleton components to the same entity.

The [`GhostDistancePartitioningSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostDistancePartitioningSystem.html) then splits all the ghosts in the world into chunks, based on the tile size defined above. Using the configurable component [`GhostConnectionPosition`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostConnectionPosition.html) and the Entities concept of [chunks](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/components-chunk-introducing.html), Netcode for Entities can create spatial partitions that enable the fast culling of entire sets of entities based on distance to the connection's character controller (or other notable object).

`GhostConnectionPosition` stores the position of a player's entity (`Ship.prefab` in the Asteroids example), which is passed into the `Scale` function via the `GhostSendSystem`, allowing each connection to determine which tiles (chunks) that connection should prioritize.

In Asteroids, this component is added to the connection entity when the (Asteroids-specific) `RpcLevelLoaded` RPC is invoked:

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

### Create a custom importance scaling function

Every component and function used in importance scaling is configurable. To create a custom importance scaling function, you need to do three things:

1. Define the three components above (a per-connection component, an optional singleton config component, and a per-chunk shared component), and set them in the `GhostImportance` singleton.
2. Define your own scaling function and set it via the `GhostImportance` singleton.
3. Define your own version of a `GhostDistancePartitioningSystem` which moves your entities between chunks (via writing to the shared component).

## Ghost relevancy

Ghost relevancy, also known as ghost filtering, is a server feature that allows you to define under what conditions a specific ghost entity is replicated on a client. You can use this to:

* Define a maximum replication distance for ghosts so that they only spawn when near a player.
* Create a server-side, anti-cheat fog of war that prevents clients from knowing about ghosts that they shouldn't be able to see.
* Only allow specific clients to be notified of a ghost's state, such as an item being dropped in a hidden information game.
* Create client-specific ghosts, such as NPCs that are only visible to a player when they've completed some quest condition.
* Temporarily pause all replication on a client while that client is in a specific state, such as when a player has died and is waiting to respawn.

Use ghost relevancy to avoid replicating entities that the player can neither see nor interact with.

> [!NOTE]
> Ghost group children do not support relevancy (nor importance, MaxSendRate, static-optimization etc.) until they've left the group, refer to the [ghost groups page](../ghost-groups.md) for more information.

The [`GhostRelevancy`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostRelevancy.html) singleton component has the following controls:

* [`GhostRelevancyMode`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostRelevancy.html#Unity_NetCode_GhostRelevancy_GhostRelevancyMode) defines the behavior of the relevancy subsystem:
    * **Disabled**: The default setting. No relevancy is applied under any circumstances.
    * **SetIsRelevant**: Only ghosts added to the relevancy set (`GhostRelevancySet`) are considered relevant to that client and serialized for the specified connection (where possible: eventual consistency and importance scaling rules still apply).
        * If you have this setting as the default, then no ghosts will be replicated to any client unless they're in the `GhostRelevancySet`. This can be useful when it's rare or impossible for a player to be viewing the entire world.
    * **SetIsIrrelevant**: Ghosts added to relevancy set (`GhostRelevancySet`) are considered not relevant to that client and won't be serialized for the specified connection. In other words, use this mode if you want to specifically ignore entities for a given client.
* [`GhostRelevancySet`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostRelevancy.html#Unity_NetCode_GhostRelevancy_GhostRelevancySet) stores the connection-ghost pairs. The behavior of the set is defined by `GhostRelevancyMode`.
* [`DefaultRelevancyQuery`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostRelevancy.html#Unity_NetCode_GhostRelevancy_DefaultRelevancyQuery) is a global rule denoting that all ghost chunks matching this query are always considered relevant to all connections (unless you've added the ghosts in said chunk to the `GhostRelevancySet`). This is useful for creating general relevancy rules (for example: the entities in charge of tracking player scores are always relevant). `GhostRelevancySet` takes precedence over this rule. Refer to the [Asteroids sample](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/Asteroids/Authoring/Server/SetAlwaysRelevantSystem.cs) for an example implementation.

```c#
var relevancy = SystemAPI.GetSingletonRW<GhostRelevancy>();
relevancy.ValueRW.DefaultRelevancyQuery = GetEntityQuery(typeof(AsteroidScore));
```

> [!NOTE]
> If a ghost has been replicated to a client and is then set to **not be** relevant to that client, the client will be notified that the entity has been **destroyed**, and will replicate that change locally. This misnomer can be confusing, as the entity being despawned does not imply the server entity was destroyed.
> For example: despawning an enemy monster in a MOBA because it became hidden in the fog of war shouldn't trigger a death animation (nor S/VFX). Thus, use some other data to notify what kind of entity-destruction state your entity has entered (such as enabling an `IsDead`/`IsCorpse` component).

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

## Preserialize ghosts

By default, all ghosts are serialized once per connection on the server. This is done on demand and each ghost is only serialized when it's actually sent to a client. This serialization process can be expensive in terms of CPU, especially when the server has many connections and many ghosts. To reduce this cost, you can use preserialization.

Preserialization is a feature that allows you to serialize ghost data once and reuse it for all connections on the server. You can enable preserialization in two ways:

1. Enabling [`UsePreserialization`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html#Unity_NetCode_GhostAuthoringComponent_UsePreSerialization) in the `GhostAuthoringComponent` inspector on your ghost prefab. This causes all ghosts of this type to use preserialization.
2. Adding the [`PreSerializedGhost`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PreSerializedGhost.html) component to the ghost entity in the server world. This causes only this specific ghost to use preserialization.

When preserialization is enabled the server only serializes the ghost once for all connections. However, preserialized ghosts are serialized regularly on every tick, even if the ghost isn't going to be sent to any client. As a result, preserialization is only recommended for ghosts that are frequently sent to multiple clients (otherwise the CPU cost might be higher than the default behavior of serializing ghosts on demand).

## __Optimization Mode__

__Optimization Mode__ is a setting available on the `GhostAuthoringComponent` that changes how often Netcode for Entities resends the `GhostField` on a spawned entity. It has two modes: __Dynamic__ and __Static__.

* __Dynamic__: This is the default setting. Use this when you expect the ghost to change often. The ghost is optimized for a small snapshot size when both changing and not changing.
* __Static__: Use this when you expect the ghost to change infrequently. The ghost isn't optimized for a small snapshot size when changing, but isn't sent at all when it's not changing.

For example, if you spawn objects that never move, set the __Optimization Mode__ to __Static__ to ensure that Netcode for Entities doesn't resynchronize their Transform.

When a `GhostField` changes, Netcode for Entities sends the changes regardless of the __Optimization Mode__. It just optimizes the number and size of the snapshots sent.

### Limitations with static-optimized ghosts

* Static-optimized ghosts are forced to enable [`UseSingleBaseline`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostPrefabCreation.Config.html#Unity_NetCode_GhostPrefabCreation_Config_UseSingleBaseline).
* Static optimization isn't supported for ghosts involved in a [ghost group](../ghost-groups.md) (neither the root, nor ghost group children), nor for ghosts containing any replicated child components. In both of these cases, ghosts are treated as __Dynamic__ at runtime.
* Ghosts that are both static-optimized and interpolated won't run `GhostField` extrapolation (`SmoothingAction.InterpolateAndExtrapolate` is forced into `SmoothingAction.Interpolate`).

## Additional resources

* [Ghosts and snapshots](../ghost-snapshots.md)
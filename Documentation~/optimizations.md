# Optimizations

Netcode optimizations fall into two categories:

* The amount of CPU time spent on the client and the server (for example, CPU time being spent on serializing components as part of the GhostSendSystem)
* The size and amount of snapshots (which depends on how much and how often Netcode sends data across the network)

This page will describe different strategies for improving both.

## Optimization Mode

Optimization Mode is a setting available in the `Ghost Authoring Component` as described in the [Ghost Snapshots](ghost-snapshots.md) documentation. The setting changes how often Netcode resends the `GhostField` on a spawned entity. It has two modes: **Dynamic** and **Static**. For example, if you spawn objects that never move, you can set Optimization Mode to **Static** to ensure Netcode doesn't resync their transform.

When a GhostField change Netcode will send the changes regardless of this setting. We are optimizing the amount and the size of the snapshots sent.

* `Dynamic` optimization mode is the default mode and tells Netcode that the ghost will change often. It will optimize the ghost for having a small snapshot size when changing and when not changing.
* `Static` optimization mode tells Netcode that the ghost will change rarely. It will not optimize the ghost for having a small snapshot size when changing, but it will not send it at all when not changing.

### Serialization cost

When a `GhostField` changes, we serialize the data and synchronize it over the network. If a job has write access to a `GhostField`, Netcode will check if the data changed.

First, it serializes the data and compares it with the previous synchronized version of it. If the job did not change the `GhostField`, it discards the serialized result.

For this reason, it is important to ensure that no jobs have write access to data if it will not change to remove this hidden CPU cost.

## Importance Scaling
The server operates on a fixed bandwidth and sends a single packet with snapshot data of customizable size on every network tick. It fills the packet with the entities of the highest importance. Several factors determine the importance of the entities: you can specify the base importance per ghost type, which Unity then scales by age. You can also supply your own method to scale the importance on a per-chunk basis.

Once a packet is full, the server sends it and the remaining entities are missing from the snapshot. Because the age of the entity influences the importance, it is more likely that the server will include those entities in the next snapshot. Netcode calculates importance only per chunk, not per entity.

### Set-up required

Below is an example of how to set up the built-in distance-based importance scaling. If you want to use a custom importance implementation, you can reuse parts of the built-in solution or replace it with your own.

### GhostImportance

`GhostImportance` is the configuration component for setting up importance scaling. `GhostSendSystem` invokes the `ScaleImportanceFunction` only if the `GhostConnectionComponentType` and `GhostImportanceDataType` are created.

The fields you can set on this is:
- `ScaleImportanceFunction`
- `GhostConnectionComponentType`
- `GhostImportanceDataType`
- `GhostImportancePerChunkDataType`

The function pointer is invoked by the `GhostSendSystem` for each chunk and returns the importance scaling for the entities contained within this chunk. The signature of the method is of the delegate type `GhostImportance.ScaleImportanceDelegate`.
The parameters are `IntPtr`s which point to instances of the three types of data described in the other three parameters.

You must add a `GhostConnectionComponentType` component to each connection to determine which tile the connection should prioritize. This `GhostSendSystem` passes this information to the `Scale` function in `GhostDistanceImportance` which then uses it to scale the importance of a chunk based on the metrics defined in the scaling function.

The `GhostImportanceDataType` configures how chunks are constructed. The distance-based importance scaling describes the size and borders of the tiles entities are grouped on. GhostSendSystem will extract pass the data to the importance scaling function.

`GhostImportancePerChunkDataType` is added to each ghost. The `GhostSendSystem` expects the type to be a shared component. This ensures that the elements in the same chunk will be grouped together by the entity system. A system is required to update each entity's chunk to regroup them. It's important to think about the way the transfer between chunks works as regular changes will not be performant.

## Distance-based importance

If a singleton entity with the `GhostImportance` component and the `GhostDistanceData` component on it exists on the server, the `GhostDistancePartitioningSystem` will split all the ghosts in the World into groups based on the tile size in that singleton.

You can use a custom function to scale the importance per chunk.

First, you must add a `GhostConnectionPosition` component to each connection to determine which tile the connection should prioritize. This `GhostSendSystem` passes this information to the `Scale` function in `GhostDistanceImportance` which is then used to scale the importance of a chunk based on its distance in tiles or any other metric you define in your code.

### Distance-based Chunks

The `GhostDistancePartitioningSystem` is responsible for grouping entities into chunks. This system depends on `GhostDistanceData` and `GhostImportance` to be added to a singleton entity. You can choose to make your own algorithm for generating chunks or set up the `GhostDistanceData` which will enable the `GhostDistancePartitioningSystem`.

### An example set up for distance-based importance

In the asteroids sample, the `LoadLevelSystem` sets up an entity to act as a singleton with `GhostDistanceData` and `GhostImportance` added:

```c#
    var grid = EntityManager.CreateEntity();
    EntityManager.AddComponentData(grid, new GhostDistanceData
    {
        TileSize = new int3(tileSize, tileSize, 256),
        TileCenter = new int3(0, 0, 128),
        TileBorderWidth = new float3(1f, 1f, 1f),
    });
    EntityManager.AddComponentData(grid, new GhostImportance
    {
        ScaleImportanceFunction = GhostDistanceImportance.ScaleFunctionPointer,
        GhostConnectionComponentType = ComponentType.ReadOnly<GhostConnectionPosition>(),
        GhostImportanceDataType = ComponentType.ReadOnly<GhostDistanceData>(),
        GhostImportancePerChunkDataType = ComponentType.ReadOnly<GhostDistancePartitionShared>(),
    });
```

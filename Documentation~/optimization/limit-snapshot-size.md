# Limit snapshot size

Limit the size of your snapshots to reduce bandwidth consumption and improve performance.

Snapshots have a minimum send size to ensure that they're not sent unless at least some new or destroyed entities need to be replicated, but there are additional methods you can use to further optimize snapshot size.

* Use [`GhostAuthoringComponent.MaxSendRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html#Unity_NetCode_GhostAuthoringComponent_MaxSendRate) to limit the resend rate of each of your ghost prefab types. This can reduce total bandwidth consumption, particularly when snapshots are filling up with large ghosts with high priorities. For example, a `LootItem` ghost prefab type can be specified to only replicate, at most, on every tenth snapshot by setting `MaxSendRate` to 10.
    * Note that `MaxSendRate` is distinct from importance. `MaxSendRate` enforces a cap on the resend interval, whereas importance informs the `GhostSendSystem` of which ghost chunks should be prioritized in the next snapshot.
* Use the per-connection component [`NetworkStreamSnapshotTargetSize`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamSnapshotTargetSize.html) to stop serializing entities into a snapshot if/when the snapshot goes above the specified byte size (`Value`). You can use this to enforce a (soft) limit on per-connection bandwidth consumption. To apply a limit globally, set a non-zero value in [`GhostSendSystemData.DefaultSnapshotPacketSize`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_DefaultSnapshotPacketSize).
* Use [`GhostSendSystemData.MaxSendChunks`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_MaxSendChunks) to limit the maximum number of chunks added to any given snapshot.
* Use [`GhostSendSystemData.MaxIterateChunks`](https://docs.unity3d.com/Packages/com.unity.netcode@subfolder?=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_MaxIterateChunks) to limit the total number of chunks the `GhostSendSystem` iterates over and serializes when looking for ghosts to replicate. This can be useful when dealing with large numbers of static ghosts.
* Use [`GhostSendSystemData.MinSendImportance`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_MinSendImportance) to prevent a chunk's entities from being sent too frequently. You can also use [`GhostSendSystemData.FirstSendImportanceMultiplier`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_FirstSendImportanceMultiplier) to bump the priority of chunks containing new entities and ensure they're replicated quickly, regardless of the `MinSendImportance` setting.
    * It's recommended to use `GhostAuthoringComponent.MaxSendRate` instead of this global setting, where possible.

> [!NOTE]
> The optimizations described here are applied on the per-chunk level, and they're applied after a chunk's contents have been added to the snapshot. Thus, in practice, real send values will be higher. For example, if `MaxSendEntities` is set to 100, but you have two chunks, each with 99 entities, then you'd actually send 198 entities.

## Reduce snapshot history size

By default, Netcode for Entities stores up to 32 snapshot history buffer entries for each connection and ghost chunk pair, as defined by `GhostSystemConstants.SnapshotHistorySize:32`. This allows future snapshots to [delta-compress](compression.md) newer `GhostField` values against the latest acked of these 32 previously-sent snapshots. The `const` value of 32 is best suited for ghosts sending at very high rates (such as 60Hz), providing roughly 500ms worth of history.

However, for MMO-scale games (where `MaxSendRate`s are often significantly lower), smaller snapshot history sizes may be preferable.
To change this `const`, define one of the following in your **Project Settings** > **Player** > **Scripting Define Symbols**:

* `NETCODE_SNAPSHOT_HISTORY_SIZE_16` is a good middle-ground between size-reduction (for static ghosts) and ack availability (for dynamic ghosts). Recommended for projects where the highest `GhostPrefabCreation.Config.MaxSendRate` is 30Hz, or where the `ClientServerTickRate.NetworkTickRate` is 30.
* `NETCODE_SNAPSHOT_HISTORY_SIZE_6` is best suited for larger scale projects, such as those with hundreds of dynamic ghosts, thousands of static ghosts, and where the player character controller is already sent at a significantly lower frequency due to congestion or widespread use of `GhostPrefabCreation.Config.MaxSendRate`.

> [!NOTE]
> Be aware that ghost chunks may not be sent to a specific connection if their entire snapshot history buffer fills up with 'in-flight' snapshots (un-acked snapshots - sent less than one round trip ago - containing this ghost chunk).
> Refer to the `PacketDumpResult_SnapshotHistorySaturated` method for debugging.

## Additional resources

* [Ghosts and snapshots](../ghost-snapshots.md)
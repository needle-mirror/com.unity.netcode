# Manage serialization costs

Manage serialization costs to optimize the performance of your game.

When a `GhostField` changes, its data is serialized and synchronized over the network. If a job has write access to a `GhostField`, then Netcode for Entities checks if the job caused the data to change. The data is serialized and compared with the previously synchronized version, and if it hasn't changed then the newly serialized result is discarded.

For this reason, it's important to ensure that no jobs have write access to data if it isn't going to change.

## Reduce serialization and deserialization CPU costs

Sending and receiving ghost data involves expensive CPU read and write operations that scale linearly with the number of ghosts serialized in a packet. The server serializes this ghost data in batches using a predictive [delta compression](compression.md) strategy. Replicated fields are delta-encoded against a predicted value (similar to a linear extrapolation) that's extrapolated from the last three baselines (the last three previously acknowledged values). The client applies the same strategy when decoding: using the same baselines (as communicated by the server) it predicts the new value and then decompresses against that predicted value.

This method is effective for predictable ghost data such as timers, linear movements, and linear increments or decrements. In these cases, the predicted value is often an exact (or very close) match for the current state value, which means the serialized delta value is either zero or near zero, allowing for good bandwidth reductions.

However, there are some downsides to a three-baseline predictive delta compression:

* It requires Netcode for Entities to continue sending snapshots to the client about a ghost, even if the ghost's data hasn't changed.
* The CPU encoding cost (on the server) is slightly higher.
* The CPU decoding cost (on the client) is also slightly higher, particularly for `GhostFields` that change infrequently.

As a result, three-baseline-based compression is primarily recommended for predictable fields. In unpredictable cases, it doesn't save many resources and you may be better [using a single baseline](#using-a-single-baseline).

### Using a single baseline

You can reduce some of this encoding cost on a per-archetype basis with the `UseSingleBaseline` option in the [`GhostAuthoringComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html).
When set, it instructs the server to always use a single baseline for delta compression for this specific prefab type.

If you want to test the impact of using a single baseline for all ghosts without modifying all prefabs, you can use the[`GhostSendSystemData.ForceSingleBaseline`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_ForceSingleBaseline) property. Use this option during development to test the impact of a single baseline in your game, in terms of both bandwidth and CPU.

Using a single baseline can reduce CPU usage for both client and server, especially when the archetype has a large number of components or fields that rarely change. The impact is usually greater client-side, where deserialization time is often reduced by around 50%.

Moreover, using a single baseline enables a specific bandwidth optimization: when any replicated entity hasn't changed for a specified period of time, you can stop resending the ghost chunk entirely (whereas with three baselines the chunks must always be sent to ensure that three baselines are always available).

The `UseSingleBaseline` option can lead to significant savings in two common scenarios:

* When a ghost prefab is suited for [`GhostOptimizationMode.Dynamic`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostOptimizationMode.html), but has frequent periods of inactivity.
* When the majority of the component data changes on a ghost type don't follow linear, predictable patterns, and so the three baselines cost isn't justified.

> [!NOTE]
> When ghosts use `GhostOptimizationMode.Static` the prefab is always serialized using a single baseline.

## Additional resources

* [Compression](compression.md)
* [Ghost optimization](optimize-ghosts.md)
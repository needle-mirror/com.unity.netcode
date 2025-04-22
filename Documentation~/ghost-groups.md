# Ghost groups

Use ghost groups to synchronize replication timings across multiple ghost instances, and prevent common gameplay state errors.

## Ghost group usage

### Configure a ghost group
To create a ghost group, you need to define a ghost group root, then define said ghost group root's children.

1. Add a [`GhostGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostGroup.html) buffer to a ghost prefab at authoring time using the **Ghost Group** toggle in the **GhostAuthoringComponent**'s Inspector window.
   This defines the ghost group root, and allows ghost group membership (by other ghost instances).
2. For each ghost group child instance;
    1. Add the [`GhostChildEntity`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostChildEntity.html) component to said child.
    2. Add the child ghost `Entity` to the `GhostGroup` buffer on the root.

### Ghost group behaviour

* All ghost group children added to the ghost group root are guaranteed to be sent whenever the root ghost entity is.
* The root ghost entity is implicitly defined as the ghost instance with the `GhostGroup` buffer component, and without the `GhostChildEntity` component.

### Ghost group limitations

* There can only be one root ghost entity per group.
* Ghost groups do not support nesting. I.e. A ghost group entry cannot be a member of multiple ghost groups.
* `Relevancy` isn't supported for ghost group child entities while they are within a ghost group. They inherit the relevancy of their root ghost entity, and marking a child as irrelevant has no effect.

> [!NOTE]
> Known issue as of 03/2025: If a relevant ghost group root becomes irrelevant, its children will not currently be made irrelevant - they will be left stranded.

* `GhostOptimizationMode.Static` isn't supported for ghost groups. You also can't mark children as `Static` while the root is `Dynamic` (or vice-versa). All `GhostGroup` ghosts are forced `Dynamic`, even if authored as `Static`.
* The `Importance`, `GhostImportance` `Importance Scaling`, and `Max Send Rate` of child ghosts is ignored when they are part of a group. Only the `GhostGroup` root ghost chunks values are used.
* `GhostGroup` serialization is significantly slower (relatively speaking), as we must traverse to the chunk of each individual child (similar to how replicating `GhostField`'s on components on `Unity.Transforms.Child` child ghost entities is slower). Consider the potential impact on performance when using ghost groups.
* Serializing `GhostGroup` entries can cause a single snapshot to be larger than the default (of `NetworkParameterConstants.MaxMessageSize`), which may increase the frequency of snapshot packet fragmentation.

> [!NOTE]
> For performance reasons, errors are not reported for incorrect `GhostGroup` usage.

## Ghost group example use case

You have a `Player` character controller ghost in a First-Person Shooter, that can pick-up, drop, and carry three individual `Gun` ghost instances.
When carried by the player, each gun is attached to different points on the characters body (using a faked parenting approach), and can drive the characters hand animation state.

Your assets may look like this:
```txt
'Player' Ghost Prefab
Importance:100, Max Send Rate:60, Owner Predicted, Has Owner, Dynamic,
RelevantWithinRadius:1km, DynamicBuffer<GhostGroup> (Count:0)
```
```txt
'Gun' Ghost Prefab
Importance:10, MaxSendRate:10, Owner Predicted, Has Owner, Static,
RelevantWithinRadius:200m
```

### Without ghost groups

Without ghost groups, you may experience the following issues during gameplay:

* When other players observe you picking up and firing a gun, they see your characters hand animation update before the gun physically moves into your hand.
* The held gun may not perfectly follow the players hand position and rotation, either.
* They may even see the gun firing FX appear from the gun on the ground (or in the process of being picked up).
* Distant players may appear to be empty-handed (due to differences in Relevancy and/or Importance leading to gun spawn delays).
* Exceptions may be thrown inside client `Gun` systems if they (incorrectly) assume that a `Gun` entity's `HoldingPlayer` entity reference will always exist, but it's been deleted in a previous snapshot before the deletion of this `Gun` was replicated (or vice-versa).

### With ghost groups

To use ghost groups in this example:

1. Add the `GhostGroup` buffer to the `Player` ghost (by checking the **GhostGroup** option on the **GhostAuthoringComponent**'s Inspector window).
2. At runtime, when picking up a gun instance, add said `Gun` ghost entity to the `Player`'s `GhostGroup` buffer...
3. ...and add the [`GhostChildEntity`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostChildEntity.html) component to said `Gun` instance.
4. Similarly, when dropping a gun, remove it from the `Player`'s `GhostGroup` buffer, and remove the [`GhostChildEntity`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostChildEntity.html) component from the (now dropped) gun.

This makes the `Player` ghost instance the ghost group root, and each picked up `Gun` ghost instance a ghost group child.
Each `Gun` ghost instance will now be replicated every time the `Player` ghost instance is, preventing the issues described in the [without ghost groups section](#without-ghost-groups).

It also means:
* Each `Gun`'s `Importance` is now effectively 100 and their `Max Send Rate` is 60 (the same as the `Player` ghost group root).
* Each `Gun` ghost instance is no longer static-optimized (and therefore, won't be forced into `UseSingleBaseline:true`).
* Each `Gun` instance is only considered relevant to a connection if its ghost group root is and spawns the moment the `Player` itself spawns (once within 1km of each connection's own `Player`).

## Additional resources

* [Ghost and snapshots](ghost-snapshots.md)
* [Ghost relevancy](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/manual/optimizations.html#relevancy)
* [Importance scaling](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/manual/optimizations.html#importance-scaling)

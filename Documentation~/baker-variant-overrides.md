# Apply variant overrides from a baker

Use `GhostVariantBakedOverride` to set per-component overrides at baking time.

[`GhostVariantBakedOverride`](xref:Unity.Netcode.GhostVariantBakedOverride) is a baking-only buffer you can use to set the same per-component overrides as [`GhostAuthoringInspectionComponent`](xref:Unity.Netcode.GhostAuthoringInspectionComponent) (variant, `GhostPrefabType`, `GhostSendType`) directly from a baker without adding an inspection component to the GameObject.

For more information about baking, refer to the [Baking overview](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/baking-overview.html) in the Unity Entities documentation.


## Usage

From a baker, add the `GhostVariantBakedOverride` buffer to your primary entity and append entries using the extension methods on [`GhostVariantOverrideBakerExtensions`](xref:Unity.Netcode.GhostVariantOverrideBakerExtensions):

```c#
public class MyAuthoringBaker : Baker<MyAuthoring>
{
    public override void Bake(MyAuthoring auth)
    {
        var entity = GetEntity(TransformUsageFlags.None);
        var overrides = AddBuffer<GhostVariantBakedOverride>(entity);

        overrides.AppendDontSerializeOverride(typeof(LocalTransform));
        overrides.AppendPrefabTypeOverride(typeof(MyMarker), GhostPrefabType.Server);
        overrides.AppendSendTypeOverride(typeof(MyData), GhostSendType.OnlyPredictedClients);
        overrides.AppendOverride<MyVariant>(typeof(MyData));
    }
}
```

The four extension methods expose the same four override axes as the inspection component. Each method validates that no entry already exists for the same `(component, target)` pair and throws an error at bake time if one does.

## Targeting

By default, each `GhostVariantBakedOverride` entry targets the primary entity of the GameObject the buffer is associated with. This means that a baker running on a child GameObject can only apply overrides scoped to that child entity, prefab root and sibling entities are unaffected.

To retarget (for example, when a child baker needs to override a component on the prefab root) pass `targetGameObject` (and optionally `targetEntitySerial`):

```c#
overrides.AppendDontSerializeOverride(typeof(LocalTransform), targetGameObject: rootGo);
```

Unity Entities baking forbids cross-baker writes to the same primary entity, so the buffer must always be associated with the baker's primary entity even when retargeting elsewhere. The aggregator in [`GhostAuthoringBakingSystem`](xref:Unity.Netcode.GhostAuthoringBakingSystem) reads the linked entity group at bake time and routes each entry to its declared target.

## Precedence

When multiple sources try to override the same `(entity, component)` pair, the following precedence rules apply:

1. A matching entry on the prefab's `GhostAuthoringInspectionComponent` wins.
2. Otherwise, the first baker-contributed entry encountered wins. Baking is deterministic, so this is stable across runs.

An inspection component override always takes precedence over a package-supplied baker default.

## Lifecycle

`GhostVariantBakedOverride` is decorated with [`[BakingType]`](xref:Unity.Entities.BakingTypeAttribute). Entries are read during baking and stripped before the live world. They never reach runtime queries.

## Additional resources

* [Creating replication schemas with `GhostComponentVariationAttribute`](ghost-variants.md)
* [`GhostVariantBakedOverride` API documentation](xref:Unity.Netcode.GhostVariantBakedOverride)
* [`GhostVariantOverrideBakerExtensions` API documentation](xref:Unity.Netcode.GhostVariantOverrideBakerExtensions)

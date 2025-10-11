# How ghost hash are calculated

The ghost prefab CRC is calculated in two steps:
- One part in the `GhostCollection.ProcessPrefab` method
- One part by the `GhostCollection.HashGhostType` method

It is a combined hash of multiple pieces of data that altogether form the final CRC of the processed ghost.

It looks something like this:

```csharp

var ghostTypeHash = FNV1A64(prefab.GhostName);
foreach(replicated component in the prefab hierarchy) // (no client-only or server-only)
{
    serializer = SerializerForComponent(info.Variant);
    sendMask = info.SendMaskOverride > 0
        ? componentMetaData.SendMaskOverride
        : serializer.SendMask
    var tempHash = FNV1A64.Combine(serializer.SerializerHash, sendMask)
    ghostTypeHash = FNV1A64.Combine(ghostTypeHash, tempHash);
}
//After all components has been processed (for both root and child entiites)
ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(FirstComponent));
ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(NumComponents));
ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(NumChildComponents));
ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(SnapshotSize));
ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ChangeMaskBits));
ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(PredictionOwnerOffset));
ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(OwnerPredicted));
ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(IsGhostGroup));
ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(EnableableBits));
```

We can divide the hash calculation in 4 main pieces:

- The stable hash of the prefab name
- The stable hash of the serializer (combine multiple info), calculated at compile time.
- The stable hash of the sendmask (left out from the serializer hash because can be overridden)
- The stable hash of the properties of the constructed prefab serializer.

All these parts must be identical on both client and server. That immediately imply:

- Components must be processed in the same order by client and server. That require deterministic sorting of the metadata information
- Serializater Variant mapping must be same client and server
- No Editor Only components should be present on the prefab

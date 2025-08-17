# Serialization and synchronization with `GhostFieldAttribute`

Use [`GhostFieldAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html) to specify which fields and properties of [`Unity.Entities.IComponentData`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IComponentData.html) or [`Unity.Entities.IBufferElementData`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IBufferElementData.html) should be serialized and replicated from server to client. When a component or buffer contains at least one field that's annotated with `GhostFieldAttribute`, a struct implementing the component serialization is automatically code generated.

In addition to `GhostFieldAttribute`, you can use [`GhostComponentAttribute`](ghostcomponentattribute.md) to further customize how replication is handled by your runtime.

## Customizing `GhostFieldAttribute` serialization

Use these properties to customize how components and buffers are serialized using `GhostFieldAttribute`. For more details, refer to the [`GhostFieldAttribute` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html).

| Property | Default value | Description |
|---|---|---|
| [`Quantization`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_Quantization) | Disabled by default on float and unavailable on integers. | Use the `Quantization` property to set [quantization](compression.md#quantization) for floating point numbers (and other supported types, refer to [ghost type templates](ghost-types-templates.md)) to limit the precision of data. The floating point number is multiplied by the quantization value and converted to an integer to save bandwidth. |
| [`Composite`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_Composite) | Disabled by default. | Use the `Composite` property to control how [delta compression](compression.md#delta-compression) computes the change field's bitmask for non-primitive fields (such as structs). When set to `true`, delta compression templating generates only one bit to indicate whether the entire struct contains any changes. If `Composite` is false, each field has its own change-bit. Use `Composite=true` if all fields are typically modified together (for example, `GUID`). |
| [`SendData`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_SendData) | Enabled by default. | Use the `SendData` property to instruct code generation not to include a field in the serialization data. This is particularly useful for non-primitive members (like structs) which have all fields serialized by default. |
| [`Smoothing`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_Smoothing)| Default setting is [`Clamp`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SmoothingAction.html#Unity_NetCode_SmoothingAction_Clamp). | Use the `Smoothing` property to control how a field is updated when the ghost is in `GhostMode.Interpolated`. Options are `Clamp` (every time a snapshot is received, clamp the client value to the latest snapshot value), `Interpolate` (interpolate the field between the last two snapshot values every frame; if no data is available for the next tick, clamp to the latest value), and `InterpolateAndExtrapolate` (interpolate the field between the last two snapshot values every frame; if no data is available for the next tick, the next value is linearly extrapolated using the previous two snapshot values). |
| [`MaxSmoothingDistance`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_MaxSmoothingDistance) |  | Use the `MaxSmoothingDistance` property to disable interpolation when the values change more than the specified limit between two snapshots. This is useful for dealing with teleportation, for example. |
| [`SubType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_SubType) |  | Use the `SubType` property to specify a custom serializer for a field using the [`GhostFieldSubType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldSubType.html) API. |

>[!NOTE] Ghosts that are marked as **both** static-optimized **and** interpolated will **never** extrapolate,
> because static-optimized ghosts do not send snapshot updates when they haven't changed, so we therefore cannot differentiate
> between 'this continuously changing value has since stopped changing' and 'we have not yet received the next continuous value'.

### `GhostField` inheritance

If a `[GhostField]` is specified for a non-primitive field type, the attribute (and some of its properties) are automatically inherited by all subfields which don't themselves implement a `[GhostField]` attribute. For example:

```c#

public struct Vector2
{
    public float x;
    [GhostField(Quantization=100)] public float y;
}

public struct MyComponent : IComponentData
{
    //Value.x will inherit the quantization value specified by the parent definition (1000).
    //Value.y will maintain its original quantization value (100).
    [GhostField(Quantized=1000)] public Vector2 Value;
}
```

> [!NOTE]
> The `SubType` property always resets to the default.

## Component serialization

To mark a component for serialization and replication, add a `[GhostField]` attribute to the values you want to send.

The component declaration must:

- Be a concrete type. Generic structs are not supported.
- Be `public` or `internal`.
- Implement either `IComponentData` or any interface inheriting from it. Generic interfaces inheriting from
  `IComponentData` are supported.

```csharp
public struct MySerializedComponent : IComponentData
{
    [GhostField]public int MyIntField;
    [GhostField(Quantization=1000)]public float MyFloatField;
    [GhostField(Quantization=1000, Smoothing=SmoothingAction.Interpolate)]public float2 Position;
    public float2 NonSerializedField;
    ...
}
```

Only `public` members of the component can be serialized. Adding `[GhostField]` to a `private` member has no effect.

## Dynamic buffer serialization

To mark a buffer for serialization and replication, all `public` fields must be annotated with a `[GhostField]` attribute.

The buffer declaration must:

- Be a concrete type. Generic structs are not supported.
- Be `public` or `internal`.
- Implement either `IBufferElementData` or any interface inheriting from it. Generic interfaces inheriting from
`IBufferElementData` are supported.

```csharp
public struct SerialisedBuffer : IBufferElementData
{
    [GhostField]public int Field0;
    [GhostField(Quantization=1000)]public float Field1;
    [GhostField(Quantization=1000)]public float2 Position;
    public float2 NonSerialisedField; // This is an explicit error!
    private float2 NonSerialisedField; // We allow this. Ensure you set this on the client, before reading from it.
    [GhostField(SendData=false)]public int NotSentAndUninitialised; // We allow this. Ensure you set this on the client, before reading from it.
    ...
}
```

You can use the [`SendData` property](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_SendData) to skip serialization and replication of a field, which means that:

- The value of the fields that aren't replicated are never altered.
- For new buffer elements, their content isn't set to default and the content is undefined (can be any value).

Dynamic buffer fields don't support [`SmoothingAction`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SmoothingAction.html) so the `GhostFieldAttribute.Smoothing` and `GhostFieldAttribute.MaxSmoothingDistance` properties are ignored on buffers.

## `ICommandData` and `IInputComponentData` serialization

You can annotate your input's fields with `[GhostField]` to replicate them from server to client. This can be useful, for example, to enable client-side prediction of other players' character controllers on your local machine.

When using automated input synchronization with [`IInputComponentData`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IInputComponentData.html):

```c#
    public struct MyCommand : IInputComponentData
    {
        [GhostField] public int Value;
    }
```

[`ICommandData`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ICommandData.html) is a subclass of [`IBufferElementData`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IBufferElementData.html) and can be serialized for replication from the server to clients. As such, the same rules as for [buffers](#dynamic-buffer-serialization) apply: if the command buffer is to be serialized, then all fields must be annotated.

When using `ICommandData`:

```c#
    [GhostComponent()]
    public struct MyCommand : ICommandData
    {
        [GhostField] public NetworkTick Tick {get; set;}
        [GhostField] public int Value;
    }
```

Command data serialization is particularly useful for implementing [remote player prediction](prediction-n4e.md#remote-player-prediction).

## Adding serialization support for custom types

The types you can serialize with `GhostFieldAttribute` are specified via templates. Refer to the [Ghost types templates page](ghost-types-templates.md#supported-types) for a list of the default supported types.

In addition to the default supported types you can also:

- Add your own templates for new types.
- Provide a custom serialization template for a type and target it using the [`SubType` property](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_SubType) of `GhostFieldAttribute`.

Refer to [how to use and write templates](ghost-types-templates.md#defining-additional-templates) for more information on creating templates.

>[!NOTE]
> Creating templates for serialization is non-trivial. If it's possible to replicate a type by adding `[GhostField]`, it's often easier to just do so. If you don't have access to a type, you can create a [variant](ghost-variants.md) instead.

## Additional resources

- [`GhostFieldAttribute` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html)
- [`Unity.Entities.IComponentData` API documentation](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IComponentData.html)
- [`Unity.Entities.IBufferElementData` API documentation](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IBufferElementData.html)
- [Ghost types templates](ghost-types-templates.md)
- [Ghost variants](ghost-variants.md)
- [Customizing replication with `GhostComponentAttribute`](ghostcomponentattribute.md)
- [Preserialize ghosts](optimization/optimize-ghosts.md#preserialize-ghosts)

# Ghost type templates

Netcode for Entities has default templates that define how ghost component types are handled during [baking](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/baking-overview.html) and serialization. You can also [create your own templates](#defining-additional-templates) to register additional types.

## Supported types

By default, Netcode for Entities has serialization templates for the following types:

* `bool`
* `Entity`
* `FixedString32Bytes`
* `FixedString64Bytes`
* `FixedString128Bytes`
* `FixedString512Bytes`
* `FixedString4096Bytes`
* `float`
* `float2`
* `float3`
* `float4`
* `byte`
* `sbyte`
* `short`
* `ushort`
* `int`
* `uint`
* `long`
* `ulong`
* `enums` (only for `int`/`uint` underlying type)
* `quaternion`
* `double`
* `NetworkEndpoint`
* `FixedList32Bytes<T>` (where T can be any supported unmanaged replicated type)
* `FixedList64Bytes<T>` (where T can be any supported unmanaged replicated type)
* `FixedList128Bytes<T>` (where T can be any supported unmanaged replicated type)
* `FixedList512Bytes<T>` (where T can be any supported unmanaged replicated type)
* `FixedList4096Bytes<T>` (where T can be any supported unmanaged replicated type)
* `unsafe fixed T[const]` ([with caveats](#supporting-unsafe-fixed-tconst)).
* C# unions ([with caveats](#how-to-support-unions))

### Supporting unsafe fixed T\[const\]
Fields such as the following:
```csharp
    public const int MyFixedArrayLength = 3;
    [GhostField(Quantization = 100)]public unsafe fixed float MyFixedArray[MyFixedArrayLength];
```
Must provide a helper method, named `{MyFieldName}Ref`, to safely fetch `ref` access in a safe context, as follows:
```csharp
    public unsafe ref float MyFixedArrayRef(int index)
    {
        if (index < 0 || index >= MyFixedArrayLength)
            throw new InvalidOperationException($"MyFixedArrayRef<float>[{index}] is out of bounds (Length:{MyFixedArrayLength})!");
        return ref MyFixedArray[index];
    }
```
Without the helper method, you will get compiler errors because Netcode for Entities can't access its unsafe fields.

### Types that support reporting of prediction errors

* `bool`
* `int`
* `uint`
* `short`
* `ushort`
* `long`
* `ulong`
* `byte`
* `sbyte`
* `bool`
* `float`
* `double`
* `float2`
* `float3`
* `float4`
* `quaternion`
* `NetworkTick`
* `NetworkEndPoint`

### Types that don't support reporting of prediction errors

* `Entity`
* All `FixedStringXXBytes<T>`
* All `FixedListXXBytes<T>`
* All `unsafe fixed T[const]` arrays
* All `DynamicBuffer<T>`
* C# unions

### Types with multiple templates

Some types have multiple templates that provide alternative ways to serialize the type. Types with multiple templates are:

* `float`
* `float2`
* `float3`
* `float4`
* `quaternion`
* `double`

For types with multiple templates, the available options are as follows:

| Setting | Options | Description |
|---|---|---|
| Quantization | Quantized or unquantized  | Quantization involves limiting the precision of data for the sake of reducing the number of bits required to send and receive that data. For example, a float value `12.456789` with a quantization factor of `1000` is sent as `int 12345`. Unquantized means the float is sent with full precision. Refer to [quantization](compression.md#quantization) for more details. |
| Smoothing method | `Clamp`, `Interpolate`, or `InterpolateAndExtrapolate` | Smoothing method specifies how a new value is applied on the client when a snapshot is received. Refer to the [`SmoothingAction` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SmoothingAction.html) for more details. |

Each of these options changes how the original value is serialized, deserialized, and applied on the client, and each template uses different, named regions to handle these cases. The code generator chooses the appropriate regions to generate, and bakes your user-defined serialization settings for fields on your types directly into the serializer for your type. You can explore these generated types in the projects' `Temp/NetCodeGenerated` folder (note that they're deleted when Unity is closed).

### Fixed size list capacity/length limitations

When fixed-size list are serialized in RPC/Command or as a field in replicated component, a limit to the list length (and therefore capacity) is enforced.

| Primitive           | Length cap |
|---------------------|------------|
| IRpcCommand         | 1024       |
| ComponentData       | 64         |
| BufferElementData   | 64         |
| ICommand            | 64         |
| IInputComponentData | 64         |

When fixed-size list fields are replicated in RPCs the maximum allowed capacity (and thus length) for any fixed size list field element is limited to 1024 elements.
> Remark: Notice that becase sending RPC larger than 1MTU is not currently supported, the packet size induce an instrisict limitation on the maximunumber of serializable elements that can be way lower than 1024.

When fixed-size list fields are replicated in `IComponentData`, `IBufferElementData`, `ICommandData` and `IInputComponentData` the maximum allowed fixed-list capacity is capped to 64 elements.

Because there is no way to enforce a lower capacity for a fixed list of the give byte size (the size implicitly define the capacity) it is permitted to use larger list with exceeding capacity, but sufficient then to hold the number of element that you require.
When this necessity arise, it is mandatory to use the [`GhostFixedListCapacity`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFixedListCapacityAttribute.html) attribute to declare what it is the expected capacity of the list.

```csharp
struct MyRpc : IRpcCommand
{
    //limit the list replicated elements to 32. The limit is enforced
    [GhostFixedListCapacity(Capacity=32)]
    public FixedList4096<float> floats;
}

struct MyComponent : IComponentData
{
    //limit the list replicated elements to 32. The limit is enforced
    [GhostFixedListCapacity(Capacity=32)]
    public FixedList4096<float> floats;
}
```

A compile time error is reported to inform what fields that are exceeding the maximum capacity and that required the attribute to be present.

The fixed-size list length will be implicitly clamped (when serialized or stored in the snapshot buffer) to the maximum allowed internal capacity if the list length exceed that threshold. Errors are reported in developer build or in general when the `NETDEBUG` define is set when then list length exceed the
maximum allowed capacity for the list.

#### Why we enforce the 64 element restriction
The idea behind supporting Fixed Lists as `[GhostField]`s is that they can be used to replicate small lists of gameplay data. They are not designed to store/replicate large amounts of data, as the higher the number of elements, the larger the number of bits needed to represent both the changeMask, and the delta-compressed changes themselves.
The cap (of 64) is intentional; it is a good compromise between flexibility (as 64 elements is quite enough for most use cases), and easier (thus faster) replication code. It also helps prevent the sending of partial snapshots (i.e. snapshots only containing a subset of a chunks entities), which is an additional benefit. _Note: We'll consider lifting this restriction in the future._

For Inputs (commands) the common use case we are optimizing for is that input should be in general small. So we preferred to enforce the rule to constrain input size. Further, because inputs can be replicated for other players (via the presence of the `[GhostField]` attribute - and associated `GhostComponent` flags), we would had a very strange and non-uniform behaviour in such case.

#### Why don't RPCs have a larger maximum allowed capacity ?
The main reasons are:
- RPCs are meant to be sent unfrequently
- They our most flexible and unique tool for message passing.
- They don't have any particular needs for complex change mask generation that justify applying the limit either.

## How types are serialized

Netcode for Entities serialize the supported types over the network by using either bit-packing (packed format) or the
"un-packed" (full bits) format.

#### packed vs unpacked format
| Type           | unpacked          | packed                                                                        |
|----------------|-------------------|-------------------------------------------------------------------------------|
| sbyte          | 32 bit            | zig-zag encoded, 4 bits + variable size payload (huffman/golomb bucket)       |
| short          | 16 bit            | zig-zag encoded, 4 bits + variable size payload (huffman/golomb bucket)       |
| int            | 32 bit            | zig-zag encoded, 4 bits + variable size payload (huffman/golomb bucket)       |
| long           | 64 bit            | zig-zag encoded, 2 x (4 bits + variable size payload (huffman/golomb bucket)) |
| byte           | 32 bit            | 4 bits + variable size payload (huffman/golomb bucket)                        |
| uint           | 32 bit            | 4 bits + variable size payload (huffman/golomb bucket)                        |
| ushort         | 16 bit            | 4 bits + variable size payload (huffman/golomb bucket)                        |
| ulong          | 64 bit            | 2 x (4 bits + variable size payload (huffman/golomb bucket))                  |
| float          | 32 bit            | 0: 1bit otherwise 32 bits                                                     |
| double         | 64 bit            | 0: 1bit otherwise 64 bits                                                     |
| FixedStringXXX | 8bit + len * 8bits | 4 bits + varialbe size payload (len) + len * (4 bits + varialbe size payload) |
| float2         | 2 * 32bits        | 2 * packed float size                                                         |
| float3         | 3 * 32bits        | 3 * packed float size                                                         |
| float4         | 4 * 32bits        | 4 * packed float size                                                         |
| quaternion     | 4 * 32bits        | 4 * packed float size                                                         |

### How to support unions
The `[GhostField]` attribute enables two netcode sub-systems; serialization, and client prediction (backup & restoring).
C# unions (i.e. combining `[StructLayout(LayoutKind.Explicit)]` and `[FieldOffset(0)]`) are partially supported via `[GhostField]`,
with the following limitations.

* `SmoothingAction` must be `Clamp`, as we cannot interpolate, nor extrapolate (as netcode cannot infer which values should be).
* `Quantization` must be `0 i.e. OFF`.
* Prediction error reporting will not work.
* `[GhostField] Entity` replication & patching (e.g. for `EntityCommandBuffer`) will not work.
* As all union members share the same underlying memory, only enable replication of the **largest** union member.
* `Composite = true` is optional.
* Delta-compression will technically work, but won't be efficient if the underlying data changes significantly when
different states are written to.

Example that works with input commands, RPCs, components, and buffers:
```csharp
    [StructLayout(LayoutKind.Explicit)]
    public struct Union
    {
        [FieldOffset(0)] [GhostField(SendData = false)] public StructA State1;
        [FieldOffset(0)] [GhostField(Quantization = 0, Smoothing = SmoothingAction.Clamp, Composite = true)] public StructB State2;
        [FieldOffset(0)] [GhostField(SendData = false)] public StructC State3;
        public struct StructA
        {
            public int A, B;
            public float C;
        }
        public struct StructB
        {
            public ulong A, B, C, D;
        }
        public struct StructC
        {
            public double A, B;
        }
        public static void Assertions()
        {
            UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<StructB>() >= UnsafeUtility.SizeOf<StructA>());
            UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<StructB>() >= UnsafeUtility.SizeOf<StructC>());
        }
    }
```

> Netcode for Entities doesn't perform any checks to verify that the union member struct you marked as replicated is the one with the largest size, nor that you used `SendData = false` on the others. You must verify this yourself.
> It's recommend that you write a [serializer template](#defining-additional-templates) for your unions, which may allow you to circumvent most of the above limitations.

### Serialization in snapshot

The replicated entity data (ghost snapshot) is composed by two part: an array of bits, the components fields `changemask`, and
the component data`payload` itself.

Both payload and changemask are delta-encoded/delta-compressed against up-to 3 previously acked state received by the client
(for that entity) or, if no ack has been received, against the zero-baseline (all zeroes).

**Change Mask Bits**

| Type               | bits              | aggregate | notes                                                                                                                                                                 |
|--------------------|-------------------|-----------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| primitive          | 1 bit             | yes       |
| fixed-size list    | 2 bits            | no        |
| fixed-size buffers | 1 bit per element | yes       |
| float2             | 1 bit             | yes       |
| float3             | 1 bit             | yes       |
| float4             | 1 bit             | yes       |
| quaternion         | 1 bit             | yes       |
| FixedString XXX    | 1 bit             | yes       |

Any struct are recursively visited and by default each members consume the corresponding number of change mask bits. If the `GhostField.Composite` flag is set, the struct aggregate in 1 bit all fields that support mask aggregation.
The only field that can't be aggregate are fixed-size list, that always consume 2 bits of mask.

The change-mask bits for a given entity are stored as an array of integers and delta-compressed against
the last state change-mask acked by the client (for the given entity).

**Component data**

The component data is always delta compressed, either against the "zero" baseline or up to 3 acked snapshot packet by the client.
That means all fields are going to be `packed` using the `StreamCompressionModel` (so huffman/golomb compressed). Netcode for Entities uses a predictive-delta compression
using up to 3 baseline to predict the next value and compute delta encode the field value against that.

The change masks are used to explicitly skip fields was value were identical to the current baseline;
The delta itself is encoded as specified in the [packed vs unpacked](#packed-vs-unpacked-format) table.

#### Some extra details about how fixed list are serialized

Fixed-size list types always use 2 bits of change-mask and a "dynamic" element mask,
    1. 1st bit denote if the length is changed in respect to the given baseline
    2. 2nd bit denote if any of the elements has changed in respect to the given baseline

Every list element is delta-compressed against the baseline element at same index and the changemask aggregated to use 1 bit per element. Thus, given the the 64 element limitation, a variable length (up to 64 bit) changemak is generated when the type is serialized.
If none of the elements different in respect the baseline, a `0` is set int the 2nd bit of the fixed-size changemask and no further data are transmitted. Otherwise, a `1` is set as 2nd bit and both the variable length element mask and the changed element data are serialized over the network.

## Changing how a type is serialized using variants

You can use [`GhostComponentVariationAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentVariationAttribute.html) to create [variants](ghost-variants.md) that allow you to overwrite the default serializer at compile time. Variants can also be applied on a per-ghost, per-component basis, using [`GhostAuthoringInspectionComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringInspectionComponent.html)

Refer to [Creating replication schemas with `GhostComponentVariationAttribute`](ghost-variants.md) for more information.

### Changing how a type is serialized using the `SubType` property

You can also have multiple templates defined and available for a given type. For example, having a 2D and a 3D template for `float3` values. The [`SubType` property](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_SubType) of `GhostFieldAttribute` allows you to choose which one to use, on a per-`[GhostField]` basis. Refer to the [Defining SubType templates section](#defining-subtype-templates) for more details.

## Defining additional templates

You can also create your own templates to register additional types (those not supported [by default](#supported-types)) so that they can be replicated correctly with the `[GhostField]` annotation.

> [!NOTE]
> Creating templates for serialization is non-trivial. If it's possible to replicate a type by adding `[GhostField]`, it's often easier to just do so. If you don't have access to a type, you can create a [variant](ghost-variants.md) instead.

### Writing a template

Template files can be added to any package or folder in the project, but must meet the following requirements:

- The template file must have a `NetCodeSourceGenerator.additionalfile` extension (for example, `MyCustomType.NetCodeSourceGenerator.additionalfile`).
- The first line  of the template file must contain a`#templateid: XXX` line. This assigns the template a globally unique user-defined ID.

You will experience errors if you create a `UserDefinedTemplate` with no associated template file, or if you create a template file with no associated `UserDefinedTemplate`. Code generation errors when building templates can also cause compiler errors.

When creating your new template, you may find it easier to build on one of the existing default template files. The following code is an example copied from the default `float` template, where the `float` is quantized and stored in an `int` field.

```c#
#templateid: MyCustomNamespace.MyCustomTypeTemplate
#region __GHOST_IMPORTS__
#endregion
namespace Generated
{
    public struct GhostSnapshotData
    {
        struct Snapshot
        {
            #region __GHOST_FIELD__
            public int __GHOST_FIELD_NAME__;
            #endregion
        }

        public void PredictDelta(uint tick, ref GhostSnapshotData baseline1, ref GhostSnapshotData baseline2)
        {
            var predictor = new GhostDeltaPredictor(tick, this.tick, baseline1.tick, baseline2.tick);
            #region __GHOST_PREDICT__
            snapshot.__GHOST_FIELD_NAME__ = predictor.PredictInt(snapshot.__GHOST_FIELD_NAME__, baseline1.__GHOST_FIELD_NAME__, baseline2.__GHOST_FIELD_NAME__);
            #endregion
        }

        public void Serialize(int networkId, ref GhostSnapshotData baseline, ref DataStreamWriter writer, StreamCompressionModel compressionModel)
        {
            #region __GHOST_WRITE__
            if ((changeMask & (1 << __GHOST_MASK_INDEX__)) != 0)
                writer.WritePackedIntDelta(snapshot.__GHOST_FIELD_NAME__, baseline.__GHOST_FIELD_NAME__, compressionModel);
            #endregion
        }

        public void Deserialize(uint tick, ref GhostSnapshotData baseline, ref DataStreamReader reader,
            StreamCompressionModel compressionModel)
        {
            #region __GHOST_READ__
            if ((changeMask & (1 << __GHOST_MASK_INDEX__)) != 0)
                snapshot.__GHOST_FIELD_NAME__ = reader.ReadPackedIntDelta(baseline.__GHOST_FIELD_NAME__, compressionModel);
            else
                snapshot.__GHOST_FIELD_NAME__ = baseline.__GHOST_FIELD_NAME__;
            #endregion
        }

        public unsafe void CopyToSnapshot(ref Snapshot snapshot, ref IComponentData component)
        {
            if (true)
            {
                #region __GHOST_COPY_TO_SNAPSHOT__
                snapshot.__GHOST_FIELD_NAME__ = (int) math.round(component.__GHOST_FIELD_REFERENCE__ * __GHOST_QUANTIZE_SCALE__);
                #endregion
            }
        }
        public unsafe void CopyFromSnapshot(ref Snapshot snapshot, ref IComponentData component)
        {
            if (true)
            {
                #region __GHOST_COPY_FROM_SNAPSHOT__
                component.__GHOST_FIELD_REFERENCE__ = snapshotBefore.__GHOST_FIELD_NAME__ * __GHOST_DEQUANTIZE_SCALE__;
                #endregion

                #region __GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_SETUP__
                var __GHOST_FIELD_NAME___Before = snapshotBefore.__GHOST_FIELD_NAME__ * __GHOST_DEQUANTIZE_SCALE__;
                var __GHOST_FIELD_NAME___After = snapshotAfter.__GHOST_FIELD_NAME__ * __GHOST_DEQUANTIZE_SCALE__;
                #endregion
                #region __GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_DISTSQ__
                var __GHOST_FIELD_NAME___DistSq = math.distancesq(__GHOST_FIELD_NAME___Before, __GHOST_FIELD_NAME___After);
                #endregion
                #region __GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE__
                component.__GHOST_FIELD_REFERENCE__ = math.lerp(__GHOST_FIELD_NAME___Before, __GHOST_FIELD_NAME___After, snapshotInterpolationFactor);
                #endregion
            }
        }
        public unsafe void RestoreFromBackup(ref IComponentData component, in IComponentData backup)
        {
            #region __GHOST_RESTORE_FROM_BACKUP__
            component.__GHOST_FIELD_REFERENCE__ = backup.__GHOST_FIELD_REFERENCE__;
            #endregion
        }
        public void CalculateChangeMask(ref Snapshot snapshot, ref Snapshot baseline, uint changeMask)
        {
            #region __GHOST_CALCULATE_CHANGE_MASK_ZERO__
            changeMask = (snapshot.__GHOST_FIELD_NAME__ != baseline.__GHOST_FIELD_NAME__) ? 1u : 0;
            #endregion
            #region __GHOST_CALCULATE_CHANGE_MASK__
            changeMask |= (snapshot.__GHOST_FIELD_NAME__ != baseline.__GHOST_FIELD_NAME__) ? (1u<<__GHOST_MASK_INDEX__) : 0;
            #endregion
        }
        #if UNITY_EDITOR || NETCODE_DEBUG
        private static void ReportPredictionErrors(ref IComponentData component, in IComponentData backup, ref UnsafeList<float> errors, ref int errorIndex)
        {
            #region __GHOST_REPORT_PREDICTION_ERROR__
            errors[errorIndex] = math.max(errors[errorIndex], math.abs(component.__GHOST_FIELD_REFERENCE__ - backup.__GHOST_FIELD_REFERENCE__));
            ++errorIndex;
            #endregion
        }
        private static int GetPredictionErrorNames(ref FixedString512Bytes names, ref int nameCount)
        {
            #region __GHOST_GET_PREDICTION_ERROR_NAME__
            if (nameCount != 0)
                names.Append(new FixedString32Bytes(","));
            names.Append(new FixedString64Bytes("__GHOST_FIELD_REFERENCE__"));
            ++nameCount;
            #endregion
        }
        #endif
    }
}
```

The recommended pattern for assigning the `#templateid` is `CustomNamespace.CustomTemplateFileName`. All default Netcode for Entities templates use an internal ID (not present in the template) with the format `NetCode.GhostSnapshotValueXXX.cs`.

For more information about template formatting, refer to the documentation in the `SourceGenerator/Documentation` folder, or reference other template files in `Editor/Templates/DefaultTypes`.

> [!NOTE]
> The [supported default types](#supported-types) use a slightly different approach to custom templates and are embedded in the generator DLLs. The template contains a set of C#-like regions, `#region __GHOST_XXX__`, that are processed by the code generator, which uses them to extract the code inside the region to create the serializer. The template uses the `__GHOST_XXX__` as a reserved keyword which is substituted at generation time with the corresponding variable names and/or values.

#### Defining `SubType` templates

You can use `SubType`s to define multiple templates for a given type. Use them by specifying them in the `GhostField` attribute.

```c#
using Unity.NetCode;

public struct MyComponent : Unity.Entities.IComponentData
{
    [GhostField(SubType=GhostFieldSubType.MySubType)] // <- This field uses the SubType `MySubType`.
    public float value;
    [GhostField] // <- This filed uses the default serializer Template for unquantized floats.
    public float value;
}

```

`SubType`s are added to projects by implementing a partial class, [`GhostFieldSubTypes`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldSubType.html), and then injecting it into the `Unity.Netcode` package using an [Assembly Definition Reference](https://docs.unity3d.com/Documentation/Manual/class-AssemblyDefinitionReferenceImporter.html). This adds new constant string literals to that class and which are then available to all your packages that already reference the `Unity.Netcode` assembly.

```c#
namespace Unity.NetCode
{
    static public partial class GhostFieldSubType
    {
        public const int MySubType = 1;
    }
}
```

Templates for `SubType`s are handled identically to other `UserDefinedTemplates`, but need to set the `SubType` field index. Refer to the [Writing a template section](#writing-a-template) for details, and note that the only difference is: `SubType = GhostFieldSubType.MySubType,`.

```c#
namespace Unity.NetCode.Generators
{
    public static partial class UserDefinedTemplates
    {
        static partial void RegisterTemplates(System.Collections.Generic.List<TypeRegistryEntry> templates, string defaultRootPath)
        {
            templates.AddRange(new[]{
                new TypeRegistryEntry
                {
                    Type = "System.Single",
                    SubType = GhostFieldSubType.MySubType,
                    ...
                },
            });
        }
    }
}
```

As when using any template registration like this, you need to be careful to specify the correct parameters when defining the `GhostField` to exactly match it. The important properties are `SubType`, in addition to `Quantized` and `Smoothing`, as these can affect how the serializer code is generated from the template.

> [!NOTE]
> The `Composite` parameter should always be false with subtypes because it's implicitly assumed that the template given is the one in use for the whole type.

### Registering a template

You can register a template with Netcode for Entities by implementing a partial class, [`UserDefinedTemplates`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.Generators.UserDefinedTemplates.html), and then injecting it into the `Unity.Netcode` package using an [Assembly Definition Reference](https://docs.unity3d.com/Documentation/Manual/class-AssemblyDefinitionReferenceImporter.html).

The partial implementation must define the method `RegisterTemplates` and add a new `TypeRegistry` entry (or entries). The class must also exist inside the `Unity.NetCode.Generators` namespace. Refer to the following example.

```c#
namespace Unity.NetCode.Generators
{
    public static partial class UserDefinedTemplates
    {
        static partial void RegisterTemplates(System.Collections.Generic.List<TypeRegistryEntry> templates, string defaultRootPath)
        {
            templates.AddRange(new[]{
                new TypeRegistryEntry
                {
                    Type = "MyCustomNamespace.MyCustomType",
                    Quantized = true,
                    Smoothing = SmoothingAction.InterpolateAndExtrapolate,
                    SupportCommand = false,
                    Composite = false,
                    Template = "MyCustomNamespace.MyCustomTypeTemplate",
                    TemplateOverride = "",
                },
            });
        }
    }
}
```

>[!NOTE]
> This example only registers `MyCustomType` when the `[GhostField]` is defined as follows `[GhostField(Quantization=100, Smoothing=SmoothingAction.InterpolateAndExtrapolate, Composite=false)]`.
> You must register all exact combinations you want to support (and register them exactly as used).

### Template definition requirements

There are a number of additional requirements for creating templates that must be adhered to.

- For `Serialize`, `Deserialize`, `__COMMAND_WRITE_PACKED__` and `__COMMAND_READ_PACKED__`, only `Packed` and `RawBits` (example: use `WriteRawBits(123, 8)` instead of `WriteByte(123)`) methods can be used from `DataStreamWriter` and `DataStreamReader`. Because Netcode does its own packing after a template's serialization, the write and read streams won't have the same byte alignment on both ends. This restriction does not apply to unpacked RPCs.
- When `Quantized` is set to true, the `__GHOST_QUANTIZE_SCALE__` variable must be present in the template. The quantization scale must also be specified when using the type in a `GhostField`.
- `Smoothing` is also important because it changes how serialization is done in the `CopyFromSnapshot` function. In particular:
    - When smoothing is set to `Clamp`, only the `__GHOST_COPY_FROM_SNAPSHOT__` is required.
    - When smoothing is set to `Interpolate` or `InterpolateAndExtrapolate`, the regions `__GHOST_COPY_FROM_SNAPSHOT__`, `__GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE__`, `GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_SETUP`, `__GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_DISTSQ__`, and `GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_CLAMP_MAX` must be present and filled in.
- The `SupportCommand` denotes if the type can be used inside `Commands` and/or `Rpc`.
- The `Template` value is mandatory, and must point to the `#templateid` defined in the target template file.
- `TemplateOverride` is optional (can be null or empty). `TemplateOverride` is used when you want to re-use an existing template, but only override a specific section of it.
    - This works well when using `Composite` types because you can point `Template` to the basic type (like the `float` template), and then point to the `TemplateOverride` only for the sections which need to be customized.
    - For example, `float2` only defines `CopyFromSnapshot`, `ReportPredictionErrors`, and `GetPredictionErrorNames`, the rest uses the basic `float` template as a composite of the 2 values `float2` contains. The assigned value must be the `#templateid` of the base template, as declared inside the other template file.
- The `Composite` flag should be `true` when declaring templates for 'container-like' types, such as types that contain multiple fields of the same type (like `float3`, `float4`, and so on). When this is set, the `Template` and `TemplateOverride` are applied to the field types, and not to the containing type.
- If you need your template to define additional fields in the snapshot (for example, to map correctly on the server), you must define `__GHOST_CALCULATE_CHANGE_MASK_NO_COMMAND__` and `__GHOST_CALCULATE_CHANGE_MASK_ZERO_NO_COMMAND__` in the changemask calculation method, as commands point to the type directly (but components have snapshots that can store additional data). These changemasks can then be correctly found for any/all additional field(s). Refer to the `GhostSnapshotValueEntity` template for an example.

You must fill in all sections.

> [!NOTE]
> When making changes to templates, you need to use the **Multiplayer** > **Force Code Generation** menu to force a new code compilation (which will then use the updated templates).

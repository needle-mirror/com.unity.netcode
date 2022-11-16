# Ghost Templates

Ghost components and ghost field types are all handled a certain way during conversion and code generation to produce the right code when building players. 
It's possible to define the desired behavior in code and on a per ghost prefab basis.

## Supported Types
Inside the package we have default templates for how to generate serializers for a limited set of types:

* bool
* Entity
* FixedString32Bytes
* FixedString64Bytes
* FixedString128Bytes
* FixedString512Bytes
* FixedString4096Bytes
* float
* float2
* float3
* float4
* byte
* sbyte
* short
* ushort
* int
* uint
* long
* ulong
* enums (only for int/uint underlying type)  
* quaternion
* double

For certain types (i.e float, double, quaternion ,float2/3/4) multiple templates exists to handle different way to serialise the type: 

* Quantized or un-quantized. Where quantized means a float value is sent as an int with a certain multiplication factor which sets the precision (12.456789 can be sent as 12345 with a quantization factor of 1000).
* Smoothing method as clamp or interpolate/extrapolate. Meaning the value can be applied from a snapshot as interpolated/extrapolated or unmodified directly (clamped).

Since each of these can change how the source value is serialized, deserialized and applied on the target

these might new templates or a region inside defined to handle certain cases (like how to interpolate the value).

Ghost component *variants* and *subtypes*. Variants can allow you to define another way for example to synchronize a float, define a different way to quantize it for example.

## Defining additional templates

It's possible to register other types which are not supported and the default templates either don't cover at all or need separate handling.

Templates are added to the project by implementing a partial class, **UserDefinedTemplates**, and injecting it into the `Unity.Netcode` package by using
an [AssemblyDefinitionReference](https://docs.unity3d.com/2020.1/Documentation/Manual/class-AssemblyDefinitionReferenceImporter.html). The partial implementation must
define the method `RegisterTemplates` and add new `TypeRegistry` entries.

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
                    Type = "MySpecialType",
                    Quantized = true,
                    Smoothing = SmoothingAction.InterpolateAndExtrapolate,
                    SupportCommand = false,
                    Composite = false,
                    Template = "Assets/Samples/NetCodeGen/Templates/MySpecialTypeTemplate.cs",
                    TemplateOverride = "",
                },
            });
        }
    }
}
```

The template _MySpecialTypeTemplate.cs_ needs to be set up similarly to default types, here is the default Float template (where the float is quantized and stored in an int):

```c#
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
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
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

When `Quantized` is set to true the *\_\_GHOST_QUANTIZE_SCALE\_\_* variable must be present in the template, and also the quantization scale **must** be specified when using the type in a `GhostField`

`Smoothing` is also important as it changes how serialization is done in the _CopyFromSnapshot_ function, all sections must be filled in.

`TemplateOverride` is used when you want to re-use an existing template but only override a specific section of it. This works well when using `Composite` types as you'll point `Template` to the basic type (like float template) and the `TemplateOverride` to only the sections which need to be customized. For example float2 only defines _CopyFromSnapshot_, _ReportPredictionErrors_ and _GetPredictionErrorNames_, the rest uses the basic float template as a composite of the 2 values float2 contains.


>![NOTE]: It's important that the templates (when using .cs extension) are in a folder with an .asmdef effectively disabling compilation on it, since this isn't real code we want compiled. It can be done by adding an invalid conditional define on the .asmdef (we use *NETCODE_CODEGEN_TEMPLATES* define in the samples). It's possible though to just store them with any extension (like .txt) and then the compiler won't consider them.

>![NOTE]: When making changes to the templates you need to use the _Multiplayer->Force Code Generation_ menu to force a new code compilation which will use the updated templates.

## Defining SubTypes and templates

Subtypes are a way to define a different way of serializing a specific type. You use them by specifying them in the `GhostField` attribute.

```c#
using Unity.NetCode;

public struct MyComponent : Unity.Entities.IComponentData
{
    [GhostField(SubType=GhostFieldSubType.MySubType)]
    public float value;
}

```

SubTypes are added to projects by implementing a partial class, **GhostFieldSubTypes**, and injecting it into the `Unity.Netcode` package by using
an [AssemblyDefinitionReference](https://docs.unity3d.com/2020.1/Documentation/Manual/class-AssemblyDefinitionReferenceImporter.html). The implementation should just
need to add new constant literals to that class (at your own discretion) and they will be available to all your packages which already reference the `Unity.Netcode` assembly.

```c#
namespace Unity.NetCode
{
    static public partial class GhostFieldSubType
    {
        public const int MySubType = 1;
    }
}
```

Templates for the subtypes are handled like normal user defined templates but need to set the subtype index. So they are added to the project by implementing the partial class, **UserDefinedTemplates**, and injecting it into the Unity.Netcode package by using
an [AssemblyDefinitionReference](https://docs.unity3d.com/2020.1/Documentation/Manual/class-AssemblyDefinitionReferenceImporter.html). The partial implementation must
define the method `RegisterTemplates` and add new`TypeRegistry` entries.

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
                    Quantized = false,
                    Smoothing = SmoothingAction.InterpolateAndExtrapolate,
                    SupportCommand = false,
                    Composite = false,
                    Template = "Assets/Samples/NetCodeGen/Templates/MyCustomTemplate.cs",
                    TemplateOverride = "",
                },
            });
        }
    }
}
```

As when using any template registration like this, you need to be careful to specify the correct parameters when defining the `GhostField` to exactly match it. The important properties are `SubType` of course, in addition to `Quantized` and `Smoothing` as these can affect how the serializer code is generated from the template.

---
**IMPORTANT**:
The `Composite` parameter should always be false with subtypes as it is assumed the template given is the one to use for the whole type.

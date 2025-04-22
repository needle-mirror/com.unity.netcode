using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Unity.NetCode.Generators
{
    public enum GenTypeKind
    {
        Invalid,
        Primitive,
        Enum,
        Struct,
        FixedList,
        FixedSizeArray
    }

    public enum ComponentType
    {
        Unknown = 0,
        Component,
        HybridComponent,
        Buffer,
        Rpc,
        CommandData,
        Input
    }

    // This is used internally in SG but needs to be kept in sync with the runtime netcode class in
    // Runtime/Authoring/GhostComponentAttribute.cs
    internal class GhostComponentAttribute
    {
        public GhostPrefabType PrefabType;
        public GhostSendType SendTypeOptimization;
        public SendToOwnerType OwnerSendType;
        public bool SendDataForChildEntity;

        public GhostComponentAttribute()
        {
            PrefabType = GhostPrefabType.All;
            SendTypeOptimization = GhostSendType.AllClients;
            OwnerSendType = SendToOwnerType.All;
            SendDataForChildEntity = false;
        }
    }

    /// <summary>
    /// A type descriptor, completely independent from roslyn types, used to generate serialization code for
    /// both ghosts and commands
    /// </summary>
    internal class TypeInformation
    {
#pragma warning disable 649
        public string Namespace;
        public string TypeFullName;
        //Only valid for type that support a different type of backend, like Enums. Return empty otherwise
        public string UnderlyingTypeName;
        //Only valid for field. Empty or null in all other cases
        public string FieldName;
        //Optional and only valid for field. Empty or null in all other cases. Used to store an alternative path or name
        //to access a field in the snapshot data, in case the access pattern does not match the automated rules
        //parent.field.name -> parent_field_name
        public string SnapshotFieldName;
        //Only valid for field. Empty or null in all other cases
        public string FieldTypeName;
        //Only valid for field. Empty or null in all other cases
        public string ContainingTypeFullName;
        public GenTypeKind Kind;
        //This is valid for the root type and always NotApplicable for the members
        public ComponentType ComponentType;
        //Children can inherit and set attribute if they are set in the mask (by default: all)
        public TypeAttribute.AttributeFlags AttributeMask = TypeAttribute.AttributeFlags.All;
        public TypeAttribute Attribute;
        //Only applicable to root
        public GhostComponentAttribute GhostAttribute;
        //The path to field starting from the root
        public string FieldPath;
        public ITypeSymbol Symbol;
#pragma warning restore 649
        //The syntax tree and text span location of the type
        public Location Location;
        //Only valid for generic types.
        public string GenericTypeName;
        public TypeInformation PointeeType;
        public List<TypeInformation> GhostFields = new List<TypeInformation>();
        public bool ShouldSerializeEnabledBit;
        public bool HasDontSupportPrefabOverridesAttribute;
        public bool IsTestVariant;
        public bool CanBatchPredict;
        //for fixed buffers and fixed list, the number of elements
        public int ElementCount;
        public TypeDescription Description
        {
            get
            {
                var description = new TypeDescription
                {
                    TypeFullName = TypeFullName,
                    Attribute = Attribute
                };
                if (Kind == GenTypeKind.Enum)
                    description.Key = UnderlyingTypeName;
                else if (Kind == GenTypeKind.FixedList)
                    description.Key = GenericTypeName;
                else
                    description.Key = TypeFullName;
                return description;
            }
        }

        public bool IsValid => Kind != GenTypeKind.Invalid;

        public override string ToString()
        {
            return $"{TypeFullName} (quantized={Attribute.quantization} composite={Attribute.aggregateChangeMask} smoothing={Attribute.smoothing} subtype={Attribute.subtype})";
        }
    }
}

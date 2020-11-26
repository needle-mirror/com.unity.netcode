using System;
using System.Collections.Generic;

namespace Unity.NetCode.Editor
{
    public struct TypeDescription
    {
        public string TypeFullName;
        public string Key;
        public TypeAttribute Attribute;

        public TypeDescription(Type type, TypeAttribute attribute)
        {
            TypeFullName = type.FullName;
            Key = !type.IsEnum ? type.FullName : typeof(Enum).FullName;
            Attribute = attribute;
        }
        public override bool Equals(object obj)
        {
            if (obj is TypeDescription)
            {
                var other = (TypeDescription)obj;
                var otherQuantization = other.Attribute.quantization > 0;
                var ourQuantization = Attribute.quantization > 0;

                var mask = (uint)TypeAttribute.AttributeFlags.Interpolated;

                if (other.Key == this.Key &&
                    other.Attribute.subtype == Attribute.subtype &&

                    (other.Attribute.smoothing & mask) == (Attribute.smoothing & mask) &&

                    ourQuantization == otherQuantization)
                    return true;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return Key.GetHashCode() ^ Attribute.GetHashCode();
        }
    }

    public class TypeTemplate
    {
        public bool SupportsQuantization = false;
        public bool Composite = false;
        public bool SupportCommand = true;
        public string TemplatePath;
        public string TemplateOverridePath;

        public override string ToString()
        {
            return
                $"IsComposite({Composite}), SupportQuantization({SupportsQuantization})\nTemplatePath: {TemplatePath}\nTemplateOverridePath: {TemplateOverridePath}\n";
        }
    }

    public struct TypeAttribute
    {
        public int  quantization;
        public int  subtype;

        public uint smoothing;

        public bool composite;
        public int  maxSmoothingDist;

        public static TypeAttribute Empty()
        {
            return new TypeAttribute
            {
                composite = false,
                smoothing = (uint)SmoothingAction.Clamp,
                quantization = -1,
                subtype = 0,
                maxSmoothingDist = 0
            };
        }

        [Flags]
        public enum AttributeFlags
        {
            None = 0,
            Interpolated = 1 << 0,
            InterpolatedAndExtrapolated = Interpolated | 1 << 1,
            Quantized = 1 << 2,
            Composite = 1 << 3,

            All = InterpolatedAndExtrapolated | Quantized | Composite
        }
        public static TypeAttribute Specialized(AttributeFlags flags, int subtype = 0)
        {
            return new TypeAttribute
            {
                composite = flags.HasFlag(AttributeFlags.Composite),
                smoothing = (uint)AttributeFlags.InterpolatedAndExtrapolated & (uint)flags,
                quantization = flags.HasFlag(AttributeFlags.Quantized) ? 1 :-1,
                subtype = subtype
            };
        }

        public override int GetHashCode()
        {
            bool isquantized = quantization > 0;
            return isquantized.GetHashCode() ^ subtype.GetHashCode() ^
                   (int) ((smoothing & (uint) AttributeFlags.Interpolated));
        }
    }
    internal class TypeInformation
    {
        public Mono.Cecil.TypeReference Type;
        public Mono.Cecil.TypeReference DeclaringType;
        public string FieldName;
        public TypeAttribute Attribute;
        //Children can inherith and set attribute if they are set in the mask (by default: all)
        public TypeAttribute.AttributeFlags AttributeMask = TypeAttribute.AttributeFlags.All;

        public string Parent;

        public List<TypeInformation> Fields;

        public TypeDescription Description
        {
            get
            {
                var typeFullName = Type.FullName.Replace("/", "+");
                return new TypeDescription
                {
                    TypeFullName = typeFullName,
                    Key = !Type.IsPrimitive && Type.Resolve().IsEnum ? typeof(Enum).FullName : typeFullName,
                    Attribute = Attribute
                };
            }
        }

        public bool IsValid => this.Type != null;

        public void Add(TypeInformation information)
        {
            Fields.Add(information);
        }

        public TypeInformation(Mono.Cecil.TypeDefinition type)
        {
            Type = type;
            Fields = new List<TypeInformation>();
            Attribute = TypeAttribute.Empty();
        }

        public TypeInformation(Mono.Cecil.IMemberDefinition field, Mono.Cecil.TypeReference fieldType, GhostFieldAttribute ghostField,
            TypeAttribute inheritedAttribute, TypeAttribute.AttributeFlags inheritedAttributeMask, string parent = null)
        {
            Type = fieldType;
            FieldName = field.Name;
            DeclaringType = field.DeclaringType;
            Fields = new List<TypeInformation>();
            Attribute = inheritedAttribute;
            AttributeMask = inheritedAttributeMask;
            //Always reset the subtype. It cannot be inherithed like this
            Attribute.subtype = 0;
            //Reset flags based on inheritance mask
            Attribute.composite &= (AttributeMask & TypeAttribute.AttributeFlags.Composite) != 0;

            Attribute.smoothing &= inheritedAttribute.smoothing;

            if((AttributeMask & TypeAttribute.AttributeFlags.Quantized) == 0)
                Attribute.quantization = -1;


            ParseAttribute(ghostField);
            Parent = string.IsNullOrEmpty(parent) ? "" : parent;
        }

        void ParseAttribute(GhostFieldAttribute attribute)
        {
            if (attribute != null)
            {
                if (attribute.Quantization >= 0) Attribute.quantization = attribute.Quantization;
                if ((int) attribute.Smoothing >= 0) Attribute.smoothing = (uint) attribute.Smoothing;
                if (attribute.SubType > 0) Attribute.subtype = attribute.SubType;
                if (attribute.Composite) Attribute.composite = attribute.Composite;
                if (attribute.MaxSmoothingDistance > 0)
                    Attribute.maxSmoothingDist = attribute.MaxSmoothingDistance;
            }
        }

        public override string ToString()
        {
            var typeName = this.Type.Name;
            var fieldName = this.FieldName != null ? this.FieldName : "";
            var attributes = "";

            if (Attribute.quantization >= 0)
                attributes = $"quantization: {Attribute.quantization} ";
            if (Attribute.composite)
                attributes += "is_composite ";
            if (Attribute.subtype != 0)
                attributes += $"subtype: {Attribute.subtype} ";
            if (Attribute.smoothing == (uint)SmoothingAction.Interpolate)
                attributes += "interpolated";

            return $"({typeName}) [{attributes}] {fieldName}\n";
        }
    }

    public class TypeRegistry
    {
        public const string k_TemplateRootPath = "Packages/com.unity.netcode/Editor/CodeGenTemplates/DefaultTypes";
        public Dictionary<TypeDescription, TypeTemplate> Templates = new Dictionary<TypeDescription, TypeTemplate>();

        public TypeRegistry(CodeGenType[] types)
        {
            RegisterTypes(types);
        }
        public bool CanGenerateType(TypeDescription description)
        {
            if (!Templates.TryGetValue(description, out var template))
            {
                return false;
            }

            if (template.SupportsQuantization && description.Attribute.quantization < 0)
            {
                UnityEngine.Debug.LogError(
                    $"{description.TypeFullName} is of type {description.TypeFullName} which requires quantization factor to be specified - ignoring field");
                return false;
            }

            if (!template.SupportsQuantization && description.Attribute.quantization > 0)
            {
                UnityEngine.Debug.LogError(
                    $"{description.TypeFullName} is of type {description.TypeFullName} which does not support quantization - ignoring field");
                return false;
            }

            return true;
        }

        public struct CodeGenType
        {
            public TypeDescription description;
            public TypeTemplate template;
        }

        public void RegisterType(Type type, TypeAttribute attribute, TypeTemplate template)
        {
            Templates.Add(new TypeDescription(type, attribute), template);
        }

        public void RegisterTypes(CodeGenType[] types)
        {
            foreach (var type in types)
            {
                Templates.Add(type.description, type.template);
            }
        }
    }
}

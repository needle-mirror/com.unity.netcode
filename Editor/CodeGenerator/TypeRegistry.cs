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

        public TypeDescription(Mono.Cecil.TypeReference typeReference, TypeAttribute attribute)
        {
            TypeFullName = typeReference.FullName.Replace("/", "+");

            if (!typeReference.IsPrimitive && typeReference.Resolve().IsEnum)
            {
                Key = typeof(Enum).FullName;
            }
            else
            {
                Key = TypeFullName;
            }
            Attribute = attribute;
        }

        public override bool Equals(object obj)
        {
            if (obj is TypeDescription)
            {
                var other = (TypeDescription)obj;
                var otherQuantization = other.Attribute.quantization > 0;
                var ourQuantization = Attribute.quantization > 0;

                if (other.Key == this.Key &&
                    other.Attribute.subtype == Attribute.subtype &&
                    other.Attribute.interpolate == Attribute.interpolate &&
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
        public bool SupportRpc = true;
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
        public bool interpolate;
        public bool composite;

        public static TypeAttribute Empty()
        {
            return new TypeAttribute
            {
                composite = false,
                interpolate = false,
                quantization = -1,
                subtype = 0
            };
        }

        [Flags]
        public enum AttributeFlags
        {
            None = 0,
            Quantized = 1 << 0,
            Composite = 1 << 1,
            Interpolated = 1 << 2
        }
        public static TypeAttribute Specialized(AttributeFlags flags, int subtype = 0)
        {
            return new TypeAttribute
            {
                composite = flags.HasFlag(AttributeFlags.Composite),
                interpolate = flags.HasFlag(AttributeFlags.Interpolated),
                quantization = flags.HasFlag(AttributeFlags.Quantized) ? 1 :-1,
                subtype = subtype
            };
        }

        public override int GetHashCode()
        {
            bool isquantized = quantization > 0;
            return isquantized.GetHashCode() ^ subtype.GetHashCode() ^ interpolate.GetHashCode();
        }
    }
    public class TypeInformation
    {
        public Mono.Cecil.TypeReference Type;
        public TypeAttribute Attribute;

        public Mono.Cecil.FieldReference FieldInfo;
        public string Parent;

        public List<TypeInformation> Fields;

        public TypeDescription Description => new TypeDescription(Type, Attribute);

        public bool IsValid => this.Type != null;

        public void Add(TypeInformation information)
        {
            Fields.Add(information);
        }

        private TypeInformation()
        {
            Fields = new List<TypeInformation>();
        }

        public TypeInformation(Mono.Cecil.TypeDefinition type)
        {
            Type = type;
            Fields = new List<TypeInformation>();
            Attribute = TypeAttribute.Empty();
        }

        public TypeInformation(Mono.Cecil.TypeReference parentType, Mono.Cecil.FieldDefinition fieldInfo, TypeAttribute inheritedAttribute,
            string parent = null)
        {
            FieldInfo = fieldInfo;
            Type = fieldInfo.FieldType;
            Fields = new List<TypeInformation>();
            Attribute = inheritedAttribute;

            ParseAttribute(CecilExtensions.GetGhostFieldAttribute(parentType, fieldInfo));
            Parent = string.IsNullOrEmpty(parent) ? "" : parent;
        }

        void ParseAttribute(GhostFieldAttribute attribute)
        {
            if (attribute != null)
            {
                if (attribute.Quantization >= 0) Attribute.quantization = attribute.Quantization;
                if (attribute.Interpolate) Attribute.interpolate = attribute.Interpolate;
                if (attribute.SubType > 0) Attribute.subtype = attribute.SubType;
                if (attribute.Composite) Attribute.composite = attribute.Composite;
            }
        }

        public override string ToString()
        {
            var typeName = this.Type.Name;
            var fieldName = this.FieldInfo?.Name ?? "";
            var attributes = "";

            if (Attribute.quantization >= 0)
                attributes = $"quantization: {Attribute.quantization} ";
            if (Attribute.composite)
                attributes += "is_composite ";
            if (Attribute.subtype != 0)
                attributes += $"subtype: {Attribute.subtype} ";
            if (Attribute.interpolate)
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

    public static class TypeExtensions
    {


        public static void PrintInfo(this Mono.Cecil.TypeDefinition type)
        {
            System.Console.WriteLine($"Printing info about ({type.Name})");
            if (type.IsBlittable())
                System.Console.WriteLine("Blittable");
            if (type.IsPublic)
                System.Console.WriteLine("Public");
            if (type.IsEnum)
                System.Console.WriteLine("Enum");
            if (type.IsStruct())
                System.Console.WriteLine("Struct");
            if (type.IsPrimitive)
                System.Console.WriteLine("Primitive");
            if (type.IsValueType)
                System.Console.WriteLine("ValueType");
            if (type.IsClass)
                System.Console.WriteLine("Class");
        }
    }
}

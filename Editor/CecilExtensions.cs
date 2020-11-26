using System;
using System.Linq;
using Unity.Entities;

namespace Unity.NetCode.Editor
{
    //Basics extensions to make generation work a little simpler and similar to the usual
    //.NET reflection
    public static class CecilExtensions
    {
        public static bool HasAttribute<T>(this Mono.Cecil.ICustomAttributeProvider type) where T: Attribute
        {
            return type.CustomAttributes.Any(a => a.AttributeType.FullName == typeof(T).FullName);
        }

        public static Mono.Cecil.CustomAttribute GetAttribute<T>(this Mono.Cecil.ICustomAttributeProvider type) where T: Attribute
        {
            return type.CustomAttributes.Where(a => a.AttributeType.FullName == typeof(T).FullName).FirstOrDefault();
        }

        public static bool IsTypeOf<T>(this Mono.Cecil.TypeReference type)
        {
            return IsTypeOf(type, typeof(T));
        }
        public static bool IsTypeOf(this Mono.Cecil.TypeReference type, Type otherType)
        {
            return type.Name == otherType.Name && type.Namespace == otherType.Namespace;
        }

        public static bool IsEntityType(this Mono.Cecil.TypeReference type)
        {
            return type.Name == "Entity" && type.Namespace == "Unity.Entities";
        }

        public static bool IsIComponentData(this Mono.Cecil.TypeReference typeReference)
        {
            var resolvedType = typeReference.Resolve();
            if (resolvedType == null)
                return false;
            return resolvedType.Interfaces.Any(i =>
                i.InterfaceType.Name == typeof(IComponentData).Name &&
                i.InterfaceType.Namespace == typeof(IComponentData).Namespace);
        }
        public static bool IsBufferElementData(this Mono.Cecil.TypeReference typeReference)
        {
            var resolvedType = typeReference.Resolve();
            if (resolvedType == null)
                return false;
            return resolvedType.Interfaces.Any(i =>
                i.InterfaceType.Name == typeof(IBufferElementData).Name &&
                i.InterfaceType.Namespace == typeof(IBufferElementData).Namespace);
        }
        public static bool IsIRpcCommand(this Mono.Cecil.TypeReference typeReference)
        {
            var resolvedType = typeReference.Resolve();
            if (resolvedType == null)
                return false;
            return resolvedType.Interfaces.Any(i =>
                i.InterfaceType.Name == typeof(IRpcCommand).Name &&
                i.InterfaceType.Namespace == typeof(IRpcCommand).Namespace);
        }
        public static bool IsICommandData(this Mono.Cecil.TypeReference typeReference)
        {
            var resolvedType = typeReference.Resolve();
            if (resolvedType == null)
                return false;
            return resolvedType.Interfaces.Any(i =>
                i.InterfaceType.Name == typeof(ICommandData).Name &&
                i.InterfaceType.Namespace == typeof(ICommandData).Namespace);
        }

        public static bool HasGhostFieldAttribute(Mono.Cecil.TypeReference parentType, Mono.Cecil.IMemberDefinition componentField)
        {
            if (!GhostAuthoringModifiers.GhostDefaultOverrides.TryGetValue(parentType.FullName.Replace('/','+'), out var newComponent))
            {
                return componentField.HasAttribute<GhostFieldAttribute>();
            }
            else
            {
                return newComponent.fields.Any(f => f.name == componentField.Name);
            }
        }

        public static GhostFieldAttribute GetGhostFieldAttribute(Mono.Cecil.TypeReference parentType, Mono.Cecil.IMemberDefinition componentField)
        {
            if (parentType != null && GhostAuthoringModifiers.GhostDefaultOverrides.TryGetValue(parentType.FullName.Replace('/','+'), out var newComponent))
            {
                foreach (var field in newComponent.fields)
                {
                    if (field.name == componentField.Name)
                        return field.attribute;
                }
                return default(GhostFieldAttribute);
            }

            var attribute = componentField.GetAttribute<GhostFieldAttribute>();
            if (attribute != null)
            {
                var fieldAttribute = new GhostFieldAttribute();
                if (attribute.HasProperties)
                {
                    foreach (var a in attribute.Properties)
                    {
                        typeof(GhostFieldAttribute).GetProperty(a.Name)?.SetValue(fieldAttribute, a.Argument.Value);
                    }
                }

                return fieldAttribute;
            }
            return default(GhostFieldAttribute);
        }

        public static GhostComponentAttribute GetGhostComponentAttribute(Mono.Cecil.TypeDefinition managedType)
        {
            var attribute = managedType.GetAttribute<GhostComponentAttribute>();
            if (attribute != null)
            {
                var ghostAttribute = new GhostComponentAttribute();
                if (attribute.HasProperties)
                {
                    foreach (var a in attribute.Properties)
                    {
                        typeof(GhostComponentAttribute).GetProperty(a.Name)?.SetValue(ghostAttribute, a.Argument.Value);
                    }
                }

                return ghostAttribute;
            }

            return null;
        }

        public static ulong ComputeVariantHash(Mono.Cecil.TypeReference variantType, Mono.Cecil.CustomAttribute attribute)
        {
            var hash = TypeHash.FNV1A64(attribute.AttributeType.FullName);
            var componentType = attribute.ConstructorArguments[0].Value as Mono.Cecil.TypeReference;
            var variantName = variantType.FullName;
            hash = TypeHash.CombineFNV1A64(hash, TypeHash.FNV1A64(componentType.FullName));
            hash = TypeHash.CombineFNV1A64(hash, TypeHash.FNV1A64(variantName));
            return hash;
        }

        public static bool IsStruct(this Mono.Cecil.TypeDefinition type)
        {
            return !type.IsPrimitive && type.IsValueType && !type.IsEnum;
        }

        public static bool IsBlittable(this Mono.Cecil.TypeReference type)
        {
            if (type.IsPrimitive || type.IsTypeOf<IntPtr>() || type.IsTypeOf<UIntPtr>())
                return true;

            return false;
        }

        public static string GetFieldTypeName(this Mono.Cecil.TypeReference type)
        {
            if (type.IsTypeOf<Byte>()) return "byte";
            if (type.IsTypeOf<SByte>()) return "sbyte";
            if (type.IsTypeOf<Int16>()) return "short";
            if (type.IsTypeOf<UInt16>()) return "ushort";
            if (type.IsTypeOf<Int32>()) return "int";
            if (type.IsTypeOf<UInt32>()) return "uint";
            if (type.IsTypeOf<Int64>()) return "long";
            if (type.IsTypeOf<UInt64>()) return "ulong";

            if (type.IsTypeOf<IntPtr>()) return "iptr";
            if (type.IsTypeOf<UIntPtr>()) return "uptr";

            if (type.IsTypeOf<Single>()) return "float";
            if (type.IsTypeOf<Double>()) return "double";

            return type.ToString().Replace("/", ".");
        }

        static public bool HasAttributeOnMemberOImplementedInterfaces<T>(this Mono.Cecil.FieldDefinition member) where T: Attribute
        {
            foreach (var m in member.DeclaringType.Interfaces)
            {
                var prop = m.InterfaceType.Resolve().Fields.FirstOrDefault(p => p.Name == member.Name);
                if (prop != null && prop.GetAttribute<T>() != null)
                    return true;
            }

            return false;
        }
        static public bool HasAttributeOnMemberOImplementedInterfaces<T>(this Mono.Cecil.PropertyDefinition member) where T: Attribute
        {
            foreach (var m in member.DeclaringType.Interfaces)
            {
                var prop = m.InterfaceType.Resolve().Properties.FirstOrDefault(p => p.Name == member.Name);
                if (prop != null && prop.GetAttribute<T>() != null)
                    return true;
            }

            return false;
        }

    }
}
using System;

namespace Unity.NetCode
{
    /// <summary>
    /// For internal use only.
    /// Markup for the generate component/buffer code-generated serializer, added automatically by the code-generation system.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public class GhostSerializerAttribute : Attribute
    {
        public readonly Type ComponentType;
        public readonly ulong VariantHash;

        public GhostSerializerAttribute(Type componentType, ulong variantHash)
        {
            ComponentType = componentType;
            VariantHash = variantHash;
        }
    }
}
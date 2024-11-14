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
        /// <summary>
        /// The component type this serializer is for.
        /// </summary>
        public readonly Type ComponentType;
        /// <summary>
        /// The calculated variant hash for this serializer. If the serialization is generated from the component type
        /// declaration, this field is 0.
        /// </summary>
        public readonly ulong VariantHash;

        /// <summary>
        /// Construct the attribute and assign the component and variant hash.
        /// </summary>
        /// <param name="componentType">The component type this serializer is for.</param>
        /// <param name="variantHash">The calculated variant hash for this serializer.</param>
        public GhostSerializerAttribute(Type componentType, ulong variantHash)
        {
            ComponentType = componentType;
            VariantHash = variantHash;
        }
    }
}

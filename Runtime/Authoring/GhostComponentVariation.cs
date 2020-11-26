using System;
using System.Reflection;

namespace Unity.NetCode
{
    /// <summary>
    /// Generate a serialization variantion for a component using the <seealso cref="GhostFieldAttribute"/> annotations
    /// present in variant declaration.
    /// The component variant can be assigned at authoring time using the GhostAuthoringComponent editor.
    /// <remarks>
    /// When declaring a variant, all fields that should be serialized must be annotated. Any missing field or new field
    /// not present in the original struct will not be serialized.
    /// </remarks>
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public class GhostComponentVariationAttribute : Attribute
    {
        readonly public Type ComponentType;
        /// <summary>
        /// User friendly name for the variation. Used mainly for UI and logging purpose.
        /// If not assigned at construction time, the annotated class name will be used instead.
        /// </summary>
        public string DisplayName { get; internal set; }
        //Setup when the internal cache is populated for code generation purpose
        public ulong VariantHash { get; internal set; }

        /// <summary>
        /// For internal use, declare the default varion for a the given type
        /// </summary>
        /// <param name="componentType"></param>
        internal GhostComponentVariationAttribute(Type componentType)
        {
            ComponentType = componentType;
            DisplayName = "None";
            VariantHash = 0;
        }

        /// <summary>
        /// Initialize and declare the variant for a given component type.
        /// </summary>
        /// <param name="componentType"></param>The type of the component we want to generate a variation for.
        /// <param name="displayName"></param>An unique name for that variant. The name must be UNIQUE for each component type.
        /// <exception cref="ArgumentException"></exception>
        //We can't constraint on a specific interface. The validation at the moment is done at compile time in the constructor
        public GhostComponentVariationAttribute(Type componentType, string displayName=null)
        {
            if(!typeof(Entities.IComponentData).IsAssignableFrom(componentType) || !componentType.IsValueType)
                throw new ArgumentException("Invalid component type {type}. Must be a struct and implements IComponentData.");

            if (componentType.GetCustomAttribute<DontSupportVariation>() != null)
                throw new ArgumentException("Component type {type} does not support variation.");

            ComponentType = componentType;
            DisplayName = displayName;
            if(displayName == "None" || displayName == "Default")
                throw new ArgumentException("GhostComponentVariationAttribute: variationName must be different from 'Default' or 'None'");
        }

        public static ulong ComputeVariantHash(Type variantType, GhostComponentVariationAttribute attribute)
        {
            var hash = Entities.TypeHash.FNV1A64(attribute.GetType().FullName);
            var componentTypeName = attribute.ComponentType.FullName.Replace("+", "/");
            var variantName = variantType.FullName.Replace("+", "/");
            hash = Entities.TypeHash.CombineFNV1A64(hash, Entities.TypeHash.FNV1A64(componentTypeName));
            hash = Entities.TypeHash.CombineFNV1A64(hash, Entities.TypeHash.FNV1A64(variantName));
            return hash;
        }
    }
}
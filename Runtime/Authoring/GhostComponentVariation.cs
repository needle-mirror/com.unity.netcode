using System;

namespace Unity.NetCode
{
    /// <summary>
    /// Generate a serialization variantion for a component using the <seealso cref="GhostFieldAttribute"/> annotations
    /// present in variant declaration.
    /// The component variant can be assigned at authoring time using the GhostAuthoringComponent editor.
    /// <remarks>
    /// When declaring a variant, all fields that should be serialized must be declared. Any missing field or new field
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
        //Setup when the internal cache is populated after domain reload.
        public ulong VariantHash { get; internal set; }

        /// <summary>
        /// Initialize and declare the variant for a given component type.
        /// </summary>
        /// <param name="componentType"></param>The type of the component we want to generate a variation for.
        /// <param name="displayName"></param>A string used for debug or display purpose. If null, the class name is used instead. "None" and "Default" are not valid names and will be treated as null.
        /// <exception cref="ArgumentException"></exception>
        //We can't constraint on a specific interface. The validation at the moment is done at compile time in the constructor
        public GhostComponentVariationAttribute(Type componentType, string displayName = null)
        {
            if (displayName == "None" || displayName == "Default")
                displayName = null;
            ComponentType = componentType;
            DisplayName = displayName;
        }
    }
}

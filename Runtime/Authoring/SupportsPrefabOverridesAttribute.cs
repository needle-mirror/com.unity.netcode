using System;

namespace Unity.NetCode
{
    /// <summary>
    /// Use this attribute to <b>allow</b> a GhostComponent to support any kind of Ghost variation.
    /// Mutually exclusive to <see cref="DontSupportPrefabOverridesAttribute"/>.
    /// </summary>
    /// <remarks>Note that if a type implements <see cref="GhostComponentVariationAttribute"/>, it implicitly supports prefab overrides.</remarks>
    /// <example>Use Case: Disabling a rendering component on the `Server` version of a Ghost.</example>
    [AttributeUsage(AttributeTargets.Struct)]
    public class SupportsPrefabOverridesAttribute : Attribute
    {
    }
}

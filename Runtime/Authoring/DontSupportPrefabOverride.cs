using System;

namespace Unity.NetCode
{
    /// <summary>
    /// Use this attribute to prevent a GhostComponent to support any kind of variation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
    sealed public class DontSupportPrefabOverrides : Attribute
    {
    }
}
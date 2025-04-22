using System;

namespace Unity.NetCode
{
    /// <summary>
    /// Attribute used to explicitly instruct code-serialization to limit the fixed-size list capacity.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field|AttributeTargets.Property, Inherited = true)]
    public class GhostFixedListCapacityAttribute : Attribute
    {
        /// <summary>
        /// The maximum number of replicated elements. When the length of the list is larger than this threshold
        /// only the first MaxReplicatedElements are replicated.
        /// </summary>
        /// <remarks>
        /// The MaxReplicatedElements must be always less or equal than 64 elements. The restriction is enforced at compile time.
        /// </remarks>
        public uint Capacity;
    }
}

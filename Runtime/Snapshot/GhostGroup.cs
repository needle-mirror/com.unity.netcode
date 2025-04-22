using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    ///     <para>
    ///         Similar to <see cref="LinkedEntityGroup" />, this buffer can be added to a ghost (via the
    ///         <c>GhostAuthoringComponent</c>),
    ///         and it denotes that a group of ghosts should all be serialized as part of this ghost.
    ///         Note: <c>LinkedEntityGroup</c> stores the root entity in the list, GhostGroup does not!
    ///     </para>
    ///     <para>
    ///         For usage, nuances, and best practices, see: https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/ghost-groups.md
    ///     </para>
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct GhostGroup : IBufferElementData
    {
        /// <summary>
        /// A child entity.
        /// </summary>
        public Entity Value;
    };
}

using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Similar to <see cref="LinkedEntityGroup"/>, this buffer can be added to the parent ghost,
    /// and denotes a group of ghost children that should all be serialized as part of this ghost.
    /// Note: LinkedEntityGroup stores the root entity in the list, GhostGroup does not!
    /// </summary>
    [GenerateAuthoringComponent]
    [InternalBufferCapacity(2)]
    public struct GhostGroup : IBufferElementData
    {
        public Entity Value;
    };
}
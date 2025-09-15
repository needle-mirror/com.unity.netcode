using System;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Temporary type, used to upgrade to new component type, to be removed before final 1.0
    /// </summary>
    [Obsolete("GhostOwnerComponent has been deprecated. Use GhostOwner instead (UnityUpgradable) -> GhostOwner", true)]
    [DontSupportPrefabOverrides]
    public struct GhostOwnerComponent : IComponentData
    {}

    /// <summary>
    /// <para>
    /// The GhostOwnerComponent is an optional component that can be added to a ghost to create a bond/relationship in
    /// between an entity and a specific client (for example, the client who spawned that entity, a bullet, the player entity).
    /// It is usually added to predicted ghost (see <see cref="PredictedGhost"/>) but can also be present on the interpolated
    /// ones.
    /// </para>
    /// <para>
    /// It is mandatory to add a <see cref="GhostOwner"/> in the following cases:
    /// </para>
    /// <para>- When a ghost is configured to be owner-predicted <see cref="GhostMode"/>, because it is necessary to distinguish in between who
    /// is predicting (the owner) and who is interpolating the ghost.
    /// </para>
    /// <para>- If you want to enable remote player prediction (see <see cref="ICommandData"/>) or, in general, to allow sending data
    /// based on ownership the <see cref="SendToOwnerType.SendToOwner"/>.
    /// </para>
    /// <para>- If you want to use the <see cref="AutoCommandTarget"/> feature.</para>
    /// </summary>
    [DontSupportPrefabOverrides]
    [GhostComponent(SendDataForChildEntity = true)]
    public struct GhostOwner : IComponentData
    {
        /// <summary>
        /// The <see cref="NetworkId"/> of the client the entity is associated with.
        /// </summary>
        [GhostField] public int NetworkId;
    }

    /// <summary>
    /// An enableable component denoting that the current world has input ownership over a ghost. E.g. a player ghost would have this enabled only on the client that owns it.
    /// This is enabled for ghosts where the <see cref="GhostOwner.NetworkId"/> matches the <see cref="NetworkId.Value"/> on the client. For <see cref="NetCodeConfig.HostWorldMode.SingleWorld"/>, this matches the connection tagged with <see cref="LocalConnection"/>. For binary world's server, this is undefined.
    /// This shouldn't be used inside the prediction group. For differentiating ghosts inside the prediction group, use the <see cref="GhostComponentAttribute"/> to strip your commands and inputs to only be on predicted ghosts.
    /// </summary>
    public struct GhostOwnerIsLocal : IComponentData, IEnableableComponent
    {}
}

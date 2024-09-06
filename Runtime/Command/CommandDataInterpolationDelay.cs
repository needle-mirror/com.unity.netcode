using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>Optional component used to access the interpolation delay in order to implement lag compensation on the server.
    /// Also exists on predicted clients (but the interpolation delay will always be 0).</para>
    /// <para>The component is not baked during conversion by default, and should be added explicitly by the user
    /// at one of two points:</para>
    /// <para> 1. At conversion time: By using the checkbox in `GhostAuthoringComponent` or your own Baker.</para>
    /// <para> 2. At runtime: After the entity is spawned.</para>
    /// </summary>
    /// <remarks>
    /// When the component is present on a ghost, the <see cref="CommandReceiveSystem{TCommandDataSerializer,TCommandData}.ReceiveJobData"/>
    /// will automatically update the <see cref="Delay"/> value with the latest reported interpolation delay from this connection
    /// (i.e. the connection that is sending the command for this entity).
    /// As such, the component is updated only for entities that are predicted, and that have at least one input command buffer.
    /// </remarks>
    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    public struct CommandDataInterpolationDelay : IComponentData
    {
        /// <summary>
        /// The latest reported interpolation delay (in ticks) reported for this entity.
        /// The delay value is updated every time the target entity receives a command from the client,
        /// as this interpolation delay is included in the command header.
        /// If the client switches its <see cref="CommandTarget"/> (for example: by entering a vehicle),
        /// by either changing the <see cref="CommandTarget"/> or by enabling another <see cref="AutoCommandTarget"/>,
        /// the value of the delay becomes stale. In other words: It is never reset to 0, and will remain the same as
        /// reported by the last received command.
        /// </summary>
        public uint Delay;
    }
}

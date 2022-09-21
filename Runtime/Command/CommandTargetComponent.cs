using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Component added to all <see cref="NetworkStreamConnection"/>, stores a reference to the entity
    /// where commands should be read from (client) or written to (server).
    /// It is mandatory to set a valid reference to the <see cref="targetEntity"/> in order to receive client
    /// commands if:
    /// <para>- you are not using the <see cref="AutoCommandTarget"/>.</para>
    /// <para>- you want to supoort thin-clients (because <see cref="AutoCommandTarget"/> does not work in that case)
    /// The use of <see cref="AutoCommandTarget"/> and CommandTargetComponent is complementary and they can used
    /// at the sam time.</para>
    /// </summary>
    /// <remarks>
    /// The target entity must have at least one `ICommandData` component on it.
    /// </remarks>
    public struct CommandTargetComponent : IComponentData
    {
        /// <inheritdoc cref="CommandTargetComponent"/>
        public Entity targetEntity;
    }
}

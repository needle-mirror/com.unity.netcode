using System;

namespace Unity.NetCode
{
    /// <summary>
    /// Describes the direction a remote is sent.
    /// </summary>
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#else
    internal
#endif // NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    enum Directionality
    {
        /// <summary>
        /// Undefined Directionality this should not be specified by users, its a guard so we can have a default and check users have specifed thier directionality explicitly.
        /// </summary>
        Undefined,
        /// <summary>
        /// Used to send a Remote from a Client instace to a Server instace, if sent in the other direction the remote will fail
        /// </summary>
        ClientToServer,
        /// <summary>
        /// Used to send a Remote from a Server instace to a Client instace, if sent in the other direction the remote will fail
        /// </summary>
        ServerToClient,
        // TODO: Revist this and see if we want this to be an option, How useful is it really?
        // BiDirectional,
    }

    /// <summary>
    /// Add to a struct, static or ghost behaviour method to turn it into an RPC
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Method)]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#else
    internal
#endif // NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    class RemoteAttribute : Attribute
    {
        private Directionality direction;

        /// <summary>
        /// <inheritdoc cref="RemoteAttribute"/>
        /// </summary>
        /// <param name="directionality">The direction in which this remote is valid to send.</param>
        public RemoteAttribute(Directionality directionality = Directionality.Undefined)
        {
            direction = directionality;
        }
    }
}

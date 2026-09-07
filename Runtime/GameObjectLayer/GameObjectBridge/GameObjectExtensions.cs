using UnityEngine;

namespace Unity.Netcode
{
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
        static class GameObjectExtensions
    {
        /// <summary>
        /// Gets the NetcodeWorld associated with this gameObject. Can be useful to check whether a given GameObject is associated with a client/server world
        /// without having a <see cref="GhostBehaviour"/> or a <see cref="GhostObject"/>
        /// </summary>
        /// <param name="self"></param>
        /// <returns>The <see cref="NetcodeWorld"/> associated with this GameObject</returns>
        public static NetcodeWorld NetcodeWorld(this GameObject self)
        {
            // This will potentially disappear or get simplified with entities integration
            return (NetcodeWorld)GhostEntityMapping.LookupEntityReferenceGameObject(self).World.EntityManager.World;
        }
    }
}

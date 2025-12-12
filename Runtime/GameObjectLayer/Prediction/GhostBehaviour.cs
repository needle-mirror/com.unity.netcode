
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using UnityEngine;
using System;

namespace Unity.NetCode
{
    /// <summary>
    /// The base class for all Monobehaviours in need to access Ghost features.
    /// </summary>
    // TODO-next prediction, GhostVariable automatic initialization, inputs, and other such features will live here.
    // TODO-release come back to this, we might not need this
    [RequireComponent(typeof(GhostAdapter))]
    // [MultiplayerRoleRestricted]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    abstract class GhostBehaviour : MonoBehaviour
    {
        internal GhostAdapter m_Ghost;

        /// <summary>
        /// Access to the <see cref="Ghost"/> for this GhostBehaviour. Only valid after the object is fully spawned and initialized.
        /// This is the "bridge" between GameObject and entities and the main point of access for Netcode features.
        /// </summary>
        // TODO-release with this being public, should it really be named "Adapter"? Using Ghost for now. GhostAdapter feels like it should be named "Ghost" or "GhostInstance" or something like that. EntityBehaviour has "EntityProxy"?
        // GhostInstance isn't great, since there's a component named like this :(.
        // this way, from a GhostBehaviour, I would call this.Ghost.GhostId for example. "Adapter" doesn't sound "unified", it sounds like it's a "helper" class
        // and not a first class citizen. For GO users, that'll be their main point of access to Netcode features.
        public GhostAdapter Ghost
        {
            get
            {
                if (m_Ghost == null)
                {
                    m_Ghost = GetComponent<GhostAdapter>();
                }
                return m_Ghost;
            }
        }

        public virtual void Awake()
        {
            // TODO-release with entities integration, this shouldn't be needed anymore, the lifecycle would be controlled by the engine
            Ghost.InternalAcquireEntityReference();
        }

        public virtual void OnDestroy()
        {
            Ghost.InternalReleaseEntityReference();
        }

    }
}
#endif

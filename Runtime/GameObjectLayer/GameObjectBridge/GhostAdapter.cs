#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID

using System;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Assertions;

namespace Unity.NetCode
{
    /// <summary>
    /// Bridge class between your GameObject and the underlying Netcode ghost entity. This is your main access point to the underlying ghost's data.
    /// It'll be in charge of initializing your GameObject's ghost and keeping it in sync with the underlying ghost.
    /// </summary>
    /// <remarks>
    /// This uses Netcode for Entities to create a ghost entity for your GameObject. The pattern is similar to a Rigidbody associated with a GameObject. The rigidbody will have its own
    /// representation in an internal physics world and will match with its GameObject counterpart.
    /// </remarks>
    /// TODO-doc look at this again once we settle on the authoring flow.
    [DisallowMultipleComponent]
    // [MultiplayerRoleRestricted]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public // TODO-release should this actually be public to users? Couldn't it just be a hidden authoring repository of settings and that's it?
#else
        [AddComponentMenu("")] // To prevent users from adding this themselves while not in experimental mode
#endif
        sealed class GhostAdapter : BaseGhostSettings
    {
        internal bool IsPrefab()
        {
            // So far didn't find another way to know if a GameObject is a prefab or not. checking for scene.IsValid doesn't work, since IsValid could be true for the little
            // per-prefab scene you get when opening a prefab for editing
            return prefabReference == null || prefabReference.Prefab == this.gameObject;
        }

        // When Instantiating a server side GameObject, we need to create the associated server side entity from its prefab. So we get GameObject --> GO Prefab --> Entity Prefab and instantiate that entity prefab automatically. TODO-release The need for this should be gone with entities integration.
        [HideInInspector][SerializeField] internal GhostPrefabReference prefabReference;
        Entity m_CachedEntity;
        World m_CachedWorld; // We don't want to keep a WorldUnmanaged here, since they can become invalid even if their IsCreated is true (as they are value types). Managed Worlds are easier to manipulate
        bool m_WasInitialized = false;

        // Internal note: Use case: I have a spawner that itself decides whether the GO is predicted at spawn or not --> the prefab is already created on disk.
        // post process has already done a pass and registered this. As a user, I would need a "SkipAutoRegistration" checkbox on the prefab that would prevent auto creation
        /// <summary>
        /// Prefabs are automatically tracked by Netcode. However this means once a prefab is registered, its settings can't be modified anymore.
        /// In order to programmatically update those settings, you need to make sure to update them before prefab registration. This setting allows you to control
        /// when prefab registration happens. Make sure to call <see cref="Netcode.RegisterPrefab"/> yourself, in the same order both client and server side.
        /// </summary>
        [Tooltip("Prefabs are automatically tracked by Netcode. However this means once a prefab is registered, its settings can't be modified anymore. In order to programmatically update those settings, you need to make sure to update them before prefab registration. This setting allows you to control when prefab registration happens. Make sure to call Netcode.RegisterPrefab yourself, in the same order both client and server side.")]
        [SerializeField] public bool SkipAutomaticPrefabRegistration;

        internal GhostEntityMapping.EntityLink EntityLink
        {
            get
            {
                Assert.IsTrue(prefabReference != null, "Prefab reference is null, make sure your GameObject is spawned from a prefab.");
                TryInitializeCachedLink();
                return new GhostEntityMapping.EntityLink() { Entity = m_CachedEntity, World = m_CachedWorld.Unmanaged };
            }
        }

        /// <summary>
        /// The underlying generated ghost <see cref="Unity.Entities.Entity"/> associated with this GameObject. Updating the GhostAdapter's data will update the entity's data which will then be synchronized using Netcode for Entities.
        /// </summary>
        public Entity Entity => EntityLink.Entity;

        /// <summary>
        /// The world this ghost belongs to. Can be a client or server world
        /// </summary>
        public World World
        {
            get
            {
                TryInitializeCachedLink(); // can't just use EntityLink, as we need a managed world, not the unmanaged world to avoid false IsCreated issues on unmanaged WorldUnmanaged
                return m_CachedWorld;
            }
        }


        #region entity mapping
        /// <summary>
        /// Lazy initialization of the mapped entity. We don't want to acquire a reference here and bump the ref count. We want to keep a single ref count bump for the whole GhostAdapter class
        /// </summary>
        void TryInitializeCachedLink()
        {
            if (!m_WasInitialized)
            {
                InternalAcquireEntityReference();
            }
        }

        /// <summary>
        /// Should be called by anything that needs to "checkout" the entity and make sure it stays alive to access its state. Don't forget to call <see cref="InternalReleaseEntityReference"/> if you acquire. Every acquire should be matched by a release.
        /// </summary>
        internal void InternalAcquireEntityReference()
        {
            var link = GhostEntityMapping.AcquireEntityReferenceGameObject(this.gameObject.GetEntityId(), gameObject.transform.GetEntityId(), prefabEntityId: prefabReference.Prefab.GetEntityId(), autoWorld: GhostSpawningContext.Current.GetWorld());
            m_CachedWorld = link.World.EntityManager.World;
            m_CachedEntity = link.Entity;
            m_WasInitialized = true;
        }

        /// <summary>
        /// Must call this if call <see cref="InternalAcquireEntityReference"/>. Every acquire should have a release.
        /// </summary>
        internal void InternalReleaseEntityReference()
        {
            if (!m_WasInitialized)
                throw new InvalidOperationException("Sanity check failed, releasing a ghost which wasn't initialized, shouldn't be here.");
            GhostEntityMapping.ReleaseGameObjectEntityReference(this.gameObject, World != null && World.IsCreated);
        }
        #endregion

        void Awake()
        {
            TryInitializeCachedLink(); // Do this at least once here, in case it hasn't been done before by other Awake from other monobehaviours
        }

        void OnDestroy()
        {
            InternalReleaseEntityReference();

            // TODO-release have some warning if trying to destroy a client side GO
        }
    }
}

#endif

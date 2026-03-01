#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID

using System;
using Unity.Collections;
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

        void Start()
        {
            // Empty Start so that didStart gets set, useful for order of operations
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
#if UNITY_ASSERTIONS
                if (prefabReference == null) Debug.LogError("Prefab reference is null, make sure your GameObject is spawned from a prefab.", this.gameObject);
#endif
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
            bool entityWasCreated = false;

            {
                // WARNING This block should disappear with entities integration. Make sure to take this into account when adding code here
                GhostGameObjectSpawnSystem.TryGetAutomaticWorld(out var potentialWorldForSpawn); // if this is a first entity creation, this is the world we'd spawn into
                var existingLink = GhostEntityMapping.LookupEntityReferenceGameObject(this.gameObject);
                if (existingLink == default) entityWasCreated = true;
                var link = GhostEntityMapping.AcquireEntityReferenceGameObject(this.gameObject.GetEntityId(), gameObject.transform.GetEntityId(), prefabEntityId: prefabReference.Prefab.GetEntityId(), autoWorld: potentialWorldForSpawn);

                InitializeWithLink(link);
            }
            if (entityWasCreated)
            {
                // TODO-next@startOverride once we have virtual Start override removed from GhostBehaviour, we can move some of this to the GhostAdapter awake
                var em = World.EntityManager;
                var ghostInfo = em.GetComponentData<GhostGameObjectLink>(Entity);
                ghostInfo.GhostAdapterId = GetEntityId();
                em.SetComponentData(Entity, ghostInfo);

                this.InitializeRuntimeGhostBehaviours(this.EntityLink, withInitialValue: true); // withInitialValue=true since this is called server side, so there's no state already set in existing ECS components
            }
        }

        private void InitializeWithLink(GhostEntityMapping.EntityLink link)
        {
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

        /// <summary>
        /// The <see cref="NetworkId"/> of the owner of this ghost. Only valid if <see cref="HasOwner"/> is true.
        /// </summary>
        // TODO-release@potentialUX this flow can be improved.
        // Current flow errors out when HasOwner hasn't been setup on the ghost prefab. We could simply force a GhostOwner component on all GhostObject ghosts
        // and we just enable/disable it. We'd need to check the perf impact of this.
        public NetworkId OwnerNetworkId
        {
            get
            {
                if (!HasOwner)
                {
                    Debug.LogError($"Trying to get the owner of a ghost that wasn't setup with ownership. Please update your {nameof(GhostAdapter)} component to reflect this.");
                    return NetworkId.Invalid;
                }

                BurstedComponentAccess.StaticGetOwnerNetworkIdBursted(World.EntityManager, Entity, out var ownerNetworkId);
                return new NetworkId { Value = ownerNetworkId };
            }
            set
            {
                if (!HasOwner)
                {
                    Debug.LogError($"Trying to set the owner of a ghost that wasn't setup with ownership. Please update your {nameof(GhostAdapter)} component to reflect this.");
                    return;
                }

                BurstedComponentAccess.StaticSetOwnerNetworkIdBursted(World.EntityManager, Entity, value);
            }
        }

        /// <summary>
        /// Whether this ghost is being predicted. See <see cref="PredictedGhost"/>.
        /// </summary>
        public bool IsPredictedGhost => World.EntityManager.HasComponent<PredictedGhost>(Entity);

        internal NetworkTime NetworkTime
        {
            get
            {
                // TODO-next@NetcodeWorld with connection and NetcodeWorld refactor: cache this
                using var query = this.World.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<NetworkTime>());
                return query.GetSingleton<NetworkTime>();
            }
        }

        public bool IsServer => World.IsServer();
        public bool IsClient => World.IsClient();


        void Awake()
        {
            TryInitializeCachedLink(); // Do this at least once here, in case it hasn't been done before by other Awake from other monobehaviours
        }

        void OnDestroy()
        {
            InternalReleaseEntityReference();

            // TODO-release have some warning if trying to destroy a client side GO
        }

        [SerializeField][HideInInspector] internal GhostBehaviour[] m_AllBehaviours;
        internal void InitializePrefabGhostBehaviours(GhostEntityMapping.EntityLink link)
        {
            // initializing here so that we don't recursively do initialization if the below calls needs that link
            InitializeWithLink(link);

            // Initializing GhostBehaviour information
            m_AllBehaviours = GetComponentsInChildren<GhostBehaviour>();
            var tracker = new GhostBehaviour.GhostBehaviourTracking();
            tracker.allBehaviourTypeInfo = new NativeArray<GhostBehaviourTypeInfo>(m_AllBehaviours.Length, Allocator.Domain); // TODO-next@prefabRegistration once we release unused prefabs, switch this back to Persistent allocator and release this allocation
            for (int i = 0; i < m_AllBehaviours.Length; i++)
            {
                var ghostBehaviourTypeInfo = Netcode.Instance.GhostBehaviourTypeManager.GhostBehaviourInfos[m_AllBehaviours[i].GetType()];
                tracker.allBehaviourTypeInfo[i] = ghostBehaviourTypeInfo;
                tracker.AnyHasUpdate |= ghostBehaviourTypeInfo.AnyHasUpdate();
            }
            link.World.EntityManager.AddComponentData(link.Entity, tracker);
            if (Debug.isDebugBuild)
            {
                // we can't support the "wheels on a car case" where a "car" GameObject contains 4x "wheel" monobehaviours. ECS side, the way for users
                // to do this is to have 4 different types, "struct Wheel1", "struct Wheel2", "struct Wheel3", "struct Wheel4", since ECS only allows one type per entity.
                // Entities just implicitly reuse the same component when trying to add it multiple times.
                // This is a restriction for GhostField only though. For GhostBridge, this is fine. I could have two different GhostBehaviours,
                // each declaring a bridge, but with the same type and so it'd reuse the same component underneath, linking them together.
                // As soon as you're using ECS components, you're bound to the new way of working, where AddComponent reuses the component if it's already added by
                // another ECS system. So this feels like a "quirk" (even a feature) of bridge?
                // It's different for per field GhostField. A field is "owned" by the containing monobehaviour. It shouldn't share its value with other instances of the
                // same monobehaviour. And unfortunately, DisallowMultipleComponent isn't inherited by child classes, adding this to GhostBehaviour is useless
                for (int i = 0; i < m_AllBehaviours.Length - 1; i++)
                {
                    for (int j = i + 1; j < m_AllBehaviours.Length; j++)
                    {
                        if (i == j) continue;

                        if (m_AllBehaviours[i].GetType().IsAssignableFrom(m_AllBehaviours[j].GetType()) ||
                            m_AllBehaviours[j].GetType().IsAssignableFrom(m_AllBehaviours[i].GetType()))
                        {
                            string message = $"Having two GhostBehaviours with a shared sets of GhostField (GhostBehaviours of the same type or inherit from one another) registered for the same ghost is undefined behaviour. {m_AllBehaviours[i].GetType()} and {m_AllBehaviours[j].GetType()} were found on ghost prefab {gameObject.name}. This is undefined behaviour.";
                            Debug.LogError(message, this);
                        }
                    }
                }
            }

            // registers the ECS components with the ghost type, so that prefab registration works and knows which serializers to setup.
            foreach (var ghostBehaviour in m_AllBehaviours)
            {
                ghostBehaviour.InitializePrefabWithEntityComponents();
            }
        }

        internal void InitializeRuntimeGhostBehaviours(GhostEntityMapping.EntityLink link, bool withInitialValue)
        {
            // initializing the ghost link here so that we don't recursively do initialization if the below calls needs that link
            InitializeWithLink(link);

            // only initializing on runtime entity, it's useless to do that work for the prefab instances for now and we don't want to have to clear this buffer (to remove the prefab versions of those monobehaviours)

            var behaviourTrackingBuffer = link.World.EntityManager.GetComponentData<GhostBehaviour.GhostBehaviourTracking>(link.Entity);
            link.World.EntityManager.SetComponentData(link.Entity, behaviourTrackingBuffer);
            // At runtime, we need to initialize which entity and world to get the supporting components from.
            foreach (var ghostBehaviour in m_AllBehaviours)
            {
                ghostBehaviour.InitializeRuntime(withInitialValue);
            }
        }
    }
}

#endif

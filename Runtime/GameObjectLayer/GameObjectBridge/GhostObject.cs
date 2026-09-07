using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Netcode.NetcodeTime;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Assertions;
using Debug = UnityEngine.Debug;

namespace Unity.Netcode
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
    [DebuggerDisplay("{GetDebugName(this)}")]
    [DefaultExecutionOrder(ExecutionOrder)]
    // [MultiplayerRoleRestricted]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    [AddComponentMenu("Multiplayer/Ghost Object", 50)]
    public // TODO-release should this actually be public to users? Couldn't it just be a hidden authoring repository of settings and that's it? It could also probably merged with GhostAuthoringComponent, just like Rigidbody can be used for baking
#else
        [AddComponentMenu("", 50)] // To prevent users from adding this themselves while not in experimental mode
#endif
        sealed class GhostObject : BaseGhostSettings
    {
        internal const int ExecutionOrder = -Int32.MaxValue;

        // When Instantiating a server side GameObject, we need to create the associated server side entity from its prefab. So we get GameObject --> GO Prefab --> Entity Prefab and instantiate that entity prefab automatically. TODO-release The need for this should be gone with entities integration.
        [HideInInspector][SerializeField] internal GhostPrefabReference prefabReference;
        internal Entity m_CachedEntity;
        internal NetcodeWorld m_CachedWorld; // We don't want to keep a WorldUnmanaged here, since they can become invalid even if their IsCreated is true (as they are value types). Managed Worlds are easier to manipulate
        internal bool WasInitialized = false;

        // Internal note: Use case: I have a spawner that itself decides whether the GO is predicted at spawn or not --> the prefab is already created on disk.
        // post process has already done a pass and registered this. As a user, I would need a "SkipAutoRegistration" checkbox on the prefab that would prevent auto creation
        /// <summary>
        /// Prefabs are automatically tracked by Netcode. However this means once a prefab is registered, its settings can't be modified anymore.
        /// In order to programmatically update those settings, you need to make sure to update them before prefab registration. This setting allows you to control
        /// when prefab registration happens. Make sure to call <see cref="Netcode.RegisterPrefab"/> yourself, in the same order both client and server side.
        /// </summary>
        [Tooltip("Prefabs are automatically tracked by Netcode. However this means once a prefab is registered, its settings can't be modified anymore. In order to programmatically update those settings, you need to make sure to update them before prefab registration. This setting allows you to control when prefab registration happens. Make sure to call Netcode.RegisterPrefab yourself, in the same order both client and server side.")]
        [SerializeField] public bool SkipAutomaticPrefabRegistration;

        /// By default, GhostObject ghosts support (and replicate) non-uniform (per-axis) 3D scale, stored in a
        /// <see cref="PostTransformMatrix"/> component on the ghost entity. Enable this to treat the scale as uniform
        /// instead: no <see cref="PostTransformMatrix"/> is added, and the scale is driven by
        /// <see cref="Unity.Transforms.LocalTransform.Scale"/> (taken from <c>transform.localScale.x</c>; y and z are ignored).
        /// Saves the PostTransformMatrix chunk memory and its per-snapshot change bit on ghosts that never scale non-uniformly.
        /// </summary>
        [Tooltip("By default, GhostObject ghosts support (and replicate) non-uniform (per-axis) 3D scale, stored in a PostTransformMatrix component on the ghost entity.\n\nEnable this to treat the scale as uniform instead: no PostTransformMatrix is added, and the scale is driven by LocalTransform.Scale (taken from transform.localScale.x; y and z are ignored).\n\nSaves the PostTransformMatrix chunk memory and its per-snapshot change bit on ghosts that never scale non-uniformly.")]
        [SerializeField] public bool UseUniformScale;

        /// <summary>
        /// The underlying generated ghost <see cref="Unity.Entities.Entity"/> associated with this GameObject. Updating the GhostObject's data will update the entity's data which will then be synchronized using Netcode for Entities.
        /// </summary>
        public Entity Entity
        {
            get
            {
#if UNITY_ASSERTIONS
                if (!WasInitialized && prefabReference == null) Debug.LogError("Prefab reference is null, make sure your GameObject is spawned from a prefab.", this.gameObject);
#endif
                TryInitializeCachedLink();
                return m_CachedEntity;
            }
        }

        /// <summary>
        /// The world this ghost belongs to. Can be a client or server world
        /// </summary>
        public NetcodeWorld World
        {
            get
            {
                TryInitializeCachedLink(); // can't just use EntityLink, as we need a managed world, not the unmanaged world to avoid false IsCreated issues on unmanaged WorldUnmanaged
                return m_CachedWorld;
            }
        }

        #region initialization
        /// <summary>
        /// Authoritative or prediction initialization
        /// Put any authority specific logic needed for initialization here.
        /// </summary>
        private void CanWriteStateRuntimeInitialize(GhostEntityMapping.EntityLink link)
        {
            RuntimeInitializeCommon();

            // withInitialValue=true since this is called server side, so there's no state already set in existing ECS components
            InitializeRuntimeGhostBehaviours(link, withInitialValue: true);
        }

        /// <summary>
        /// Non-authoritative initialization
        /// Put any client specific logic needed for initialization here.
        /// </summary>
        internal void ClientRuntimeInitialize(GhostEntityMapping.EntityLink link)
        {
            InitializeWithLink(link);
            RuntimeInitializeCommon();

            // withInitialValue=false since this is a spawn from the network, we already have values in ECS components.
            InitializeRuntimeGhostBehaviours(link, withInitialValue: false);
        }

        private void RuntimeInitializeCommon()
        {
            var manager = World.EntityManager;
            var ghostInfo = manager.GetComponentData<GhostGameObjectLink>(Entity);
            ghostInfo.GhostObjectId = GetEntityId();
            manager.SetComponentData(Entity, ghostInfo);
        }
        #endregion

        #region entity mapping
        /// <summary>
        /// Lazy initialization of the mapped entity. We don't want to acquire a reference here and bump the ref count. We want to keep a single ref count bump for the whole GhostObject class
        /// </summary>
        void TryInitializeCachedLink()
        {
            if (!WasInitialized)
            {
                InternalAcquireEntityReference();
            }
        }

        /// <summary>
        /// Should be called by anything that needs to "checkout" the entity and make sure it stays alive to access its state. Don't forget to call <see cref="InternalReleaseEntityReference"/> if you acquire. Every acquire should be matched by a release.
        /// </summary>
        internal void InternalAcquireEntityReference()
        {
            bool creatingEntity = false;

            GhostEntityMapping.EntityLink link;
            {
                // WARNING This block should disappear with entities integration. Make sure to take this into account when adding code here
                var existingLink = GhostEntityMapping.LookupEntityReferenceGameObject(this.gameObject);
                WorldUnmanaged potentialWorldForSpawn = default;
                if (existingLink == default)
                {
                    creatingEntity = true;
                    // TODO@EntitiesIntegration should keep this validation
                    GhostGameObjectSpawnSystem.TryGetAndValidateWorldForSpawn(out potentialWorldForSpawn); // if this is a first entity creation, this is the world we'd spawn into
                }

                link = GhostEntityMapping.AcquireEntityReferenceGameObject(this.gameObject.GetEntityId(), gameObject.transform.GetEntityId(), prefabEntityId: prefabReference.Prefab.GetEntityId(), forWorld: potentialWorldForSpawn);
                var transform = this.transform;
                // Need to set transform data now, since there's systems that'll look at transform positions and they should have the non-default values now
                if (creatingEntity)
                {
                    var em = link.World.EntityManager;
                    // 3D (non-uniform) scale lives in the PostTransformMatrix when present, and LocalTransform.Scale
                    // must then stay 1 (consumers combine both, storing the scale twice would double-apply it).
                    // Without a PostTransformMatrix, fall back to uniform scaling.
                    var hasPostTransformMatrix = em.HasComponent<PostTransformMatrix>(link.Entity);
                    if (hasPostTransformMatrix)
                    {
                        // The GO layer owns the PostTransformMatrix and always writes it as a pure scale matrix
                        // (see TransformUpdateGameObjectToEntityJob).
                        em.SetComponentData(link.Entity, new PostTransformMatrix
                        {
                            Value = Mathematics.float4x4.Scale(transform.localScale)
                        });
                    }
                    em.SetComponentData(link.Entity, new LocalTransform()
                    {
                        Position = transform.localPosition,
                        Rotation = transform.localRotation,
                        Scale = hasPostTransformMatrix ? 1f : transform.localScale.x
                    });
                }

                InitializeWithLink(link);
            }
            if (creatingEntity)
            {
                // TODO-next@startOverride once we have virtual Start override removed from GhostBehaviour, we can move some of this to the GhostObject awake
                var em = link.World.EntityManager;
                if (em.HasComponent<PendingClientGameObjectSpawn>(link.Entity))
                    em.RemoveComponent<PendingClientGameObjectSpawn>(link.Entity); // Since this is an authoritative (or predicted) spawn, the client spawn system shouldn't touch this, we're already initialized here

                // creatingEntity should only ever be true server side or when predicting. Finish initializing the ghostObject for clients who can write state.
                CanWriteStateRuntimeInitialize(link);
            }
        }

        private void InitializeWithLink(GhostEntityMapping.EntityLink link)
        {
            m_CachedWorld = (NetcodeWorld)link.World.EntityManager.World;
            m_CachedEntity = link.Entity;
            WasInitialized = true;
        }

        /// <summary>
        /// Must call this if call <see cref="InternalAcquireEntityReference"/>. Every acquire should have a release.
        /// </summary>
        internal void InternalReleaseEntityReference()
        {
            if (!WasInitialized)
                throw new InvalidOperationException("Sanity check failed, releasing a ghost which wasn't initialized, shouldn't be here.");

            GhostEntityMapping.ReleaseGameObjectEntityReference(this.gameObject, World != null && World.IsCreated);
        }
        #endregion

        /// <summary>
        /// The current non-smoothed, tick-accurate transform attached to this ghost.
        /// </summary>
        // NonSerialized: runtime handle to the live transform; there's no authored initial value to serialize, and
        // GhostComponentRef<LocalTransform> would otherwise trip the UAC1001 serialization analyzer (LocalTransform isn't [Serializable]).
        [NonSerialized] public GhostComponentRef<LocalTransform> GhostTransform; // TODO-release@rename should potentially rename this to "TickTransform"? Should be related to how we name PredictedUpdate

        /// <summary>
        /// The current non-smoothed, tick-accurate position (in local space) of the ghost.
        /// </summary>
        public Vector3 Position
        {
            get => GhostTransform.Value.Position;
            set => GhostTransform.ValueAsRef.Position = value;
        }

        /// <summary>
        /// Returns both the position and rotation of the <see cref="LocalTransform"/>.
        /// </summary>
        /// <remarks>
        /// Currently this is being used for debugging purposes within the NGO SDK.
        /// </remarks>
        internal (Vector3 Position, Quaternion Rotation) GetPositionAndRotation()
        {
            var ecsTransform = GhostTransform.Value;
            return (ecsTransform.Position, ecsTransform.Rotation);
        }

        /// <summary>
        /// The current non-smoothed position (in local space) of the ghost. The ghost and gameobject transform may not be necessarily
        /// in sync (i.e when switching prediction mode, when smoothing host ghosts or with prediction error smoothing).
        /// </summary>
        public Quaternion Rotation
        {
            get => GhostTransform.Value.Rotation;
            set => GhostTransform.ValueAsRef.Rotation = value;
        }

        /// <summary>
        /// Takes the tick-accurate transform and applies it to the <see cref="GameObject.transform"/>
        /// </summary>
        // Internal note: Pattern similar to <see cref="Rigidbody.PublishTransform"/>
        public void PublishTransform()
        {
            var t = GhostTransform.Value;
            gameObject.transform.SetLocalPositionAndRotation(t.Position, t.Rotation);
            // The uniform LocalTransform.Scale is combined with the optional non-uniform 3D scale stored in the
            // PostTransformMatrix (if present). SignedScaleFromScaleOnlyMatrix so negative (mirrored) scale survives.
            var scale = t.Scale * Vector3.one;
            var entityManager = World.EntityManager;
            if (entityManager.HasComponent<PostTransformMatrix>(Entity))
                scale = t.Scale * (Vector3)MatrixScaleHelper.SignedScaleFromScaleOnlyMatrix(entityManager.GetComponentData<PostTransformMatrix>(Entity).Value);
            gameObject.transform.localScale = scale;
        }

        /// <summary>
        /// The <see cref="NetworkId"/> of the owner of this ghost. Only valid if <see cref="BaseGhostSettings.HasOwner"/> is true.
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
                    Debug.LogError($"Trying to get the owner of a ghost that wasn't setup with ownership. Please update your {nameof(GhostObject)} component to reflect this.");
                    return NetworkId.Invalid;
                }

                BurstedComponentAccess.StaticGetOwnerNetworkIdBursted(World.EntityManager, Entity, out var ownerNetworkId);
                return new NetworkId { Value = ownerNetworkId };
            }
            set
            {
                if (!HasOwner)
                {
                    Debug.LogError($"Trying to set the owner of a ghost that wasn't setup with ownership. Please update your {nameof(GhostObject)} component to reflect this.");
                    return;
                }

                BurstedComponentAccess.StaticSetOwnerNetworkIdBursted(World.EntityManager, Entity, value);
            }
        }

        /// <summary>
        /// Various metadata associated with this ghost. <see cref="GhostInstance"/>
        /// </summary>
        public GhostInstance GhostInfo
        {
            get
            {
                BurstedComponentAccess.StaticGetGhostInstanceBursted(World.EntityManager, Entity, out var ghost);
                return ghost;
            }
        }

        /// <summary>
        /// The networked ID for this ghost, shared across the network. <see cref="GhostInstance.ghostId"/>
        /// If you plan to use other fields like <see cref="SpawnTick"/> or <see cref="GhostType"/> consider using <see cref="GhostInfo"/> instead for better batched performance
        /// </summary>
        public int GhostId => GhostInfo.ghostId;
        /// <summary>
        /// The tick at which this ghost was spawn. <see cref="GhostInstance.spawnTick"/>
        /// If you plan to use other fields like <see cref="GhostId"/> or <see cref="GhostType"/> consider using <see cref="GhostInfo"/> instead for better batched performance
        /// </summary>
        public NetworkTick SpawnTick => GhostInfo.spawnTick;
        /// <summary>
        /// The ghost type (which prefab) was used to spawn this ghost. <see cref="GhostInstance.ghostType"/>
        /// If you plan to use other fields like <see cref="SpawnTick"/> or <see cref="GhostId"/> consider using <see cref="GhostInfo"/> instead for better batched performance
        /// </summary>
        public int GhostType => GhostInfo.ghostType;

        /// <summary>
        /// Whether this ghost is being predicted. See <see cref="PredictedGhost"/>.
        /// </summary>
        public bool IsPredictedGhost => World.EntityManager.HasComponent<PredictedGhost>(Entity);

        internal NetworkTime NetworkTime => this.World.NetworkTime;

        public bool IsServer => World.IsServer();

        public bool IsClient => World.IsClient();

        private bool m_IsPrefab;
        void Awake()
        {
            m_IsPrefab = IsPrefab();
            if (!m_IsPrefab)
            {
                TryInitializeCachedLink(); // Do this at least once here, in case it hasn't been done before by other Awake from other monobehaviours
            }
        }

        void Start()
        {
            // Empty Start so that didStart gets set, useful for order of operations
        }

        /// <summary>
        /// Marks whether this gameObject is being destroyed
        /// </summary>
        internal bool IsDestroying { get; private set; }

        void OnDestroy()
        {
            if (!m_IsPrefab)
            {
                InternalReleaseEntityReference();
            }
            IsDestroying = true;

            // TODO-release have some warning if trying to destroy a client side GO
        }

        #region prefab
        internal bool IsPrefab()
        {
            // So far didn't find another way to know if a GameObject is a prefab or not. checking for scene.IsValid doesn't work, since IsValid could be true for the little
            // per-prefab scene you get when opening a prefab for editing
            return prefabReference == null || prefabReference.Prefab == this.gameObject;
        }

        /// <summary>
        /// Used by the test frameworks (N4E & NGO) to initialize runtime created GameObjects as prefab assets.
        /// Calls the function that is normally run by the <see cref="GhostPrefabPostProcessor"/>.
        /// </summary>
        /// <remarks>
        /// This shouldn't be called outside of internal test code.
        /// </remarks>
        internal void InitializeAsPrefab()
        {
            GhostPrefabReference.CreateForPrefab(this);
        }
        #endregion

        [SerializeField][HideInInspector] internal GhostBehaviour[] m_AllBehaviours;
        internal void InitializePrefabGhostBehaviours(GhostEntityMapping.EntityLink link)
        {
            // initializing here so that we don't recursively do initialization if the below calls needs that link
            InitializeWithLink(link);

            // Initializing GhostBehaviour information
            m_AllBehaviours = GetComponentsInChildren<GhostBehaviour>(true);
            var tracker = new GhostBehaviour.GhostBehaviourTracking();
            tracker.allBehaviourTypeInfo = new NativeArray<GhostBehaviourTypeInfo>(m_AllBehaviours.Length, Allocator.Domain); // TODO-next@prefabRegistration once we release unused prefabs, switch this back to Persistent allocator and release this allocation
            for (int i = 0; i < m_AllBehaviours.Length; i++)
            {
                if (Netcode.Instance.GhostBehaviourTypeManager.GhostBehaviourInfos.TryGetValue(m_AllBehaviours[i].GetType(), out GhostBehaviourTypeInfo ghostBehaviourTypeInfo))
                {
                    tracker.allBehaviourTypeInfo[i] = ghostBehaviourTypeInfo;
                    tracker.AnyHasUpdate |= ghostBehaviourTypeInfo.AnyHasUpdate();
                }
                else
                {
                    Debug.LogError($"[{nameof(GhostObject)}][{nameof(InitializePrefabGhostBehaviours)}] {m_AllBehaviours[i].name} of type {m_AllBehaviours[i].GetType()} was not found in GhostBehaviourTypeManager.GhostBehaviourInfos! Skipping entry...");
                }
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
                ghostBehaviour.InitializePrefabWithEntityComponents(link, this);
            }
        }

        internal void InitializeRuntimeGhostBehaviours(GhostEntityMapping.EntityLink link, bool withInitialValue)
        {
            // initializing the ghost link here so that we don't recursively do initialization if the below calls needs that link
            Assert.IsTrue(WasInitialized, $"Sanity check failed. {nameof(InitializeRuntimeGhostBehaviours)} should not be used before the link has been initialized!");

            var entity = link.Entity;
            GhostTransform.Initialize(this.World, entity, withInitialValue: false); // LocalTransform is initialized elsewhere

            // only initializing on runtime entity, it's useless to do that work for the prefab instances for now and we don't want to have to clear this buffer (to remove the prefab versions of those monobehaviours)

            var behaviourTrackingBuffer = link.World.EntityManager.GetComponentData<GhostBehaviour.GhostBehaviourTracking>(entity);
            link.World.EntityManager.SetComponentData(entity, behaviourTrackingBuffer);
            // At runtime, we need to initialize which entity and world to get the supporting components from.
            foreach (var ghostBehaviour in m_AllBehaviours)
            {
                ghostBehaviour.InitializeRuntime(withInitialValue);
            }
        }

        internal FixedList128Bytes<ComponentType> GetDefaultAttachedComponents()
        {
            // These are the components Netcode automatically attaches to this GhostObject's entity (see PrefabRegistry).
            // They must be listed here so that the GhostAuthoringInspectionComponent can surface them in the UI and, more
            // importantly, so that per-prefab variant overrides the user sets on them (e.g. DontSerializeVariant on
            // PostTransformMatrix to disable 3D scale replication) are actually picked up and applied during registration.
            var components = new FixedList128Bytes<ComponentType>
            {
                ComponentType.ReadWrite<LocalTransform>(),
            };
            if (!UseUniformScale)
                components.Add(ComponentType.ReadWrite<PostTransformMatrix>());
            return components;
        }

        /// <summary>
        /// All in ReadWrite mode
        /// </summary>
        /// <returns></returns>
        internal IEnumerable<ComponentType> GetComponentTypes()
        {
            foreach (var ghostBehaviour in m_AllBehaviours)
            {
                foreach (var comp in ghostBehaviour.GetComponentTypesInternal())
                {
                    yield return comp;
                }
            }
        }

        public static string GetDebugName(GhostObject self)
        {
            if (self.WasInitialized)
                return $"Ghost {self.Entity} world {self.World}";
            return "not initialized";
        }
    }
}

using Unity.Entities;
using UnityEngine;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Assertions;

// Since Netcode is in charge of spawning ghosts for users client side, we need to know what to spawn for them. It's already handled entities side, but for GameObjects, we need to know which GameObject prefab to spawn.
// The classes here are in charge of that.
// A lot of the work here will change with entities integration.
// Right now, we're dynamically generating prefab entities at runtime and registering them to N4E's ghost collection, as if they were (already supported) runtime created ghost types (which they sort of are).
namespace Unity.NetCode
{
    /// <summary>
    /// Resides on the entity associated with a GameObject and links back to the GameObject
    /// </summary>
    internal struct GhostGameObjectLink : ICleanupComponentData
    {
        public EntityId AssociatedGameObject;
        public EntityId AssociatedTransform;
        public EntityId GhostObjectId;

        public GhostGameObjectLink(EntityId go, EntityId associatedTransform)
        {
            this.AssociatedGameObject = go;
            this.AssociatedTransform = associatedTransform;
            this.GhostObjectId = default;
        }
    }

    internal interface IPrefabOverrideProvider
    {
        // This is an interface since we can't have circular dependencies between the authoring and the netcode assemblies. This works entities side since authoring is in charge of baking asynchronously. But since GhostObject requires runtime baking, this lives in the runtime assembly
        NativeParallelHashMap<GhostPrefabCreation.Component, GhostPrefabCreation.ComponentOverride> GetPrefabOverrides();
    }

    /// <summary>
    /// The GameObject-layer (GhostObject) per-prefab default variants, where they differ from the global defaults.
    /// Applied as per-prefab overrides during prefab registration (see <see cref="PrefabsRegistry"/>) and mirrored by
    /// the inspection UI (EntityPrefabComponentsPreview) so the default it displays matches the runtime behaviour.
    /// An explicit user variant override (GhostAuthoringInspectionComponent) always takes precedence over these.
    /// </summary>
    internal static class GhostObjectVariantDefaults
    {
        /// <summary>Default variant override for GhostObject for a root component, applied as a per-prefab override at registration
        /// time (hence "OverrideHash", it is not written to the DefaultVariants map). 0 when the GO layer uses the
        /// global default.</summary>
        // Internal note: because this is applied as a per-prefab override (rank: user-specified hash), it takes precedence over
        // any user DefaultVariantSystemBase rule for PostTransformMatrix, meaning there's currently no project-wide way
        // to disable 3D scale sync for GhostObjects (only the per-prefab inspection override can). If users ask for it,
        // skip the injection in PrefabsRegistry.RegisterPrefab when a PostTransformMatrix rule exists in the
        // DefaultVariants map that was NOT registered by TransformDefaultVariantSystem (provenance is available via
        // GhostVariantRules.DefaultVariantsManaged.LastSystem). All internal, so this can change without breaking API.
        internal static ulong GetDefaultVariantOverrideHash(Type componentManagedType)
        {
            // GhostObject ghosts replicate their 3D (non-uniform) scale by default. The global default for
            // PostTransformMatrix is DontSerializeVariant so entities-only ghosts pay no bandwidth without opting in.
            if (componentManagedType == typeof(PostTransformMatrix))
                return GhostVariantsUtility.UncheckedVariantHashNBC(typeof(PostTransformMatrix3DScaleVariant), ComponentType.ReadWrite<PostTransformMatrix>());
            return 0;
        }
    }

    // TMP while waiting for entities integration. Storing the prefab registry dictionary inside the world. Makes it easier to manage for single host world vs binary world
    // Note some of this is going to get refactored in an upcoming PR with auto prefab registration
    [CreateAfter(typeof(DefaultVariantSystemGroup))]
    [CreateBefore(typeof(GhostCollectionSystem))] // So that prefab stripping applies right after we auto register prefabs here
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class ApplyOfflineCacheOnInitializationSystem : SystemBase
    {
        protected override void OnCreate()
        {
            var allPrefabs = Resources.FindObjectsOfTypeAll<GhostPrefabReference>();
            List<GameObject> toRegister = new();

            foreach (var prefabReference in allPrefabs)
            {
                if (!prefabReference.SkipAutomaticPrefabRegistration)
                    toRegister.Add(prefabReference.Prefab);
            }
            PrefabsRegistry.RegisterPrefabBatch(toRegister, this.World);

            Netcode.Instance.m_OfflineCache.InitializeWorld((NetcodeWorld)World);

            Enabled = false;
        }

        protected override void OnUpdate() { }
    }

    //The prefab registry is just a resource manager, that live and exist across world.
    internal class PrefabsRegistry
    {
        /// <summary>
        /// <see cref="RegisterPrefab"/> but in a batch, this way we call stripping in a batch too.
        /// </summary>
        /// <param name="prefabs"></param>
        /// <param name="forWorld"></param>
        internal static void RegisterPrefabBatch(List<GameObject> prefabs, World forWorld)
        {
            foreach (var prefab in prefabs)
                RegisterPrefab(prefab, forWorld, autoStrip: false);
            var stripQuery = GhostCollectionSystem.GetRuntimeStripQuery(forWorld.EntityManager);
            GhostCollectionSystem.RuntimeStripPrefabs(forWorld.EntityManager, NetDebugSystem.GetDefaultNetDebug(), stripQuery); // make sure all prefabs are stripped as soon as possible, so that
            // GameObjects can use them as soon as possible without waiting for a frame (normally called from GhostCollectionSystem.
        }

        /// <summary>
        /// Creates the entity prefab for a given GameObject based prefab. A prefab must be registered before it can
        /// be spawned (or pre-spawned), it can be done right before the prefab is instantiates, as long as it's happens on both server
        /// and client.
        /// </summary>
        /// <remarks>
        /// This creates the prefab entity in the given world. The registration happens later in ECS systems.
        /// </remarks>
        /// <param name="prefab">GameObject prefab to register</param>
        /// <param name="forWorld">World to link the registration with</param>
        /// <param name="autoStrip">Keep this as true if calling this method alone. Set to false if you plan to do a batch of prefab creation and want to strip all of them in a single batch afterward. <see cref="GhostCollectionSystem.RuntimeStripPrefabs"/></param>
        internal static void RegisterPrefab(GameObject prefab, World forWorld, bool autoStrip = true)
        {
            // already initialized for this world
            if (prefab.EntityExt(isPrefab: true, forWorld.Unmanaged) != default)
                return;

            var ghostObject = prefab.GetComponent<GhostObject>();
            var prefabLink = GhostEntityMapping.AcquireEntityReferencePrefab(prefab.GetEntityId(), prefab.transform.GetEntityId(), forWorld: forWorld.Unmanaged);
            var prefabEntity = prefabLink.Entity;
            ghostObject.InitializePrefabGhostBehaviours(prefabLink);

            // TODO-release handle child GOs that could have networked data as well
            var transforms = prefab.GetComponentsInChildren<Transform>(includeInactive: true);
            var entityManager = forWorld.EntityManager;
            entityManager.SetName(prefabEntity, prefab.name);
            var goEntityId = prefab.gameObject.GetEntityId();
            entityManager.AddComponentData(prefabEntity, EntityGuidFromGameObject(prefab));

            FixedList128Bytes<ComponentType> componentsWithDefaultValuesToAdd = new(); // TODO should replace the Add by Set and just add all components in one batch. micro optim, should wait to see a real perf problem with this

            componentsWithDefaultValuesToAdd.Add(ComponentType.ReadOnly<Prefab>());

            //TODO-release This will go away with transform ref
            entityManager.AddComponentData(prefabEntity, LocalTransform.FromPositionRotationScale(prefab.transform.localPosition, prefab.transform.localRotation, 1f));
            componentsWithDefaultValuesToAdd.Add(ComponentType.ReadOnly<LocalToWorld>());
            if ((forWorld.IsHost() && ghostObject.SingleWorldHostInterpolationSmoothing != SingleWorldHostInterpolationMode.Disabled) ||
                !forWorld.IsServer()
               )
            {
                // only smooth if necessary. SmoothingEnabled is used as a signal to the various transform syncing systems that smoothing occured and that
                // we should set the authoritative value back on transform when going into prediction
                // smoothing can happen with host interpolation, client side prediction switching or client side prediction error smoothing
                // so we're enabling smoothing on standalone clients or if the host ghost has interpolation enabled
                componentsWithDefaultValuesToAdd.Add(ComponentType.ReadOnly<SmoothingEnabled>());
            }

            if (!ghostObject.UseUniformScale)
                entityManager.AddComponentData(prefabEntity, new PostTransformMatrix { Value = float4x4.Scale(prefab.transform.localScale) });
            entityManager.AddComponentData(prefabEntity, new LocalTransform() { Position = prefab.transform.localPosition, Rotation = prefab.transform.localRotation,
                Scale = ghostObject.UseUniformScale ? prefab.transform.localScale.x : 1f });
            entityManager.AddComponentData(prefabEntity, new PendingClientGameObjectSpawn() { ShouldBeActive = prefab.activeSelf });

            // entityManager.SetEnabled(prefabEntity, prefab.activeSelf); // TODO-release this should be handled by entities engine side.

#if UNITY_EDITOR

            // Following is for BoundingBoxDebugGhostDrawer to draw bounding boxes around the ghost
            // TODO-release this can be a perf hit to gather all renderers in children, should find a way to debug gate this. Only when the drawer is enabled? can be enabled at runtime? Only ifdef DEBUG?
            var meshBounds = new GhostDebugMeshBounds().Initialize(prefab, prefabEntity, forWorld);
            entityManager.AddComponentData(prefabEntity, meshBounds);
#endif
            if (ghostObject.HasOwner) // TODO-release should always have an owner?
            {
                componentsWithDefaultValuesToAdd.Add(ComponentType.ReadOnly<GhostOwner>());

                // TODO-release https://jira.unity3d.com/browse/MTT-7127 on any new manually created instance, the actual value vvv will be updated on the next frame by GhostUpdateSystem after its instantiation.
                // Any scripts using this value in Awake will have an unknown value
                // This should be fine for network spawned instances, as they'll be spawned after ghost update system has set this correctly for the new entity
                componentsWithDefaultValuesToAdd.Add(ComponentType.ReadOnly<GhostOwnerIsLocal>());
            }

            if (ghostObject.SupportAutoCommandTarget)
                entityManager.AddComponentData(prefabEntity, new AutoCommandTarget { Enabled = true });

            componentsWithDefaultValuesToAdd.Add(ComponentType.ReadOnly<GhostPrefabRuntimeStrip>()); // So that further automatic component registration is done the same way entity and GO side. For example automatically adding authority smoothing on host entities

            entityManager.AddComponent(prefabEntity, new ComponentTypeSet(componentsWithDefaultValuesToAdd));

            // TODO-next handle hierarchy of GameObjects
            // TODO-next should handle GhostGroup as well, we could wrap it
            //Add all the prefab transform to the linked entity group. This is not done when the prefab entities are
            //created.
            //(like linear traversal of a list instead of walking a hierarchy)
            // Note: the following notes are for when we have entities integration with a persistent world.
            //TODO-release we add this silly reset because the persistent world state is "persistent", as such because we
            //reset the prefab registry instance, we redo all the RegisterPrefab and so we call this method mulitple times.
            //IT does not break for such simple case scenario, but in general it is not robust and shoiuld npt work like this.
            //The prefab creation MUST be done only once.
            // var linkedEntityGroup = entityManager.AddBuffer<LinkedEntityGroup>(prefabEntity);
            // linkedEntityGroup.Clear();

            //walk the whole hierarchy and add all the replicated children.
            //What actually means replicated children?
            //Any child that is marked to be replicated. How do we mark that a child entity is replicated ?
            //well.. it must have replicated components.. So chicken-egg problem here (since the replication concept is
            //added after)
            //for now, we will just add all the hierarchy here, in DFS order or by using transform ref directly
            // foreach (Transform transform in transforms)
            // {
            //     GhostEntityMapping.AcquireEntityReferencePrefab(transform.gameObject.GetEntityId(), transform.GetEntityId(), m_World);
            //     linkedEntityGroup.Add(transform.gameObject.EntityIfPrefab(m_World));
            // }

            //TODO-next: NetDebug should not be a per-world object but a static
            //Debug.Log($"[{entityManager.World.Name}:PrefabRegistry] Creating Ghost Prefab Entity '{ghostAuthoring.gameObject.name}'.");

            var config = ghostObject.AsConfig(prefab.gameObject.name); // TODO-next@prefabRegistration two prefabs with the same name will clash

            NativeParallelHashMap<GhostPrefabCreation.Component, GhostPrefabCreation.ComponentOverride> overrides = default;
            if (Application.isPlaying)
            {
                var inspectionComponent = prefab.GetComponent<IPrefabOverrideProvider>();
                if (inspectionComponent != null)
                    overrides = inspectionComponent.GetPrefabOverrides();
            }

            if (!ghostObject.UseUniformScale)
            {
                if (!overrides.IsCreated)
                    overrides = new NativeParallelHashMap<GhostPrefabCreation.Component, GhostPrefabCreation.ComponentOverride>(1, Allocator.Temp);
                var postTransformMatrixKey = new GhostPrefabCreation.Component { ComponentType = ComponentType.ReadWrite<PostTransformMatrix>(), ChildIndex = 0 };
                var scaleVariantHash = GhostObjectVariantDefaults.GetDefaultVariantOverrideHash(typeof(PostTransformMatrix));
                if (overrides.TryGetValue(postTransformMatrixKey, out var existingOverride))
                {
                    if ((existingOverride.OverrideType & GhostPrefabCreation.ComponentOverrideType.Variant) == 0)
                    {
                        existingOverride.OverrideType |= GhostPrefabCreation.ComponentOverrideType.Variant;
                        existingOverride.Variant = scaleVariantHash;
                        overrides[postTransformMatrixKey] = existingOverride;
                    }
                }
                else
                {
                    overrides.Add(postTransformMatrixKey, new GhostPrefabCreation.ComponentOverride
                    {
                        OverrideType = GhostPrefabCreation.ComponentOverrideType.Variant,
                        Variant = scaleVariantHash,
                    });
                }
            }
            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefabEntity, config, overrides);
            if (autoStrip && forWorld is NetcodeWorld netWorld)
            {
                // It's possible for a world here to not be a netcode world (for baking preview for example). This runtime stripping is only valid for runtime netcode worlds so we do the casting here.

                // individual per prefab stripping is useful in case users add prefabs later at runtime. With this, we can guarantee a call to register prefab will have all the right components stripped right after the callsite
                // This way if we do
                // Netcode.RegisterPrefab(myAddressable); // at anytime in the frame
                // GameObject.Instantiate(myAddressable); // prefab is already stripped, so instantiating is valid nowThis way we're sure we can call those two right one after the other and not have to wait for the next ECS update to Instantiate.
                // And this way we're sure we have all the appropriate stripping no matter the world
                GhostCollectionSystem.RuntimeStripPrefabs(entityManager, NetDebugSystem.GetDefaultNetDebug(), netWorld.RuntimeStripQuery);
            }

            // TODO-release the below was what we used with entities integration, since then we use baking/persistent worlds to generate GO prefabs. We'll need to come back to this once we have something concrete
            // TODO-release also validate there's no mem leak with this after the above, as it's the CodeGhostPrefab that disposes the blob assets in prefab creation
            // GhostPrefabCreation.ConvertToGhostPrefab_Internal(entityManager, prefabEntity, config, NetcodeConversionTarget.ClientAndServer, default);

            var createdEntity = prefab.EntityExt(isPrefab: true, forWorld.Unmanaged);
            Assert.IsTrue(createdEntity != Entity.Null);

            // m_PrefabResources.Add(createdEntity, prefab.GetComponent<GhostObject>().prefabReference);
            //Ok, now the real deal: how do we make everything we have in the ghost collection to still work ?
            // - We can't perform stripping on the prefab itself, but we can have a proper instantiation with a different archetype
            // - We still need to have a mapping GUID/Entity for the spanwing. Asking myself: can't this be already the this registry role ? We don't
            //   need to load the resource in the current world (can be even ref-counted in that sense)
            // - We need to build the schema (and this again is the same for client and server world).
            //     - it is mapping serializer types to indices in an array of serializers.
            //     - it is building the list of component / serializers mapping for root and children
            //     - add some further data. But all this data
            //we just need to register this "entity" and its blob data.
            //However, the problem being: all the operations assumes that the entity exist in that world (not another)
            //We need to pass all the information that can be used without requiring accessing another world

            // Sam note: the above comment from Cristian was made with entities integration. It's still relevant to think about, but right now this backport is solving this a different way. We'll need to look at the entities integration version of this implementation later.
        }

        internal static EntityGuid EntityGuidFromGameObject(GameObject go)
        {
            // Not doing anything with namespaceId and serial. This isn't for real baking, so no need to complexify for now.
            return new EntityGuid(go.GetEntityId(), EntityId.None, namespaceId: 0, serial: 0);
        }
    }
}

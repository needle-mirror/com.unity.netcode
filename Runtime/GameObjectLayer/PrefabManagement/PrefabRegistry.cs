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
    internal struct GhostGameObjectLink : ICleanupComponentData
    {
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
        public EntityId AssociatedGameObject;

        public GhostGameObjectLink(EntityId go)
        {
            this.AssociatedGameObject = go;
        }
#endif
    }
    // TMP while waiting for entities integration. Storing the prefab registry dictionary inside the world. Makes it easier to manage for single host world vs binary world
    // Note some of this is going to get refactored in an upcoming PR with auto prefab registration
    [CreateAfter(typeof(DefaultVariantSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class PrefabRegistryInitSystem : SystemBase
    {
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
        protected override void OnCreate()
        {
            var allPrefabs = Resources.FindObjectsOfTypeAll<GhostPrefabReference>();

            foreach (var prefabReference in allPrefabs)
            {
                if (!prefabReference.Ghost.SkipAutomaticPrefabRegistration)
                    PrefabsRegistry.RegisterPrefab(prefabReference.Prefab, World);
            }

            Netcode.Instance.InitializePlaceholderPrefabs(World);

            Enabled = false;
        }
#endif

        protected override void OnUpdate() { }
    }

#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID

    //The prefab registry is just a resource manager, that live and exist across world.
    internal class PrefabsRegistry
    {
         // private Dictionary<Entity, GhostPrefabReference> m_PrefabResources = new();

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
         public static void RegisterPrefab(GameObject prefab, World forWorld)
         {
             // already initialized for this world
             if (prefab.EntityExt(isPrefab: true, forWorld.Unmanaged) != default)
                 return;

             CreateEntityPrefab(prefab, forWorld);
             var createdEntity = prefab.EntityExt(isPrefab: true, forWorld.Unmanaged);
             Assert.IsTrue(createdEntity != Entity.Null);
             // m_PrefabResources.Add(createdEntity, prefab.GetComponent<GhostAdapter>().prefabReference);
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

        private static void CreateEntityPrefab(GameObject prefab, World world)
        {
            using var _ = GhostSpawningContext.CreateSpawnContext(world.Unmanaged); // spawn context for creating the prefab entity

            var ghostAuthoring = prefab.GetComponent<GhostAdapter>();
            var prefabEntity = GhostEntityMapping.AcquireEntityReferencePrefab(prefab.GetEntityId(), prefab.transform.GetEntityId(), forWorld: world.Unmanaged).Entity;

            // TODO-release handle child GOs that could have networked data as well
            var transforms = prefab.GetComponentsInChildren<Transform>(includeInactive:true);
            var entityManager = world.EntityManager;
            entityManager.SetName(prefabEntity, prefab.name);
            entityManager.AddComponentData(prefabEntity, default(Prefab));
            //TODO-release This will go away with transform ref
            entityManager.AddComponentData(prefabEntity, LocalTransform.FromPositionRotationScale(prefab.transform.localPosition, prefab.transform.localRotation, 1f));
            entityManager.AddComponentData(prefabEntity, new LocalToWorld());
            entityManager.AddComponentData(prefabEntity, new PostTransformMatrix{Value = float4x4.Scale(prefab.transform.localScale)});
            entityManager.AddComponentData(prefabEntity, new LocalTransform() { Position = prefab.transform.localPosition, Rotation = prefab.transform.localRotation});
            entityManager.SetEnabled(prefabEntity, prefab.activeSelf); // TODO-release this should be handled by entities engine side.

#if UNITY_EDITOR
            // Following is for BoundingBoxDebugGhostDrawer to draw bounding boxes around the ghost
            // TODO-release this can be a perf hit to gather all renderers in children, should find a way to debug gate this. Only when the drawer is enabled? can be enabled at runtime? Only ifdef DEBUG?
            var meshBounds = new GhostDebugMeshBounds().Initialize(prefab, prefabEntity, world);
            entityManager.AddComponentData(prefabEntity, meshBounds);
#endif
            if (ghostAuthoring.HasOwner) // TODO-release should always have an owner?
            {
                entityManager.AddComponentData(prefabEntity, default(GhostOwner));
                // TODO-release https://jira.unity3d.com/browse/MTT-7127 on any new manually created instance, the actual value vvv will be updated on the next frame by GhostUpdateSystem after its instantiation.
                // Any scripts using this value in Awake will have an unknown value
                // This should be fine for network spawned instances, as they'll be spawned after ghost update system has set this correctly for the new entity
                entityManager.AddComponentData(prefabEntity, default(GhostOwnerIsLocal));
            }
            if (ghostAuthoring.SupportAutoCommandTarget)
                entityManager.AddComponentData(prefabEntity, new AutoCommandTarget {Enabled = true});

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

            var config = ghostAuthoring.AsConfig(prefab.gameObject.name); // TODO-next two prefabs with the same name will clash

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefabEntity, config, default);
            // TODO-release the below was what we used with entities integration, since then we use baking/persistent worlds to generate GO prefabs. We'll need to come back to this once we have something concrete
            // TODO-release also validate there's no mem leak with this after the above, as it's the CodeGhostPrefab that disposes the blob assets in prefab creation
            // GhostPrefabCreation.ConvertToGhostPrefab_Internal(entityManager, prefabEntity, config, NetcodeConversionTarget.ClientAndServer, default);
        }
    }
#endif
}

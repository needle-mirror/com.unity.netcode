#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
namespace Unity.NetCode
{
    /// <summary>
    /// Provides functionality for managing the mapping between GameObjects and entities in a Unity ECS environment.
    /// </summary>
    /// <remarks>
    /// The <see cref="GhostEntityMapping"/> struct serves as a centralized system for creating,
    /// retrieving, and releasing entity references associated with GameObjects. It supports operations such as entity
    /// creation in the right client/server world and reference counting for the created entities
    /// It also supports creating entities FROM prefabs and creating entities FOR those GameObject prefabs
    /// </remarks>
    // This is global to mimic entity integration logic (where a prefab gameObject for example would be global)
    // General structure inspired from Motion's EntityMapping, but adapted heavily to fit Netcode spawning use cases
    // Most of this will disappear with Entities Integration
    struct GhostEntityMapping : IDisposable
    {
        internal struct MappedEntity
        {
            public WorldUnmanaged World;
            public Entity Entity;
            public int RefCount;
            public int TransformIndex;
        }

        // Used to wrap the logic around the key for the map above. Since our key changes depending on if we're a prefab or a runtime GO, we have some APIs here for our own sanity.
        internal struct GameObjectKey : IEquatable<GameObjectKey>
        {
            public EntityId gameObjectId;
            public ulong worldSequenceId;
            public bool hasWorld;

            public static GameObjectKey GetForPrefab(EntityId prefabId, WorldUnmanaged world)
            {
                return new GameObjectKey() { gameObjectId = prefabId, worldSequenceId = world.SequenceNumber, hasWorld = true};
            }

            // Internal note: having a GameObject parameter helps prevent cases where I go GetForGameObject(ghostAdapter.GetEntityId) --> MonoBehaviours also have EntityIds, which can cause easy to miss issues
            public static GameObjectKey GetForGameObject(GameObject gameObject)
            {
                return GetForGameObject(gameObject.GetEntityId());
            }

            public static GameObjectKey GetForGameObject(EntityId goID) => new GameObjectKey() { gameObjectId = goID, worldSequenceId = 0, hasWorld = false }; // SequenceNumber is a ulong, so any negative value is invalid

            public bool Equals(GameObjectKey other)
            {
                return gameObjectId == other.gameObjectId && worldSequenceId == other.worldSequenceId && hasWorld == other.hasWorld;
            }

            public override int GetHashCode()
            {
                return (int)math.hash(new int3(gameObjectId.GetHashCode(), worldSequenceId.GetHashCode(), hasWorld ? 1 : 0));
                // can't use HashCode.Combine with burst, its result isn't the same bursted and non-bursted
                // return HashCode.Combine(gameObjectId.GetHashCode(), worldSequenceId, hasWorld);
            }
        }

        // The EntityId to World key makes it so we can have a single gameObject prefab for two different entity prefab in different worlds
        internal NativeHashMap<GameObjectKey, MappedEntity> m_MappedEntities;
        NativeHashSet<EntityId> m_CurrentObjectInitializingCheck;

        public GhostEntityMapping(bool _)
        {
            this.m_MappedEntities = new(64, Allocator.Persistent);
            m_CurrentObjectInitializingCheck = new(1, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (m_MappedEntities.IsCreated)
                m_MappedEntities.Dispose();
            if (m_CurrentObjectInitializingCheck.IsCreated)
                m_CurrentObjectInitializingCheck.Dispose();
        }

        /// <summary>
        /// Lookup the entity reference for this GameObject.
        /// This will return null if the entity reference has not been created.
        /// </summary>
        /// <param name="gameObject">The object to lookup</param>
        /// <returns>An <see cref="EntityLink"/> for the object, or default if no mapping exists</returns>
        public static EntityLink LookupEntityReferenceGameObject(GameObject gameObject)
        {
            var entityId = gameObject.GetEntityId();
            return LookupEntityReference(GameObjectKey.GetForGameObject(gameObject.GetEntityId()));
        }

        /// <inheritdoc cref="LookupEntityReferenceGameObject(GameObject)"/>
        public static EntityLink LookupEntityReferenceGameObject(EntityId entityId)
        {
            return LookupEntityReference(GameObjectKey.GetForGameObject(entityId));
        }

        /// <inheritdoc cref="LookupEntityReferenceGameObject(GameObject)"/>
        public static EntityLink LookupEntityReferencePrefab(GameObject gameObject, WorldUnmanaged forWorld)
        {
            return LookupEntityReference(GameObjectKey.GetForPrefab(gameObject.GetEntityId(), forWorld));
        }

        /// <inheritdoc cref="LookupEntityReferenceGameObject(GameObject)"/>
        public static EntityLink LookupEntityReferencePrefab(EntityId gameObjectId, WorldUnmanaged forWorld)
        {
            return LookupEntityReference(GameObjectKey.GetForPrefab(gameObjectId, forWorld));
        }

        /// <inheritdoc cref="LookupEntityReferenceGameObject(GameObject)"/>
        private static EntityLink LookupEntityReference(GameObjectKey key)
        {
            ref var self = ref Netcode.Unmanaged.m_EntityMapping;
            if (self.m_MappedEntities.IsCreated && self.m_MappedEntities.TryGetValue(key, out var mappedEntity))
                return new EntityLink { World = mappedEntity.World, Entity = mappedEntity.Entity };
            return default;
        }

        /// <summary>
        /// Creates an entity or increments the ref counter for the entity associated with the given GameObject.
        /// There's separate methods for prefabs and GameObjects, as they'll be tracked differently.
        /// Prefab keys are tracked per world, while GameObjects are tracked globally (with a null world in their key). A single prefab can have multiple entities associated with it (per world), while
        /// a GameObject is already per world, so no need to include the world in its key, it'll be unique already.
        /// </summary>
        /// <param name="prefabObjectId"></param>
        /// <param name="transformId"></param>
        /// <param name="forWorld"></param>
        /// <returns></returns>
        internal static EntityLink AcquireEntityReferencePrefab(EntityId prefabObjectId, EntityId transformId, WorldUnmanaged forWorld)
        {
            return AcquireEntityReference(prefabObjectId, transformId, isPrefabGameObject: true, forWorld: forWorld);
        }

        /// <inheritdoc cref="AcquireEntityReferencePrefab"/>
        internal static EntityLink AcquireEntityReferenceGameObject(EntityId gameObjectId, EntityId transformId, EntityId prefabEntityId, WorldUnmanaged autoWorld)
        {
            return AcquireEntityReference(gameObjectId, transformId, isPrefabGameObject: false, forWorld: autoWorld, prefabId: prefabEntityId);
        }

        /// <inheritdoc cref="AcquireEntityReferencePrefab"/>
        private static EntityLink AcquireEntityReference(EntityId gameObjectId, EntityId transformId, bool isPrefabGameObject , WorldUnmanaged forWorld, EntityId prefabId = default)
        {
            ref var self = ref Netcode.Unmanaged.m_EntityMapping;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (self.m_CurrentObjectInitializingCheck.Contains(gameObjectId))
                throw new InvalidOperationException($"Already initializing object {(int)gameObjectId}, sanity check failed.");
            self.m_CurrentObjectInitializingCheck.Add(gameObjectId);
            try
            {
#endif
                var world = forWorld;

                GameObjectKey mapKey;
                if (isPrefabGameObject)
                    mapKey = GameObjectKey.GetForPrefab(gameObjectId, world);
                else
                    mapKey = GameObjectKey.GetForGameObject(gameObjectId);
                if (self.m_MappedEntities.TryGetValue(mapKey, out var mappedEntity))
                {
                    ++mappedEntity.RefCount;
                    self.m_MappedEntities[mapKey] = mappedEntity;
                }
                else
                {
#if UNITY_EDITOR
                    if (!Application.isPlaying)
                        throw new InvalidOperationException("Sanity check failed, shouldn't be here");
#endif
                    var injectedWorld = forWorld;

                    Assert.IsTrue(injectedWorld.IsCreated, "sanity check failed");

                    var injectedEntity = GhostSpawningContext.Current.SpawnedEntity;
                    if (injectedEntity == Entity.Null)
                    {
                        if (prefabId != default)
                        {
                            var prefabEntity = prefabId.EntityExt(isPrefab: true, injectedWorld);
                            if (prefabEntity == Entity.Null)
                                throw new InvalidOperationException("prefab GameObject was specified, but no associated prefab entity was found, have you registered your prefab?");
                            injectedEntity = injectedWorld.EntityManager.Instantiate(prefabEntity);
                        }
                        else
                            injectedEntity = injectedWorld.EntityManager.CreateEntity();
                    }

                    Assert.IsTrue(injectedEntity != Entity.Null, "sanity check failed");

                    var entity = injectedEntity;
                    int transformIndex = -1;
                    if (!isPrefabGameObject)
                    {
                        // TODO-release cache query
                        // we only track runtime GameObject's transforms, we don't want to do useless iterations over prefab transforms
                        using var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<PerWorldIndexedTransformTrackingSingleton>();
                        var trackingSingleton = injectedWorld.EntityManager.CreateEntityQuery(builder).GetSingleton<PerWorldIndexedTransformTrackingSingleton>();
                        transformIndex = trackingSingleton.AddGameObjectToTrack(gameObjectId, entity, transformId);
                    }

                    mappedEntity = new MappedEntity()
                    {
                        World = injectedWorld,
                        Entity = entity,
                        RefCount = 1,
                        TransformIndex = transformIndex
                    };

                    // We keep the key using forWorld which might be null, to keep the same consistent keying for prefab vs runtime ghosts
                    self.m_MappedEntities.Add(mapKey, mappedEntity);

                    if (!injectedWorld.EntityManager.AddComponentData(mappedEntity.Entity, new GhostGameObjectLink(mapKey.gameObjectId)))
                        injectedWorld.EntityManager.SetComponentData(mappedEntity.Entity, new GhostGameObjectLink(mapKey.gameObjectId));
                }

                return new EntityLink { World = mappedEntity.World, Entity = mappedEntity.Entity };
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            }
            finally
            {
                self.m_CurrentObjectInitializingCheck.Remove(gameObjectId);
            }
#endif
        }

        /// <summary>
        /// Release the entity reference for this GameObject.
        /// If the reference count goes to zero, the entity is destroyed.
        /// </summary>
        // TODO-release this is never used?
        public static void ReleasePrefabReference(GameObject prefab, World forWorld)
        {
            ReleaseEntityReference(GameObjectKey.GetForPrefab(prefab.GetEntityId(), forWorld.Unmanaged), forWorld.IsCreated);
        }

        /// <inheritdoc cref="ReleasePrefabReference"/>
        public static void ReleaseGameObjectEntityReference(GameObject gameObject, bool worldIsCreated)
        {
            ReleaseEntityReference(GameObjectKey.GetForGameObject(gameObject.GetEntityId()), worldIsCreated);
        }

        /// <inheritdoc cref="ReleasePrefabReference"/>
        public static void ReleaseGameObjectEntityReference(EntityId gameObjectId, bool worldIsCreated)
        {
            ReleaseEntityReference(GameObjectKey.GetForGameObject(gameObjectId), worldIsCreated);
        }

        /// <inheritdoc cref="ReleasePrefabReference"/>
        private static EntityLink ReleaseEntityReference(GameObjectKey gameObjectKey, bool worldIsCreated)
        {
            ref var self = ref Netcode.Unmanaged.m_EntityMapping;
            if (self.m_MappedEntities.TryGetValue(gameObjectKey, out var mappedEntity))
            {
                if (--mappedEntity.RefCount > 0)
                    self.m_MappedEntities[gameObjectKey] = mappedEntity;
                else
                {
                    if (worldIsCreated)
                    {
                        var em = mappedEntity.World.EntityManager;

                        if (em.Exists(mappedEntity.Entity))
                        {
                            em.RemoveComponent<GhostGameObjectLink>(mappedEntity.Entity); // This is the right way to destroy a ghost, so we remove the component directly here
                            em.DestroyEntity(mappedEntity.Entity);
                        }
                        // TODO-release cache query
                        var trackingSingleton = em.CreateEntityQuery(typeof(PerWorldIndexedTransformTrackingSingleton)).GetSingleton<PerWorldIndexedTransformTrackingSingleton>();
                        trackingSingleton.RemoveGameObjectToTrack(mappedEntity);
                    }
                    self.m_MappedEntities.Remove(gameObjectKey); // needs to happen after, some remove methods in the if above require that mapping
                }
            }
            return default;
        }

        /// <summary>
        /// Represents a link between a GameObject and an entity and its associated world.
        /// </summary>
        public struct EntityLink : IEquatable<EntityLink>
        {
            public WorldUnmanaged World;
            public Entity Entity;

            public readonly bool WasInitialized => Entity != Entity.Null && World.IsCreated;
            public readonly bool Equals(EntityLink other)
            {
                if (!this.WasInitialized || !other.WasInitialized) return this.WasInitialized == other.WasInitialized; // avoid the null ref with world checks if either is not initialized
                return Entity == other.Entity && World.EntityManager.Equals(other.World.EntityManager);
            }

            public readonly override bool Equals(object obj) => obj is EntityLink other && Equals(other);
            public readonly override int GetHashCode() => (World, Entity).GetHashCode();
            public static bool operator ==(EntityLink left, EntityLink right) => left.Equals(right);
            public static bool operator !=(EntityLink left, EntityLink right) => !left.Equals(right);

            public FixedString512Bytes ToFixedString()
            {
                if (this == default) return "default";
                FixedString512Bytes toRet = this.Entity.ToFixedString();
                toRet.Append(" world:");
                toRet.Append(this.World.Name);
                return toRet;
            }

            public override string ToString()
            {
                return ToFixedString().ToString();
            }
        }
    }

    /// <summary>
    /// Utilities to use entity mapping directly on GameObjects. Should be useless once entities integration comes in.
    /// </summary>
    internal static class MappingExtensions
    {
        /// <inheritdoc cref="EntityExt(UnityEngine.GameObject,bool,Unity.Entities.WorldUnmanaged)"/>
        public static Entity EntityExt(this GhostAdapter self, WorldUnmanaged forWorld = default)
        {
            if (self.IsPrefab())
                return GhostEntityMapping.LookupEntityReferencePrefab(self.gameObject, forWorld).Entity;
            return GhostEntityMapping.LookupEntityReferenceGameObject(self.gameObject).Entity;
        }

        /// <inheritdoc cref="EntityExt(UnityEngine.GameObject,bool,Unity.Entities.WorldUnmanaged)"/>
        public static Entity EntityExt(this EntityId entityId, bool isPrefab, WorldUnmanaged forWorld = default)
        {
            if (isPrefab)
                return GhostEntityMapping.LookupEntityReferencePrefab(entityId, forWorld).Entity;
            return GhostEntityMapping.LookupEntityReferenceGameObject(entityId).Entity;
        }

        /// <summary>
        /// Gets the associated entity mapped with a GameObject. This should be removed with entities integration.
        /// </summary>
        public static Entity EntityExt(this GameObject go, bool isPrefab, WorldUnmanaged forWorld = default)
        {
            if (isPrefab)
                return GhostEntityMapping.LookupEntityReferencePrefab(go, forWorld).Entity;
            return GhostEntityMapping.LookupEntityReferenceGameObject(go).Entity;
        }

        /// <summary>
        /// Returns the world currently mapped with this runtime GameObject
        /// Note that this is currently invalid for prefab GameObjects.
        /// </summary>
        /// <param name="go"></param>
        /// <returns></returns>
        public static World WorldExt(this GameObject go)
        {
            return GhostEntityMapping.LookupEntityReferenceGameObject(go).World.EntityManager.World;
        }
    }
}
#endif

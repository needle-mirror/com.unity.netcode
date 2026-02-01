
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// Single access point for tracking lists. This is required because of the way TransformAccessArray works, since it needs an index based access. So we need to manage an entity list
    /// with matching indices to be able to read/write that entity.
    /// All of this should go away with better integrated entities transforms
    /// </summary>
    internal struct PerWorldIndexedTransformTrackingSingleton : IDisposable, IComponentData
    {
        // these lists share the same index, for TransformAccessArray jobs
        internal NativeList<Entity> m_EntitiesForTransforms;
        internal TransformAccessArray m_Transforms;
        internal NativeList<GhostEntityMapping.GameObjectKey> m_IndexedGameObjectIds;

        public PerWorldIndexedTransformTrackingSingleton(Allocator allocator)
        {
            m_EntitiesForTransforms = new NativeList<Entity>(allocator);
            m_Transforms = new TransformAccessArray(10);
            m_IndexedGameObjectIds = new(allocator);
        }

        public int AddGameObjectToTrack(EntityId objId, Entity entity, EntityId transformId)
        {
            var index = m_EntitiesForTransforms.Length;
            this.m_EntitiesForTransforms.Add(entity);
            this.m_Transforms.Add(transformId);
            this.m_IndexedGameObjectIds.Add(GhostEntityMapping.GameObjectKey.GetForGameObject(objId));
            return index;
        }

        public void RemoveGameObjectToTrack(GhostEntityMapping.MappedEntity mappedEntity)
        {
            var entityKeyToRemove = this.m_IndexedGameObjectIds[mappedEntity.TransformIndex];
            var lastIndex = this.m_IndexedGameObjectIds.Length - 1;
            var swappedKey = lastIndex >= 0 ? this.m_IndexedGameObjectIds[lastIndex] : entityKeyToRemove;
            this.m_Transforms.RemoveAtSwapBack(mappedEntity.TransformIndex);
            this.m_EntitiesForTransforms.RemoveAtSwapBack(mappedEntity.TransformIndex);
            this.m_IndexedGameObjectIds.RemoveAtSwapBack(mappedEntity.TransformIndex);
            if (Netcode.Unmanaged.m_EntityMapping.m_MappedEntities.TryGetValue(swappedKey, out var swappedMappedEntity))
            {
                swappedMappedEntity.TransformIndex = mappedEntity.TransformIndex;
                Netcode.Unmanaged.m_EntityMapping.m_MappedEntities[swappedKey] = swappedMappedEntity;
            }
        }

        public void Dispose()
        {
            this.m_EntitiesForTransforms.Dispose();
            this.m_Transforms.Dispose();
            this.m_IndexedGameObjectIds.Dispose();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    internal partial class RegisterGhostTransformTrackingSystem : SystemBase
    {
        Entity m_SingletonEntity;
        protected override void OnCreate()
        {
            m_SingletonEntity = this.EntityManager.CreateEntity();
            this.EntityManager.AddComponentData(m_SingletonEntity, new PerWorldIndexedTransformTrackingSingleton(Allocator.Persistent));
            Enabled = false;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            this.EntityManager.GetComponentData<PerWorldIndexedTransformTrackingSingleton>(m_SingletonEntity).Dispose();
        }

        protected override void OnUpdate() { }
    }
}

#endif

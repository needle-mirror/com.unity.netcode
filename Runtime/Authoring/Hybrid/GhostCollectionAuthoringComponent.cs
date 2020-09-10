using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    [ConverterVersion("timj", 4)]
    public class GhostCollectionAuthoringComponent : MonoBehaviour, IConvertGameObjectToEntity, IDeclareReferencedPrefabs
    {
        [Serializable]
        public struct Ghost
        {
            public GhostAuthoringComponent prefab;
            public bool enabled;
        }

        public List<Ghost> Ghosts = new List<Ghost>();

        public void DeclareReferencedPrefabs(List<GameObject> referencedPrefabs)
        {
            foreach (var prefab in Ghosts)
            {
                if (prefab.enabled)
                    referencedPrefabs.Add(prefab.prefab.gameObject);
            }
        }
        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, default(GhostPrefabCollectionComponent));
            var prefabs = dstManager.AddBuffer<GhostPrefabBuffer>(entity);
            foreach (var prefab in Ghosts)
            {
                if (!prefab.enabled)
                    continue;
                var prefabEnt = conversionSystem.GetPrimaryEntity(prefab.prefab.gameObject);
                if (dstManager.Exists(prefabEnt))
                    prefabs.Add(new GhostPrefabBuffer{Value = prefabEnt});
                else
                    Debug.LogError($"The prefab {prefab.prefab.Name} in the ghost collection was no converted to an entity, skipping it");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    [ConverterVersion("timj", 2)]
    public class GhostCollectionAuthoringComponent : MonoBehaviour, IConvertGameObjectToEntity
    {
        public string RootPath = "";
        public string SerializerCollectionPath = "GhostSerializerCollection.cs";
        public string DeserializerCollectionPath = "GhostDeserializerCollection.cs";
        public string NamePrefix = "";

        [Serializable]
        public struct Ghost
        {
            public GhostAuthoringComponent prefab;
            public bool enabled;
        }

        public List<Ghost> Ghosts = new List<Ghost>();

        public Type FindComponentWithName(string name)
        {
            var allTypes = TypeManager.GetAllTypes();
            foreach (var componentType in allTypes)
            {
                if (componentType.Type != null && componentType.Type.Name == name)
                    return componentType.Type;
            }

            return null;
        }

        private void EnsurePrefab(Entity entity, EntityManager dstManager)
        {
            if (!dstManager.HasComponent<Prefab>(entity))
            {
                dstManager.AddComponent<Prefab>(entity);
                if (dstManager.HasComponent<LinkedEntityGroup>(entity))
                {
                    var group = dstManager.GetBuffer<LinkedEntityGroup>(entity).ToNativeArray(Allocator.Temp);
                    for (int i = 1; i < group.Length; ++i)
                    {
                        dstManager.AddComponent<Prefab>(group[i].Value);
                    }
                }
            }
            // These should only appear on the scene objects themselves and not the prefab
            // but might get there in the prefab creation process (like when you create prefab from scene object)
            if (dstManager.HasComponent<PreSpawnedGhostId>(entity))
            {
                dstManager.RemoveComponent<PreSpawnedGhostId>(entity);
                dstManager.RemoveComponent<SubSceneGhostComponentHash>(entity);
            }
        }
        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            var conversionTarget = GhostAuthoringConversion.GetConversionTarget(conversionSystem);

            if (conversionTarget == NetcodeConversionTarget.Undefined)
            {
                throw new InvalidOperationException(
                    $"A ghost prefab can only be created in the client or server world, not {dstManager.World.Name}");
            }

            var collection = default(GhostPrefabCollectionComponent);
            Type enableSystem = null;

            if (conversionTarget == NetcodeConversionTarget.Server)
            {
                enableSystem = FindComponentWithName($"Enable{NamePrefix}GhostSendSystemComponent");
                if (enableSystem == null)
                    throw new InvalidOperationException($"Could not find Enable{NamePrefix}GhostSendSystemComponent, make sure the ghost collection is generated");
                var prefabList = new NativeList<GhostPrefabBuffer>(Allocator.Temp);
                foreach (var ghost in Ghosts)
                {
                    if (ghost.prefab == null || !ghost.enabled)
                        continue;
                    var prefabEntity =
                        GameObjectConversionUtility.ConvertGameObjectHierarchy(ghost.prefab.gameObject,
                            conversionSystem.ForkSettings(1));
                    EnsurePrefab(prefabEntity, dstManager);
                    prefabList.Add(new GhostPrefabBuffer {Value = prefabEntity});
                }

                collection.serverPrefabs = conversionSystem.CreateAdditionalEntity(this);
                var prefabs = dstManager.AddBuffer<GhostPrefabBuffer>(collection.serverPrefabs);
                for (int i = 0; i < prefabList.Length; ++i)
                    prefabs.Add(prefabList[i]);
            }
            else if (conversionTarget == NetcodeConversionTarget.Client)
            {
                enableSystem = FindComponentWithName($"Enable{NamePrefix}GhostReceiveSystemComponent");
                if (enableSystem == null)
                    throw new InvalidOperationException($"Could not find Enable{NamePrefix}GhostReceiveSystemComponent, make sure the ghost collection is generated");
                var predictedList = new NativeList<GhostPrefabBuffer>(Allocator.Temp);
                var interpolatedList = new NativeList<GhostPrefabBuffer>(Allocator.Temp);
                foreach (var ghost in Ghosts)
                {
                    if (ghost.prefab == null || !ghost.enabled)
                        continue;
                    var origInstantiate = ghost.prefab.DefaultClientInstantiationType;
                    ghost.prefab.DefaultClientInstantiationType =
                        GhostAuthoringComponent.ClientInstantionType.Interpolated;
                    var prefabEntity =
                        GameObjectConversionUtility.ConvertGameObjectHierarchy(ghost.prefab.gameObject,
                            conversionSystem.ForkSettings(1));
                    ghost.prefab.DefaultClientInstantiationType =
                        GhostAuthoringComponent.ClientInstantionType.Predicted;
                    var predictedPrefabEntity =
                        GameObjectConversionUtility.ConvertGameObjectHierarchy(ghost.prefab.gameObject,
                            conversionSystem.ForkSettings(2));
                    ghost.prefab.DefaultClientInstantiationType = origInstantiate;
                    EnsurePrefab(predictedPrefabEntity, dstManager);
                    EnsurePrefab(prefabEntity, dstManager);
                    predictedList.Add(new GhostPrefabBuffer {Value = predictedPrefabEntity});
                    interpolatedList.Add(new GhostPrefabBuffer {Value = prefabEntity});
                }

                collection.clientInterpolatedPrefabs = conversionSystem.CreateAdditionalEntity(this);
                var interpolatedPrefabs = dstManager.AddBuffer<GhostPrefabBuffer>(collection.clientInterpolatedPrefabs);
                for (int i = 0; i < interpolatedList.Length; ++i)
                    interpolatedPrefabs.Add(interpolatedList[i]);
                collection.clientPredictedPrefabs = conversionSystem.CreateAdditionalEntity(this);
                var predictedPrefabs = dstManager.AddBuffer<GhostPrefabBuffer>(collection.clientPredictedPrefabs);
                for (int i = 0; i < predictedList.Length; ++i)
                    predictedPrefabs.Add(predictedList[i]);
            }

            dstManager.AddComponentData(entity, collection);
            if (enableSystem != null)
                dstManager.AddComponent(entity, enableSystem);
        }
    }
}

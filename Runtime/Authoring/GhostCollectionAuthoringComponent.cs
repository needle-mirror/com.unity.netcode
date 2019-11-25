using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    [ConverterVersion("timj", 1)]
    public class GhostCollectionAuthoringComponent : MonoBehaviour, IConvertGameObjectToEntity
    {
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

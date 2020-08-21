using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    [ConverterVersion("timj", 3)]
    public class GhostCollectionAuthoringComponent : MonoBehaviour, IConvertGameObjectToEntity
    {
        [Serializable]
        public struct Ghost
        {
            public GhostAuthoringComponent prefab;
            public bool enabled;
        }

        public List<Ghost> Ghosts = new List<Ghost>();

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

            // TODO: this should build a light weight version of the ghost component types instead of extracting htat at runtime
            var metaDataList = new NativeList<GhostMetaDataBuffer>(Allocator.Temp);
            foreach (var ghost in Ghosts)
            {
                if (ghost.prefab == null || !ghost.enabled)
                    continue;
                var ghostName = default(FixedString32);
                var error = ghostName.CopyFrom(ghost.prefab.Name);
                if (error != CopyError.None)
                    UnityEngine.Debug.LogWarning($"{error} while copying ghost name {ghost.prefab.Name}");
                metaDataList.Add(new GhostMetaDataBuffer {Importance = ghost.prefab.Importance, Name = ghostName});
            }
            collection.ghostMetaData = conversionSystem.CreateAdditionalEntity(this);
            var ghostMetaData = dstManager.AddBuffer<GhostMetaDataBuffer>(collection.ghostMetaData);
            for (int i = 0; i < metaDataList.Length; ++i)
            {
                ghostMetaData.Add(metaDataList[i]);
            }
            // TODO: once the component list is separate this can go back to being only for server, and maybe add a client / server config
            //if (conversionTarget == NetcodeConversionTarget.Server)
            {
                var prefabList = new NativeList<GhostPrefabBuffer>(Allocator.Temp);
                foreach (var ghost in Ghosts)
                {
                    if (ghost.prefab == null || !ghost.enabled)
                        continue;
                    ghost.prefab.ForceServerConversion = true;
                    var prefabEntity =
                        GameObjectConversionUtility.ConvertGameObjectHierarchy(ghost.prefab.gameObject,
                            conversionSystem.ForkSettings(1));
                    ghost.prefab.ForceServerConversion = false;
                    EnsurePrefab(prefabEntity, dstManager);
                    prefabList.Add(new GhostPrefabBuffer {Value = prefabEntity});
                }

                collection.serverPrefabs = conversionSystem.CreateAdditionalEntity(this);
                var prefabs = dstManager.AddBuffer<GhostPrefabBuffer>(collection.serverPrefabs);
                for (int i = 0; i < prefabList.Length; ++i)
                    prefabs.Add(prefabList[i]);
            }
            if (conversionTarget == NetcodeConversionTarget.Client)
            {
                var predictedList = new NativeList<GhostPrefabBuffer>(Allocator.Temp);
                var interpolatedList = new NativeList<GhostPrefabBuffer>(Allocator.Temp);
                foreach (var ghost in Ghosts)
                {
                    if (ghost.prefab == null || !ghost.enabled)
                        continue;
                    var origInstantiate = ghost.prefab.DefaultGhostMode;
                    var prefabEntity = Entity.Null;
                    var predictedPrefabEntity = Entity.Null;

                    ghost.prefab.DefaultGhostMode =
                        GhostAuthoringComponent.GhostMode.Interpolated;
                    if ((ghost.prefab.SupportedGhostModes & GhostAuthoringComponent.GhostModeMask.Interpolated) != 0)
                    {
                        prefabEntity = GameObjectConversionUtility.ConvertGameObjectHierarchy(ghost.prefab.gameObject,
                                conversionSystem.ForkSettings(2));
                        EnsurePrefab(prefabEntity, dstManager);
                    }
                    ghost.prefab.DefaultGhostMode =
                        GhostAuthoringComponent.GhostMode.Predicted;
                    if ((ghost.prefab.SupportedGhostModes & GhostAuthoringComponent.GhostModeMask.Predicted) != 0)
                    {
                        predictedPrefabEntity = GameObjectConversionUtility.ConvertGameObjectHierarchy(ghost.prefab.gameObject,
                                conversionSystem.ForkSettings(3));
                        EnsurePrefab(predictedPrefabEntity, dstManager);
                    }
                    ghost.prefab.DefaultGhostMode = origInstantiate;
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
        }
    }
}

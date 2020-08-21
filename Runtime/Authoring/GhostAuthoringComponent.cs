using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    public class GhostAuthoringComponent : MonoBehaviour
    {
#if UNITY_EDITOR
        void OnValidate()
        {
            ValidatePrefabId();
        }

        void ValidatePrefabId()
        {
            if (UnityEditor.PrefabUtility.IsPartOfNonAssetPrefabInstance(gameObject) || Application.isPlaying)
                return;
            var guid = "";
            if (gameObject.transform.parent == null)
            {
                // The common case is a root object in a prefab, in this case we always validate the guid to detect cloned files
                var prefabStage = UnityEditor.Experimental.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject);
                if (prefabStage != null)
                {
#if UNITY_2020_1_OR_NEWER
                    var assetPath = prefabStage.assetPath;
#else
                    var assetPath = prefabStage.prefabAssetPath;
#endif
                    guid = UnityEditor.AssetDatabase.AssetPathToGUID(assetPath);
                }
                else if (UnityEditor.PrefabUtility.GetPrefabAssetType(gameObject) != UnityEditor.PrefabAssetType.NotAPrefab)
                {
                    var path = UnityEditor.AssetDatabase.GetAssetPath(gameObject);
                    if (String.IsNullOrEmpty(path))
                        return;
                    guid = UnityEditor.AssetDatabase.AssetPathToGUID(path);
                }
            }
            if (guid != prefabId)
            {
                UnityEditor.Undo.RecordObject(this, "");
                prefabId = guid;
            }
        }
#endif

        public enum GhostModeMask
        {
            Interpolated = 1,
            Predicted = 2,
            All = 3
        }
        public enum GhostMode
        {
            Interpolated,
            Predicted,
            OwnerPredicted
        }
        public enum GhostOptimizationMode
        {
            Dynamic,
            Static
        }

        internal bool ForceServerConversion;
        public GhostMode DefaultGhostMode = GhostMode.Interpolated;
        public GhostModeMask SupportedGhostModes = GhostModeMask.All;
        public GhostOptimizationMode OptimizationMode = GhostOptimizationMode.Dynamic;
        public int Importance = 1;
        public string prefabId = "";
        public string Name;

        [HideInInspector] public bool doNotStrip = false;
    }

    [ConverterVersion("timj", 3)]
    [UpdateInGroup(typeof(GameObjectAfterConversionGroup))]
    public class GhostAuthoringConversion : GameObjectConversionSystem
    {
        public static NetcodeConversionTarget GetConversionTarget(GameObjectConversionSystem system)
        {
            // Detect target using build settings (This is used from sub scenes)
#if UNITY_EDITOR
            {
                var settings = system.GetBuildConfigurationComponent<NetCodeConversionSettings>();
                if (settings != null)
                {
                    //Debug.LogWarning("BuildSettings conversion for: " + settings.Target);
                    return settings.Target;
                }
            }
#endif

            if (system.DstEntityManager.World.GetExistingSystem<ClientSimulationSystemGroup>() != null)
                return NetcodeConversionTarget.Client;
            if (system.DstEntityManager.World.GetExistingSystem<ServerSimulationSystemGroup>() != null)
                return NetcodeConversionTarget.Server;

            return NetcodeConversionTarget.Undefined;
        }

        protected override void OnUpdate()
        {
            Entities.ForEach((GhostAuthoringComponent ghostAuthoring) =>
            {
                if (String.IsNullOrEmpty(ghostAuthoring.prefabId))
                    throw new InvalidOperationException($"The ghost {ghostAuthoring.gameObject.name} is not a valid prefab, all ghosts must be the top-level GameObject in a prefab. Ghost instances in scenes must be instances of such prefabs and changes should be made on the prefab asset, not the prefab instance");
                DeclareLinkedEntityGroup(ghostAuthoring.gameObject);
                if (ghostAuthoring.doNotStrip)
                    return;
                var entity = GetPrimaryEntity(ghostAuthoring);

                var target = ghostAuthoring.ForceServerConversion ? NetcodeConversionTarget.Server : GetConversionTarget(this);

                // FIXME: A2 hack
                if (target == NetcodeConversionTarget.Undefined)
                {
                    //  throw new InvalidOperationException($"A ghost prefab can only be created in the client or server world, not {DstEntityManager.World.Name}");
                    Debug.LogWarning(
                        $"A ghost prefab can only be created in the client or server world, not {DstEntityManager.World.Name}.\nDefaulting to server conversion.");
                    target = NetcodeConversionTarget.Server;
                }

                if (ghostAuthoring.prefabId.Length != 32)
                    throw new InvalidOperationException("Invalid guid for ghost prefab type");
                var ghostType = new GhostTypeComponent();
                ghostType.guid0 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(0, 8), 16);
                ghostType.guid1 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(8, 8), 16);
                ghostType.guid2 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(16, 8), 16);
                ghostType.guid3 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(24, 8), 16);
                DstEntityManager.AddComponentData(entity, ghostType);

                // FIXME: maybe stripping should be individual systems running before this to make sure it can be changed in a way that always triggers a reconvert - and to avoid reflection
                var components = DstEntityManager.GetComponentTypes(entity);
                if (target == NetcodeConversionTarget.Server)
                {
                    // Make sure different ghost types are in different chunks
                    DstEntityManager.AddSharedComponentData(entity, new SharedGhostTypeComponent{SharedValue = ghostType});
                    DstEntityManager.AddComponentData(entity, new GhostComponent());
                    DstEntityManager.AddComponentData(entity, new PredictedGhostComponent());
                    // Create server version of prefab
                    foreach (var comp in components)
                    {
                        var attr = comp.GetManagedType().GetCustomAttribute<GhostComponentAttribute>();
                        if (attr != null && (attr.PrefabType & GhostPrefabType.Server) == 0)
                            DstEntityManager.RemoveComponent(entity, comp);
                    }
                    if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Interpolated)
                        DstEntityManager.AddComponentData(entity, default(GhostAlwaysInterpolatedComponent));
                    else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Predicted)
                        DstEntityManager.AddComponentData(entity, default(GhostAlwaysPredictedComponent));
                    else if (ghostAuthoring.DefaultGhostMode == GhostAuthoringComponent.GhostMode.OwnerPredicted)
                    {
                        if (!DstEntityManager.HasComponent<GhostOwnerComponent>(entity))
                            throw new InvalidOperationException("OwnerPrediction mode can only be used on prefabs which have a GhostOwnerComponent");
                        DstEntityManager.AddComponentData(entity, default(GhostOwnerPredictedComponent));
                    }
                    else if (ghostAuthoring.DefaultGhostMode == GhostAuthoringComponent.GhostMode.Predicted)
                    {
                        DstEntityManager.AddComponentData(entity, default(GhostDefaultPredictedComponent));
                    }
                }
                else if (target == NetcodeConversionTarget.Client)
                {
                    DstEntityManager.AddComponentData(entity, new SnapshotData());
                    DstEntityManager.AddBuffer<SnapshotDataBuffer>(entity);

                    DstEntityManager.AddComponentData(entity, new GhostComponent());
                    if (ghostAuthoring.DefaultGhostMode == GhostAuthoringComponent.GhostMode.Interpolated)
                    {
                        foreach (var comp in components)
                        {
                            var attr = comp.GetManagedType().GetCustomAttribute<GhostComponentAttribute>();
                            if (attr != null && (attr.PrefabType & GhostPrefabType.InterpolatedClient) == 0)
                                DstEntityManager.RemoveComponent(entity, comp);
                        }
                    }
                    else if (ghostAuthoring.DefaultGhostMode == GhostAuthoringComponent.GhostMode.Predicted)
                    {
                        DstEntityManager.AddComponentData(entity, new PredictedGhostComponent());
                        foreach (var comp in components)
                        {
                            var attr = comp.GetManagedType().GetCustomAttribute<GhostComponentAttribute>();
                            if (attr != null && (attr.PrefabType & GhostPrefabType.PredictedClient) == 0)
                                DstEntityManager.RemoveComponent(entity, comp);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Cannot convert a owner predicted ghost as a scene instance");
                    }
                }
                if (ghostAuthoring.OptimizationMode == GhostAuthoringComponent.GhostOptimizationMode.Static)
                    DstEntityManager.AddComponentData(entity, default(GhostSimpleDeltaCompression));
            });
            Entities.WithNone<GhostAuthoringComponent>().ForEach((Transform trans) =>
            {
                if (trans.parent != null && trans.root.gameObject.GetComponent<GhostAuthoringComponent>() != null)
                {
                    var entity = GetPrimaryEntity(trans);
                    DstEntityManager.AddComponentData(entity, default(GhostChildEntityComponent));
                }
            });
        }
    }
}

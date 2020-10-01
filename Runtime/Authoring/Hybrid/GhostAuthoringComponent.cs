using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;
using Unity.Collections;
using UnityEngine;

namespace Unity.NetCode
{
    [DisallowMultipleComponent]
    public class GhostAuthoringComponent : MonoBehaviour
    {
#if UNITY_EDITOR
        private bool _IsRecursive = false;
        void OnValidate()
        {
            if (_IsRecursive)
                return;
            try
            {
                _IsRecursive = true;
                ValidatePrefabId();
            }
            finally
            {
                _IsRecursive = false;
            }
        }

        void ValidatePrefabId()
        {
            if (gameObject.scene.IsValid())
                return;
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

        /// <summary>
        /// Force the ghost conversion to treat this GameObject as if it was a prefab. This is used if you want to pragrammatically create
        /// a ghost prefab as a GameObject and convert it to an Entity prefab with ConvertGameObjectHeirarchy.
        /// </summary>
        [HideInInspector] public bool ForcePrefabConversion;
        public GhostMode DefaultGhostMode = GhostMode.Interpolated;
        public GhostModeMask SupportedGhostModes = GhostModeMask.All;
        public GhostOptimizationMode OptimizationMode = GhostOptimizationMode.Dynamic;
        public int Importance = 1;
        public string prefabId = "";
        public string Name;
    }

    [ConverterVersion("timj", 4)]
    [UpdateInGroup(typeof(GameObjectAfterConversionGroup))]
    public class GhostAuthoringConversion : GameObjectConversionSystem
    {
        private static NetcodeConversionTarget GetConversionTarget(GameObjectConversionSystem system, bool isPrefab)
        {
            // Detect target using build settings (This is used from sub scenes)
#if UNITY_EDITOR
            {
                if (system.TryGetBuildConfigurationComponent<NetCodeConversionSettings>(out var settings))
                {
                    //Debug.LogWarning("BuildSettings conversion for: " + settings.Target);
                    return settings.Target;
                }
            }
#endif

            // Prefabs are always converted as client and server when using convert to entity since they need to have a single blob asset
            if (!isPrefab)
            {
                if (system.DstEntityManager.World.GetExistingSystem<ClientSimulationSystemGroup>() != null)
                    return NetcodeConversionTarget.Client;
                if (system.DstEntityManager.World.GetExistingSystem<ServerSimulationSystemGroup>() != null)
                    return NetcodeConversionTarget.Server;
            }

            return NetcodeConversionTarget.ClientAndServer;
        }

        protected override void OnUpdate()
        {
            using (var context = new BlobAssetComputationContext<int, GhostPrefabMetaData>(BlobAssetStore, 16, Allocator.Temp))
            {
                Entities.ForEach((GhostAuthoringComponent ghostAuthoring) =>
                {
                    bool isPrefab = !ghostAuthoring.gameObject.scene.IsValid() || ghostAuthoring.ForcePrefabConversion;
                    var target = GetConversionTarget(this, isPrefab);
                    // Check if the ghost is valid before starting to process
                    if (String.IsNullOrEmpty(ghostAuthoring.prefabId))
                        throw new InvalidOperationException($"The ghost {ghostAuthoring.gameObject.name} is not a valid prefab, all ghosts must be the top-level GameObject in a prefab. Ghost instances in scenes must be instances of such prefabs and changes should be made on the prefab asset, not the prefab instance");

                    if (!isPrefab && ghostAuthoring.DefaultGhostMode == GhostAuthoringComponent.GhostMode.OwnerPredicted && target != NetcodeConversionTarget.Server)
                        throw new InvalidOperationException($"Cannot convert a owner predicted ghost {ghostAuthoring.Name} as a scene instance");

                    if (!isPrefab && DstEntityManager.World.GetExistingSystem<ClientSimulationSystemGroup>() != null)
                        throw new InvalidOperationException($"The ghost {ghostAuthoring.gameObject.name} cannot be created on the client, either put it in a sub-scene or spawn it on the server only");

                    if (ghostAuthoring.prefabId.Length != 32)
                        throw new InvalidOperationException("Invalid guid for ghost prefab type");

                    // All ghosts should have a linked entity group
                    DeclareLinkedEntityGroup(ghostAuthoring.gameObject);
                    var entity = GetPrimaryEntity(ghostAuthoring);

                    // Generate a ghost type component so the ghost can be identified by mathcing prefab asset guid
                    var ghostType = new GhostTypeComponent();
                    ghostType.guid0 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(0, 8), 16);
                    ghostType.guid1 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(8, 8), 16);
                    ghostType.guid2 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(16, 8), 16);
                    ghostType.guid3 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(24, 8), 16);
                    DstEntityManager.AddComponentData(entity, ghostType);

                    // FIXME: maybe stripping should be individual systems running before this to make sure it can be changed in a way that always triggers a reconvert - and to avoid reflection
                    var components = DstEntityManager.GetComponentTypes(entity);
                    if (target != NetcodeConversionTarget.Client)
                    {
                        // If this ghost should be usable on a server we must add a shared ghost type to make sure different ghost types
                        // with the same archetype end up in different chunks. If conversion is client and server the client needs to remove
                        // this at runtime
                        DstEntityManager.AddSharedComponentData(entity, new SharedGhostTypeComponent{SharedValue = ghostType});
                    }
                    if (target != NetcodeConversionTarget.Server)
                    {
                        // Converting to client or client and server, if client and server this should be stripped from servers at runtime
                        DstEntityManager.AddComponentData(entity, new SnapshotData());
                        DstEntityManager.AddBuffer<SnapshotDataBuffer>(entity);
                    }
                    // All types have the ghost components
                    DstEntityManager.AddComponentData(entity, new GhostComponent());
                    // No need to add the predicted ghost component for interpolated only ghosts if the data is only used by the client
                    if (target != NetcodeConversionTarget.Client || ghostAuthoring.SupportedGhostModes != GhostAuthoringComponent.GhostModeMask.Interpolated)
                        DstEntityManager.AddComponentData(entity, new PredictedGhostComponent());
                    if (target == NetcodeConversionTarget.Server)
                    {
                        // If converting server-only data we can remove all components which are not used on the server
                        foreach (var comp in components)
                        {
                            var attr = comp.GetManagedType().GetCustomAttribute<GhostComponentAttribute>();
                            if (attr != null && (attr.PrefabType & GhostPrefabType.Server) == 0)
                                DstEntityManager.RemoveComponent(entity, comp);
                        }
                    }
                    else if (target == NetcodeConversionTarget.Client)
                    {
                        // If converting client-only data we can remove all components which are not used on the client
                        // If the ghost is interpolated only we can also remove all componens which are not used on interpolated clients,
                        // and if it is predicted only we can remove everything which is not used on predicted clients
                        foreach (var comp in components)
                        {
                            var attr = comp.GetManagedType().GetCustomAttribute<GhostComponentAttribute>();
                            if (attr == null)
                                continue;
                            if ((attr.PrefabType & GhostPrefabType.Client) == 0)
                                DstEntityManager.RemoveComponent(entity, comp);
                            else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Interpolated && (attr.PrefabType & GhostPrefabType.InterpolatedClient) == 0)
                                DstEntityManager.RemoveComponent(entity, comp);
                            else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Predicted && (attr.PrefabType & GhostPrefabType.PredictedClient) == 0)
                                DstEntityManager.RemoveComponent(entity, comp);
                        }
                    }
                    // Even if converting for client and server we can remove components which are only for predicted clients when
                    // the ghost is always interpolated, or components which are only for interpolated clients if the ghost is always
                    // predicted
                    else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Interpolated)
                    {
                        foreach (var comp in components)
                        {
                            var attr = comp.GetManagedType().GetCustomAttribute<GhostComponentAttribute>();
                            if (attr != null && (attr.PrefabType & (GhostPrefabType.InterpolatedClient | GhostPrefabType.Server)) == 0)
                                DstEntityManager.RemoveComponent(entity, comp);
                        }
                    }
                    else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Predicted)
                    {
                        foreach (var comp in components)
                        {
                            var attr = comp.GetManagedType().GetCustomAttribute<GhostComponentAttribute>();
                            if (attr != null && (attr.PrefabType & (GhostPrefabType.PredictedClient | GhostPrefabType.Server)) == 0)
                                DstEntityManager.RemoveComponent(entity, comp);
                        }
                    }

                    // This logic needs to match the logic creating LinkedEntityGroups, gather a list of all child entities
                    var linkedEntities = new NativeList<Entity>(1, Allocator.Temp);
                    var selfAndChildren = ghostAuthoring.gameObject.GetComponentsInChildren<Transform>(true);
                    foreach (var transform in selfAndChildren)
                    {
                        foreach (var child in GetEntities(transform.gameObject))
                        {
                            if (DstEntityManager.Exists(child))
                                linkedEntities.Add(child);
                        }
                    }
                    // Mark all child entities as ghost children, entity 0 is the root and hsould not be marked
                    for (int i = 1; i < linkedEntities.Length; ++i)
                    {
                        DstEntityManager.AddComponentData(linkedEntities[i], default(GhostChildEntityComponent));
                    }

                    if (isPrefab)
                    {
                        var contentHash = TypeHash.FNV1A64(ghostAuthoring.Importance);
                        contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int)ghostAuthoring.SupportedGhostModes));
                        contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int)ghostAuthoring.DefaultGhostMode));
                        contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int)ghostAuthoring.OptimizationMode));
                        contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64(ghostAuthoring.Name.ToString()));
                        foreach (var comp in components)
                        {
                            contentHash = TypeHash.CombineFNV1A64(contentHash, TypeManager.GetTypeInfo(comp.TypeIndex).StableTypeHash);
                            var attr = comp.GetManagedType().GetCustomAttribute<GhostComponentAttribute>();
                            if (attr != null)
                            {
                                contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int)attr.PrefabType));
                            }
                        }
                        var blobHash = new Unity.Entities.Hash128(ghostType.guid0 ^ (uint)(contentHash>>32), ghostType.guid1 ^ (uint)(contentHash), ghostType.guid2, ghostType.guid3);
                        context.AssociateBlobAssetWithUnityObject(blobHash, ghostAuthoring.gameObject);
                        if (context.NeedToComputeBlobAsset(blobHash))
                        {
                            var builder = new BlobBuilder(Allocator.Temp);
                            ref var root = ref builder.ConstructRoot<GhostPrefabMetaData>();

                            // Store importance, supported modes, default mode and name in the meta data blob asset
                            root.Importance = ghostAuthoring.Importance;
                            root.SupportedModes = GhostPrefabMetaData.GhostMode.Both;
                            root.DefaultMode = GhostPrefabMetaData.GhostMode.Interpolated;
                            if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Interpolated)
                                root.SupportedModes = GhostPrefabMetaData.GhostMode.Interpolated;
                            else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Predicted)
                            {
                                root.SupportedModes = GhostPrefabMetaData.GhostMode.Predicted;
                                root.DefaultMode = GhostPrefabMetaData.GhostMode.Predicted;
                            }
                            else if (ghostAuthoring.DefaultGhostMode == GhostAuthoringComponent.GhostMode.OwnerPredicted)
                            {
                                if (!DstEntityManager.HasComponent<GhostOwnerComponent>(entity))
                                    throw new InvalidOperationException("OwnerPrediction mode can only be used on prefabs which have a GhostOwnerComponent");
                                root.DefaultMode = GhostPrefabMetaData.GhostMode.Both;
                            }
                            else if (ghostAuthoring.DefaultGhostMode == GhostAuthoringComponent.GhostMode.Predicted)
                            {
                                root.DefaultMode = GhostPrefabMetaData.GhostMode.Predicted;
                            }
                            root.StaticOptimization = (ghostAuthoring.OptimizationMode == GhostAuthoringComponent.GhostOptimizationMode.Static);
                            builder.AllocateString(ref root.Name, ghostAuthoring.Name);

                            var serverComponents = new NativeList<ulong>(components.Length, Allocator.Temp);
                            var removeOnServer = new NativeList<ulong>(components.Length, Allocator.Temp);
                            var removeOnClient = new NativeList<ulong>(components.Length, Allocator.Temp);
                            var disableOnPredicted = new NativeList<ulong>(components.Length, Allocator.Temp);
                            var disableOnInterpolated = new NativeList<ulong>(components.Length, Allocator.Temp);

                            // Snapshot data buffers should be removed from the server, and shared ghost type from the client
                            removeOnServer.Add(TypeManager.GetTypeInfo(ComponentType.ReadWrite<SnapshotData>().TypeIndex).StableTypeHash);
                            removeOnServer.Add(TypeManager.GetTypeInfo(ComponentType.ReadWrite<SnapshotDataBuffer>().TypeIndex).StableTypeHash);
                            removeOnClient.Add(TypeManager.GetTypeInfo(ComponentType.ReadWrite<SharedGhostTypeComponent>().TypeIndex).StableTypeHash);

                            // If both interpolated and predicted clients are supported the interpolated client needs to disable the prediction component
                            // If the ghost is interpolated only the prediction component can be removed on clients
                            if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.All)
                                disableOnInterpolated.Add(TypeManager.GetTypeInfo(ComponentType.ReadWrite<PredictedGhostComponent>().TypeIndex).StableTypeHash);
                            else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Interpolated)
                                removeOnClient.Add(TypeManager.GetTypeInfo(ComponentType.ReadWrite<PredictedGhostComponent>().TypeIndex).StableTypeHash);

                            foreach (var comp in components)
                            {
                                var hash = TypeManager.GetTypeInfo(comp.TypeIndex).StableTypeHash;
                                var attr = comp.GetManagedType().GetCustomAttribute<GhostComponentAttribute>();
                                if (attr == null)
                                {
                                    serverComponents.Add(hash);
                                    continue;
                                }
                                if ((attr.PrefabType & GhostPrefabType.Server) == 0)
                                    removeOnServer.Add(hash);
                                else
                                    serverComponents.Add(hash);

                                // If something is not used on the client, remove it. Make sure to include things that is interpolated only if ghost
                                // is predicted only and the other way around
                                if ((attr.PrefabType & GhostPrefabType.Client) == 0)
                                    removeOnClient.Add(hash);
                                else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Interpolated &&
                                    (attr.PrefabType & GhostPrefabType.InterpolatedClient) == 0)
                                    removeOnClient.Add(hash);
                                else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Predicted &&
                                    (attr.PrefabType & GhostPrefabType.PredictedClient) == 0)
                                    removeOnClient.Add(hash);

                                // If the prefab only supports a single mode on the client there is no need to enable / disable, if is handled by the
                                // previous loop removing components on the client instead
                                if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.All)
                                {
                                    // Components available on predicted but not interpolated should be disabled on interpolated clients
                                    if ((attr.PrefabType & GhostPrefabType.InterpolatedClient) == 0 && (attr.PrefabType & GhostPrefabType.PredictedClient) != 0)
                                        disableOnInterpolated.Add(hash);
                                    if ((attr.PrefabType & GhostPrefabType.InterpolatedClient) != 0 && (attr.PrefabType & GhostPrefabType.PredictedClient) == 0)
                                        disableOnPredicted.Add(hash);
                                }
                            }
                            var blobServerComponents = builder.Allocate(ref root.ServerComponentList, serverComponents.Length);
                            for (int i = 0; i < serverComponents.Length; ++i)
                                blobServerComponents[i] = serverComponents[i];

                            // A pre-spawned instance can be created in ClientServer even if the prefab is not, so anything which should
                            // be usable on the server needs to know what to remove from the server version
                            if (target != NetcodeConversionTarget.Client)
                            {
                                // Client only data never needs information about the server
                                var blobRemoveOnServer = builder.Allocate(ref root.RemoveOnServer, removeOnServer.Length);
                                for (int i = 0; i < removeOnServer.Length; ++i)
                                    blobRemoveOnServer[i] = removeOnServer[i];
                            }
                            else
                                builder.Allocate(ref root.RemoveOnServer, 0);
                            if (target != NetcodeConversionTarget.Server)
                            {
                                var blobRemoveOnClient = builder.Allocate(ref root.RemoveOnClient, removeOnClient.Length);
                                for (int i = 0; i < removeOnClient.Length; ++i)
                                    blobRemoveOnClient[i] = removeOnClient[i];
                            }
                            else
                                builder.Allocate(ref root.RemoveOnClient, 0);

                            if (target != NetcodeConversionTarget.Server)
                            {
                                // The data for interpolated / predicted diff is required unless this is server-only
                                var blobDisableOnPredicted = builder.Allocate(ref root.DisableOnPredictedClient, disableOnPredicted.Length);
                                for (int i = 0; i < disableOnPredicted.Length; ++i)
                                    blobDisableOnPredicted[i] = disableOnPredicted[i];
                                var blobDisableOnInterpolated = builder.Allocate(ref root.DisableOnInterpolatedClient, disableOnInterpolated.Length);
                                for (int i = 0; i < disableOnInterpolated.Length; ++i)
                                    blobDisableOnInterpolated[i] = disableOnInterpolated[i];
                            }
                            else
                            {
                                builder.Allocate(ref root.DisableOnPredictedClient, 0);
                                builder.Allocate(ref root.DisableOnInterpolatedClient, 0);
                            }

                            var blobAsset = builder.CreateBlobAssetReference<GhostPrefabMetaData>(Allocator.Persistent);
                            context.AddComputedBlobAsset(blobHash, blobAsset);
                        }
                        context.GetBlobAsset(blobHash, out var blob);
                        DstEntityManager.AddComponentData(entity, new GhostPrefabMetaDataComponent {Value = blob});
                    }
                });
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    public class GhostAuthoringComponent : MonoBehaviour
    {
        [Serializable]
        public struct GhostComponentField
        {
            public string name;
            public int quantization;
            public bool interpolate;
            internal FieldInfo field;

            public FieldInfo Field
            {
                get { return field; }
                set { field = value; }
            }
        }

        [Serializable]
        public struct GhostComponent
        {
            public string name;
            public bool interpolatedClient;
            public bool predictedClient;
            public bool server;
            public ClientSendType sendDataTo;
            public GhostComponentField[] fields;
            public bool manualFieldList;
            public int entityIndex;
            internal string namespaceName;

            public string NamespaceName
            {
                get { return namespaceName; }
                set { namespaceName = value; }
            }

            internal string shortName;

            public string ShortName
            {
                get { return shortName; }
                set { shortName = value; }
            }
        }

        public string prefabId = "";

#if UNITY_EDITOR
        void OnValidate()
        {
            ValidatePrefabId();
        }

        void ValidatePrefabId()
        {
            if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(gameObject) || Application.isPlaying)
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
                prefabId = guid;
        }
#endif

        public enum ClientInstantionType
        {
            Interpolated,
            Predicted,
            OwnerPredicted
        }
        public enum ClientSendType
        {
            All,
            Interpolated,
            Predicted
        }

        public ClientInstantionType DefaultClientInstantiationType = ClientInstantionType.Interpolated;
        public string RootPath = "";
        public string SnapshotDataPath = "";
        public string UpdateSystemPath = "";
        public string SerializerPath = "";
        public string Importance = "1";
        public string PredictingPlayerNetworkId = "";
        public GhostComponent[] Components = new GhostComponent[0];

        [HideInInspector] public bool doNotStrip = false;
    }

    [ConverterVersion("timj", 2)]
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
            var typeLookup = new Dictionary<string, Type>();
            var allTypes = TypeManager.GetAllTypes();
            foreach (var compType in allTypes)
            {
                if (compType.Category == TypeManager.TypeCategory.BufferData &&
                    compType.Type != null && compType.Type.Name.EndsWith("SnapshotData"))
                {
                    if (typeLookup.ContainsKey(compType.Type.Name))
                        throw new InvalidOperationException(
                            $"Found multiple snapshot data types named {compType.Type.Name}, namespaces are not fully supported for snapshot data");
                    typeLookup.Add(compType.Type.Name, compType.Type);
                }
            }

            Entities.ForEach((GhostAuthoringComponent ghostAuthoring) =>
            {
                if (String.IsNullOrEmpty(ghostAuthoring.prefabId))
                    throw new InvalidOperationException($"The ghost {ghostAuthoring.gameObject.name} is not a valid prefab, all ghosts must be the top-level GameObject in a prefab. Ghost instances in scenes must be instances of such prefabs.");
                DeclareLinkedEntityGroup(ghostAuthoring.gameObject);
                if (ghostAuthoring.doNotStrip)
                    return;
                var entity = GetPrimaryEntity(ghostAuthoring);

                var target = GetConversionTarget(this);

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
                var toRemove = new HashSet<string>();
                if (target == NetcodeConversionTarget.Server)
                {
                    // Make sure different ghost types are in different chunks
                    DstEntityManager.AddSharedComponentData(entity, new SharedGhostTypeComponent{SharedValue = ghostType});
                    DstEntityManager.AddComponentData(entity, new GhostComponent());
                    DstEntityManager.AddComponentData(entity, new PredictedGhostComponent());
                    // Create server version of prefab
                    foreach (var comp in ghostAuthoring.Components)
                    {
                        if (!comp.server && comp.entityIndex == 0)
                            toRemove.Add(comp.name);
                    }
                }
                else if (target == NetcodeConversionTarget.Client)
                {
                    var snapshotTypeName = $"{ghostAuthoring.name}SnapshotData";
                    if (!typeLookup.TryGetValue(snapshotTypeName, out var snapshotType))
                    {
                        throw new InvalidOperationException(
                            $"Could not find snapshot data {snapshotTypeName}, did you generate the ghost code?");
                    }

                    DstEntityManager.AddComponent(entity, snapshotType);
                    DstEntityManager.AddComponentData(entity, new GhostComponent());
                    if (ghostAuthoring.DefaultClientInstantiationType ==
                        GhostAuthoringComponent.ClientInstantionType.Interpolated)
                    {
                        foreach (var comp in ghostAuthoring.Components)
                        {
                            if (!comp.interpolatedClient && comp.entityIndex == 0)
                                toRemove.Add(comp.name);
                        }
                    }
                    else
                    {
                        DstEntityManager.AddComponentData(entity, new PredictedGhostComponent());
                        foreach (var comp in ghostAuthoring.Components)
                        {
                            if (!comp.predictedClient && comp.entityIndex == 0)
                                toRemove.Add(comp.name);
                        }

                    }
                }

                // Add list of things to strip based on target world
                // Strip the things in GhostAuthoringConversion
                var components = DstEntityManager.GetComponentTypes(entity);
                foreach (var comp in components)
                {
                    if (toRemove.Contains(comp.GetManagedType().FullName))
                        DstEntityManager.RemoveComponent(entity, comp);
                }

            });
        }
    }
}

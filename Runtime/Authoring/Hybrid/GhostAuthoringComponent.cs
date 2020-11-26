using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Entities;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

#if UNITY_EDITOR
using UnityEditor.Experimental.SceneManagement;
#endif

namespace Unity.NetCode
{
    [DisallowMultipleComponent]
    public class GhostAuthoringComponent : MonoBehaviour, IDeclareReferencedPrefabs, ISerializationCallbackReceiver
    {
        public void DeclareReferencedPrefabs(List<GameObject> referencedPrefabs)
        {
#if UNITY_EDITOR
            bool isPrefab = !gameObject.scene.IsValid() || ForcePrefabConversion;
            if (!isPrefab)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(prefabId);
                GameObject prefab = null;
                if (!String.IsNullOrEmpty(path))
                    prefab = (GameObject)UnityEditor.AssetDatabase.LoadAssetAtPath(path, typeof(GameObject));
                if (prefab != null)
                    referencedPrefabs.Add(prefab);
            }
#endif
        }
#if UNITY_EDITOR
        //Not the fastest way but on average is taking something like 10-50us or less to find the type,
        //so seem reasonably fast even with tens of components per prefab
        Type GetTypeFromFullTypeName(string fullName)
        {
            Type type = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(fullName, false);
                type = t;
                if (type != null)
                    break;
            }
            return type;
        }

        // Remove modifiers is one of the conditions is true:
        // - If a gameobject children is removed
        // - If the component type has been removed or as an attribute that invalidate prefab override
        void ValideModifiers()
        {
            bool RemoveInvalidOverride(in ComponentOverride mod, Transform root)
            {
                if (mod.gameObject == null || !mod.gameObject.transform.IsChildOf(root))
                    return true;
                var compType = GetTypeFromFullTypeName(mod.fullTypeName);
                if (compType == null || compType.GetCustomAttribute<DontSupportPrefabOverrides>() != null)
                    return true;
                return false;
            }

            var parent = transform;
            for (int i=0;i<ComponentOverrides.Count;)
            {
                var mod = ComponentOverrides[i];
                if (RemoveInvalidOverride(mod, parent))
                {
                    ComponentOverrides.RemoveAt(i);
                    //Prefab should be marked as dirty in that case
                    if(!gameObject.scene.IsValid())
                        EditorUtility.SetDirty(gameObject);
                }
                else
                {
                    ++i;
                }
            }
        }
        void OnValidate()
        {
            // Modifiers validation must be done also if scene is valid
            ValideModifiers();

            if (gameObject.scene.IsValid())
                return;

            var path = UnityEditor.AssetDatabase.GetAssetPath(gameObject);
            if (String.IsNullOrEmpty(path))
                return;
            var guid = UnityEditor.AssetDatabase.AssetPathToGUID(path);
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

        [Serializable]
        internal class ComponentOverride
        {
            public const int UseDefaultValue = -1;
            //For sake of serialization we are using the type fullname because we can't rely on the TypeIndex for the component.
            //StableTypeHash cannot be used either because layout or fields changes affect the hash too (so is not a good candidate for that)
            public string fullTypeName;
            //The gameObject reference (root or child)
            public GameObject gameObject;
            //Override what mode are available for that type. if 0, the component is removed from the prefab/entity instance
            public int PrefabType = UseDefaultValue;
            //Override to witch client it will be sent to.
            public int OwnerPredictedSendType = UseDefaultValue;
            public int SendToChild = UseDefaultValue;
            //Select witch variant we would like to use. 0 means the default
            public ulong ComponentVariant;
            //Editor only thing
            public bool isExpanded { get; set; }
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
        internal List<ComponentOverride> ComponentOverrides = new List<ComponentOverride>();

        [Serializable]
        private struct OverrideValue
        {
            public ulong Value;
            public int OverrideIndex;

            public OverrideValue(int key, ulong value)
            {
                OverrideIndex = key;
                Value = value;
            }
        }

        [SerializeField]private GameObject[] RefGameObjects;
        [SerializeField]private string[] ComponentNames;
        [SerializeField]private OverrideValue[] PrefabTypeOverrides;
        [SerializeField]private OverrideValue[] SendTypeOverrides;
        [SerializeField]private OverrideValue[] VariantOverrides;

        //Custom serialization hook for the overrides.
        public void OnBeforeSerialize()
        {
            var t1 = new List<OverrideValue>(ComponentOverrides.Count);
            var t2 = new List<OverrideValue>(ComponentOverrides.Count);
            var t3 = new List<OverrideValue>(ComponentOverrides.Count);
            RefGameObjects = new GameObject[ComponentOverrides.Count];
            ComponentNames = new string[ComponentOverrides.Count];
            int index = 0;
            foreach (var m in ComponentOverrides)
            {
                RefGameObjects[index] = m.gameObject;
                ComponentNames[index] = m.fullTypeName;
                if (m.PrefabType != ComponentOverride.UseDefaultValue)
                    t1.Add(new OverrideValue(index, (ulong)m.PrefabType));
                if (m.OwnerPredictedSendType != ComponentOverride.UseDefaultValue)
                    t2.Add( new OverrideValue(index, (ulong)m.OwnerPredictedSendType));
                if(m.ComponentVariant != 0)
                    t3.Add(new OverrideValue(index, m.ComponentVariant));
                ++index;
            }
            PrefabTypeOverrides = t1.ToArray();
            SendTypeOverrides = t2.ToArray();
            VariantOverrides = t3.ToArray();
        }

        public void OnAfterDeserialize()
        {
            if (RefGameObjects == null)
                return;

            int count = RefGameObjects.Length;
            ComponentOverrides.Clear();
            ComponentOverrides.Capacity = RefGameObjects.Length;

            for (int i = 0; i < count; ++i)
            {
                var refGameObject = RefGameObjects[i];
                var typeFullName = ComponentNames[i];
                ComponentOverrides.Add(new ComponentOverride
                {
                    gameObject = refGameObject,
                    fullTypeName = typeFullName,
                });
            }

            foreach (var p in PrefabTypeOverrides)
            {
                ComponentOverrides[p.OverrideIndex].PrefabType = (int)p.Value;
            }
            foreach (var p in SendTypeOverrides)
            {
                ComponentOverrides[p.OverrideIndex].OwnerPredictedSendType = (int)p.Value;
            }
            foreach (var p in VariantOverrides)
            {
                ComponentOverrides[p.OverrideIndex].ComponentVariant = p.Value;
            }
        }
    }

    [ConverterVersion("timj", 6)]
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
                    var gameObject = ghostAuthoring.gameObject;
                    //There are some issue with conversion at runtime in some occasions we cannot use PrefabStage checks or similar here
                    bool isPrefab = !gameObject.scene.IsValid() || ghostAuthoring.ForcePrefabConversion;
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
                    var rootEntity = GetPrimaryEntity(ghostAuthoring);

                    // Generate a ghost type component so the ghost can be identified by mathcing prefab asset guid
                    var ghostType = new GhostTypeComponent();
                    ghostType.guid0 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(0, 8), 16);
                    ghostType.guid1 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(8, 8), 16);
                    ghostType.guid2 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(16, 8), 16);
                    ghostType.guid3 = Convert.ToUInt32(ghostAuthoring.prefabId.Substring(24, 8), 16);
                    DstEntityManager.AddComponentData(rootEntity, ghostType);

                    // FIXME: maybe stripping should be individual systems running before this to make sure it can be changed in a way that always triggers a reconvert - and to avoid reflection
                    if (target != NetcodeConversionTarget.Client)
                    {
                        // If this ghost should be usable on a server we must add a shared ghost type to make sure different ghost types
                        // with the same archetype end up in different chunks. If conversion is client and server the client needs to remove
                        // this at runtime
                        DstEntityManager.AddSharedComponentData(rootEntity, new SharedGhostTypeComponent {SharedValue = ghostType});
                    }
                    // All types have the ghost components
                    DstEntityManager.AddComponentData(rootEntity, new GhostComponent());
                    // No need to add the predicted ghost component for interpolated only ghosts if the data is only used by the client
                    if (target != NetcodeConversionTarget.Client || ghostAuthoring.SupportedGhostModes != GhostAuthoringComponent.GhostModeMask.Interpolated)
                        DstEntityManager.AddComponentData(rootEntity, new PredictedGhostComponent());

                    var selfAndChildren = ghostAuthoring.gameObject.GetComponentsInChildren<Transform>(true);
                    // This logic needs to match the logic creating LinkedEntityGroups, gather a list of all child entities
                    var linkedEntities = new NativeList<Entity>(selfAndChildren.Length, Allocator.Temp);

                    var componentCounts = new NativeList<int>(selfAndChildren.Length, Allocator.Temp);
                    var instanceIds = new NativeList<int>(selfAndChildren.Length, Allocator.Temp);

                    foreach (var transform in selfAndChildren)
                    {
                        foreach (var child in GetEntities(transform.gameObject))
                        {
                            if (DstEntityManager.Exists(child))
                            {
                                linkedEntities.Add(child);
                                //Each child entity is bound to the same gameobject. This is why we set the same istanceID
                                instanceIds.Add(transform.gameObject.GetInstanceID());
                            }
                        }
                    }

                    var rootComponents = DstEntityManager.GetComponentTypes(rootEntity);
                    // Collects all hierarchy components
                    var allComponents = new NativeList<ComponentType>(rootComponents.Length*linkedEntities.Length, Allocator.Temp);
                    allComponents.AddRange(rootComponents);
                    componentCounts.Add(rootComponents.Length);

                    // Mark all child entities as ghost children, entity 0 is the root and should not have the GhostChildEntityComponent
                    for (int i = 1; i < linkedEntities.Length; ++i)
                    {
                        DstEntityManager.AddComponentData(linkedEntities[i], default(GhostChildEntityComponent));
                        var childComponents = DstEntityManager.GetComponentTypes(linkedEntities[i]);
                        allComponents.AddRange(childComponents);
                        componentCounts.Add(childComponents.Length);
                    }

                    //PrefabTypes is not part of the ghost metadata blob. But it is computed and stored in this array
                    //to simplify the subsequent logics. This value depend on serialization variant selected for the type
                    var prefabTypes = new NativeArray<GhostPrefabType>(allComponents.Length, Allocator.Temp);
                    var sendMasksOverride = new NativeArray<int>(allComponents.Length, Allocator.Temp);
                    var sendToChildOverride = new NativeArray<int>(allComponents.Length, Allocator.Temp);
                    var variants = new NativeArray<ulong>(allComponents.Length, Allocator.Temp);
                    var entities = new NativeArray<Entity>(allComponents.Length, Allocator.Temp);
                    //Keep track of any component that should be removed on the client. Used later
                    //to check if we need to add a DynamicSnapshotData component to the client ghost.
                    //This simply the logic a little since this depend on the current serialization variant
                    //choosen for the component
                    var removedFromClient = new NativeArray<bool>(allComponents.Length, Allocator.Temp);

                    // Setup all components GhostType, variants, sendMask and sendToChild arrays. Used later to mark components to be added or removed.
                    var compIdx = 0;
                    for (int k = 0; k < linkedEntities.Length; ++k)
                    {
                        var instanceId = instanceIds[k];
                        var numComponents = componentCounts[k];
                        var ent = linkedEntities[k];
                        for (int i = 0; i < numComponents; ++i, ++compIdx)
                        {
                            entities[compIdx] = ent;
                            var compFullTypeName = allComponents[compIdx].GetManagedType().FullName;
                            var modifier = ghostAuthoring.ComponentOverrides.FirstOrDefault(m =>
                                m.fullTypeName == compFullTypeName && m.gameObject.GetInstanceID() == instanceId);

                            //Initializathe the value with common default and they overwrite them in case is necessary.
                            prefabTypes[compIdx] = GhostPrefabType.All;
                            variants[compIdx] = 0;
                            sendMasksOverride[compIdx] = GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                            sendToChildOverride[compIdx] = GhostAuthoringComponent.ComponentOverride.UseDefaultValue;

                            // Always check the presence of DontSupportPrefabOverrides at conversion time. It is possible that
                            // some modifiers couldn't be removed if the prefab hasn't been refreshed.
                            // If the followint check pass, a warning will be reported and the overrides ignored.
                            var disableModifier = allComponents[compIdx].GetManagedType().GetCustomAttribute<DontSupportPrefabOverrides>();
                            if (disableModifier != null && modifier != null)
                            {
                                // This log is not reported in the editor but in Logs/AssetImportWorkerXXX logs
                                Debug.LogWarning(
                                    $"{ghostAuthoring.Name} has modifiers for {compFullTypeName} but the component has a [DontSupportPrefabOverrides] attribute.\n" +
                                    "The modifier will be ignored, please update the GhostAuthoringComponent configuration");
                                modifier = null;
                            }
                            //Initialize the commond default and then overwrite in case
                            if (modifier != null)
                            {
                                variants[compIdx] = modifier.ComponentVariant;
                                var attr = GetVariantGhostAttribute(allComponents[compIdx].GetManagedType(), modifier.ComponentVariant);
                                //Only override the the default if the property is meant to (so always check for UseDefaultValue first)
                                if (modifier.PrefabType != GhostAuthoringComponent.ComponentOverride.UseDefaultValue)
                                    prefabTypes[compIdx] = (GhostPrefabType) modifier.PrefabType;
                                else if (attr != null)
                                    prefabTypes[compIdx] = attr.PrefabType;
                                if (modifier.OwnerPredictedSendType != GhostAuthoringComponent.ComponentOverride.UseDefaultValue)
                                    sendMasksOverride[compIdx] = modifier.OwnerPredictedSendType;
                                if (modifier.SendToChild != GhostAuthoringComponent.ComponentOverride.UseDefaultValue)
                                    sendToChildOverride[compIdx] = modifier.SendToChild;
                            }
                            else
                            {
                                var attr = GetGhostAttribute(allComponents[compIdx].GetManagedType());
                                if (attr != null)
                                    prefabTypes[compIdx] = attr.PrefabType;
                            }
                        }
                    }

                    if (target == NetcodeConversionTarget.Server)
                    {
                        // If converting server-only data we can remove all components which are not used on the server
                        for (int i=0;i< allComponents.Length;++i)
                        {
                            var comp = allComponents[i];
                            var prefabType = prefabTypes[i];
                            if((prefabType & GhostPrefabType.Server) == 0)
                            {
                                DstEntityManager.RemoveComponent(entities[i], comp);
                                if(typeof(ICommandData).IsAssignableFrom(comp.GetManagedType()))
                                    Debug.LogWarning($"{ghostAuthoring.gameObject.name}: ICommandData {comp} is configured to be present only on client ghosts. Will be removed from from the server target");
                            }
                        }
                    }
                    else if (target == NetcodeConversionTarget.Client)
                    {
                        // If converting client-only data we can remove all components which are not used on the client
                        // If the ghost is interpolated only we can also remove all componens which are not used on interpolated clients,
                        // and if it is predicted only we can remove everything which is not used on predicted clients
                        for (int i=0;i< allComponents.Length;++i)
                        {
                            var comp = allComponents[i];
                            var prefabType = prefabTypes[i];
                            if (prefabType == GhostPrefabType.All)
                                continue;
                            if(typeof(ICommandData).IsAssignableFrom(comp.GetManagedType()))
                            {
                                if ((prefabType & GhostPrefabType.Client) == 0)
                                    Debug.LogWarning($"{ghostAuthoring.gameObject.name}: ICommandData {comp} is configured to be present only on the server. Will be removed from from the client target");
                                else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Predicted && (prefabType & GhostPrefabType.PredictedClient) == 0)
                                    Debug.LogWarning($"{ghostAuthoring.gameObject.name}: ICommandData {comp} is configured to be present only on interpolated ghost. Will be removed from the client target");
                            }

                            if ((prefabType & GhostPrefabType.Client) == 0)
                            {
                                DstEntityManager.RemoveComponent(entities[i], comp);
                                removedFromClient[i] = true;
                            }
                            else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Interpolated && (prefabType & GhostPrefabType.InterpolatedClient) == 0)
                            {
                                DstEntityManager.RemoveComponent(entities[i], comp);
                                removedFromClient[i] = true;
                            }
                            else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Predicted && (prefabType & GhostPrefabType.PredictedClient) == 0)
                            {
                                DstEntityManager.RemoveComponent(entities[i], comp);
                                removedFromClient[i] = true;
                            }

                        }
                    }
                    // Even if converting for client and server we can remove components which are only for predicted clients when
                    // the ghost is always interpolated, or components which are only for interpolated clients if the ghost is always
                    // predicted
                    else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Interpolated)
                    {
                        for (int i=0;i< allComponents.Length;++i)
                        {
                            var comp = allComponents[i];
                            var prefabType = prefabTypes[i];
                            if ((prefabType & (GhostPrefabType.InterpolatedClient | GhostPrefabType.Server)) == 0)
                            {
                                DstEntityManager.RemoveComponent(entities[i], comp);
                                removedFromClient[i] = true;
                            }

                        }
                    }
                    else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Predicted)
                    {
                        for (int i=0;i< allComponents.Length;++i)
                        {
                            var comp = allComponents[i];
                            var prefabType = prefabTypes[i];
                            if ((prefabType & (GhostPrefabType.PredictedClient | GhostPrefabType.Server)) == 0)
                            {
                                DstEntityManager.RemoveComponent(entities[i], comp);
                                removedFromClient[i] = true;
                                if(typeof(ICommandData).IsAssignableFrom(comp.GetManagedType()))
                                    Debug.LogWarning($"{ghostAuthoring.gameObject.name}: ICommandData {comp} is configured to be present only on interpolated ghost. Will be removed from the client and server target");
                            }
                        }
                    }
                    else
                    {
                        for (int i=0;i< allComponents.Length;++i)
                        {
                            var comp = allComponents[i];
                            var prefabType = prefabTypes[i];
                            if (prefabType == 0)
                            {
                                DstEntityManager.RemoveComponent(entities[i], comp);
                                removedFromClient[i] = true;
                            }
                        }
                    }

                    var hasBuffers = false;
                    //Check if the entity has any buffers left and SnapshotDynamicData buffer to for client. Must be stripped on server
                    if (target != NetcodeConversionTarget.Server)
                    {
                        //if the prefab does not support at least one client mode (is server only) then there is no reason to add the dynamic buffer snapshot
                        //Need to conside the variant serialization, that is why is using the removedFromClient
                        for (int i = 0; i < allComponents.Length && !hasBuffers; ++i)
                            hasBuffers |= (allComponents[i].IsBuffer && !removedFromClient[i]) && (prefabTypes[i] & GhostPrefabType.Client) != 0;
                        // Converting to client or client and server, if client and server this should be stripped from servers at runtime
                        DstEntityManager.AddComponentData(rootEntity, new SnapshotData());
                        DstEntityManager.AddBuffer<SnapshotDataBuffer>(rootEntity);
                        if(hasBuffers)
                            DstEntityManager.AddBuffer<SnapshotDynamicDataBuffer>(rootEntity);
                    }

                    if (isPrefab)
                    {
                        var contentHash = TypeHash.FNV1A64(ghostAuthoring.Importance);
                        contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int)ghostAuthoring.SupportedGhostModes));
                        contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int)ghostAuthoring.DefaultGhostMode));
                        contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int)ghostAuthoring.OptimizationMode));
                        contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64(ghostAuthoring.Name));
                        for (int i=0;i< rootComponents.Length;++i)
                        {
                            var comp = rootComponents[i];
                            var prefabType = prefabTypes[i];
                            contentHash = TypeHash.CombineFNV1A64(contentHash, TypeManager.GetTypeInfo(comp.TypeIndex).StableTypeHash);
                            contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int)prefabType));
                        }

                        compIdx = componentCounts[0];
                        for (int i=1;i<linkedEntities.Length;++i)
                        {
                            contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64(i));
                            var numComponent = componentCounts[i];
                            for (int k = 0; k < numComponent; ++k, ++compIdx)
                            {
                                var comp = allComponents[compIdx];
                                var prefabType = prefabTypes[compIdx];
                                contentHash = TypeHash.CombineFNV1A64(contentHash, TypeManager.GetTypeInfo(comp.TypeIndex).StableTypeHash);
                                contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int)prefabType));
                            }
                        }

                        var blobHash = new Unity.Entities.Hash128(ghostType.guid0 ^ (uint)(contentHash>>32), ghostType.guid1 ^ (uint) (contentHash), ghostType.guid2, ghostType.guid3);
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
                                if (!DstEntityManager.HasComponent<GhostOwnerComponent>(rootEntity))
                                    throw new InvalidOperationException("OwnerPrediction mode can only be used on prefabs which have a GhostOwnerComponent");
                                root.DefaultMode = GhostPrefabMetaData.GhostMode.Both;
                            }
                            else if (ghostAuthoring.DefaultGhostMode == GhostAuthoringComponent.GhostMode.Predicted)
                            {
                                root.DefaultMode = GhostPrefabMetaData.GhostMode.Predicted;
                            }
                            root.StaticOptimization = (ghostAuthoring.OptimizationMode == GhostAuthoringComponent.GhostOptimizationMode.Static);
                            builder.AllocateString(ref root.Name, ghostAuthoring.Name);

                            var serverComponents = new NativeList<ulong>(allComponents.Length, Allocator.Temp);
                            var serverVariants = new NativeList<ulong>(allComponents.Length, Allocator.Temp);
                            var serverSendMasks = new NativeList<int>(allComponents.Length, Allocator.Temp);
                            var serverSendToChild = new NativeList<int>(allComponents.Length, Allocator.Temp);
                            var removeOnServer = new NativeList<GhostPrefabMetaData.ComponentReference>(allComponents.Length, Allocator.Temp);
                            var removeOnClient = new NativeList<GhostPrefabMetaData.ComponentReference>(allComponents.Length, Allocator.Temp);
                            var disableOnPredicted = new NativeList<GhostPrefabMetaData.ComponentReference>(allComponents.Length, Allocator.Temp);
                            var disableOnInterpolated = new NativeList<GhostPrefabMetaData.ComponentReference>(allComponents.Length, Allocator.Temp);

                            // Snapshot data buffers should be removed from the server, and shared ghost type from the client
                            removeOnServer.Add(new GhostPrefabMetaData.ComponentReference(0,TypeManager.GetTypeInfo(ComponentType.ReadWrite<SnapshotData>().TypeIndex).StableTypeHash));
                            removeOnServer.Add(new GhostPrefabMetaData.ComponentReference(0,TypeManager.GetTypeInfo(ComponentType.ReadWrite<SnapshotDataBuffer>().TypeIndex).StableTypeHash));
                            removeOnClient.Add(new GhostPrefabMetaData.ComponentReference(0,TypeManager.GetTypeInfo(ComponentType.ReadWrite<SharedGhostTypeComponent>().TypeIndex).StableTypeHash));
                            if(hasBuffers)
                                removeOnServer.Add(new GhostPrefabMetaData.ComponentReference(0, TypeManager.GetTypeInfo(ComponentType.ReadWrite<SnapshotDynamicDataBuffer>().TypeIndex).StableTypeHash));

                            // If both interpolated and predicted clients are supported the interpolated client needs to disable the prediction component
                            // If the ghost is interpolated only the prediction component can be removed on clients
                            if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.All)
                                disableOnInterpolated.Add(new GhostPrefabMetaData.ComponentReference(0,TypeManager.GetTypeInfo(ComponentType.ReadWrite<PredictedGhostComponent>().TypeIndex).StableTypeHash));
                            else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Interpolated)
                                removeOnClient.Add(new GhostPrefabMetaData.ComponentReference(0,TypeManager.GetTypeInfo(ComponentType.ReadWrite<PredictedGhostComponent>().TypeIndex).StableTypeHash));

                            compIdx = 0;
                            var blobNumServerComponentsPerEntity = builder.Allocate(ref root.NumServerComponentsPerEntity, linkedEntities.Length);
                            for (int k = 0; k < linkedEntities.Length; ++k)
                            {
                                int prevCount = serverComponents.Length;
                                var numComponents = componentCounts[k];
                                for (int i=0;i<numComponents;++i, ++compIdx)
                                {
                                    var comp = allComponents[compIdx];
                                    var prefabType = prefabTypes[compIdx];
                                    var hash = TypeManager.GetTypeInfo(comp.TypeIndex).StableTypeHash;
                                    if (prefabType == GhostPrefabType.All)
                                    {
                                        serverComponents.Add(hash);
                                        serverSendMasks.Add(sendMasksOverride[compIdx]);
                                        serverSendToChild.Add(sendToChildOverride[compIdx]);
                                        serverVariants.Add(variants[compIdx]);
                                        continue;
                                    }

                                    bool isCommandData = typeof(ICommandData).IsAssignableFrom(comp.GetManagedType());
                                    if (isCommandData)
                                    {
                                        //report warning for some configuration that imply stripping the component from some variants
                                        if ((prefabType & GhostPrefabType.Server) == 0)
                                            Debug.LogWarning($"{ghostAuthoring.gameObject.name}: ICommandData {comp} is configured to be present only on the clients. Will be removed from server ghost prefab");
                                        if ((prefabType & GhostPrefabType.Client) == 0)
                                            Debug.LogWarning($"{ghostAuthoring.gameObject.name}: ICommandData {comp} is configured to be present only on the server. Will be removed from from the client ghost prefab");
                                        else if (prefabType == GhostPrefabType.InterpolatedClient)
                                            Debug.LogWarning($"{ghostAuthoring.gameObject.name}: ICommandData {comp} is configured to be present only on interpolated ghost. Will be removed from the server and predicted ghost prefab");
                                        //Check the disabled components for potential and reportor warning for some cases
                                        if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.All)
                                        {
                                            if ((prefabType & GhostPrefabType.InterpolatedClient) != 0 && (prefabType & GhostPrefabType.PredictedClient) == 0)
                                                Debug.LogWarning($"{ghostAuthoring.gameObject.name}: ICommandData {comp} is configured to be present only on interpolated ghost. Will be disabled on predicted ghost after spawning");
                                        }
                                    }
                                    if ((prefabType & GhostPrefabType.Server) == 0)
                                        removeOnServer.Add(new GhostPrefabMetaData.ComponentReference(k,hash));
                                    else
                                    {
                                        serverComponents.Add(hash);
                                        serverSendMasks.Add(sendMasksOverride[compIdx]);
                                        serverSendToChild.Add(sendToChildOverride[compIdx]);
                                        serverVariants.Add(variants[compIdx]);
                                    }

                                    // If something is not used on the client, remove it. Make sure to include things that is interpolated only if ghost
                                    // is predicted only and the other way around
                                    if ((prefabType & GhostPrefabType.Client) == 0)
                                        removeOnClient.Add(new GhostPrefabMetaData.ComponentReference(k,hash));
                                    else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Interpolated && (prefabType & GhostPrefabType.InterpolatedClient) == 0)
                                        removeOnClient.Add(new GhostPrefabMetaData.ComponentReference(k,hash));
                                    else if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.Predicted && (prefabType & GhostPrefabType.PredictedClient) == 0)
                                        removeOnClient.Add(new GhostPrefabMetaData.ComponentReference(k,hash));

                                    // If the prefab only supports a single mode on the client there is no need to enable / disable, if is handled by the
                                    // previous loop removing components on the client instead
                                    if (ghostAuthoring.SupportedGhostModes == GhostAuthoringComponent.GhostModeMask.All)
                                    {
                                        // Components available on predicted but not interpolated should be disabled on interpolated clients
                                        if ((prefabType & GhostPrefabType.InterpolatedClient) == 0 && (prefabType & GhostPrefabType.PredictedClient) != 0)
                                            disableOnInterpolated.Add(new GhostPrefabMetaData.ComponentReference(k,hash));
                                        if ((prefabType & GhostPrefabType.InterpolatedClient) != 0 && (prefabType & GhostPrefabType.PredictedClient) == 0)
                                            disableOnPredicted.Add(new GhostPrefabMetaData.ComponentReference(k,hash));
                                    }
                                }
                                blobNumServerComponentsPerEntity[k] = serverComponents.Length - prevCount;
                            }
                            var blobServerComponents = builder.Allocate(ref root.ServerComponentList, serverComponents.Length);
                            for (int i = 0; i < serverComponents.Length; ++i)
                            {
                                blobServerComponents[i].StableHash = serverComponents[i];
                                blobServerComponents[i].Variant = serverVariants[i];
                                blobServerComponents[i].SendMaskOverride = serverSendMasks[i];
                                blobServerComponents[i].SendToChildEntityOverride = serverSendToChild[i];
                            }

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
                        DstEntityManager.AddComponentData(rootEntity, new GhostPrefabMetaDataComponent {Value = blob});
                        if (target == NetcodeConversionTarget.ClientAndServer)
                            // Flag this prefab as needing runtime stripping
                            DstEntityManager.AddComponentData(rootEntity, new GhostPrefabRuntimeStrip());
                    }
                });
            }
        }

        //Need to consider also the attribute from the override
        private static GhostComponentAttribute GetGhostAttribute(Type managedType)
        {
            if (GhostAuthoringModifiers.GhostDefaultOverrides.TryGetValue(managedType.FullName, out var ghostOverride))
                return ghostOverride.attribute;
            return managedType.GetCustomAttribute<GhostComponentAttribute>();
        }
        //Return the serialization variant ghostcomponent attribute if present or the the default for the type
        private static GhostComponentAttribute GetVariantGhostAttribute(Type componentType, ulong variantHash)
        {
            if (variantHash != 0 && GhostAuthoringModifiers.VariantsCache.TryGetValue(componentType.FullName, out var variantList))
            {
                var variant = variantList.FirstOrDefault(v => v.Attribute.VariantHash == variantHash);
                return GetGhostAttribute(variant.VariantType);
            }
            return GetGhostAttribute(componentType);
        }
    }
}

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.NotBurstCompatible;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace Unity.Netcode.Editor
{
    /// <summary>
    /// Extract from the prefab the converted entities components, in respect to the selected variant and default
    /// mapping provided by the user
    /// </summary>
    class EntityPrefabComponentsPreview
    {
        struct ComponentNameComparer : IComparer<ComponentType>
        {
            public int Compare(ComponentType x, ComponentType y) =>
                string.Compare(x.GetManagedType().FullName, y.GetManagedType().FullName, StringComparison.Ordinal);
        }

        /// <summary>Triggers the baking conversion process on the 'authoringComponent' and appends all resulting baked entities and components to the 'bakedDataMap'.</summary>
        public void BakeEntireNetcodePrefab(BaseGhostSettings ghostAuthoring, GhostAuthoringInspectionComponent inspectionComponent, Dictionary<GhostAuthoringInspectionComponent, BakedResult> cachedBakedResults)
        {
            GhostAuthoringInspectionComponent.forceBake = false;
            if (ghostAuthoring == null)
            {
                Debug.LogError($"Attempting to bake `GhostAuthoringInspectionComponent` '{inspectionComponent.name}', but no root `GhostAuthoringComponent` found!");
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar($"Baking '{ghostAuthoring}'...", "Baking triggered by the GhostAuthoringInspectionComponent.", .9f);

                // TODO - Handle exceptions due to invalid prefab setup. E.g.
                // "InvalidOperationException: OwnerPrediction mode can only be used on prefabs which have a GhostOwner"
                using var tempWorld = new NetcodeWorld(nameof(EntityPrefabComponentsPreview));
                using var blobAssetStore = new BlobAssetStore(128);
                ghostAuthoring.ForcePrefabConversion = true;
                var bakingSettings = new BakingSettings(BakingUtility.BakingFlags.AddEntityGUID, blobAssetStore);

                Entity primaryEntity;
                World existingWorld = null;
                var reusedRegisteredPrefab = false;

                var isGhostObject = false;
                if (ghostAuthoring is GhostObject ghostObject)
                {
                    isGhostObject = true;
                    // Check if this GhostObject is an instance that was created from an already registered prefab
                    var registeredLink = GetRegisteredLinkForPrefab(ghostObject);
                    if (registeredLink.WasInitialized)
                    {
                        // If so, use the values from the already registered prefab
                        primaryEntity = registeredLink.Entity;
                        existingWorld = registeredLink.World.EntityManager.World;
                        // Mark this as a reused prefab so we can ensure the inspector is greyed out.
                        reusedRegisteredPrefab = true;
                    }
                    else
                    {
                        BakingUtility.PrepareWorldForBaking(tempWorld, bakingSettings); // initializes a few required singletons like GhostComponentSerializerCollectionData
                        Netcode.RegisterPrefab(ghostObject.gameObject, tempWorld);
                        primaryEntity = ghostObject.Entity;
                    }
                }
                else
                {
                    BakingUtility.BakeGameObjects(tempWorld, new[] { ghostAuthoring.gameObject }, bakingSettings);
                    var bakingSystem = tempWorld.GetExistingSystemManaged<BakingSystem>();
                    primaryEntity = bakingSystem.GetEntity(ghostAuthoring.gameObject);
                }

                var bakeResult = new BakedResult
                {
                    GhostAuthoring = ghostAuthoring,
                    GameObjectResults = new(32),
                    ReusedRegisteredPrefab = reusedRegisteredPrefab,
                };

                var world = existingWorld ?? tempWorld;

                var ghostBlobAsset = world.EntityManager.GetComponentData<GhostPrefabMetaData>(primaryEntity).Value;

                // One-shot collection of baker-contributed GhostVariantOverride entries from every linked entity.
                // Targeting is resolved against the host EntityGuid here so the per-component lookup is just field
                // equality. Mirrors the aggregation in GhostAuthoringBakingSystem.ProcessRoot.
                var bakerOverrides = CollectBakerVariantOverrides(world, primaryEntity);

                var primaryEntitiesMap = new HashSet<Entity>(16);

                CreatedBakedResultForPrimaryEntities(bakeResult, world, primaryEntitiesMap, ghostBlobAsset, cachedBakedResults, isGhostObject, bakerOverrides, primaryEntity);
                CreatedBakedResultForAdditionalEntities(bakeResult, world, primaryEntitiesMap, ghostBlobAsset, bakerOverrides);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                GhostAuthoringInspectionComponent.forceRebuildInspector = true;
                ghostAuthoring.ForcePrefabConversion = false;
            }
        }


        internal static int CountComponents(GameObject go)
        {
            return go.GetComponents<Component>().Length;
        }

        static List<GhostVariantBakedOverride> CollectBakerVariantOverrides(World world, Entity rootEntity)
        {
            var collected = new List<GhostVariantBakedOverride>(8);
            if (!world.EntityManager.HasComponent<LinkedEntityGroup>(rootEntity))
                return collected;

            var leg = world.EntityManager.GetBuffer<LinkedEntityGroup>(rootEntity);
            for (int i = 0; i < leg.Length; ++i)
            {
                var linked = leg[i].Value;
                if (!world.EntityManager.HasBuffer<GhostVariantBakedOverride>(linked))
                    continue;
                var hostGuid = world.EntityManager.GetComponentData<EntityGuid>(linked);
                var buf = world.EntityManager.GetBuffer<GhostVariantBakedOverride>(linked);
                for (int j = 0; j < buf.Length; ++j)
                {
                    var ov = buf[j];
                    GhostVariantBakedOverride.ResolveSelfTargeting(ref ov, hostGuid);
                    collected.Add(ov);
                }
            }
            return collected;
        }

        static void CreatedBakedResultForPrimaryEntities(BakedResult bakedResult, World world, HashSet<Entity> primaryEntitiesMap, BlobAssetReference<GhostPrefabBlobMetaData> blobAssetReference, Dictionary<GhostAuthoringInspectionComponent, BakedResult> cachedBakedResults, bool isGhostObject, List<GhostVariantBakedOverride> bakerOverrides, Entity ghostObjectEntity)
        {
            foreach (var t in bakedResult.GhostAuthoring.GetComponentsInChildren<Transform>())
            {
                var go = t.gameObject;

                var sourcePrefabPath = AssetDatabase.GetAssetPath(go);
                var goResult = new BakedGameObjectResult
                {
                    AuthoringRoot = bakedResult,
                    SourceGameObject = go,
                    SourceInspection = go.GetComponent<GhostAuthoringInspectionComponent>(),
                    SourcePrefabPath = sourcePrefabPath,
                    BakedEntities = new List<BakedEntityResult>(2),
                    NumComponents = CountComponents(go),
                };
                var discoveredInspectionComponent = goResult.SourceInspection;
                if (discoveredInspectionComponent != null)
                    cachedBakedResults[discoveredInspectionComponent] = bakedResult;

                Entity primaryEntity;
                if (isGhostObject)
                {
                    // Only for GhostObjects
                    if (bakedResult.GhostAuthoring.gameObject != t.gameObject)
                    {
                        // Breaks the inspector window to bake children of the rootObject
                        // GhostObjects are not allowed to be nested so this is accurate.
                        continue;
                    }

                    // Use the provided already baked entity, because nested GhostObjects are not supported in prefabs
                    primaryEntity = ghostObjectEntity;
                }
                else
                {
                    var bakingSystem = world.GetExistingSystemManaged<BakingSystem>();
                    primaryEntity = bakingSystem.GetEntity(go);
                }
                if (world.EntityManager.Exists(primaryEntity))
                {
                    goResult.BakedEntities.Add(CreateBakedEntityResult(goResult, 0, world, primaryEntity, false, blobAssetReference, bakerOverrides));
                    primaryEntitiesMap.Add(primaryEntity);
                }
                bakedResult.GameObjectResults[go] = goResult;
            }
        }

        static void CreatedBakedResultForAdditionalEntities(BakedResult bakedResult, World world, HashSet<Entity> primaryEntitiesMap, BlobAssetReference<GhostPrefabBlobMetaData> blobAssetReference, List<GhostVariantBakedOverride> bakerOverrides)
        {
            // Note: We only expect the ROOT entity to have a LinkedEntityGroup,
            // but checking EVERY baked GameObject as this is not an assumption we control.
            foreach (var kvp in bakedResult.GameObjectResults)
            {
                // TODO - Test-case to ensure the root entity does not contain ALL linked entities (even for children + additional).
                for (int index = 0, max = kvp.Value.BakedEntities.Count; index < max; index++)
                {
                    var bakedEntityResult = kvp.Value.BakedEntities[index];
                    var primaryEntity = bakedEntityResult.Entity;
                    if (!world.EntityManager.HasComponent<LinkedEntityGroup>(primaryEntity))
                        continue;

                    var linkedEntityGroup = world.EntityManager.GetBuffer<LinkedEntityGroup>(primaryEntity);
                    for (int i = 1; i < linkedEntityGroup.Length; ++i)
                    {
                        var linkedEntity = linkedEntityGroup[i].Value;

                        // Child entities are considered 'primary' entities. Thus, ignore them.
                        // I.e. During Baking, if users call `CreateAdditionalEntity`, it won't be 'primary'.
                        if (primaryEntitiesMap.Contains(linkedEntity))
                            continue;

                        // Find the actual authoring GameObject for this linked entity. It might be one of our children.
                        var foundActualAuthoring = TryGetAuthoringForAdditionalEntity(linkedEntity, world, bakedResult.GameObjectResults.Values, out var actualAuthoring);
                        if (!foundActualAuthoring)
                        {
                            Debug.LogWarning($"Expected to find the source BakedGameObjectResult for Additional Entity '{linkedEntity.ToFixedString()}' ('{world.EntityManager.GetName(linkedEntity)}') (via EntityGuid search), but did not! Assuming the authoring GameObject is '{kvp.Value.SourceGameObject.name}'! Please file a bug report if this assumption is false.", kvp.Value.SourceGameObject);

                            actualAuthoring = kvp.Value;
                        }
                        var entityResult = CreateBakedEntityResult(actualAuthoring, i, world, linkedEntity, true, blobAssetReference, bakerOverrides);
                        actualAuthoring.BakedEntities.Add(entityResult);
                    }
                }
            }
        }

        static bool TryGetAuthoringForAdditionalEntity(Entity additionalEntity, World bakingWorld, Dictionary<GameObject, BakedGameObjectResult>.ValueCollection results, out BakedGameObjectResult found)
        {
            found = default;
            if (!bakingWorld.EntityManager.HasComponent<EntityGuid>(additionalEntity))
            {
                Debug.LogError($"Additional entity '{additionalEntity.ToFixedString()}' did not have an EntityGuid! Thus, cannot find Authoring for it!");
                return false;
            }
            var additionalEntitiesEntityGuid = bakingWorld.EntityManager.GetComponentData<EntityGuid>(additionalEntity);

            foreach (var result in results)
            {
                foreach (var x in result.BakedEntities)
                {
                    if (x.Guid.OriginatingEntityId == additionalEntitiesEntityGuid.OriginatingEntityId)
                    {
                        found = result;
                        return true;
                    }
                }
            }

            return false;
        }

        static BakedEntityResult CreateBakedEntityResult(BakedGameObjectResult authoring, int entityIndex, World world, Entity convertedEntity, bool isLinkedEntity, BlobAssetReference<GhostPrefabBlobMetaData> blobAssetReference, List<GhostVariantBakedOverride> bakerOverrides)
        {
            var guid = world.EntityManager.GetComponentData<EntityGuid>(convertedEntity);
            var result = new BakedEntityResult
            {
                GoParent = authoring,
                Entity = convertedEntity,
                Guid = guid,
                EntityName = world.EntityManager.GetName(convertedEntity),
                EntityIndex = entityIndex,
                BakedComponents = new List<BakedComponentItem>(16),
                IsLinkedEntity = isLinkedEntity,
            };

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostComponentSerializerCollectionData>());
            var collectionData = query.GetSingleton<GhostComponentSerializerCollectionData>();

            AddToComponentList(result, result.BakedComponents, collectionData, world, convertedEntity, entityIndex, blobAssetReference, bakerOverrides);

            var variantTypesList = new NativeList<ComponentTypeSerializationStrategy>(4, Allocator.Temp);
            foreach (var compItem in result.BakedComponents)
            {
                // Resolve with the inspection override when set; otherwise fall back to the context-contributed
                // default (baker override or GhostObject-layer default), so serializationStrategy agrees with
                // defaultSerializationStrategy and the SaveVariant self-heal below doesn't write a spurious override.
                var searchHash = compItem.VariantHash != 0 ? compItem.VariantHash : compItem.ContextDefaultVariantHash;

                variantTypesList.Clear();
                for (int i = 0; i < compItem.availableSerializationStrategies.Length; i++)
                {
                    variantTypesList.Add(compItem.availableSerializationStrategies[i]);
                }
                compItem.serializationStrategy = collectionData.SelectSerializationStrategyForComponentWithHash(ComponentType.ReadWrite(compItem.managedType), searchHash, variantTypesList, result.IsRoot);
                compItem.sendToOwnerType = compItem.serializationStrategy.IsSerialized != 0 ? collectionData.Serializers[compItem.serializationStrategy.SerializerIndex].SendToOwner : SendToOwnerType.None;

                compItem.SaveVariant(true, false);
            }
            variantTypesList.Dispose();
            return result;
        }

        static void AddToComponentList(BakedEntityResult parent, List<BakedComponentItem> newComponents, GhostComponentSerializerCollectionData collectionData, World world, Entity convertedEntity, int entityIndex, BlobAssetReference<GhostPrefabBlobMetaData> blobAssetReference, List<GhostVariantBakedOverride> bakerOverrides)
        {
            var compTypes = world.EntityManager.GetComponentTypes(convertedEntity);
            compTypes.Sort(default(ComponentNameComparer));

            // Store all types:
            for (int i = 0; i < compTypes.Length; ++i)
                CreateBakedComponentItem(compTypes[i]);

            // Store the types that have been removed from BOTH the server and client (as they'd not be found via the above):
            TryAddRemoved(ref blobAssetReference.Value.RemoveOnServerOnlyWorld);
            TryAddRemoved(ref blobAssetReference.Value.RemoveOnClientWorlds);

            void TryAddRemoved(ref BlobArray<GhostPrefabBlobMetaData.ComponentReference> removedArray)
            {
                for (var i = 0; i < removedArray.Length; i++)
                {
                    var removedCompRef = removedArray[i];
                    if (removedCompRef.EntityIndex != entityIndex) continue;
                    var removedComp = ComponentType.FromTypeIndex(TypeManager.GetTypeIndexFromStableTypeHash(removedCompRef.StableHash));

                    foreach (var item in newComponents)
                    {
                        if (IsAlreadyAdded(item, removedComp))
                        {
                            return;
                        }
                    }
                    CreateBakedComponentItem(removedComp);
                }
                return;

                bool IsAlreadyAdded(BakedComponentItem x, ComponentType removedComp) => x.managedType == removedComp.GetManagedType();
            }

            void CreateBakedComponentItem(ComponentType componentType)
            {
                var managedType = componentType.GetManagedType();
                if (managedType == typeof(Prefab) || managedType == typeof(LinkedEntityGroup))
                    return;

                var componentItem = new BakedComponentItem
                {
                    EntityParent = parent,
                    fullname = managedType.FullName,
                    managedType = managedType,
                    entityIndex = entityIndex,
                };

                // Collect baker-contributed overrides for this (entity, component) FIRST so the baker-supplied
                // variant hash can drive defaultVariant below — otherwise the inspector tags the SYSTEM default
                // with "(Default)" even when a baker has changed it.
                if (bakerOverrides.Count > 0)
                {
                    var componentTypeFullNameHash = TypeManager.GetFullNameHash(componentType.TypeIndex);
                    for (int i = 0; i < bakerOverrides.Count; ++i)
                    {
                        var ov = bakerOverrides[i];
                        if (ov.ComponentTypeFullNameHash != componentTypeFullNameHash) continue;
                        if (ov.TargetGameObjectInstanceId != parent.Guid.OriginatingEntityId) continue;
                        if (ov.TargetEntitySerial != parent.Guid.Serial) continue;
                        componentItem.BakerContributedOverrides ??= new List<GhostVariantBakedOverride>(2);
                        componentItem.BakerContributedOverrides.Add(ov);
                        if (componentItem.BakerContributedVariantHash == 0 && ov.VariantHash != 0)
                            componentItem.BakerContributedVariantHash = ov.VariantHash;
                        if (componentItem.BakerContributedPrefabType == GhostVariantBakedOverride.NoPrefabTypeOverride
                            && ov.PrefabType != GhostVariantBakedOverride.NoPrefabTypeOverride)
                            componentItem.BakerContributedPrefabType = ov.PrefabType;
                        if (componentItem.BakerContributedSendType == GhostVariantBakedOverride.NoSendTypeOverride
                            && ov.SendTypeOptimization != GhostVariantBakedOverride.NoSendTypeOverride)
                            componentItem.BakerContributedSendType = ov.SendTypeOptimization;
                    }
                }

                // GhostObject (GameObject-layer) prefabs apply per-prefab default variants at registration time
                // (see GhostObjectVariantDefaults / PrefabRegistry). Mirror them here so the inspector displays the
                // true effective default rather than the global (system) default.
                if (parent.IsRoot && parent.GoParent.RootAuthoring is GhostObject)
                    componentItem.GhostObjectContributedVariantHash = GhostObjectVariantDefaults.GetDefaultVariantOverrideHash(managedType);

                using var availableSs = collectionData.GetAllAvailableSerializationStrategiesForType(managedType, componentItem.VariantHash, parent.IsRoot);
                var canSerializeInAtLeastOneVariant = GhostComponentSerializerCollectionData.AnyVariantsAreSerialized(in availableSs);
                // Pass the context-contributed variant hash (baker override or GO-layer default, 0 if none) so the
                // resolver returns the context's chosen variant as the "default" for this prefab. Falls back to the
                // system default when the context did not contribute one.
                var defaultVariant = collectionData.GetCurrentSerializationStrategyForComponent(managedType, componentItem.ContextDefaultVariantHash, parent.IsRoot);

                // Remove test variants as they cannot be selected:
                for (var j = availableSs.Length - 1; j >= 0; j--)
                {
                    var ss = availableSs[j];
                    if (ss.IsTestVariant != 0)
                        availableSs.RemoveAt(j);
                }

                // Cache the availableVariants names.
                var ssDisplayNames = new string[availableSs.Length];
                for (var j = 0; j < availableSs.Length; j++)
                {
                    var vt = availableSs[j];
                    ssDisplayNames[j] = vt.DisplayName.ToString();
                    if (defaultVariant.Hash == availableSs[j].Hash)
                    {
                        var defaultTag = ComponentTypeSerializationStrategy.GetDefaultDisplayName(defaultVariant.DefaultRule).ToString();
                        // Check if the rule was registered by Netcode
                        if (defaultTag.Length != 0 && (defaultVariant.DefaultRule & ComponentTypeSerializationStrategy.DefaultType.YesAsIsUserSpecifiedNewDefault) != 0)
                        {
                            var registeringSystem =
                                world.GetExistingSystemManaged<GhostComponentSerializerCollectionSystemGroup>()?.DefaultVariantRules?.TryGetRuleRegistrationSystem(ComponentType.ReadWrite(managedType));
                            if (registeringSystem != null && registeringSystem.GetType().Assembly == typeof(TransformDefaultVariantSystem).Assembly)
                                defaultTag = "Netcode Default";
                        }
                        if (defaultTag.Length != 0)
                            ssDisplayNames[j] += $" ({defaultTag})";
                    }
                }

                componentItem.availableSerializationStrategies = availableSs.ToArrayNBC();
                componentItem.availableSerializationStrategyDisplayNames = ssDisplayNames;
                componentItem.anyVariantIsSerialized = canSerializeInAtLeastOneVariant;
                componentItem.defaultSerializationStrategy = defaultVariant;

                newComponents.Add(componentItem);
            }
        }

        /// <summary>
        /// Gets the registered entity information for the prefab of a given ghostObject.
        /// Ensure you check <see cref="GhostEntityMapping.EntityLink.WasInitialized"/> on the returned link.
        /// </summary>
        /// <remarks>
        /// This allows the <see cref="GhostAuthoringInspectionComponentEditor"/> to display the UI for the already baked prefab of this GhostObject instance.
        /// This saves processing as it's only valid to edit the inspector values from a prefab, the instance of that prefab is only ever ephemeral.
        /// </remarks>
        private static GhostEntityMapping.EntityLink GetRegisteredLinkForPrefab(GhostObject ghostObject)
        {
            if (ghostObject == null)
            {
                return default;
            }

            var prefabObj = ghostObject.IsPrefab() ? ghostObject.gameObject : ghostObject.prefabReference.Prefab;
            var prefab = prefabObj.GetComponent<GhostObject>();
            if (prefab == null || prefab.m_CachedEntity == Entity.Null || !prefab.m_CachedWorld.ExistsAndIsCreated())
            {
                return default;
            }
            return new GhostEntityMapping.EntityLink()
            {
                Entity = prefab.m_CachedEntity, World = prefab.m_CachedWorld.Unmanaged
            };
        }
    }
}

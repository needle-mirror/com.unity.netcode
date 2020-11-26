using System;
using Unity.Entities;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    public class GhostCollectionSystem : SystemBase
    {
        struct ComponentHashComparer : IComparer<GhostComponentSerializer.State>
        {
            public int Compare(GhostComponentSerializer.State x, GhostComponentSerializer.State y)
            {
                var hashX = TypeManager.GetTypeInfo(x.ComponentType.TypeIndex).StableTypeHash;
                var hashY = TypeManager.GetTypeInfo(y.ComponentType.TypeIndex).StableTypeHash;

                if (hashX < hashY)
                    return -1;
                if (hashX > hashY)
                    return 1;
                return 0;
            }
        }
        private bool m_ComponentCollectionInitialized;
        private Entity m_CollectionSingleton;
        private List<GhostComponentSerializer.State> m_PendingGhostComponentCollection = new List<GhostComponentSerializer.State>();
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private List<string> m_PredictionErrorNames;
        private List<string> m_GhostNames;
        private int m_PrevPredictionErrorNamesCount;
        private int m_PrevGhostNamesCount;
        #endif

        private EntityQuery m_InGameQuery;

        public void AddSerializer(GhostComponentSerializer.State state)
        {
            //This is always enforced to avoid bad usage of the api
            if (m_ComponentCollectionInitialized)
            {
                throw new InvalidOperationException("Cannot register new GhostComponentSerializer after the RpcSystem has started running");
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            for (int i = 0; i < m_PendingGhostComponentCollection.Count; ++i)
            {
                // FIXME: this is a workaround for fast enter play mode
                if (m_PendingGhostComponentCollection[i].ComponentType == state.ComponentType &&
                    m_PendingGhostComponentCollection[i].VariantHash == state.VariantHash)
                {
                    throw new InvalidOperationException($"GhostComponentSerializer for type {state.ComponentType.GetManagedType().Name} and variant {state.VariantHash} is already registered");
                }
            }
#endif
            //When the state is registered the serializer hash is computed once
            state.SerializerHash = state.ExcludeFromComponentCollectionHash == 0 ? HashGhostComponentSerializer(state) : 0;
            m_PendingGhostComponentCollection.Add(state);
        }

        /// <summary>
        /// Compute the number of uint necessary to encode the required number of bits
        /// </summary>
        /// <param name="numBits"></param>
        /// <returns></returns>
        public static int ChangeMaskArraySizeInUInts(int numBits)
        {
            return (numBits + 31)>>5;
        }
        /// <summary>
        /// Align the give size to 16 byte boundary
        /// </summary>
        /// <param name="size"></param>
        /// <returns></returns>
        public static int SnapshotSizeAligned(int size)
        {
            return (size + 15) & (~15);
        }
        /// <summary>
        /// Align the give size to 16 byte boundary
        /// </summary>
        /// <param name="size"></param>
        /// <returns></returns>
        public static uint SnapshotSizeAligned(uint size)
        {
            return (size + 15u) & (~15u);
        }

        //Hash requirements:
        // R0: if components are different or in different order the hash should change
        // R1: different size, owneroffsets, maskbits, partialcomponents etc must result in a different hash
        // R2: if a ghost present the same components, with the same fields but different [GhostField] attributes (such as, subType, interpoled, composite)
        //     must result in a different hash, even though the resulting serialization sizes and masks are the same
        public ulong CalculateComponentCollectionHash()
        {
            //Lazy create the component collection if not created when we want to compute the component collection hash
            if (!m_ComponentCollectionInitialized)
            {
                CreateComponentCollection();
            }
            ulong componentCollectionHash = 0;
            var ghostComponentCollection = EntityManager.GetBuffer<GhostComponentSerializer.State>(m_CollectionSingleton);
            for (int i = 0; i < ghostComponentCollection.Length; ++i)
            {
                var comp = ghostComponentCollection[i];
                if(comp.SerializerHash !=0)
                {
                    componentCollectionHash = TypeHash.CombineFNV1A64(componentCollectionHash, comp.SerializerHash);
                }
            }
            return componentCollectionHash;
        }

        private ulong HashGhostComponentSerializer(in GhostComponentSerializer.State comp)
        {
            //this will give us a good starting point
            var compHash = TypeManager.GetTypeInfo(comp.ComponentType.TypeIndex).StableTypeHash;
            if (compHash == 0)
                throw new InvalidOperationException($"Unexpected 0 hash for type {comp.ComponentType}");
            compHash = TypeHash.CombineFNV1A64(compHash, comp.GhostFieldsHash);
            //ComponentSize might depend on #ifdef or other compilation/platform rules so it must be not included. we will leave the comment here
            //so it is clear why we don't consider this field
            //compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.ComponentSize));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.SnapshotSize));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.ChangeMaskBits));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64((int)comp.SendToOwner));
            return compHash;
        }

        private ulong HashGhostType(in GhostCollectionPrefabSerializer ghostType)
        {
            ulong ghostTypeHash = ghostType.TypeHash;
            if (ghostTypeHash == 0)
                throw new InvalidOperationException($"Unexpected 0 hash for ghosttype");
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.FirstComponent));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.NumComponents));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.NumChildComponents));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.SnapshotSize));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.ChangeMaskBits));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.PredictionOwnerOffset));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.OwnerPredicted));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.IsGhostGroup));
            return ghostTypeHash;
        }

        protected override void OnCreate()
        {
            RequireSingletonForUpdate<GhostCollection>();
            m_CollectionSingleton = EntityManager.CreateEntity(ComponentType.ReadWrite<GhostCollection>());
            EntityManager.AddBuffer<GhostCollectionPrefab>(m_CollectionSingleton);
            EntityManager.AddBuffer<GhostCollectionPrefabSerializer>(m_CollectionSingleton);
            EntityManager.AddBuffer<GhostCollectionComponentIndex>(m_CollectionSingleton);
            EntityManager.AddBuffer<GhostCollectionPrefab>(m_CollectionSingleton);
            EntityManager.AddBuffer<GhostComponentSerializer.State>(m_CollectionSingleton);
            EntityManager.AddBuffer<GhostCollectionComponentType>(m_CollectionSingleton);

            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_PredictionErrorNames = new List<string>();
            m_GhostNames = new List<string>();
            #endif

            m_InGameQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
        }
        protected override void OnDestroy()
        {
            EntityManager.DestroyEntity(m_CollectionSingleton);
        }
        protected override void OnUpdate()
        {
            if (!m_ComponentCollectionInitialized)
            {
                CreateComponentCollection();
            }

            bool isServer = (World.GetExistingSystem<ServerSimulationSystemGroup>() != null);

            // Perform runtime stripping of all prefabs which need it
            Entities
                .WithStructuralChanges()
                .WithoutBurst()
                .WithAll<GhostPrefabRuntimeStrip>()
                .WithAll<Prefab>()
                .ForEach((Entity prefabEntity, in GhostPrefabMetaDataComponent metaData) =>
            {
                ref var ghostMetaData = ref metaData.Value.Value;
                // Delete everything from toBeDeleted from the prefab
                if (isServer)
                {
                    var entities = default(NativeArray<LinkedEntityGroup>);
                    //Need to make a copy since we are making structural changes (removing components). The entity values
                    //remains the same but the chunks (and so the memory) they pertains does not.
                    if(ghostMetaData.RemoveOnServer.Length > 0)
                        entities = EntityManager.GetBuffer<LinkedEntityGroup>(prefabEntity).ToNativeArray(Allocator.Temp);
                    for (int rm = 0; rm < ghostMetaData.RemoveOnServer.Length; ++rm)
                    {
                        var indexHashPair = ghostMetaData.RemoveOnServer[rm];
                        var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(indexHashPair.StableHash));
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (indexHashPair.EntityIndex != 0)
                        {
                            if (indexHashPair.EntityIndex != 0 && indexHashPair.EntityIndex >= entities.Length)
                                throw new InvalidProgramException($"Cannot remove server component from child entity {indexHashPair.EntityIndex} for ghost {ghostMetaData.Name.ToString()}. Child index out of bound");
                        }
#endif
                        var ent = entities[indexHashPair.EntityIndex].Value;
                        if (EntityManager.HasComponent(ent, compType))
                            EntityManager.RemoveComponent(ent, compType);
                    }
                }
                else
                {
                    var entities = default(NativeArray<LinkedEntityGroup>);
                    //Need to make a copy since we are making structural changes (removing components). The entity values
                    //remains the same but the chunks (and so the memory) they pertains does not.
                    if(ghostMetaData.RemoveOnClient.Length > 0)
                        entities = EntityManager.GetBuffer<LinkedEntityGroup>(prefabEntity).ToNativeArray(Allocator.Temp);
                    for (int rm = 0; rm < ghostMetaData.RemoveOnClient.Length; ++rm)
                    {
                        var indexHashPair = ghostMetaData.RemoveOnClient[rm];
                        var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(indexHashPair.StableHash));
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if(indexHashPair.EntityIndex != 0 && indexHashPair.EntityIndex >= entities.Length)
                            throw new InvalidProgramException($"Cannot remove client component from child entity {indexHashPair.EntityIndex} for ghost {ghostMetaData.Name.ToString()}. Child index out of bound");
#endif
                        var ent = entities[indexHashPair.EntityIndex].Value;
                        if (EntityManager.HasComponent(ent, compType))
                            EntityManager.RemoveComponent(ent, compType);
                    }
                }
                EntityManager.RemoveComponent<GhostPrefabRuntimeStrip>(prefabEntity);
            }).Run();


            // if not in game clear the ghost collections
            if (m_InGameQuery.IsEmptyIgnoreFilter)
            {
                EntityManager.GetBuffer<GhostCollectionPrefab>(m_CollectionSingleton).Clear();
                EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(m_CollectionSingleton).Clear();
                EntityManager.GetBuffer<GhostCollectionComponentIndex>(m_CollectionSingleton).Clear();
                #if UNITY_EDITOR || DEVELOPMENT_BUILD
                m_PredictionErrorNames.Clear();
                m_GhostNames.Clear();
                if (m_PrevPredictionErrorNamesCount > 0 || m_PrevGhostNamesCount > 0)
                {
                    World.GetExistingSystem<GhostStatsCollectionSystem>().SetGhostNames(m_GhostNames, m_PredictionErrorNames);
                    m_PrevPredictionErrorNamesCount = 0;
                    m_PrevGhostNamesCount = 0;
                }
                #endif
                SetSingleton(default(GhostCollection));
                return;
            }

            // TODO: Using run on these is only required because the prefab processing cannot run in a job yet
            var ghostCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefab>();
            var collectionSingleton = m_CollectionSingleton;

            {
                var ghostCollectionList = ghostCollectionFromEntity[collectionSingleton];
                for (int i = 0; i < ghostCollectionList.Length; ++i)
                {
                    var ghost = ghostCollectionList[i];
                    if (!EntityManager.Exists(ghost.GhostPrefab))
                    {
                        ghost.GhostPrefab = Entity.Null;
                        ghostCollectionList[i] = ghost;
                    }
                }
            }

            // Update the list of available prefabs
            if (isServer)
            {
                // The server adds all ghost prefabs to the ghost collection if they are not already there
                Entities.WithNone<GhostPrefabRuntimeStrip>().WithAll<Prefab>().ForEach((Entity ent, in GhostTypeComponent ghostType) =>
                {
                    var ghostCollectionList = ghostCollectionFromEntity[collectionSingleton];
                    for (int i = 0; i < ghostCollectionList.Length; ++i)
                    {
                        var ghost = ghostCollectionList[i];
                        if (ghost.GhostType == ghostType)
                        {
                            if (ghost.GhostPrefab == Entity.Null)
                            {
                                ghost.GhostPrefab = ent;
                                ghostCollectionList[i] = ghost;
                            }
                            return;
                        }
                    }
                    ghostCollectionList.Add(new GhostCollectionPrefab{GhostType = ghostType, GhostPrefab = ent});
                }).Run();
            }
            else
            {
                // The client scans for Entity.Null and sets up the correct prefab
                Entities.WithNone<GhostPrefabRuntimeStrip>().WithAll<Prefab>().ForEach((Entity ent, in GhostTypeComponent ghostType) =>
                {
                    var ghostCollectionList = ghostCollectionFromEntity[collectionSingleton];
                    for (int i = 0; i < ghostCollectionList.Length; ++i)
                    {
                        var ghost = ghostCollectionList[i];
                        if (ghost.GhostPrefab == Entity.Null && ghost.GhostType == ghostType)
                        {
                            ghost.GhostPrefab = ent;
                            ghostCollectionList[i] = ghost;
                        }
                    }
                }).Run();
            }

            var ghostCollection = EntityManager.GetBuffer<GhostCollectionPrefab>(m_CollectionSingleton);
            var ghostSerializerCollection = EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(m_CollectionSingleton);

            // This must be done on the main thread for now
            for (int i = ghostSerializerCollection.Length; i < ghostCollection.Length; ++i)
            {
                var ghost = ghostCollection[i];
                // Load each ghost in this set and add it to m_GhostTypeCollection
                // If the prefab is not loaded yet, do not process any more ghosts
                ulong hash = 0;
                // TODO: this could give the client some time to load the prefabs by having a loading countdown
                if (ghost.GhostPrefab != Entity.Null)
                {
                    // This can be setup - do so
                    ProcessGhostPrefab(ghostSerializerCollection, ghost.GhostPrefab);
                    hash = HashGhostType(ghostSerializerCollection[i]);
                }
                if ((ghost.Hash != 0 && ghost.Hash != hash) || hash == 0)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (hash == 0)
                        UnityEngine.Debug.LogError($"The ghost collection contains a ghost which does not have a valid prefab on the client");
                    else
                        UnityEngine.Debug.LogError($"Received a ghost from the server which has a different hash on the client");
#endif
                    Entities
                        .WithAll<NetworkIdComponent>()
                        .WithNone<NetworkStreamDisconnected>()
                        .WithoutBurst()
                        .WithStructuralChanges()
                        .ForEach((Entity entity) => {
                        EntityManager.AddComponentData(entity, new NetworkStreamRequestDisconnect{Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
                    }).Run();
                    ghostCollection = EntityManager.GetBuffer<GhostCollectionPrefab>(m_CollectionSingleton);
                    ghostSerializerCollection = EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(m_CollectionSingleton);
                    break;
                }
                ghost.Hash = hash;
                ghostCollection[i] = ghost;
            }
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_PrevPredictionErrorNamesCount < m_PredictionErrorNames.Count || m_PrevGhostNamesCount < m_GhostNames.Count)
            {
                World.GetExistingSystem<GhostStatsCollectionSystem>().SetGhostNames(m_GhostNames, m_PredictionErrorNames);
                m_PrevPredictionErrorNamesCount = m_PredictionErrorNames.Count;
                m_PrevGhostNamesCount = m_GhostNames.Count;
            }
            #endif
            SetSingleton(new GhostCollection
            {
                NumLoadedPrefabs = ghostSerializerCollection.Length,
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
                NumPredictionErrorNames = m_PredictionErrorNames.Count,
            #endif
                IsInGame = true
            });
        }
        private void ProcessGhostPrefab(DynamicBuffer<GhostCollectionPrefabSerializer> ghostSerializerCollection, Entity prefabEntity)
        {
            ref var ghostMetaData = ref EntityManager.GetComponentData<GhostPrefabMetaDataComponent>(prefabEntity).Value.Value;
            ref var componentInfo = ref ghostMetaData.ServerComponentList;
            ref var componentInfoLen = ref ghostMetaData.NumServerComponentsPerEntity;
            //Compute the total number of components that include also all entities children.
            //The blob array contains for each entity child a list of component hashes
            int componentCount = componentInfo.Length;
            var components = new NativeArray<ComponentType>(componentCount, Allocator.Temp);
            var hasLinkedGroup = EntityManager.HasComponent<LinkedEntityGroup>(prefabEntity);
            componentCount = 0;
            for (int i = 0; i < componentInfo.Length; ++i)
            {
                components[componentCount++] = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(componentInfo[i].StableHash));
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (ghostMetaData.SupportedModes != GhostPrefabMetaData.GhostMode.Both && ghostMetaData.SupportedModes != ghostMetaData.DefaultMode)
                throw new InvalidOperationException($"The ghost {ghostMetaData.Name.ToString()} has a default mode which is not supported");
#endif
            var fallbackPredictionMode = GhostSpawnBuffer.Type.Interpolated;
            if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Predicted)
                fallbackPredictionMode = GhostSpawnBuffer.Type.Predicted;
            var ghostComponentIndex = EntityManager.GetBuffer<GhostCollectionComponentIndex>(GetSingletonEntity<GhostCollection>());
            var ghostType = new GhostCollectionPrefabSerializer
            {
                TypeHash = 0,
                FirstComponent = ghostComponentIndex.Length,
                NumComponents = 0,
                NumChildComponents = 0,
                SnapshotSize = 0,
                ChangeMaskBits = 0,
                PredictionOwnerOffset = -1,
                OwnerPredicted = (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Both) ? 1 : 0,
                PartialComponents = 0,
                BaseImportance = ghostMetaData.Importance,
                FallbackPredictionMode = fallbackPredictionMode,
                IsGhostGroup = EntityManager.HasComponent<GhostGroup>(prefabEntity) ? 1 : 0,
                StaticOptimization = ghostMetaData.StaticOptimization,
                NumBuffers = 0,
                MaxBufferSnapshotSize = 0
            };
            int childOffset = 0;
            // Map the component types to things in the collection and create lists of function pointers
            AddComponents(ref ghostMetaData, 0,ref ghostType, components.GetSubArray(0, componentInfoLen[0]), childOffset);
            childOffset += componentInfoLen[0];
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (componentInfoLen.Length  > 1 && !hasLinkedGroup)
                throw new InvalidOperationException($"The ghost {ghostMetaData.Name.ToString()} expect {componentInfoLen.Length} child entities byt no LinkedEntityGroup is present");
#endif
            if (hasLinkedGroup)
            {
                var linkedEntityGroup = EntityManager.GetBuffer<LinkedEntityGroup>(prefabEntity);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (componentInfoLen.Length  != linkedEntityGroup.Length)
                    throw new InvalidOperationException($"The ghost {ghostMetaData.Name.ToString()} expect {components.Length} child entities but {linkedEntityGroup.Length} are present.");
#endif
                for (var entityIndex = 1; entityIndex < linkedEntityGroup.Length; ++entityIndex)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (!EntityManager.HasComponent<GhostChildEntityComponent>(linkedEntityGroup[entityIndex].Value))
                        throw new InvalidOperationException($"The ghost {ghostMetaData.Name.ToString()} has a child entity without the GhostChildEntityComponent");
#endif
                    AddComponents(ref ghostMetaData, entityIndex,ref ghostType, components.GetSubArray(childOffset, componentInfoLen[entityIndex]), childOffset);
                    childOffset += componentInfoLen[entityIndex];
                }
            }
            if (ghostType.PredictionOwnerOffset < 0)
            {
                ghostType.PredictionOwnerOffset = 0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (ghostType.OwnerPredicted != 0)
                {
                    throw new InvalidOperationException(
                        $"The ghost {ghostMetaData.Name.ToString()} is owner predicted, but the ghost owner component could not be found");
                }
#endif
            }
            else
            {
                ghostType.PredictionOwnerOffset += SnapshotSizeAligned(4 + ChangeMaskArraySizeInUInts(ghostType.ChangeMaskBits)*4);
            }
            // Reserve space for tick and change mask in the snapshot
            ghostType.SnapshotSize += SnapshotSizeAligned(4 + ChangeMaskArraySizeInUInts(ghostType.ChangeMaskBits)*4);
            ghostSerializerCollection.Add(ghostType);
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_GhostNames.Add(ghostMetaData.Name.ToString());
            #endif
        }
        private void CreateComponentCollection()
        {
            var ghostSerializerCollection = EntityManager.GetBuffer<GhostComponentSerializer.State>(GetSingletonEntity<GhostCollection>());
            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(GetSingletonEntity<GhostCollection>());
            ghostSerializerCollection.ResizeUninitialized(m_PendingGhostComponentCollection.Count);
            ghostComponentCollection.Capacity = m_PendingGhostComponentCollection.Count;

            //Create the ghost serializer collection and unique list of component types
            //that also provide an inverse mapping into the ghost serializer list
            for (int i = 0; i < m_PendingGhostComponentCollection.Count; ++i)
                ghostSerializerCollection[i] = m_PendingGhostComponentCollection[i];
            ghostSerializerCollection.AsNativeArray().Sort(default(ComponentHashComparer));

            for (int i = 0; i < ghostSerializerCollection.Length;)
            {
                int firstSerializer = i;
                var compType = ghostSerializerCollection[i].ComponentType;
                do
                {
                    ++i;
                } while (i < ghostSerializerCollection.Length && ghostSerializerCollection[i].ComponentType == compType);
                ghostComponentCollection.Add(new GhostCollectionComponentType
                {
                    Type = compType,
                    FirstSerializer = firstSerializer,
                    LastSerializer = i-1
                });
            }
            m_PendingGhostComponentCollection.Clear();
            m_ComponentCollectionInitialized = true;
        }
        private void AddComponents(ref GhostPrefabMetaData ghostMeta, int ghostChildIndex, ref GhostCollectionPrefabSerializer ghostType, in NativeArray<ComponentType> components, int childOffset)
        {
            ghostType.TypeHash = TypeHash.FNV1A64(ghostMeta.Name.ToString());
            var supportedModes = ghostMeta.SupportedModes;
            ref var componentInfo = ref ghostMeta.ServerComponentList;
            var ghostSerializerCollection = EntityManager.GetBuffer<GhostComponentSerializer.State>(GetSingletonEntity<GhostCollection>());
            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(GetSingletonEntity<GhostCollection>());
            var ghostComponentIndex = EntityManager.GetBuffer<GhostCollectionComponentIndex>(GetSingletonEntity<GhostCollection>());
            for (var i = 0; i < components.Length; ++i)
            {
                if (ghostChildIndex == 0)
                {
                    if (components[i] == ComponentType.ReadWrite<GhostOwnerComponent>())
                        ghostType.PredictionOwnerOffset = ghostType.SnapshotSize;
                }

                var componentVariant = componentInfo[childOffset+i].Variant;
                for (int componentIndex = 0; componentIndex < ghostComponentCollection.Length; ++componentIndex)
                {
                    if (ghostComponentCollection[componentIndex].Type != components[i])
                        continue;

                    int serializerIndex = ghostComponentCollection[componentIndex].FirstSerializer;
                    while (serializerIndex <= ghostComponentCollection[componentIndex].LastSerializer && ghostSerializerCollection[serializerIndex].VariantHash != componentVariant)
                        ++serializerIndex;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (serializerIndex > ghostComponentCollection[componentIndex].LastSerializer)
                        throw new InvalidOperationException($"Cannot find serializer for {components[i]} with hash {componentVariant}");
#endif
                    var compState = ghostSerializerCollection[serializerIndex];
                    var sendToChildEntity = componentInfo[childOffset+i].SendToChildEntityOverride >= 0
                        ? componentInfo[childOffset+i].SendToChildEntityOverride
                        : compState.SendForChildEntities;
                    if (ghostChildIndex != 0 && sendToChildEntity == 0)
                        continue;
                    var sendMask = componentInfo[childOffset+i].SendMaskOverride >= 0
                        ? (GhostComponentSerializer.SendMask)componentInfo[childOffset+i].SendMaskOverride
                        : compState.SendMask;
                    if (sendMask == 0)
                        continue;
                    if ((sendMask & GhostComponentSerializer.SendMask.Interpolated) == 0 && supportedModes == GhostPrefabMetaData.GhostMode.Interpolated)
                        continue;
                    if ((sendMask & GhostComponentSerializer.SendMask.Predicted) == 0 && supportedModes == GhostPrefabMetaData.GhostMode.Predicted)
                        continue;
                    // Found something
                    ++ghostType.NumComponents;
                    if (ghostChildIndex != 0)
                        ++ghostType.NumChildComponents;
                    if (!components[i].IsBuffer)
                    {
                        ghostType.SnapshotSize += SnapshotSizeAligned(compState.SnapshotSize);
                        ghostType.ChangeMaskBits += compState.ChangeMaskBits;
                    }
                    else
                    {
                        ghostType.SnapshotSize += SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                        ghostType.ChangeMaskBits += GhostSystemConstants.DynamicBufferComponentMaskBits; //1bit for the content and 1 bit for the len
                        ghostType.MaxBufferSnapshotSize = math.max(compState.SnapshotSize, ghostType.MaxBufferSnapshotSize);
                        ++ghostType.NumBuffers;
                    }
                    ghostComponentIndex.Add(new GhostCollectionComponentIndex
                    {
                        EntityIndex = ghostChildIndex,
                        ComponentIndex = componentIndex,
                        SerializerIndex = serializerIndex,
                        SendMask = sendMask,
                        SendForChildEntity = sendToChildEntity,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        PredictionErrorBaseIndex = m_PredictionErrorNames.Count
#endif
                    });
                    if (sendMask != (GhostComponentSerializer.SendMask.Interpolated | GhostComponentSerializer.SendMask.Predicted))
                        ghostType.PartialComponents = 1;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    var errorNames = compState.PredictionErrorNames.ToString().Split(',');
                    for (int name = 0; name < errorNames.Length; ++name)
                    {
                        if (ghostChildIndex == 0)
                            errorNames[name] = $"{ghostMeta.Name.ToString()}.{compState.ComponentType}.{errorNames[name]}";
                        else
                            errorNames[name] = $"{ghostMeta.Name.ToString()}[{ghostChildIndex}].{compState.ComponentType}.{errorNames[name]}";
                    }
                    m_PredictionErrorNames.AddRange(errorNames);
#endif
                    var serializationHash = TypeHash.CombineFNV1A64(compState.SerializerHash, TypeHash.FNV1A64((int)sendMask));
                    serializationHash = TypeHash.CombineFNV1A64(serializationHash, TypeHash.FNV1A64(sendToChildEntity));
                    ghostType.TypeHash = TypeHash.CombineFNV1A64(ghostType.TypeHash, serializationHash);
                }
            }
        }

        /// <summary>
        /// Convert a prefab from the prefab collection to a predictive spawn version of the prefab. This will not modify the prefab on the server,
        /// so it is safe to unconditionally call this method to get a prefab that can be used for predictive spawning in a prediction system.
        /// </summary>
        public static Entity CreatePredictedSpawnPrefab(EntityManager entityManager, Entity prefab)
        {
            if (entityManager.World.GetExistingSystem<ServerSimulationSystemGroup>() != null)
                return prefab;
            ref var toRemove = ref entityManager.GetComponentData<GhostPrefabMetaDataComponent>(prefab).Value.Value.DisableOnPredictedClient;
            prefab = entityManager.Instantiate(prefab);
            entityManager.AddComponentData(prefab, default(Prefab));
            entityManager.AddComponentData(prefab, default(PredictedGhostSpawnRequestComponent));
            // TODO: disable instead of deleting
            //Need copy because removing component will invalidate the buffer pointer, since introduce structural changes
            var linkedEntityGroup = entityManager.GetBuffer<LinkedEntityGroup>(prefab).ToNativeArray(Allocator.Temp);
            for (int rm = 0; rm < toRemove.Length; ++rm)
            {
                var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toRemove[rm].StableHash));
                entityManager.RemoveComponent(linkedEntityGroup[toRemove[rm].EntityIndex].Value, compType);
            }
            return prefab;
        }
    }
}

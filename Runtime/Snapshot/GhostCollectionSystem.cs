using System;
using System.Diagnostics;
using Unity.Entities;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    public partial class GhostCollectionSystem : SystemBase
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
                //same component are sorted by variant hash
                if (x.VariantHash < y.VariantHash)
                    return -1;
                if (x.VariantHash > y.VariantHash)
                    return 1;
                else
                    return 0;
            }
        }
        private bool m_ComponentCollectionInitialized;
        private Entity m_CollectionSingleton;
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private NativeList<FixedString512Bytes> m_PredictionErrorNames;
        private NativeList<FixedString128Bytes> m_GhostNames;
        private int m_PrevPredictionErrorNamesCount;
        private int m_PrevGhostNamesCount;
        #endif

        private EntityQuery m_InGameQuery;
        private EntityQuery m_AllConnectionsQuery;
        private EntityQuery m_RuntimeStripQuery;
        private NetDebugSystem m_NetDebugSystem;
        private GhostComponentSerializerCollectionSystemGroup m_GhostComponentSerializerCollectionSystemGroup;
        private bool m_IsServer;

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
            EntityManager.SetName(m_CollectionSingleton, "Ghost Collection");
            EntityManager.AddBuffer<GhostCollectionPrefab>(m_CollectionSingleton);
            EntityManager.AddBuffer<GhostCollectionPrefabSerializer>(m_CollectionSingleton);
            EntityManager.AddBuffer<GhostCollectionComponentIndex>(m_CollectionSingleton);
            EntityManager.AddBuffer<GhostCollectionPrefab>(m_CollectionSingleton);
            EntityManager.AddBuffer<GhostComponentSerializer.State>(m_CollectionSingleton);
            EntityManager.AddBuffer<GhostCollectionComponentType>(m_CollectionSingleton);

            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_PredictionErrorNames = new NativeList<FixedString512Bytes>(16, Allocator.Persistent);
            m_GhostNames = new NativeList<FixedString128Bytes>(16, Allocator.Persistent);
            #endif

            m_InGameQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
            m_AllConnectionsQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkIdComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());

            m_NetDebugSystem = World.GetOrCreateSystem<NetDebugSystem>();
            m_GhostComponentSerializerCollectionSystemGroup = World.GetExistingSystem<GhostComponentSerializerCollectionSystemGroup>();
            m_IsServer = World.GetExistingSystem<ServerSimulationSystemGroup>() != null;
        }
        protected override void OnDestroy()
        {
            m_InGameQuery.Dispose();
            m_AllConnectionsQuery.Dispose();
            EntityManager.DestroyEntity(m_CollectionSingleton);
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_PredictionErrorNames.Dispose();
            m_GhostNames.Dispose();
            #endif
        }
        protected override void OnUpdate()
        {
            if (!m_ComponentCollectionInitialized)
            {
                CreateComponentCollection();
            }
            RuntimeStripPrefabs();

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
            if (m_IsServer)
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

                // This can give the client some time to load the prefabs by having a loading countdown
                if (ghost.GhostPrefab == Entity.Null && ghost.Loading == GhostCollectionPrefab.LoadingState.LoadingActive)
                {
                    ghost.Loading = GhostCollectionPrefab.LoadingState.LoadingNotActive;
                    ghostCollection[i] = ghost;
                    break;
                }
                ulong hash = 0;
                if (ghost.GhostPrefab != Entity.Null)
                {
                    // This can be setup - do so
                    ProcessGhostPrefab(ghostSerializerCollection, ghost.GhostPrefab);
                    hash = HashGhostType(ghostSerializerCollection[i]);
                }
                if ((ghost.Hash != 0 && ghost.Hash != hash) || hash == 0)
                {
                    if (hash == 0)
                        m_NetDebugSystem.NetDebug.LogError($"The ghost collection contains a ghost which does not have a valid prefab on the client");
                    else
                    {
                        ref var ghostMetaData = ref EntityManager.GetComponentData<GhostPrefabMetaDataComponent>(ghost.GhostPrefab).Value.Value;
                        m_NetDebugSystem.NetDebug.LogError($"Received a ghost - {ghostMetaData.Name.ToString()} - from the server which has a different hash on the client (got {ghost.Hash} but expected {hash})");
                    }
                    // This cannot be jobified because the query is created to avoid a dependency on these entities
                    var connections = m_AllConnectionsQuery.ToEntityArray(Allocator.Temp);
                    for (int con = 0; con < connections.Length; ++con)
                        EntityManager.AddComponentData(connections[con], new NetworkStreamRequestDisconnect{Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
                    ghostCollection = EntityManager.GetBuffer<GhostCollectionPrefab>(m_CollectionSingleton);
                    ghostSerializerCollection = EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(m_CollectionSingleton);
                    //contnue and log all the errors
                    continue;
                }
                ghost.Hash = hash;
                ghostCollection[i] = ghost;
            }
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_PrevPredictionErrorNamesCount < m_PredictionErrorNames.Length || m_PrevGhostNamesCount < m_GhostNames.Length)
            {
                World.GetExistingSystem<GhostStatsCollectionSystem>().SetGhostNames(m_GhostNames, m_PredictionErrorNames);
                m_PrevPredictionErrorNamesCount = m_PredictionErrorNames.Length;
                m_PrevGhostNamesCount = m_GhostNames.Length;
            }
            #endif
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            SetSingleton(new GhostCollection
            {
                NumLoadedPrefabs = ghostSerializerCollection.Length,
                NumPredictionErrorNames = m_PredictionErrorNames.Length,
                IsInGame = true
            });
            #else
            SetSingleton(new GhostCollection
            {
                NumLoadedPrefabs = ghostSerializerCollection.Length,
                IsInGame = true
            });
            #endif
        }
        private void ProcessGhostPrefab(DynamicBuffer<GhostCollectionPrefabSerializer> ghostSerializerCollection, Entity prefabEntity)
        {
            ref var ghostMetaData = ref EntityManager.GetComponentData<GhostPrefabMetaDataComponent>(prefabEntity).Value.Value;
            ref var componentInfoLen = ref ghostMetaData.NumServerComponentsPerEntity;
            var ghostName = ghostMetaData.Name.ToString();
            //Compute the total number of components that include also all entities children.
            //The blob array contains for each entity child a list of component hashes
            var hasLinkedGroup = EntityManager.HasComponent<LinkedEntityGroup>(prefabEntity);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (ghostMetaData.SupportedModes != GhostPrefabMetaData.GhostMode.Both && ghostMetaData.SupportedModes != ghostMetaData.DefaultMode)
                throw new InvalidOperationException($"The ghost {ghostName} has a default mode which is not supported");
#endif
            var fallbackPredictionMode = GhostSpawnBuffer.Type.Interpolated;
            if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Predicted)
                fallbackPredictionMode = GhostSpawnBuffer.Type.Predicted;
            var ghostComponentIndex = EntityManager.GetBuffer<GhostCollectionComponentIndex>(GetSingletonEntity<GhostCollection>());
            var ghostType = new GhostCollectionPrefabSerializer
            {
                TypeHash = TypeHash.FNV1A64(ghostName),
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
                MaxBufferSnapshotSize = 0,
                profilerMarker = new Unity.Profiling.ProfilerMarker(ghostName)
            };
            int childOffset = 0;

            // Map the component types to things in the collection and create lists of function pointers
            AddComponents(ref ghostMetaData, 0, childOffset, ref ghostType, ghostName);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (componentInfoLen.Length  > 1 && !hasLinkedGroup)
                throw new InvalidOperationException($"The ghost {ghostName} expect {componentInfoLen.Length} child entities byt no LinkedEntityGroup is present");
#endif
            if (hasLinkedGroup)
            {
                var linkedEntityGroup = EntityManager.GetBuffer<LinkedEntityGroup>(prefabEntity);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (componentInfoLen.Length  != linkedEntityGroup.Length)
                    throw new InvalidOperationException($"The ghost {ghostName} expect {componentInfoLen.Length} child entities but {linkedEntityGroup.Length} are present.");
#endif
                for (var entityIndex = 1; entityIndex < linkedEntityGroup.Length; ++entityIndex)
                {
                    childOffset += componentInfoLen[entityIndex-1];
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (!EntityManager.HasComponent<GhostChildEntityComponent>(linkedEntityGroup[entityIndex].Value))
                        throw new InvalidOperationException($"The ghost {ghostName} has a child entity without the GhostChildEntityComponent");
#endif
                    AddComponents(ref ghostMetaData, entityIndex, childOffset, ref ghostType, ghostName);
                }
            }
            if (ghostType.PredictionOwnerOffset < 0)
            {
                ghostType.PredictionOwnerOffset = 0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (ghostType.OwnerPredicted != 0)
                {
                    throw new InvalidOperationException(
                        $"The ghost {ghostName} is owner predicted, but the ghost owner component could not be found");
                }
                if(ghostType.PartialSendToOwner != 0)
                    m_NetDebugSystem.NetDebug.DebugLog($"Ghost {ghostName} has some components that have SendToOwner != All but not GhostOwnerComponent is present.\nThe flag will be ignored at runtime");
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
            m_GhostNames.Add(ghostName);
            #endif
        }
        private void RuntimeStripPrefabs()
        {
            if (m_RuntimeStripQuery.IsEmptyIgnoreFilter)
                return;
            bool isServer = m_IsServer;

            // Perform runtime stripping of all prefabs which need it
            Entities
                .WithStoreEntityQueryInField(ref m_RuntimeStripQuery)
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
                        if (indexHashPair.EntityIndex >= entities.Length)
                            throw new InvalidOperationException($"Cannot remove server component from child entity {indexHashPair.EntityIndex} for ghost {ghostMetaData.Name.ToString()}. Child index out of bound");
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
                        if(indexHashPair.EntityIndex >= entities.Length)
                            throw new InvalidOperationException($"Cannot remove client component from child entity {indexHashPair.EntityIndex} for ghost {ghostMetaData.Name.ToString()}. Child index out of bound");
#endif
                        var ent = entities[indexHashPair.EntityIndex].Value;
                        if (EntityManager.HasComponent(ent, compType))
                            EntityManager.RemoveComponent(ent, compType);
                    }
                }
                EntityManager.RemoveComponent<GhostPrefabRuntimeStrip>(prefabEntity);
            }).Run();
        }
        private void CreateComponentCollection()
        {
            var collection = m_GhostComponentSerializerCollectionSystemGroup;

            var collectionSingleton = GetSingletonEntity<GhostCollection>();
            var ghostSerializerCollection = EntityManager.GetBuffer<GhostComponentSerializer.State>(collectionSingleton);
            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(collectionSingleton);
            ghostSerializerCollection.ResizeUninitialized(collection.GhostComponentCollection.Count);
            ghostComponentCollection.Capacity = collection.GhostComponentCollection.Count;

            //Create the ghost serializer collection and unique list of component types
            //that also provide an inverse mapping into the ghost serializer list
            for (int i = 0; i < collection.GhostComponentCollection.Count; ++i)
                ghostSerializerCollection[i] = collection.GhostComponentCollection[i];
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
            m_ComponentCollectionInitialized = true;
            collection.CollectionInitialized = true;
        }

        private void AddComponents(ref GhostPrefabMetaData ghostMeta, int ghostChildIndex, int childOffset, ref GhostCollectionPrefabSerializer ghostType, string ghostName)
        {
            var ghostSerializerCollection = EntityManager.GetBuffer<GhostComponentSerializer.State>(GetSingletonEntity<GhostCollection>());
            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(GetSingletonEntity<GhostCollection>());

            ref var serverComponents = ref ghostMeta.ServerComponentList;
            var componentCount = ghostMeta.NumServerComponentsPerEntity[ghostChildIndex];
            var ghostComponentIndex = EntityManager.GetBuffer<GhostCollectionComponentIndex>(GetSingletonEntity<GhostCollection>());
            for (var i = 0; i < componentCount; ++i)
            {
                var type = ComponentType.ReadWrite(
                    TypeManager.GetTypeIndexFromStableTypeHash(serverComponents[childOffset + i].StableHash));
                if (ghostChildIndex == 0)
                {
                    if (type == ComponentType.ReadWrite<GhostOwnerComponent>())
                        ghostType.PredictionOwnerOffset = ghostType.SnapshotSize;
                }

                int componentIndex = 0;
                while(componentIndex < ghostComponentCollection.Length &&
                      ghostComponentCollection[componentIndex].Type.TypeIndex != type.TypeIndex)
                    ++componentIndex;

                if(componentIndex >= ghostComponentCollection.Length)
                    continue;

                var componentInfo = serverComponents[childOffset + i];

                var variant = m_GhostComponentSerializerCollectionSystemGroup.GetVariantType(type, componentInfo.Variant);
                //skip component if client only or don't send variants are selected.
                if (!variant.IsSerialized)
                    continue;

                int serializerIndex = ghostComponentCollection[componentIndex].FirstSerializer;
                while (serializerIndex <= ghostComponentCollection[componentIndex].LastSerializer &&
                       ghostSerializerCollection[serializerIndex].VariantHash != variant.Hash)
                    ++serializerIndex;

                //If the component look for the default but no serializer is present with hash = 0 we will grab the first variation in the list
                //In case multiple variants exists (with hash != 0) and no default has been selected then an error is reported
                if (variant.Hash == 0 &&
                    (serializerIndex > ghostComponentCollection[componentIndex].LastSerializer))
                {
                    if ((ghostComponentCollection[componentIndex].LastSerializer -
                         ghostComponentCollection[componentIndex].FirstSerializer) > 0)
                    {
                        UnityEngine.Debug.LogError(
                            $"Conflict for {componentInfo} detected. Impossible to select a serialization for the component type.\n" +
                            "Multiple GhostComponentVarition are presents but the component does not present ghostfields or a default serialization has not been generated\n" +
                            "Please assign a default variant to use for the type");
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        throw new InvalidOperationException(
                            $"Cannot assign a default serialization for component {componentInfo}");
#endif
                    }

                    serializerIndex = ghostComponentCollection[componentIndex].FirstSerializer;
                }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (serializerIndex > ghostComponentCollection[componentIndex].LastSerializer)
                    throw new InvalidOperationException(
                        $"Cannot find serializer for {componentInfo} with hash {variant.Hash} for ghost {ghostName}");
#endif

                //Apply prefab overrides if any
                var compState = ghostSerializerCollection[serializerIndex];
                var sendToChildEntity = componentInfo.SendToChildEntityOverride >= 0
                    ? componentInfo.SendToChildEntityOverride
                    : compState.SendForChildEntities;
                var sendMask = componentInfo.SendMaskOverride >= 0
                    ? (GhostComponentSerializer.SendMask) componentInfo.SendMaskOverride
                    : compState.SendMask;

                if (ghostChildIndex != 0 && sendToChildEntity == 0)
                    continue;
                if (sendMask == 0)
                    continue;
                var supportedModes = ghostMeta.SupportedModes;
                if ((sendMask & GhostComponentSerializer.SendMask.Interpolated) == 0 &&
                    supportedModes == GhostPrefabMetaData.GhostMode.Interpolated)
                    continue;
                if ((sendMask & GhostComponentSerializer.SendMask.Predicted) == 0 &&
                    supportedModes == GhostPrefabMetaData.GhostMode.Predicted)
                    continue;

                // Found something
                ++ghostType.NumComponents;
                if (ghostChildIndex != 0)
                    ++ghostType.NumChildComponents;
                if (!type.IsBuffer)
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
                    PredictionErrorBaseIndex = m_PredictionErrorNames.Length
#endif
                });
                if (sendMask != (GhostComponentSerializer.SendMask.Interpolated |
                                 GhostComponentSerializer.SendMask.Predicted))
                    ghostType.PartialComponents = 1;

                if (compState.SendToOwner != SendToOwnerType.All)
                    ghostType.PartialSendToOwner = 1;

                AppendPredictionErrorNames(ghostName, ghostChildIndex, compState);

                var serializationHash =
                    TypeHash.CombineFNV1A64(compState.SerializerHash, TypeHash.FNV1A64((int) sendMask));
                serializationHash = TypeHash.CombineFNV1A64(serializationHash, TypeHash.FNV1A64(sendToChildEntity));
                ghostType.TypeHash = TypeHash.CombineFNV1A64(ghostType.TypeHash, serializationHash);
            }
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        private unsafe void AppendPredictionErrorNames(string ghostName, int ghostChildIndex, in GhostComponentSerializer.State compState)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            int strStart = 0;
            int strEnd = 0;
            int strLen = compState.PredictionErrorNames.Length;
            FixedString512Bytes errorName = default;
            while (strStart < strLen)
            {
                strEnd = strStart;
                while (strEnd < strLen && compState.PredictionErrorNames[strEnd] != ',')
                    ++strEnd;
                errorName = ghostName;
                if (ghostChildIndex != 0)
                {
                    errorName.Append('[');
                    errorName.Append(ghostChildIndex);
                    errorName.Append(']');
                }
                errorName.Append('.');
                errorName.Append(compState.ComponentType.GetDebugTypeName());
                errorName.Append('.');
                errorName.Append(compState.PredictionErrorNames.GetUnsafePtr() + strStart, strEnd-strStart);
                m_PredictionErrorNames.Add(errorName);
                // Skip the ,
                strStart = strEnd + 1;
            }
#endif
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
            for (int child = 1; child < linkedEntityGroup.Length; ++child)
            {
                entityManager.AddComponentData(linkedEntityGroup[child].Value, default(Prefab));
            }
            for (int rm = 0; rm < toRemove.Length; ++rm)
            {
                var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toRemove[rm].StableHash));
                entityManager.RemoveComponent(linkedEntityGroup[toRemove[rm].EntityIndex].Value, compType);
            }
            return prefab;
        }
    }
}

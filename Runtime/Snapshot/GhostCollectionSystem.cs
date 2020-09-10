using System;
using Unity.Entities;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using System.Collections.Generic;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    public class GhostCollectionSystem : SystemBase
    {
        public struct GhostComponentIndex
        {
            public int EntityIndex;
            public int ComponentIndex;
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            public int PredictionErrorBaseIndex;
            #endif
        }
        public struct GhostTypeState
        {
            public ulong TypeHash;
            public int FirstComponent;
            public int NumComponents;
            public int NumChildComponents;
            public int SnapshotSize;
            public int ChangeMaskBits;
            public int PredictionOwnerOffset;
            public int PartialComponents;
            public int BaseImportance;
            public GhostSpawnBuffer.Type FallbackPredictionMode;
            public int IsGhostGroup;
            public bool StaticOptimization;
        }
        struct ComponentHashComparer : IComparer<GhostComponentSerializer.State>
        {
            public int Compare(GhostComponentSerializer.State x, GhostComponentSerializer.State y)
            {
                if (x.ComponentType < y.ComponentType)
                    return -1;
                if (x.ComponentType > y.ComponentType)
                    return 1;
                return 0;
            }
        }
        public NativeArray<GhostComponentSerializer.State> m_GhostComponentCollection;
        public NativeList<GhostTypeState> m_GhostTypeCollection;
        public NativeList<GhostComponentIndex> m_GhostComponentIndex;
        private List<GhostComponentSerializer.State> m_PendingGhostComponentCollection = new List<GhostComponentSerializer.State>();
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        public int PredictionErrorCount => m_PredictionErrorNames.Count;
        private List<string> m_PredictionErrorNames;
        #endif
        private ulong m_GhostTypeCollectionHash;
        public ulong GhostTypeCollectionHash => m_GhostTypeCollectionHash;


        public void AddSerializer(GhostComponentSerializer.State state)
        {
            //This is always enforced to avoid bad usage of the api
            if (m_GhostComponentCollection.IsCreated)
            {
                throw new InvalidOperationException("Cannot register new GhostComponentSerializer after the RpcSystem has started running");
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            for (int i = 0; i < m_PendingGhostComponentCollection.Count; ++i)
            {
                // FIXME: this is a workaround for fast enter play mode
                if (m_PendingGhostComponentCollection[i].ComponentType == state.ComponentType)
                {
                    throw new InvalidOperationException($"GhostComponentSerializer for type {state.ComponentType.GetManagedType().Name} is already registered");
                }
            }
#endif
            //When the state is registered the serializer hash is computed once
            state.SerializerHash = state.ExcludeFromComponentCollectionHash == 0 ? HashGhostComponentSerializer(state) : 0;
            m_PendingGhostComponentCollection.Add(state);
        }

        public static int ChangeMaskArraySizeInUInts(int numBits)
        {
            return (numBits + 31)>>5;
        }
        public static int SnapshotSizeAligned(int size)
        {
            return (size + 15) & (~15);
        }

        //Hash requirements:
        // R0: if components are different or in different order the hash should change
        // R1: different size, owneroffsets, maskbits, partialcomponents etc must result in a different hash
        // R2: if a ghost present the same components, with the same fields but different [GhostField] attributes (such as, subType, interpoled, composite)
        //     must result in a different hash, even though the resulting serialization sizes and masks are the same
        public ulong CalculateComponentCollectionHash()
        {
            //Lazy create the component collection if not created when we want to compute the component collection hash
            if (!m_GhostComponentCollection.IsCreated)
            {
                CreateComponentCollection();
            }
            ulong componentCollectionHash = 0;
            if (m_GhostComponentCollection.Length > 0)
            {
                for (int i = 0; i < m_GhostComponentCollection.Length; ++i)
                {
                    var comp = m_GhostComponentCollection[i];
                    if(comp.SerializerHash !=0)
                    {
                        componentCollectionHash = TypeHash.CombineFNV1A64(componentCollectionHash, comp.SerializerHash);
                    }
                }
            }
            return componentCollectionHash;
        }

        public ulong CalculateGhostCollectionHash()
        {
            if (!m_GhostTypeCollection.IsCreated)
                throw new InvalidOperationException("GhostCollectionSystem.CalculateGhostCollectionHash must be called after the system has configured the ghost collection");

            m_GhostTypeCollectionHash = 0;
            if (m_GhostTypeCollection.Length > 0)
            {
                m_GhostTypeCollectionHash = HashGhostType(m_GhostTypeCollection[0]);
                for (int i = 1; i < m_GhostTypeCollection.Length; ++i)
                {
                    var ghotsTypeHash = HashGhostType(m_GhostTypeCollection[i]);
                    m_GhostTypeCollectionHash = TypeHash.CombineFNV1A64(m_GhostTypeCollectionHash, ghotsTypeHash);
                }
            }
            return m_GhostTypeCollectionHash;
        }

        private ulong HashGhostComponentSerializer(in GhostComponentSerializer.State comp)
        {
            //this will give us a good starting point
            var compHash = TypeManager.GetTypeInfo(comp.ComponentType.TypeIndex).StableTypeHash;
            if (compHash == 0)
                throw new InvalidOperationException(String.Format("Unexpected 0 hash for type {0}", comp.ComponentType.GetManagedType()));
            compHash = TypeHash.CombineFNV1A64(compHash, comp.GhostFieldsHash);
            //ComponentSize might depend on #ifdef or other compilation/platform rules so it must be not included. we will leave the comment here
            //so it is clear why we don't consider this field
            //compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.ComponentSize));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.SnapshotSize));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.ChangeMaskBits));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64((int)comp.SendMask));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.SendForChildEntities));
            return compHash;
        }

        private ulong HashGhostType(in GhostTypeState ghostType)
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
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64((int)ghostType.PredictionOwnerOffset));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.IsGhostGroup));
            return ghostTypeHash;
        }

        protected override void OnCreate()
        {
            m_GhostTypeCollectionHash = 0;
            RequireSingletonForUpdate<GhostPrefabCollectionComponent>();
        }
        protected override void OnDestroy()
        {
            if (m_GhostComponentCollection.IsCreated)
                m_GhostComponentCollection.Dispose();
            if (m_GhostTypeCollection.IsCreated)
                m_GhostTypeCollection.Dispose();
            if (m_GhostComponentIndex.IsCreated)
                m_GhostComponentIndex.Dispose();
        }
        protected override void OnUpdate()
        {
            if (!m_GhostComponentCollection.IsCreated)
            {
                CreateComponentCollection();
            }
            // FIXME: needs to be more advanced than this
            if (m_GhostTypeCollection.IsCreated)
                return;

            m_GhostTypeCollection = new NativeList<GhostTypeState>(16, Allocator.Persistent);
            m_GhostComponentIndex = new NativeList<GhostComponentIndex>(16, Allocator.Persistent);
            m_GhostTypeCollection.Clear();
            m_GhostComponentIndex.Clear();
            var ghostCollection = GetSingletonEntity<GhostPrefabCollectionComponent>();
            var prefabList = EntityManager.GetBuffer<GhostPrefabBuffer>(ghostCollection).ToNativeArray(Allocator.Temp);

            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_PredictionErrorNames = new List<string>();
            var ghostNames = new List<string>();
            #endif
            bool isServer = (World.GetExistingSystem<ServerSimulationSystemGroup>() != null);
            for (var prefab = 0; prefab < prefabList.Length; ++prefab)
            {
                var prefabEntity = prefabList[prefab].Value;
                ref var ghostMetaData = ref EntityManager.GetComponentData<GhostPrefabMetaDataComponent>(prefabEntity).Value.Value;
                ref var componentHashes = ref ghostMetaData.ServerComponentList;
                var components = new NativeArray<ComponentType>(componentHashes.Length, Allocator.Temp);
                for (int i = 0; i < componentHashes.Length; ++i)
                {
                    components[i] = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(componentHashes[i]));
                }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (ghostMetaData.SupportedModes != GhostPrefabMetaData.GhostMode.Both && ghostMetaData.SupportedModes != ghostMetaData.DefaultMode)
                    throw new InvalidOperationException($"The ghost {ghostMetaData.Name.ToString()} has a default mode which is not supported");
#endif
                var fallbackPredictionMode = GhostSpawnBuffer.Type.Interpolated;
                if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Predicted)
                    fallbackPredictionMode = GhostSpawnBuffer.Type.Predicted;
                bool isOwnerPredicted = (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Both);
                var ghostType = new GhostTypeState
                {
                    TypeHash = 0,
                    FirstComponent = m_GhostComponentIndex.Length,
                    NumComponents = 0,
                    NumChildComponents = 0,
                    SnapshotSize = 0,
                    ChangeMaskBits = 0,
                    PredictionOwnerOffset = -1,
                    PartialComponents = 0,
                    BaseImportance = ghostMetaData.Importance,
                    FallbackPredictionMode = fallbackPredictionMode,
                    IsGhostGroup = EntityManager.HasComponent<GhostGroup>(prefabEntity) ? 1 : 0,
                    StaticOptimization = ghostMetaData.StaticOptimization
                };
                // Map the component types to things in the collection and create lists of function pointers
                AddComponents(ref ghostMetaData.Name, 0, ref ghostType, components, prefabEntity, 0, ghostMetaData.SupportedModes);
                if (EntityManager.HasComponent<LinkedEntityGroup>(prefabEntity))
                {
                    var linkedEntityGroup = EntityManager.GetBuffer<LinkedEntityGroup>(prefabEntity);
                    for (var entityIndex = 1; entityIndex < linkedEntityGroup.Length; ++entityIndex)
                    {
                        if (!EntityManager.HasComponent<GhostChildEntityComponent>(linkedEntityGroup[entityIndex].Value))
                            continue;
                        components = EntityManager.GetComponentTypes(linkedEntityGroup[entityIndex].Value);
                        AddComponents(ref ghostMetaData.Name, entityIndex, ref ghostType, components, prefabEntity, entityIndex, ghostMetaData.SupportedModes);
                    }
                }
                if (!isOwnerPredicted)
                    ghostType.PredictionOwnerOffset = 0;
                else if (ghostType.PredictionOwnerOffset < 0)
                {
                    ghostType.PredictionOwnerOffset = 0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    throw new InvalidOperationException($"The ghost {ghostMetaData.Name.ToString()} is owner predicted, but the ghost owner component could not be found");
#endif
                }
                else
                    ghostType.PredictionOwnerOffset += SnapshotSizeAligned(4 + ChangeMaskArraySizeInUInts(ghostType.ChangeMaskBits)*4);
                // Reserve space for tick and change mask in the snapshot
                ghostType.SnapshotSize += SnapshotSizeAligned(4 + ChangeMaskArraySizeInUInts(ghostType.ChangeMaskBits)*4);
                m_GhostTypeCollection.Add(ghostType);
                #if UNITY_EDITOR || DEVELOPMENT_BUILD
                ghostNames.Add(ghostMetaData.Name.ToString());
                #endif
                // Delete everything from toBeDeleted from the prefab
                if (isServer)
                {
                    for (int rm = 0; rm < ghostMetaData.RemoveOnServer.Length; ++rm)
                    {
                        var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(ghostMetaData.RemoveOnServer[rm]));
                        if (EntityManager.HasComponent(prefabEntity, compType))
                            EntityManager.RemoveComponent(prefabEntity, compType);
                    }
                }
                else
                {
                    for (int rm = 0; rm < ghostMetaData.RemoveOnClient.Length; ++rm)
                    {
                        var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(ghostMetaData.RemoveOnClient[rm]));
                        if (EntityManager.HasComponent(prefabEntity, compType))
                            EntityManager.RemoveComponent(prefabEntity, compType);
                    }
                }
            }
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            World.GetExistingSystem<GhostStatsCollectionSystem>().SetGhostNames(ghostNames, m_PredictionErrorNames);
            #endif
            CalculateGhostCollectionHash();
        }
        private void CreateComponentCollection()
        {
            m_GhostComponentCollection = new NativeArray<GhostComponentSerializer.State>(m_PendingGhostComponentCollection.Count, Allocator.Persistent);
            for (int i = 0; i < m_PendingGhostComponentCollection.Count; ++i)
            {
                m_GhostComponentCollection[i] = m_PendingGhostComponentCollection[i];
            }
            m_GhostComponentCollection.Sort(default(ComponentHashComparer));
            m_PendingGhostComponentCollection.Clear();
        }
        private void AddComponents(ref BlobString ghostName, int ghostChildIndex, ref GhostTypeState ghostType, in NativeArray<ComponentType> components, Entity prefabEntity, int entityIndex, GhostPrefabMetaData.GhostMode supportedModes)
        {
            ghostType.TypeHash = TypeHash.FNV1A64(ghostName.ToString());

            for (var i = 0; i < components.Length; ++i)
            {
                if (entityIndex == 0)
                {
                    if (components[i] == ComponentType.ReadWrite<GhostOwnerComponent>())
                        ghostType.PredictionOwnerOffset = ghostType.SnapshotSize;
                }
                for (var j = 0; j < m_GhostComponentCollection.Length; ++j)
                {
                    var compState = m_GhostComponentCollection[j];
                    if (components[i] == compState.ComponentType)
                    {
                        if (entityIndex != 0 && compState.SendForChildEntities == 0)
                            continue;
                        if ((compState.SendMask & GhostComponentSerializer.SendMask.Interpolated) == 0 && supportedModes == GhostPrefabMetaData.GhostMode.Interpolated)
                            continue;
                        if ((compState.SendMask & GhostComponentSerializer.SendMask.Predicted) == 0 && supportedModes == GhostPrefabMetaData.GhostMode.Predicted)
                            continue;
                        // Found something
                        ++ghostType.NumComponents;
                        if (entityIndex != 0)
                            ++ghostType.NumChildComponents;
                        ghostType.SnapshotSize += SnapshotSizeAligned(compState.SnapshotSize);
                        ghostType.ChangeMaskBits += compState.ChangeMaskBits;
                        m_GhostComponentIndex.Add(new GhostComponentIndex
                        {
                            EntityIndex = entityIndex,
                            ComponentIndex = j,
                            #if UNITY_EDITOR || DEVELOPMENT_BUILD
                            PredictionErrorBaseIndex = PredictionErrorCount
                            #endif
                        });
                        if (compState.SendMask != (GhostComponentSerializer.SendMask.Interpolated | GhostComponentSerializer.SendMask.Predicted))
                            ghostType.PartialComponents = 1;

                        #if UNITY_EDITOR || DEVELOPMENT_BUILD
                        var errorNames = compState.PredictionErrorNames.ToString().Split(',');
                        for (int name = 0; name < errorNames.Length; ++name)
                        {
                            if (ghostChildIndex == 0)
                                errorNames[name] = $"{ghostName.ToString()}.{compState.ComponentType.ToString()}.{errorNames[name]}";
                            else
                                errorNames[name] = $"{ghostName.ToString()}[{ghostChildIndex}].{compState.ComponentType.ToString()}.{errorNames[name]}";
                        }
                        m_PredictionErrorNames.AddRange(errorNames);
                        #endif
                        ghostType.TypeHash = TypeHash.CombineFNV1A64(ghostType.TypeHash, compState.SerializerHash);
                    }
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
            for (int rm = 0; rm < toRemove.Length; ++rm)
            {
                var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toRemove[rm]));
                entityManager.RemoveComponent(prefab, compType);
            }
            return prefab;
        }
    }
}
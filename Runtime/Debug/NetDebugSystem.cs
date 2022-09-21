#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Burst;

namespace Unity.NetCode
{
    /// <summary>
    /// Systems responsible to initialize and create the <see cref="NetDebug"/> singleton and to flush all logs.
    /// </summary>
    [BurstCompile]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    public partial struct NetDebugSystem : ISystem
    {
        private EntityQuery m_NetDebugQuery;

#if NETCODE_DEBUG
        private ComponentLookup<GhostPrefabMetaDataComponent> m_GhostPrefabMetaDataComponent;
        private ComponentLookup<PrefabDebugName> m_PrefabDebugNameData;
        private BufferLookup<GhostCollectionComponentType> m_GhostCollectionBuffer;
        private EntityQuery m_GhostCollectionQuery;
        private EntityQuery m_prefabsWithoutDebugNameQuery;
        private NativeParallelHashMap<int, FixedString128Bytes> m_ComponentTypeNameLookupData;
#endif

        private void CreateNetDebugSingleton(EntityManager entityManager)
        {
            var netDebugEntity = entityManager.CreateEntity(ComponentType.ReadWrite<NetDebug>());
            entityManager.SetName(netDebugEntity, "NetDebug");
            var netDebug = new NetDebug();
            netDebug.Initialize();

#if NETCODE_DEBUG
            m_ComponentTypeNameLookupData = new NativeParallelHashMap<int, FixedString128Bytes>(1024, Allocator.Persistent);
            netDebug.ComponentTypeNameLookup = m_ComponentTypeNameLookupData.AsReadOnly();
#endif
            entityManager.SetComponentData(netDebugEntity, netDebug);
        }
        public void OnCreate(ref SystemState state)
        {
            m_NetDebugQuery = state.GetEntityQuery(ComponentType.ReadWrite<NetDebug>());
            CreateNetDebugSingleton(state.EntityManager);
            // Declare write dependency
            m_NetDebugQuery.GetSingletonRW<NetDebug>();
#if NETCODE_DEBUG
            m_GhostPrefabMetaDataComponent = state.GetComponentLookup<GhostPrefabMetaDataComponent>(true);
            m_PrefabDebugNameData = state.GetComponentLookup<PrefabDebugName>();
            m_GhostCollectionBuffer = state.GetBufferLookup<GhostCollectionComponentType>(true);
            m_GhostCollectionQuery = state.GetEntityQuery(ComponentType.ReadWrite<GhostCollection>());
            m_prefabsWithoutDebugNameQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<Prefab>(),
                ComponentType.ReadOnly<GhostPrefabMetaDataComponent>(),
                ComponentType.Exclude<PrefabDebugName>());

#if UNITY_EDITOR
            if (MultiplayerPlayModePreferences.ApplyLoggerSettings)
                m_NetDebugQuery.GetSingletonRW<NetDebug>().ValueRW.LogLevel = MultiplayerPlayModePreferences.TargetLogLevel;
#endif
#endif
        }

        public void OnDestroy(ref SystemState state)
        {
            m_NetDebugQuery.GetSingletonRW<NetDebug>().ValueRW.Dispose();
#if NETCODE_DEBUG
            m_ComponentTypeNameLookupData.Dispose();
#endif
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
#if NETCODE_DEBUG
            using var prefabsWithoutDebugName = m_prefabsWithoutDebugNameQuery.ToEntityArray(Allocator.Temp);

            state.EntityManager.AddComponent<PrefabDebugName>(m_prefabsWithoutDebugNameQuery);

            m_GhostPrefabMetaDataComponent.Update(ref state);
            m_PrefabDebugNameData.Update(ref state);

            foreach (var entity in prefabsWithoutDebugName)
            {
                var prefabMetaData = m_GhostPrefabMetaDataComponent[entity];
                ref var prefabName = ref prefabMetaData.Value.Value.Name;
                var prefabNameString = new FixedString64Bytes();
                prefabName.CopyTo(ref prefabNameString);
                m_PrefabDebugNameData[entity] = new PrefabDebugName {Name = prefabNameString};
            }

            if (m_GhostCollectionQuery.CalculateEntityCount() == 0 || !m_ComponentTypeNameLookupData.IsEmpty) return;

            state.CompleteDependency();

            m_GhostCollectionBuffer.Update(ref state);

            var collection = m_GhostCollectionQuery.GetSingletonEntity();
            var ghostComponentTypes = m_GhostCollectionBuffer[collection];
            for (var i = 0; i < ghostComponentTypes.Length; ++i)
            {
                var typeIndex = ghostComponentTypes[i].Type.TypeIndex;
                var typeName = ghostComponentTypes[i].Type.ToFixedString();
                m_ComponentTypeNameLookupData.Add(typeIndex, typeName);
            }
#endif
        }
    }
}

#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Jobs;

namespace Unity.NetCode
{
#if USE_UNITY_LOGGING
    [UpdateInWorld(TargetWorld.Default)]
    public partial class NetDebugUpdateLoggingSystem : SystemBase
    {
        private JobHandle m_PrevHandle;
        protected override void OnDestroy()
        {
            Logging.Internal.LoggerManager.FlushAll();
            Logging.Internal.LoggerManager.DeleteAllLoggers();
        }

        protected override void OnUpdate()
        {
            m_PrevHandle.Complete();
            m_PrevHandle = Logging.Internal.LoggerManager.ScheduleUpdateLoggers(); // can be called like this
        }
    }
#endif

    public partial class NetDebugSystem : SystemBase
    {
        private NetDebug m_NetDebug;
        public NetDebug NetDebug => m_NetDebug;

        public NetDebug.LogLevelType LogLevel
        {
            get => m_NetDebug.LogLevel;
            set => m_NetDebug.LogLevel = value;
        }

#if NETCODE_DEBUG
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        public NativeParallelHashMap<int, FixedString128Bytes> ComponentTypeNameLookup { get; private set; }
#endif

        protected override void OnCreate()
        {
            m_NetDebug.Initialize();
#if NETCODE_DEBUG
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            ComponentTypeNameLookup = new NativeParallelHashMap<int, FixedString128Bytes>(1024, Allocator.Persistent);
#else
            Enabled = false;
#endif
        }

        protected override void OnDestroy()
        {
#if NETCODE_DEBUG
            ComponentTypeNameLookup.Dispose();
#endif
            m_NetDebug.Dispose();
        }

        protected override void OnUpdate()
        {
#if NETCODE_DEBUG
            var commandBuffer = m_Barrier.CreateCommandBuffer();
            Entities.WithoutBurst().WithNone<PrefabDebugName>().ForEach((Entity entity, ref Prefab prefab, ref GhostPrefabMetaDataComponent prefabMetaData) =>
            {
                ref var prefabName = ref prefabMetaData.Value.Value.Name;
                var prefabNameString = new FixedString64Bytes(prefabName.ToString());
                commandBuffer.AddComponent(entity, new PrefabDebugName(){Name = prefabNameString});
            }).Schedule();
            m_Barrier.AddJobHandleForProducer(Dependency);

            if (HasSingleton<GhostCollection>() && ComponentTypeNameLookup.IsEmpty)
            {
                var collection = GetSingletonEntity<GhostCollection>();
                var ghostComponentTypes = GetBuffer<GhostCollectionComponentType>(collection);
                for (int i = 0; i < ghostComponentTypes.Length; ++i)
                {
                    int typeIndex = ghostComponentTypes[i].Type.TypeIndex;
                    var typeName = ghostComponentTypes[i].Type.ToString();
                    ComponentTypeNameLookup.Add(typeIndex, new FixedString128Bytes(typeName));
                }
            }
#endif
        }
    }
}

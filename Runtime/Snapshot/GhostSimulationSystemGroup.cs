using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    [UpdateBefore(typeof(TransformSystemGroup))]
    public class GhostSimulationSystemGroup : ComponentSystemGroup
    {
    }

    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostPredictionSystemGroup))]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    public class GhostUpdateSystemGroup : ComponentSystemGroup
    {
        // having the group own the ghost map is a bit of a hack to solve a problem with accessing the receiver system from the default spawn system (because it is generic)
        protected override void OnCreate()
        {
            m_ghostEntityMap = new NativeHashMap<int, GhostEntity>(2048, Allocator.Persistent);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_ghostSnapshotTickMinMax = new NativeArray<uint>(JobsUtility.MaxJobThreadCount * JobsUtility.CacheLineSize/4, Allocator.Persistent);
            m_GhostStatsCollectionSystem = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif
        }

        protected override void OnDestroy()
        {
            LastGhostMapWriter.Complete();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_ghostSnapshotTickMinMax.Dispose();
#endif
            m_ghostEntityMap.Dispose();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        protected override void OnUpdate()
        {
            // Gather the min/max age stats
            var intsPerCacheLine = JobsUtility.CacheLineSize/4;
            for (int i = 1; i < JobsUtility.MaxJobThreadCount; ++i)
            {
                if (m_ghostSnapshotTickMinMax[intsPerCacheLine*i] != 0 &&
                    (m_ghostSnapshotTickMinMax[0] == 0 ||
                    SequenceHelpers.IsNewer(m_ghostSnapshotTickMinMax[0], m_ghostSnapshotTickMinMax[intsPerCacheLine*i])))
                    m_ghostSnapshotTickMinMax[0] = m_ghostSnapshotTickMinMax[intsPerCacheLine*i];
                if (m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1] != 0 &&
                    (m_ghostSnapshotTickMinMax[1] == 0 ||
                    SequenceHelpers.IsNewer(m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1], m_ghostSnapshotTickMinMax[1])))
                    m_ghostSnapshotTickMinMax[1] = m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1];
                m_ghostSnapshotTickMinMax[intsPerCacheLine*i] = 0;
                m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1] = 0;
            }
            // Pass the min and max to stats collection
            m_GhostStatsCollectionSystem.SetSnapshotTick(m_ghostSnapshotTickMinMax[0], m_ghostSnapshotTickMinMax[1]);
            m_ghostSnapshotTickMinMax[0] = 0;
            m_ghostSnapshotTickMinMax[1] = 0;
            base.OnUpdate();
        }
#endif

        public JobHandle LastGhostMapWriter;
        public NativeHashMap<int, GhostEntity> GhostEntityMap => m_ghostEntityMap;
        private NativeHashMap<int, GhostEntity> m_ghostEntityMap;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public NativeArray<uint> GhostSnapshotTickMinMax => m_ghostSnapshotTickMinMax;
        private NativeArray<uint> m_ghostSnapshotTickMinMax;
        private GhostStatsCollectionSystem m_GhostStatsCollectionSystem;
#endif
    }

    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class GhostSpawnSystemGroup : ComponentSystemGroup
    {
    }
}

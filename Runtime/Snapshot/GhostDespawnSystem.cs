using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostUpdateSystem))]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    public class GhostDespawnSystem : SystemBase
    {
        internal struct DelayedDespawnGhost
        {
            public SpawnedGhost ghost;
            public uint tick;
        }

        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private GhostReceiveSystem m_GhostReceiveSystem;

        protected override void OnCreate()
        {
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_ClientSimulationSystemGroup = World.GetOrCreateSystem<ClientSimulationSystemGroup>();
            m_GhostReceiveSystem = World.GetExistingSystem<GhostReceiveSystem>();
            m_interpolatedDespawnQueue = new NativeQueue<DelayedDespawnGhost>(Allocator.Persistent);
            m_predictedDespawnQueue = new NativeQueue<DelayedDespawnGhost>(Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            LastQueueWriter.Complete();
            m_interpolatedDespawnQueue.Dispose();
            m_predictedDespawnQueue.Dispose();
        }

        protected override void OnUpdate()
        {
            var commandBuffer = m_Barrier.CreateCommandBuffer();
            var interpolatedDespawnQueue = m_interpolatedDespawnQueue;
            var predictedDespawnQueue = m_predictedDespawnQueue;
            var spawnedGhostMap = m_GhostReceiveSystem.SpawnedGhostEntityMap;
            var interpolatedTick = m_ClientSimulationSystemGroup.InterpolationTick;
            var predictedTick = m_ClientSimulationSystemGroup.ServerTick;
            Dependency = Job.WithCode(() => {
                while (interpolatedDespawnQueue.Count > 0 &&
                       !SequenceHelpers.IsNewer(interpolatedDespawnQueue.Peek().tick, interpolatedTick))
                {
                    var spawnedGhost = interpolatedDespawnQueue.Dequeue();
                    if (spawnedGhostMap.TryGetValue(spawnedGhost.ghost, out var ent))
                    {
                        commandBuffer.DestroyEntity(ent);
                        spawnedGhostMap.Remove(spawnedGhost.ghost);
                    }
                }

                while (predictedDespawnQueue.Count > 0 &&
                       !SequenceHelpers.IsNewer(predictedDespawnQueue.Peek().tick, predictedTick))
                {
                    var spawnedGhost = predictedDespawnQueue.Dequeue();
                    if (spawnedGhostMap.TryGetValue(spawnedGhost.ghost, out var ent))
                    {
                        commandBuffer.DestroyEntity(ent);
                        spawnedGhostMap.Remove(spawnedGhost.ghost);
                    }
                }
            }).Schedule(JobHandle.CombineDependencies(Dependency, LastQueueWriter, m_GhostReceiveSystem.LastGhostMapWriter));
            LastQueueWriter = Dependency;
            m_Barrier.AddJobHandleForProducer(Dependency);
            m_GhostReceiveSystem.LastGhostMapWriter = Dependency;
        }

        internal NativeQueue<DelayedDespawnGhost> InterpolatedDespawnQueue => m_interpolatedDespawnQueue;
        internal NativeQueue<DelayedDespawnGhost> PredictedDespawnQueue => m_predictedDespawnQueue;
        public JobHandle LastQueueWriter;
        private NativeQueue<DelayedDespawnGhost> m_interpolatedDespawnQueue;
        private NativeQueue<DelayedDespawnGhost> m_predictedDespawnQueue;
    }
}

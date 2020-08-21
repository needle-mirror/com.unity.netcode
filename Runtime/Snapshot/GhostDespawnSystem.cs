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
    public class GhostDespawnSystem : JobComponentSystem
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

        [BurstCompile]
        struct GhostDespawnJob : IJob
        {
            public EntityCommandBuffer commandBuffer;
            public NativeQueue<DelayedDespawnGhost> interpolatedDespawnQueue;
            public NativeQueue<DelayedDespawnGhost> predictedDespawnQueue;
            public NativeHashMap<SpawnedGhost, Entity> spawnedGhostMap;
            public uint interpolatedTick;
            public uint predictedTick;

            public void Execute()
            {
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
            }
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var job = new GhostDespawnJob
            {
                commandBuffer = m_Barrier.CreateCommandBuffer(),
                interpolatedDespawnQueue = m_interpolatedDespawnQueue,
                predictedDespawnQueue = m_predictedDespawnQueue,
                spawnedGhostMap = m_GhostReceiveSystem.SpawnedGhostEntityMap,
                interpolatedTick = m_ClientSimulationSystemGroup.InterpolationTick,
                predictedTick = m_ClientSimulationSystemGroup.ServerTick,
            };
            LastQueueWriter = job.Schedule(JobHandle.CombineDependencies(inputDeps, LastQueueWriter, m_GhostReceiveSystem.LastGhostMapWriter));
            m_Barrier.AddJobHandleForProducer(LastQueueWriter);
            m_GhostReceiveSystem.LastGhostMapWriter = LastQueueWriter;
            return LastQueueWriter;
        }

        internal NativeQueue<DelayedDespawnGhost> InterpolatedDespawnQueue => m_interpolatedDespawnQueue;
        internal NativeQueue<DelayedDespawnGhost> PredictedDespawnQueue => m_predictedDespawnQueue;
        public JobHandle LastQueueWriter;
        private NativeQueue<DelayedDespawnGhost> m_interpolatedDespawnQueue;
        private NativeQueue<DelayedDespawnGhost> m_predictedDespawnQueue;
    }
}

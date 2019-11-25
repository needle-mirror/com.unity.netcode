using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostUpdateSystemGroup))]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    public class GhostDespawnSystem : JobComponentSystem
    {
        public struct DelayedDespawnGhost
        {
            public Entity ghost;
            public uint tick;
        }

        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;

        protected override void OnCreate()
        {
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_ClientSimulationSystemGroup = World.GetOrCreateSystem<ClientSimulationSystemGroup>();
            m_interpolatedDespawnQueue = new NativeQueue<DelayedDespawnGhost>(Allocator.Persistent);
            m_predictedDespawnQueue = new NativeQueue<DelayedDespawnGhost>(Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            LastQueueWriter.Complete();
            m_interpolatedDespawnQueue.Dispose();
            m_predictedDespawnQueue.Dispose();
        }

        struct GhostDespawnJob : IJob
        {
            public EntityCommandBuffer commandBuffer;
            public NativeQueue<DelayedDespawnGhost> interpolatedDespawnQueue;
            public NativeQueue<DelayedDespawnGhost> predictedDespawnQueue;
            public uint interpolatedTick;
            public uint predictedTick;
            public ComponentType ghostType;

            public void Execute()
            {
                while (interpolatedDespawnQueue.Count > 0 &&
                       !SequenceHelpers.IsNewer(interpolatedDespawnQueue.Peek().tick, interpolatedTick))
                {
                    commandBuffer.RemoveComponent(interpolatedDespawnQueue.Dequeue().ghost, ghostType);
                }

                while (predictedDespawnQueue.Count > 0 &&
                       !SequenceHelpers.IsNewer(predictedDespawnQueue.Peek().tick, predictedTick))
                {
                    commandBuffer.RemoveComponent(predictedDespawnQueue.Dequeue().ghost, ghostType);
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
                interpolatedTick = m_ClientSimulationSystemGroup.InterpolationTick,
                predictedTick = m_ClientSimulationSystemGroup.ServerTick,
                ghostType = ComponentType.ReadWrite<GhostComponent>()
            };
            LastQueueWriter = job.Schedule(JobHandle.CombineDependencies(inputDeps, LastQueueWriter));
            m_Barrier.AddJobHandleForProducer(LastQueueWriter);
            return LastQueueWriter;
        }

        public NativeQueue<DelayedDespawnGhost> InterpolatedDespawnQueue => m_interpolatedDespawnQueue;
        public NativeQueue<DelayedDespawnGhost> PredictedDespawnQueue => m_predictedDespawnQueue;
        public JobHandle LastQueueWriter;
        private NativeQueue<DelayedDespawnGhost> m_interpolatedDespawnQueue;
        private NativeQueue<DelayedDespawnGhost> m_predictedDespawnQueue;
    }
}

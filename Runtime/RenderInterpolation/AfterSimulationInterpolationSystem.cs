using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(TransformSystemGroup))]
    public class AfterSimulationInterpolationSystem : JobComponentSystem
    {
        private BeforeSimulationInterpolationSystem beforeSystem;

        // Commands needs to be applied before next simulation is run
        private EndSimulationEntityCommandBufferSystem barrier;
        private EntityQuery positionInterpolationGroup;
        private EntityQuery rotationInterpolationGroup;
        private EntityQuery newPositionInterpolationGroup;
        private EntityQuery newRotationInterpolationGroup;

        protected override void OnCreate()
        {
            positionInterpolationGroup = GetEntityQuery(ComponentType.ReadWrite<CurrentSimulatedPosition>(),
                ComponentType.ReadOnly<PreviousSimulatedPosition>(), ComponentType.ReadOnly<Translation>());
            rotationInterpolationGroup = GetEntityQuery(ComponentType.ReadWrite<CurrentSimulatedRotation>(),
                ComponentType.ReadOnly<PreviousSimulatedRotation>(), ComponentType.ReadOnly<Rotation>());
            newPositionInterpolationGroup = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Translation>(), ComponentType.ReadWrite<CurrentSimulatedPosition>()
                },
                None = new[] {ComponentType.ReadWrite<PreviousSimulatedPosition>()}
            });
            newRotationInterpolationGroup = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] {ComponentType.ReadOnly<Rotation>(), ComponentType.ReadWrite<CurrentSimulatedRotation>()},
                None = new[] {ComponentType.ReadWrite<PreviousSimulatedRotation>()}
            });

            barrier = World.GetOrCreateSystem<EndSimulationEntityCommandBufferSystem>();
            beforeSystem = World.GetOrCreateSystem<BeforeSimulationInterpolationSystem>();

            RequireSingletonForUpdate<FixedClientTickRate>();
        }

        [BurstCompile]
        struct UpdateCurrentPosJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<Translation> positionType;
            public ComponentTypeHandle<CurrentSimulatedPosition> curPositionType;
            public uint simStartComponentVersion;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                // For all chunks where trans has changed since start of simulation
                // Copy trans to currentTrans
                if (ChangeVersionUtility.DidChange(chunk.GetChangeVersion(positionType), simStartComponentVersion))
                {
                    // Transform was interpolated by the rendering system
                    var curPos = chunk.GetNativeArray(curPositionType);
                    var pos = chunk.GetNativeArray(positionType);
                    // FIXME: use a memcopy since size and layout must be identical
                    for (int ent = 0; ent < curPos.Length; ++ent)
                    {
                        curPos[ent] = new CurrentSimulatedPosition {Value = pos[ent].Value};
                    }
                }
            }
        }

        [BurstCompile]
        struct UpdateCurrentRotJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<Rotation> rotationType;
            public ComponentTypeHandle<CurrentSimulatedRotation> curRotationType;
            public uint simStartComponentVersion;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                // For all chunks where trans has changed since start of simulation
                // Copy trans to currentTrans
                if (ChangeVersionUtility.DidChange(chunk.GetChangeVersion(rotationType), simStartComponentVersion))
                {
                    // Transform was interpolated by the rendering system
                    var curRot = chunk.GetNativeArray(curRotationType);
                    var rot = chunk.GetNativeArray(rotationType);
                    // FIXME: use a memcopy since size and layout must be identical
                    for (int ent = 0; ent < curRot.Length; ++ent)
                    {
                        curRot[ent] = new CurrentSimulatedRotation {Value = rot[ent].Value};
                    }
                }
            }
        }

        [BurstCompile]
        struct InitCurrentPosJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<Translation> positionType;
            public ComponentTypeHandle<CurrentSimulatedPosition> curPositionType;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var curPos = chunk.GetNativeArray(curPositionType);
                var pos = chunk.GetNativeArray(positionType);
                var entity = chunk.GetNativeArray(entityType);
                // FIXME: use a memcopy since size and layout must be identical
                for (int ent = 0; ent < curPos.Length; ++ent)
                {
                    var cp = pos[ent];
                    curPos[ent] = new CurrentSimulatedPosition {Value = cp.Value};
                    commandBuffer.AddComponent(chunkIndex, entity[ent],
                        new PreviousSimulatedPosition {Value = cp.Value});
                }
            }
        }

        [BurstCompile]
        struct InitCurrentRotJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<Rotation> rotationType;
            public ComponentTypeHandle<CurrentSimulatedRotation> curRotationType;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var curRot = chunk.GetNativeArray(curRotationType);
                var rot = chunk.GetNativeArray(rotationType);
                var entity = chunk.GetNativeArray(entityType);
                // FIXME: use a memcopy since size and layout must be identical
                for (int ent = 0; ent < curRot.Length; ++ent)
                {
                    var cr = rot[ent];
                    curRot[ent] = new CurrentSimulatedRotation {Value = cr.Value};
                    commandBuffer.AddComponent(chunkIndex, entity[ent],
                        new PreviousSimulatedRotation {Value = cr.Value});
                }
            }
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var handles = new NativeArray<JobHandle>(2, Allocator.Temp);
            var curPosJob = new UpdateCurrentPosJob();
            curPosJob.positionType = GetComponentTypeHandle<Translation>(true);
            curPosJob.curPositionType = GetComponentTypeHandle<CurrentSimulatedPosition>();
            curPosJob.simStartComponentVersion = beforeSystem.simStartComponentVersion;
            handles[0] = curPosJob.Schedule(positionInterpolationGroup, inputDeps);

            var curRotJob = new UpdateCurrentRotJob();
            curRotJob.rotationType = GetComponentTypeHandle<Rotation>(true);
            curRotJob.curRotationType = GetComponentTypeHandle<CurrentSimulatedRotation>();
            curRotJob.simStartComponentVersion = beforeSystem.simStartComponentVersion;
            handles[1] = curRotJob.Schedule(rotationInterpolationGroup, inputDeps);

            var initPosJob = new InitCurrentPosJob();
            initPosJob.positionType = curPosJob.positionType;
            initPosJob.curPositionType = curPosJob.curPositionType;
            initPosJob.entityType = GetEntityTypeHandle();

            var initRotJob = new InitCurrentRotJob();
            initRotJob.rotationType = curRotJob.rotationType;
            initRotJob.curRotationType = curRotJob.curRotationType;
            initRotJob.entityType = initPosJob.entityType;

            if (!newPositionInterpolationGroup.IsEmptyIgnoreFilter ||
                !newRotationInterpolationGroup.IsEmptyIgnoreFilter)
            {
                initPosJob.commandBuffer = barrier.CreateCommandBuffer().AsParallelWriter();
                initRotJob.commandBuffer = barrier.CreateCommandBuffer().AsParallelWriter();
                handles[0] = initPosJob.Schedule(newPositionInterpolationGroup, handles[0]);
                handles[1] = initRotJob.Schedule(newRotationInterpolationGroup, handles[1]);
            }

            beforeSystem.simEndComponentVersion = GlobalSystemVersion;

            var handle = JobHandle.CombineDependencies(handles);
            barrier.AddJobHandleForProducer(handle);
            return handle;
        }
    }
}

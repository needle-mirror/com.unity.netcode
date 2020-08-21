using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.NetCode
{
    public struct RenderInterpolationParameters
    {
        public double startTime;
        public float fixedDeltaTime;
    }

    [UpdateInGroup(typeof(ClientPresentationSystemGroup))]
    public class RenderInterpolationSystem : JobComponentSystem
    {
        // FIXME: should use singleton component
        public static RenderInterpolationParameters parameters;
        private EntityQuery posInterpolationGroup;
        private EntityQuery rotInterpolationGroup;
        private uint lastInterpolationVersion;

        protected override void OnCreate()
        {
            posInterpolationGroup = GetEntityQuery(
                ComponentType.ReadWrite<Translation>(),
                ComponentType.ReadOnly<CurrentSimulatedPosition>(),
                ComponentType.ReadOnly<PreviousSimulatedPosition>());
            rotInterpolationGroup = GetEntityQuery(
                ComponentType.ReadWrite<Rotation>(),
                ComponentType.ReadOnly<CurrentSimulatedRotation>(),
                ComponentType.ReadOnly<PreviousSimulatedRotation>());

            RequireSingletonForUpdate<FixedClientTickRate>();
        }

        [BurstCompile]
        struct PosInterpolateJob : IJobChunk
        {
            public float curWeight;
            public float prevWeight;
            public ComponentTypeHandle<Translation> positionType;
            [ReadOnly] public ComponentTypeHandle<CurrentSimulatedPosition> curPositionType;
            [ReadOnly] public ComponentTypeHandle<PreviousSimulatedPosition> prevPositionType;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                // If current was written after copying it to prev we need to interpolate, otherwise they must be identical
                if (ChangeVersionUtility.DidChange(chunk.GetChangeVersion(curPositionType),
                    chunk.GetChangeVersion(prevPositionType)))
                {
                    var prevPos = chunk.GetNativeArray(prevPositionType);
                    var curPos = chunk.GetNativeArray(curPositionType);
                    var pos = chunk.GetNativeArray(positionType);
                    for (var ent = 0; ent < pos.Length; ++ent)
                    {
                        var p = curPos[ent].Value * curWeight + prevPos[ent].Value * prevWeight;
                        pos[ent] = new Translation {Value = p};
                    }
                }
            }
        }

        [BurstCompile]
        struct RotInterpolateJob : IJobChunk
        {
            public float curWeight;
            public float prevWeight;
            public ComponentTypeHandle<Rotation> rotationType;
            [ReadOnly] public ComponentTypeHandle<CurrentSimulatedRotation> curRotationType;
            [ReadOnly] public ComponentTypeHandle<PreviousSimulatedRotation> prevRotationType;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                // If current was written after copying it to prev we need to interpolate, otherwise they must be identical
                if (ChangeVersionUtility.DidChange(chunk.GetChangeVersion(curRotationType),
                    chunk.GetChangeVersion(prevRotationType)))
                {
                    var prevRot = chunk.GetNativeArray(prevRotationType);
                    var curRot = chunk.GetNativeArray(curRotationType);
                    var rot = chunk.GetNativeArray(rotationType);
                    for (var ent = 0; ent < rot.Length; ++ent)
                    {
                        var a = math.slerp(prevRot[ent].Value, curRot[ent].Value, curWeight);
                        rot[ent] = new Rotation {Value = a};
                    }
                }
            }
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var posInterpolateJob = new PosInterpolateJob();
            var rotInterpolateJob = new RotInterpolateJob();
            posInterpolateJob.positionType = GetComponentTypeHandle<Translation>();
            posInterpolateJob.prevPositionType = GetComponentTypeHandle<PreviousSimulatedPosition>(true);
            posInterpolateJob.curPositionType = GetComponentTypeHandle<CurrentSimulatedPosition>(true);
            rotInterpolateJob.rotationType = GetComponentTypeHandle<Rotation>();
            rotInterpolateJob.prevRotationType = GetComponentTypeHandle<PreviousSimulatedRotation>(true);
            rotInterpolateJob.curRotationType = GetComponentTypeHandle<CurrentSimulatedRotation>(true);

            posInterpolateJob.curWeight = rotInterpolateJob.curWeight =
                (float)(Time.ElapsedTime - parameters.startTime) / parameters.fixedDeltaTime;
            posInterpolateJob.prevWeight = rotInterpolateJob.prevWeight = 1.0f - posInterpolateJob.curWeight;

            lastInterpolationVersion = GlobalSystemVersion;

            JobHandle dep = posInterpolateJob.Schedule(posInterpolationGroup, inputDeps);
            return rotInterpolateJob.Schedule(rotInterpolationGroup, dep);
        }
    }
}

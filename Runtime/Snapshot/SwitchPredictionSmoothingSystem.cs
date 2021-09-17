using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.Burst;
using Unity.Collections;

namespace Unity.NetCode
{
    [WriteGroup(typeof(LocalToWorld))]
    public struct SwitchPredictionSmoothing : IComponentData
    {
        public float3 InitialPosition;
        public quaternion InitialRotation;
        public float CurrentFactor;
        public float Duration;
        public uint SkipVersion;
    }

    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(TRSToLocalToWorldSystem))]
    public partial class SwitchPredictionSmoothingSystem : SystemBase
    {
        private EntityQuery SwitchPredictionSmoothingGroup;
        private EndSimulationEntityCommandBufferSystem CommandBufferSystem;

        protected override void OnCreate()
        {
            SwitchPredictionSmoothingGroup = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    typeof(Translation),
                    typeof(Rotation),
                    typeof(SwitchPredictionSmoothing),
                    typeof(LocalToWorld)
                }
            });
            RequireForUpdate(SwitchPredictionSmoothingGroup);
            CommandBufferSystem = World.GetExistingSystem<EndSimulationEntityCommandBufferSystem>();
        }

        protected override void OnUpdate()
        {
            var deltaTime = Time.DeltaTime;

            Dependency = new SwitchPredictionSmoothingJob
            {
                EntityType = GetEntityTypeHandle(),
                TranslationType = GetComponentTypeHandle<Translation>(true),
                RotationType = GetComponentTypeHandle<Rotation>(true),
                NonUniformScaleType = GetComponentTypeHandle<NonUniformScale>(true),
                ScaleType = GetComponentTypeHandle<Scale>(true),
                CompositeScaleType = GetComponentTypeHandle<CompositeScale>(true),
                SwitchPredictionSmoothingType = GetComponentTypeHandle<SwitchPredictionSmoothing>(),
                LocalToWorldType = GetComponentTypeHandle<LocalToWorld>(),
                DeltaTime = deltaTime,
                AppliedVersion = World.GetExistingSystem<GhostUpdateSystem>().LastSystemVersion,
                CommandBuffer = CommandBufferSystem.CreateCommandBuffer().AsParallelWriter(),
            }.ScheduleParallel(SwitchPredictionSmoothingGroup, Dependency);
            CommandBufferSystem.AddJobHandleForProducer(Dependency);
        }

        [BurstCompile]
        struct SwitchPredictionSmoothingJob : IJobEntityBatch
        {
            [ReadOnly] public EntityTypeHandle EntityType;
            [ReadOnly] public ComponentTypeHandle<Translation> TranslationType;
            [ReadOnly] public ComponentTypeHandle<Rotation> RotationType;
            [ReadOnly] public ComponentTypeHandle<NonUniformScale> NonUniformScaleType;
            [ReadOnly] public ComponentTypeHandle<Scale> ScaleType;
            [ReadOnly] public ComponentTypeHandle<CompositeScale> CompositeScaleType;
            public ComponentTypeHandle<SwitchPredictionSmoothing> SwitchPredictionSmoothingType;
            public ComponentTypeHandle<LocalToWorld> LocalToWorldType;
            public float DeltaTime;
            public uint AppliedVersion;
            public EntityCommandBuffer.ParallelWriter CommandBuffer;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                var hasNonUniformScale = batchInChunk.Has(NonUniformScaleType);
                var hasScale = batchInChunk.Has(ScaleType);
                var hasAnyScale = hasNonUniformScale || hasScale || batchInChunk.Has(CompositeScaleType);

                NativeArray<Translation> positions = batchInChunk.GetNativeArray(TranslationType);
                NativeArray<Rotation> orientations = batchInChunk.GetNativeArray(RotationType);
                NativeArray<NonUniformScale> nonUniformScales = batchInChunk.GetNativeArray(NonUniformScaleType);
                NativeArray<Scale> scales = batchInChunk.GetNativeArray(ScaleType);
                NativeArray<CompositeScale> compositeScales = batchInChunk.GetNativeArray(CompositeScaleType);
                NativeArray<SwitchPredictionSmoothing> switchPredictionSmoothings = batchInChunk.GetNativeArray(SwitchPredictionSmoothingType);
                NativeArray<LocalToWorld> localToWorlds = batchInChunk.GetNativeArray(LocalToWorldType);

                for (int i = 0, count = batchInChunk.Count; i < count; ++i)
                {
                    var currentPosition = positions[i].Value;
                    var currentRotation = orientations[i].Value;

                    var smoothing = switchPredictionSmoothings[i];
                    if (smoothing.SkipVersion != AppliedVersion)
                    {
                        if (smoothing.CurrentFactor == 0)
                        {
                            smoothing.InitialPosition = positions[i].Value - smoothing.InitialPosition;
                            smoothing.InitialRotation = math.mul(orientations[i].Value, math.inverse(smoothing.InitialRotation));
                        }

                        smoothing.CurrentFactor = math.saturate(smoothing.CurrentFactor + DeltaTime / smoothing.Duration);
                        switchPredictionSmoothings[i] = smoothing;
                        if (smoothing.CurrentFactor == 1)
                        {
                            CommandBuffer.RemoveComponent<SwitchPredictionSmoothing>(batchIndex, batchInChunk.GetNativeArray(EntityType)[i]);
                        }

                        currentPosition = currentPosition - math.lerp(smoothing.InitialPosition, new float3(0,0,0), smoothing.CurrentFactor);
                        currentRotation = math.mul(currentRotation, math.inverse(math.slerp(smoothing.InitialRotation, quaternion.identity, smoothing.CurrentFactor)));
                    }

                    var tr = new float4x4(currentRotation, currentPosition);
                    if (hasAnyScale)
                    {
                        var scale = hasNonUniformScale
                            ? float4x4.Scale(nonUniformScales[i].Value)
                            : hasScale
                            ? float4x4.Scale(new float3(scales[i].Value))
                            : compositeScales[i].Value;
                        tr = math.mul(tr, scale);
                    }

                    localToWorlds[i] = new LocalToWorld { Value = tr };
                }
            }
        }
    }
}

using Unity.Assertions;
using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;

namespace Unity.NetCode
{
    /// <summary>
    /// A struct that is temporarily added to a ghosts entity when it switching between predicted / interpolated mode.
    /// Added by <see cref="GhostPredictionSwitchingSystem"/> while processing the <see cref="GhostPredictionSwitchingQueues"/>.
    /// </summary>
    [WriteGroup(typeof(LocalToWorld))]
    public struct SwitchPredictionSmoothing : IComponentData
    {
        /// <summary>
        /// The initial position of the ghost (in world space).
        /// </summary>
        public float3 InitialPosition;
        /// <summary>
        /// The initial rotation of the ghost (in world space).
        /// </summary>
        public quaternion InitialRotation;
        /// <summary>
        /// The smoothing fraction to apply to the current transform. Always in between 0 and 1f.
        /// </summary>
        public float CurrentFactor;
        /// <summary>
        /// The duration in second of the transition. Setup when the component is added and then remain constant.
        /// </summary>
        public float Duration;
        /// <summary>
        /// The current version of the system when the component added to entity.
        /// </summary>
        public uint SkipVersion;
    }

    /// <summary>
    /// System that manage the prediction transition for all ghost that present a <see cref="SwitchPredictionSmoothing"/>
    /// components.
    /// <para>
    /// The system applying a visual smoohting to the ghost, by modifying the entity <see cref="LocalToWorld"/> matrix.
    /// When the transition is completed, the system removes the <see cref="SwitchPredictionSmoothing"/> component.
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(TRSToLocalToWorldSystem))]
    [BurstCompile]
    public partial struct SwitchPredictionSmoothingSystem : ISystem
    {
        EntityQuery m_SwitchPredictionSmoothingQuery;

        EntityTypeHandle m_EntityTypeHandle;
        ComponentTypeHandle<Translation> m_TranslationHandle;
        ComponentTypeHandle<Rotation> m_RotationHandle;
        ComponentTypeHandle<NonUniformScale> m_NonUniformScaleHandle;
        ComponentTypeHandle<Scale> m_ScaleHandle;
        ComponentTypeHandle<CompositeScale> m_CompositeScaleHandle;
        ComponentTypeHandle<SwitchPredictionSmoothing> m_SwitchPredictionSmoothingHandle;
        ComponentTypeHandle<LocalToWorld> m_LocalToWorldHandle;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Translation, Rotation>()
                .WithAllRW<SwitchPredictionSmoothing, LocalToWorld>();
            m_SwitchPredictionSmoothingQuery = state.GetEntityQuery(builder);
            state.RequireForUpdate(m_SwitchPredictionSmoothingQuery);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_TranslationHandle = state.GetComponentTypeHandle<Translation>(true);
            m_RotationHandle = state.GetComponentTypeHandle<Rotation>(true);
            m_NonUniformScaleHandle = state.GetComponentTypeHandle<NonUniformScale>(true);
            m_ScaleHandle = state.GetComponentTypeHandle<Scale>(true);
            m_CompositeScaleHandle = state.GetComponentTypeHandle<CompositeScale>(true);
            m_SwitchPredictionSmoothingHandle = state.GetComponentTypeHandle<SwitchPredictionSmoothing>();
            m_LocalToWorldHandle = state.GetComponentTypeHandle<LocalToWorld>();
        }
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var deltaTime = SystemAPI.Time.DeltaTime;
            var commandBuffer = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);

            m_EntityTypeHandle.Update(ref state);
            m_TranslationHandle.Update(ref state);
            m_RotationHandle.Update(ref state);
            m_NonUniformScaleHandle.Update(ref state);
            m_ScaleHandle.Update(ref state);
            m_CompositeScaleHandle.Update(ref state);
            m_SwitchPredictionSmoothingHandle.Update(ref state);
            m_LocalToWorldHandle.Update(ref state);

            state.Dependency = new SwitchPredictionSmoothingJob
            {
                EntityType = m_EntityTypeHandle,
                TranslationType = m_TranslationHandle,
                RotationType = m_RotationHandle,
                NonUniformScaleType = m_NonUniformScaleHandle,
                ScaleType = m_ScaleHandle,
                CompositeScaleType = m_CompositeScaleHandle,
                SwitchPredictionSmoothingType = m_SwitchPredictionSmoothingHandle,
                LocalToWorldType = m_LocalToWorldHandle,
                DeltaTime = deltaTime,
                AppliedVersion = SystemAPI.GetSingleton<GhostUpdateVersion>().LastSystemVersion,
                CommandBuffer = commandBuffer.AsParallelWriter(),
            }.ScheduleParallel(m_SwitchPredictionSmoothingQuery, state.Dependency);
        }

        [BurstCompile]
        struct SwitchPredictionSmoothingJob : IJobChunk
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

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);

                var hasNonUniformScale = chunk.Has(NonUniformScaleType);
                var hasScale = chunk.Has(ScaleType);
                var hasAnyScale = hasNonUniformScale || hasScale || chunk.Has(CompositeScaleType);

                NativeArray<Translation> positions = chunk.GetNativeArray(TranslationType);
                NativeArray<Rotation> orientations = chunk.GetNativeArray(RotationType);
                NativeArray<NonUniformScale> nonUniformScales = chunk.GetNativeArray(NonUniformScaleType);
                NativeArray<Scale> scales = chunk.GetNativeArray(ScaleType);
                NativeArray<CompositeScale> compositeScales = chunk.GetNativeArray(CompositeScaleType);
                NativeArray<SwitchPredictionSmoothing> switchPredictionSmoothings = chunk.GetNativeArray(SwitchPredictionSmoothingType);
                NativeArray<LocalToWorld> localToWorlds = chunk.GetNativeArray(LocalToWorldType);
                NativeArray<Entity> chunkEntities = chunk.GetNativeArray(EntityType);

                for (int i = 0, count = chunk.Count; i < count; ++i)
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
                            CommandBuffer.RemoveComponent<SwitchPredictionSmoothing>(unfilteredChunkIndex, chunkEntities[i]);
                        }

                        currentPosition -= math.lerp(smoothing.InitialPosition, new float3(0,0,0), smoothing.CurrentFactor);
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

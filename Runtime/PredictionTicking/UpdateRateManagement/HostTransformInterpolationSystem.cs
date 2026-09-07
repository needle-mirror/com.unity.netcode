using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode.NetcodeTime;
using Unity.Transforms;

namespace Unity.Netcode
{
    /// <summary>
    /// Use this component to signal whether you want Netcode to smooth your single world host transforms for you or not.
    /// Added by default on host, but can be stripped in the GhostAuthoring component or by disabling this component.
    /// Not applicable to binary worlds - this component simply will not be added to ghosts on them.
    /// Note: This WriteGroup overrides smoothing applied by Unity.Physics.
    /// </summary>
    [WriteGroup(typeof(LocalToWorld))]
    public struct NetcodeSmoothHostLocalToWorld : IComponentData
    {
        internal float3 LastPosition;
        internal float3 CurrentPosition;
        internal quaternion LastRotation;
        internal quaternion CurrentRotation;
        internal bool HasCache => !math.all(LastRotation.value == 0);
    }

    /// <summary>
    /// On a single world host, partial ticks are not run. So, we need to interpolate ghosts, whenever the tick rate is below the render rate.
    /// Note: This adds roughly one <see cref="ClientServerTickRate.SimulationTickRate"/> tick of input latency to host worlds.
    /// </summary>
    /// <remarks>
    /// This needs to interpolate transforms before the LocalToWorld system runs, so that children world positions are updated correctly.
    /// LocalTransform - which isn't modified here - needs to be set to the authoritative value before the GhostSendSystem runs,
    /// which runs after transform group.
    /// So: IF we were to interpolate LocalTransform, we'd need to do: <c>interpolate LocalTransform -> update LocalToWorld -> revert change to LocalTransform -> send</c>.
    /// Instead, we do the same as the prediction switch smoothing, where we just smooth the LocalToWorld directly, and we don't touch the LocalTransform.
    /// </remarks>
    [UpdateInGroup(typeof(TransformSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(LocalToWorldSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [BurstCompile]
    internal partial struct HostTransformInterpolationSystem : ISystem
    {
        private EntityQuery m_ToInterpolateQuery;

        public void OnCreate(ref SystemState state)
        {
            if (!state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }

            m_ToInterpolateQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<GhostInstance, LocalTransform, LocalToWorld, NetcodeSmoothHostLocalToWorld>());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();

            // TODO - Can't do change filtering the normal way, as we want this to run also in frames where before/after are the same, to interpolate slowly towards the after.
            // Instead, we'd want to store a list of changed chunks ONLY when the prediction group runs, then operate on those.

            state.Dependency = new CacheAndInterpolateLocalToWorldsJob
            {
                NetworkTime = networkTime,
                NetcodeSmoothHostLocalToWorldHandleRW = SystemAPI.GetComponentTypeHandle<NetcodeSmoothHostLocalToWorld>(isReadOnly: false),
                PostTransformMatrixTypeHandleRO = SystemAPI.GetComponentTypeHandle<PostTransformMatrix>(isReadOnly: true),
                EntityTypeHandleRO = SystemAPI.GetEntityTypeHandle(),
                LocalTransformTypeHandleRO = SystemAPI.GetComponentTypeHandle<LocalTransform>(isReadOnly: true),
                LocalToWorldTypeHandleRW = SystemAPI.GetComponentTypeHandle<LocalToWorld>(isReadOnly: false),
            }.ScheduleParallel(m_ToInterpolateQuery, state.Dependency);

            // TODO - for GameObjects, interpolate the GO transform directly.
            // TODO - Possibly allow customization i.e. via relevancy.
        }

        /// <summary>
        /// Job that smoothes the ghost entities LocalToWorld's when on host worlds.
        /// </summary>
        [BurstCompile]
        public unsafe struct CacheAndInterpolateLocalToWorldsJob : IJobChunk
        {
            [ReadOnly] internal NetworkTime NetworkTime;
            [ReadOnly] public ComponentTypeHandle<LocalTransform> LocalTransformTypeHandleRO;
            [ReadOnly] public ComponentTypeHandle<PostTransformMatrix> PostTransformMatrixTypeHandleRO;
            [ReadOnly] public EntityTypeHandle EntityTypeHandleRO;
            public ComponentTypeHandle<NetcodeSmoothHostLocalToWorld> NetcodeSmoothHostLocalToWorldHandleRW;
            public ComponentTypeHandle<LocalToWorld> LocalToWorldTypeHandleRW;

            [BurstCompile]
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var chunkLocalTransforms = (LocalTransform*)chunk.GetRequiredComponentDataPtrRO(ref LocalTransformTypeHandleRO);
                var chunkLocalToWorlds = (LocalToWorld*)chunk.GetRequiredComponentDataPtrRW(ref LocalToWorldTypeHandleRW);
                var chunkPostTransformMatrices = (PostTransformMatrix*)chunk.GetComponentDataPtrRO(ref PostTransformMatrixTypeHandleRO);

                var cache = (NetcodeSmoothHostLocalToWorld*)chunk.GetRequiredComponentDataPtrRW(ref NetcodeSmoothHostLocalToWorldHandleRW);
                var entityIt = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                if (chunkPostTransformMatrices != null)
                {
                    while(entityIt.NextEntityIndex(out var entityIndex))
                    {
                        Interpolate(entityIndex, chunkLocalTransforms, ref cache[entityIndex], out var newTransform);
                        chunkLocalToWorlds[entityIndex].Value = math.mul(newTransform.ToMatrix(), chunkPostTransformMatrices[entityIndex].Value);
                    }
                }
                else
                {
                    while(entityIt.NextEntityIndex(out var entityIndex))
                    {
                        Interpolate(entityIndex, chunkLocalTransforms, ref cache[entityIndex], out var newTransform);
                        chunkLocalToWorlds[entityIndex].Value = newTransform.ToMatrix();
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void Interpolate(int entityIndex, in LocalTransform* chunkLocalTransforms,
                ref NetcodeSmoothHostLocalToWorld cache, out LocalTransform worldTransform)
            {
                ref readonly var localTransform = ref chunkLocalTransforms[entityIndex];
                worldTransform = localTransform;
                bool distanceIsWithinMargin = false;

                if (Hint.Unlikely(!cache.HasCache))
                {
                    cache.LastPosition = cache.CurrentPosition = localTransform.Position;
                    cache.LastRotation = cache.CurrentRotation = localTransform.Rotation;
                }
                else if (!NetworkTime.IsOffFrame)
                {
                    // Interpolation has a hard distance limit. It works the same as GhostField.MaxSmoothingDistance.
                    const float maxSmoothingDistance = 5f; // TODO - Improve when teleportation is a first class feature.
                    distanceIsWithinMargin = math.distancesq(worldTransform.Position, cache.CurrentPosition) < maxSmoothingDistance * maxSmoothingDistance;
                    cache.LastPosition = distanceIsWithinMargin ? cache.CurrentPosition : localTransform.Position;
                    cache.LastRotation = distanceIsWithinMargin ? cache.CurrentRotation : localTransform.Rotation;

                    cache.CurrentPosition = localTransform.Position;
                    cache.CurrentRotation = localTransform.Rotation;
                }
                worldTransform.Position = math.lerp(cache.LastPosition, cache.CurrentPosition, NetworkTime.InterpolationTickFraction);
                worldTransform.Rotation = math.slerp(cache.LastRotation, cache.CurrentRotation, NetworkTime.InterpolationTickFraction);
            }
        }
    }
}

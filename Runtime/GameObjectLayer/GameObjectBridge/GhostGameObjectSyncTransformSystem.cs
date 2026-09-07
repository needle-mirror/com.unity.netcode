using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Jobs;

// Important
// Most of this here will be gone with future engine side transform work

// Systems in this namespace are in charge of the GO<-->Entity transform syncing. They implicitely sync transforms for users. As soon as you have a GhostObject, the transform is synced
// With future TransformRef work, this won't be required.
namespace Unity.Netcode
{
    #region Authoritative transform syncing

    /// <summary>
    /// Signals that the ghost might have smoothed transform values and to reset its transform to the authoritative, tick-accurate value when entering prediction.
    /// This applies to smoothing from any source, such as prediction smoothing or interpolation.
    /// </summary>
    internal struct SmoothingEnabled : IComponentData
    {

    }
    /// <summary>
    /// Entity to GO transform syncing
    /// </summary>
    [BurstCompile]
    internal struct TransformUpdateEntityToGameObjectJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeList<Entity> Entities;
        [ReadOnly] public ComponentLookup<LocalTransform> LocalTransformFromEntity;
        [ReadOnly] public ComponentLookup<PostTransformMatrix> PostTransformMatrixFromEntity;
        [ReadOnly] public ComponentLookup<SmoothingEnabled> SmoothingEnabledFromEntity;

        [BurstCompile]
        public void Execute(int index, TransformAccess transform)
        {
            var ent = Entities[index];
            if (!SmoothingEnabledFromEntity.HasComponent(ent))
                // if there's no smoothing, then we don't need to set the simulation transform back on the GO
                return;
            var trans = LocalTransformFromEntity[ent];

            // for rigidbodies, this assumes we disable auto wakeup before we update transform positions
            transform.localPosition = trans.Position;
            transform.localRotation = trans.Rotation;
            // The uniform LocalTransform.Scale is combined with the non-uniform 3D scale stored in the PostTransformMatrix (when present).
            // SignedScaleFromScaleOnlyMatrix so negative (mirrored) scale is applied back to the GameObject.
            var scale = trans.Scale * Vector3.one;
            if (PostTransformMatrixFromEntity.HasComponent(ent))
                scale = trans.Scale * (Vector3)MatrixScaleHelper.SignedScaleFromScaleOnlyMatrix(PostTransformMatrixFromEntity[ent].Value);
            transform.localScale = scale;
        }
    }
    /// <summary>
    /// Entity to GO transform syncing
    /// </summary>
    internal abstract partial class GhostObjectEntityToGameObjectTransformSystemBase : SystemBase
    {
        protected override void OnUpdate()
        {
            var transformTracking = this.GetEntityQuery(ComponentType.ReadOnly<PerWorldIndexedTransformTrackingSingleton>()).GetSingleton<PerWorldIndexedTransformTrackingSingleton>();

            var transformJob = new TransformUpdateEntityToGameObjectJob
            {
                Entities = transformTracking.m_EntitiesForTransforms,
                LocalTransformFromEntity = GetComponentLookup<LocalTransform>(true),
                PostTransformMatrixFromEntity = GetComponentLookup<PostTransformMatrix>(true),
                SmoothingEnabledFromEntity = GetComponentLookup<SmoothingEnabled>(isReadOnly: true)
            };
            Dependency = transformJob.Schedule(transformTracking.m_Transforms, Dependency);
        }
    }

    /// <summary>
    /// GO to entity transform syncing
    /// </summary>
    [BurstCompile]
    internal struct TransformUpdateGameObjectToEntityJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeList<Entity> Entities;
        [NativeDisableParallelForRestriction] public ComponentLookup<LocalTransform> LocalTransformFromEntity;
        [NativeDisableParallelForRestriction] public ComponentLookup<PostTransformMatrix> PostTransformMatrixFromEntity;
        [BurstCompile]
        public void Execute(int index, TransformAccess transform)
        {
            var ent = Entities[index];
            // Capture the GameObject's (potentially non-uniform, potentially negative) 3D scale into the
            // PostTransformMatrix. The GO layer owns this matrix and always writes it as a pure scale matrix:
            // building it is cheaper than decompose-and-preserve, and unconditionally stable for negative scale.
            // (Consequence: rotation/shear a user system authored into a GhostObject's PostTransformMatrix is NOT
            // preserved; that contract only exists on the ECS-side replication apply path, see SetScale.)
            // LocalTransform.Scale must then stay 1: consumers combine Scale * PostTransformMatrix scale, so storing
            // the scale in both would apply it twice. Without a PostTransformMatrix, fall back to uniform scale.
            var uniformScale = transform.localScale.x;
            if (PostTransformMatrixFromEntity.HasComponent(ent))
            {
                PostTransformMatrixFromEntity[ent] = new PostTransformMatrix { Value = float4x4.Scale((float3)transform.localScale) };
                uniformScale = 1f;
            }
            LocalTransformFromEntity[ent] = new LocalTransform
            {
                Position = transform.localPosition,
                Rotation = transform.localRotation,
                Scale = uniformScale,
            };
        }
    }

    /// <summary>
    /// GO to entity transform syncing
    /// </summary>
    // TODO-release minor perf improvement, we can probably remove that system inheritance and just have each child system launch the job themselves.
    internal abstract partial class GhostObjectGameObjectToEntityTransformSystemBase : SystemBase
    {
        protected override void OnUpdate()
        {
            var transformTracking = this.GetEntityQuery(ComponentType.ReadOnly<PerWorldIndexedTransformTrackingSingleton>()).GetSingleton<PerWorldIndexedTransformTrackingSingleton>();

            var transformJob = new TransformUpdateGameObjectToEntityJob
            {
                Entities = transformTracking.m_EntitiesForTransforms,
                LocalTransformFromEntity = GetComponentLookup<LocalTransform>(),
                PostTransformMatrixFromEntity = GetComponentLookup<PostTransformMatrix>()
            };
            Dependency = transformJob.ScheduleReadOnly(transformTracking.m_Transforms, 16, Dependency); // TODO-release test batch size
        }
    }

    /// <summary>
    /// GO to entity transform syncing
    /// Copy game object transforms to entities on the server so they are sent
    /// </summary>
    [UpdateInGroup(typeof(TransformSystemGroup), OrderFirst = true)] // this needs to update before GhostSendSystem and LocalToWorld
    [UpdateBefore(typeof(HostTransformInterpolationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class GhostObjectGameObjectToEntityTransformSystem : GhostObjectGameObjectToEntityTransformSystemBase
    {}

    /// <summary>
    /// GO to entity transform syncing
    /// Copy entities transforms to game objects on the client so they are received
    /// </summary>
    [UpdateInGroup(typeof(GhostGameObjectSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)] // both host and standalone client. On host to set back the ghost to its authoritative transform after it was smoothed last frame. This shouldn't happen on server, as we still do want to allow updating transforms from outside the prediction loop
    internal partial class GhostObjectEntityToGameObjectTransformSystem : GhostObjectEntityToGameObjectTransformSystemBase
    { }


    /// <summary>
    /// Entity to GO transform syncing for prediction
    /// </summary>
    // TODO-release FIXME: better to handle these in GhostBehaviourPredictionSystem?
    // TODO-release we'll need to filter the transform list in the job for predicted ghosts only, since right now it'll do a bunch of useless copies on interpolated ghosts as well. We'll probably need to maintain two lists for this.
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(GhostBehaviourPredictionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateBefore(typeof(PredictedFixedStepSimulationSystemGroup))] // so physics prediction benefits from this as well
    internal partial class PredictedGhostObjectEntityToGameObjectTransformSystem : GhostObjectEntityToGameObjectTransformSystemBase
    {
        // We should never have entity to GO in a server/host ? What if we have a hybrid model, where user systems update the GO transform? Meh will be irrelevant once we have unified transform.
        protected override void OnCreate()
        {
            base.OnCreate();
            if (World.IsHost()) Enabled = false;
        }
    }

    /// <summary>
    /// GO to Entity transform syncing for prediction
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostBehaviourPredictionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class PredictedGhostObjectGameObjectToEntityTransformSystem : GhostObjectGameObjectToEntityTransformSystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            if (World.IsHost()) Enabled = false;
        }
    }
    #endregion

    #region Visual transform syncing

    /// <summary>
    /// Entity to GO LocalToWorld transform syncing (for smoothing related to prediction switching, host transform smoothing or prediction error smoothing)
    /// </summary>
    // TODO-release@potentialOptim do some change filtering on this, so it doesn't run on ALL GOs, but only on those that need smoothing?
    [BurstCompile]
    internal struct TransformLocalToWorldUpdateEntityToGameObjectJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeList<Entity> Entities;
        [ReadOnly] public ComponentLookup<LocalToWorld> LocalToWorldFromEntity;
        [ReadOnly] public ComponentLookup<LocalTransform> LocalTransformFromEntity;
        [ReadOnly] public ComponentLookup<PostTransformMatrix> PostTransformMatrixFromEntity;

        [BurstCompile]
        public void Execute(int index, TransformAccess transform)
        {

            var ent = Entities[index];
            var ltw = LocalToWorldFromEntity[ent];

            transform.localPosition = ltw.Position;

            // LocalToWorld is smoothed, but WHICH axes are mirrored is not: a mirror flip is discrete, there is nothing
            // to interpolate. So take the magnitudes from the smoothed matrix and the signs from the authoritative
            // (unsmoothed) LocalTransform/PostTransformMatrix, giving the GameObject the same signs the server authored.
            // Un-mirroring the basis before extracting the rotation is what keeps that rotation honest: orthonormalize
            // cannot represent a reflection, so handed a mirrored basis it invents a 180 degree rotation the ghost never
            // had (and pushes the mirror onto z). That is visible to anything reading rotation or scale on its own,
            // e.g. a ghost replicating position + scale but not rotation.
            var authoritativeScale = new float3(LocalTransformFromEntity[ent].Scale);
            if (PostTransformMatrixFromEntity.HasComponent(ent))
                authoritativeScale *= MatrixScaleHelper.SignedScaleFromScaleOnlyMatrix(PostTransformMatrixFromEntity[ent].Value);
            var signs = new float3(
                authoritativeScale.x < 0f ? -1f : 1f,
                authoritativeScale.y < 0f ? -1f : 1f,
                authoritativeScale.z < 0f ? -1f : 1f);

            var unmirrored = ltw.Value;
            unmirrored.c0.xyz *= signs.x;
            unmirrored.c1.xyz *= signs.y;
            unmirrored.c2.xyz *= signs.z;

            transform.localRotation = unmirrored.Rotation();
            transform.localScale = (Vector3)(signs * ltw.Value.Scale());
        }
    }

    /// <summary>
    /// Writes to the GameObject position for all the GameObject ghosts according to its smoothed value (from LocalToWorld). Useful for various
    /// netcode smoothing points, like single world host authority smoothing, prediction switching or prediction error correction smoothing.
    /// The game object transform will be at this point out-of-sync (just for rendering purpose)
    /// It is always possible to get the true transform from
    /// <see cref="GhostObject.GhostTransform"/>
    /// <see cref="GhostObject.Position"/>
    /// <see cref="GhostObject.Rotation"/>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [CreateAfter(typeof(GhostPredictionSmoothingSystem))]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    unsafe partial class GhostObjectEntityToGameObjectLocalToWorldSmoothedTransformSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var transformTracking = this.GetEntityQuery(ComponentType.ReadOnly<PerWorldIndexedTransformTrackingSingleton>()).GetSingleton<PerWorldIndexedTransformTrackingSingleton>();

            var job = new TransformLocalToWorldUpdateEntityToGameObjectJob()
            {
                Entities = transformTracking.m_EntitiesForTransforms,
                LocalToWorldFromEntity = GetComponentLookup<LocalToWorld>(isReadOnly: true),
                LocalTransformFromEntity = GetComponentLookup<LocalTransform>(isReadOnly: true),
                PostTransformMatrixFromEntity = GetComponentLookup<PostTransformMatrix>(isReadOnly: true),
            };
            Dependency = job.Schedule(transformTracking.m_Transforms, Dependency);
        }
    }

    #endregion
}

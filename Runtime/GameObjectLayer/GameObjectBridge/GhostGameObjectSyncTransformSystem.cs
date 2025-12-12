#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Jobs;

// Important
// Most of this here will be gone with future engine side transform work

// Systems in this namespace are in charge of the GO<-->Entity transform syncing. They implicitely sync transforms for users. As soon as you have a GhostAdapter, the transform is synced
// With future TransformRef work, this won't be required.
namespace Unity.NetCode
{
    /// <summary>
    /// Entity to GO transform syncing
    /// </summary>
    [BurstCompile]
    internal struct TransformUpdateEntityToGameObjectJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeList<Entity> Entities;
        [ReadOnly] public ComponentLookup<LocalTransform> LocalTransformFromEntity;
        [ReadOnly] public ComponentLookup<PostTransformMatrix> PostTransformMatrixFromEntity;

        [BurstCompile]
        public void Execute(int index, TransformAccess transform)
        {
            var ent = Entities[index];
            var trans = LocalTransformFromEntity[ent];
            var mtx = PostTransformMatrixFromEntity[ent];
            transform.localPosition = trans.Position;
            transform.localRotation = trans.Rotation;
            transform.localScale = new Vector3(mtx.Value.c0.x, mtx.Value.c1.y, mtx.Value.c2.z);
        }
    }
    /// <summary>
    /// Entity to GO transform syncing
    /// </summary>
    internal abstract partial class GhostAdapterEntityToGameObjectTransformSystemBase : SystemBase
    {
        protected override void OnUpdate()
        {
            var transformTracking = this.GetEntityQuery(ComponentType.ReadOnly<PerWorldIndexedTransformTrackingSingleton>()).GetSingleton<PerWorldIndexedTransformTrackingSingleton>();

            var transformJob = new TransformUpdateEntityToGameObjectJob
            {
                Entities = transformTracking.m_EntitiesForTransforms,
                LocalTransformFromEntity = GetComponentLookup<LocalTransform>(true),
                PostTransformMatrixFromEntity = GetComponentLookup<PostTransformMatrix>(true)
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
            PostTransformMatrixFromEntity[ent] = new PostTransformMatrix {Value = float4x4.Scale(transform.localScale)};
            LocalTransformFromEntity[ent] = new LocalTransform
            {
                Position = transform.localPosition,
                Rotation = transform.localRotation,
                Scale = 1f
            };
        }
    }

    /// <summary>
    /// GO to entity transform syncing
    /// </summary>
    // TODO-release minor perf improvement, we can probably remove that system inheritance and just have each child system launch the job themselves.
    internal abstract partial class GhostAdapterGameObjectToEntityTransformSystemBase : SystemBase
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
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast=true)] // UpdateBefore is not enough, OrderLast is required to be as close to the send as possible, to give time for the GO to update its transform
    [UpdateBefore(typeof(GhostSendSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class GhostAdapterGameObjectToEntityTransformSystem : GhostAdapterGameObjectToEntityTransformSystemBase
    {}

    /// <summary>
    /// GO to entity transform syncing
    /// Copy entities transforms to game objects on the client so they are received
    /// </summary>
    [UpdateInGroup(typeof(GhostGameObjectSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class GhostAdapterEntityToGameObjectTransformSystem : GhostAdapterEntityToGameObjectTransformSystemBase
    {
        // We should never have entity to GO in a server/host ? What if we have a hybrid model, where user systems update the GO transform? Meh will be irrelevant once we have unified transform.
        protected override void OnCreate()
        {
            base.OnCreate();
            if (World.IsHost()) Enabled = false;
        }
    }


    /// <summary>
    /// Entity to GO transform syncing for prediction
    /// </summary>
    // TODO-release FIXME: better to handle these in GhostBehaviourPredictionSystem?
    // TODO-release we'll need to filter the transform list in the job for predicted ghosts only, since right now it'll do a bunch of useless copies on interpolated ghosts as well. We'll probably need to maintain two lists for this.
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(GhostBehaviourPredictionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateBefore(typeof(PredictedFixedStepSimulationSystemGroup))] // so physics prediction benefits from this as well
    internal partial class PredictedGhostAdapterEntityToGameObjectTransformSystem : GhostAdapterEntityToGameObjectTransformSystemBase
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
    internal partial class PredictedGhostAdapterGameObjectToEntityTransformSystem : GhostAdapterGameObjectToEntityTransformSystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            if (World.IsHost()) Enabled = false;
        }
    }
}
#endif

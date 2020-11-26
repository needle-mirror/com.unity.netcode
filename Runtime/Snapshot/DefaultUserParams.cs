using AOT;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Unity.NetCode
{
    /*
        Example 1:
        This registers the DefaultTranslationSmoothingAction for Translation on your predicted Ghost

        var smoothing = world.GetExistingSystem<GhostPredictionSmoothingSystem>();
        smoothing?.RegisterSmoothingAction<Translation>(DefaultTranslateSmoothingAction.Action);

        Example 2:
        Here we also register the DefaultUserParamsComponent as user data. Note the DefautlUserParamsComponent must be
        attached to your PredictedGhost.

        var smoothing = world.GetExistingSystem<GhostPredictionSmoothingSystem>();
        smoothing?.RegisterSmoothingAction<Translation, DefaultUserParams>(DefaultTranslateSmoothingAction.Action);
    */

    [GenerateAuthoringComponent]
    [GhostComponent(PrefabType = GhostPrefabType.PredictedClient)]
    public struct DefaultUserParams : IComponentData
    {
        public float maxDist;
        public float delta;
    }

    [BurstCompile]
    public unsafe struct DefaultTranslateSmoothingAction
    {
        public abstract class DefaultStaticUserParams
        {
            public static readonly SharedStatic<float> maxDist = SharedStatic<float>.GetOrCreate<DefaultStaticUserParams, MaxDistKey>();
            public static readonly SharedStatic<float> delta = SharedStatic<float>.GetOrCreate<DefaultStaticUserParams, DeltaKey>();

            static DefaultStaticUserParams()
            {
                maxDist.Data = 10;
                delta.Data = 1;
            }
            class MaxDistKey {}
            class DeltaKey {}
        }

        public static PortableFunctionPointer<GhostPredictionSmoothingSystem.SmoothingActionDelegate>
            Action =
                new PortableFunctionPointer<GhostPredictionSmoothingSystem.SmoothingActionDelegate>(SmoothingAction);

        [BurstCompile]
        [MonoPInvokeCallback(typeof(GhostPredictionSmoothingSystem.SmoothingActionDelegate))]
        private static void SmoothingAction(void* currentData, void* previousData, void* usrData)
        {
            ref var trans = ref UnsafeUtility.AsRef<Translation>(currentData);
            ref var backup = ref UnsafeUtility.AsRef<Translation>(previousData);

            float maxDist = DefaultStaticUserParams.maxDist.Data;
            float delta = DefaultStaticUserParams.delta.Data;

            if (usrData != null)
            {
                ref var userParam = ref UnsafeUtility.AsRef<DefaultUserParams>(usrData);
                maxDist = userParam.maxDist;
                delta = userParam.delta;
            }

            var dist = math.distance(trans.Value, backup.Value);
            if (dist < maxDist && dist > delta && dist > 0)
            {
                trans.Value = backup.Value + (trans.Value - backup.Value) * delta / dist;
            }
        }
    }
}
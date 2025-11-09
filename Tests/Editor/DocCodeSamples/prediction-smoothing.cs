using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace DocumentationCodeSamples
{
    [BurstCompile]
    unsafe partial class prediction_smoothing
    {
        public partial struct SmoothingExampleSystem : ISystem
        {
            public void OnUpdate(ref SystemState state)
            {
                #region SmoothingRegistration
                var ghostPredictionSmoothing = SystemAPI.GetSingleton<GhostPredictionSmoothing>();
                //pass null as user data
                ghostPredictionSmoothing.RegisterSmoothingAction<LocalTransform>(state.EntityManager, CustomSmoothing.Action);
                //will pass as user data a pointer to a MySmoothingActionParams chunk component
                ghostPredictionSmoothing.RegisterSmoothingAction<LocalTransform, CustomSmoothingActionParams>(state.EntityManager, CustomSmoothing.Action);
                #endregion
            }
        }

        [GhostComponent(PrefabType = GhostPrefabType.PredictedClient)]
        public struct CustomSmoothingActionParams : IComponentData
        {
        }

        #region CustomSmoothingAction
        [BurstCompile]
        public unsafe class CustomSmoothing
        {
            public static readonly PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>
                Action =
                    new PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>(SmoothingAction);

            [BurstCompile(DisableDirectCall = true)]
            private static void SmoothingAction(IntPtr currentData, IntPtr previousData, IntPtr userData)
            {
                ref var trans = ref UnsafeUtility.AsRef<LocalTransform>(currentData.ToPointer());
                ref var backup = ref UnsafeUtility.AsRef<LocalTransform>(previousData.ToPointer());

                var dist = math.distance(trans.Position, backup.Position);
                //UnityEngine.Debug.Log($"Custom smoothing, diff {trans.Value - backup.Value}, dist {dist}");
                if (dist > 0)
                    trans.Position = backup.Position + (trans.Position - backup.Position) / dist;
            }
        }
        #endregion
    }
}

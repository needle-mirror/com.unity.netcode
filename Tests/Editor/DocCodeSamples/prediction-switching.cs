using Unity.Entities;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    partial class prediction_switching
    {
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        public partial struct PredictionSwitchingExampleSystem : ISystem
        {
            public void OnUpdate(ref SystemState state)
            {
                // Use dummy entities for the example to compile.
                var entityA = new Entity();
                var entityB = new Entity();

                #region PredictionSwitching
                // Fetch the singleton as RW as we're modifying singleton collection data.
                ref var ghostPredictionSwitchingQueues = ref SystemAPI.GetSingletonRW<GhostPredictionSwitchingQueues>().ValueRW;

                // Converts ghost entityA to Predicted, instantly (i.e. as soon as the `GhostPredictionSwitchingSystem` runs). If this entity is moving, it will teleport.
                ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = entityA,
                    TransitionDurationSeconds = 0f,
                });

                // Converts ghost entityB to Interpolated, over 1 second.
                // A lerp is applied to the Transform (both Position and Rotation) automatically, smoothing (and somewhat hiding) the change in timelines.
                ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = entityB,
                    TransitionDurationSeconds = 1f,
                });
                #endregion
            }
        }
    }
}

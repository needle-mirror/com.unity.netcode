using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace DocumentationCodeSamples
{
    partial class prediction_n4e
    {
        public struct MyCommandInput : ICommandData
        {
            public NetworkTick Tick { get; set; }
        }

        public partial struct PredictionExampleSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.RequireForUpdate<NetworkTime>();
            }

            public void OnUpdate(ref SystemState state)
            {
                #region PredictionQuery
                foreach (var localTransform in SystemAPI.Query<RefRW<LocalTransform>>().WithAll<PredictedGhost, Simulate>())
                {
                    // Your update logic here
                }
                #endregion

                #region ShouldPredict
                var serverTick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
                foreach (var (localTransform, predictedGhost) in SystemAPI.Query<RefRW<LocalTransform>, RefRW<PredictedGhost>>().WithAll<Simulate>())
                {
                    if (!predictedGhost.ValueRW.ShouldPredict(serverTick))
                        return;

                    // Your update logic here
                }
                #endregion

                #region IInputComponentData
                foreach (var (localTransform, input, entity) in SystemAPI.Query<RefRW<LocalTransform>, RefRO<MyInput>>().WithEntityAccess())
                {
                    // Your update logic here
                }
                #endregion

                #region Commands
                var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
                foreach (var (localTransform, inputBuffer, entity) in SystemAPI.Query<RefRW<LocalTransform>, DynamicBuffer<MyCommandInput>>().WithEntityAccess())
                {
                    if (!inputBuffer.GetDataAtTick(tick, out var input))
                        return;
                }
                #endregion
            }
        }
    }
}

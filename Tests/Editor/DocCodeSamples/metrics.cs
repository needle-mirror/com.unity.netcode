using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    partial class metrics
    {
        public partial struct MetricsSystem : ISystem
        {
            #region MetricsSystem
            public void OnCreate(ref SystemState state)
            {
                var typeList = new NativeArray<ComponentType>(8, Allocator.Temp);
                typeList[0] = ComponentType.ReadWrite<GhostMetricsMonitor>();
                typeList[1] = ComponentType.ReadWrite<NetworkMetrics>();
                typeList[2] = ComponentType.ReadWrite<SnapshotMetrics>();
                typeList[3] = ComponentType.ReadWrite<GhostNames>();
                typeList[4] = ComponentType.ReadWrite<GhostMetrics>();
                typeList[5] = ComponentType.ReadWrite<GhostSerializationMetrics>();
                typeList[6] = ComponentType.ReadWrite<PredictionErrorNames>();
                typeList[7] = ComponentType.ReadWrite<PredictionErrorMetrics>();

                var metricSingleton = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(typeList));
                FixedString64Bytes singletonName = "MetricsMonitor";
                state.EntityManager.SetName(metricSingleton, singletonName);
            }
            #endregion

            public void Method()
            {
                #region GetNetworkMetrics
                var networkMetrics = SystemAPI.GetSingleton<NetworkMetrics>();
                #endregion
            }
        }
    }
}

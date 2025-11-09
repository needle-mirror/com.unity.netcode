using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    partial class network_protocol_checks // Partial due to ISystem sourcegen creating a partial class of this
    {
        #region DisablingProtocolChecks
        [BurstCompile] // BurstCompile is optional
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
        [UpdateInGroup(typeof(InitializationSystemGroup))]
        [CreateAfter(typeof(RpcSystem))]
        public partial struct SetRpcSystemDynamicAssemblyListSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                SystemAPI.GetSingletonRW<RpcCollection>().ValueRW.DynamicAssemblyList = true;
                state.Enabled = false;
            }
        }
        #endregion
    }
}

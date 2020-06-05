using Unity.Burst;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    public struct NetworkIdComponent : IComponentData
    {
        public int Value;
    }

    [BurstCompile]
    internal struct RpcSetNetworkId : IRpcCommand
    {
        public int nid;
        public int simTickRate;
        public int netTickRate;
        public int simMaxSteps;

        public void Serialize(ref DataStreamWriter writer)
        {
            writer.WriteInt(nid);
            writer.WriteInt(simTickRate);
            writer.WriteInt(netTickRate);
            writer.WriteInt(simMaxSteps);
        }

        public void Deserialize(ref DataStreamReader reader)
        {
            nid = reader.ReadInt();
            simTickRate = reader.ReadInt();
            netTickRate = reader.ReadInt();
            simMaxSteps = reader.ReadInt();
        }

        [BurstCompile]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var rpcData = default(RpcSetNetworkId);
            rpcData.Deserialize(ref parameters.Reader);

            parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new NetworkIdComponent {Value = rpcData.nid});
            var ent = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, ent, new ClientServerTickRateRefreshRequest
            {
                MaxSimulationStepsPerFrame = rpcData.simMaxSteps,
                NetworkTickRate = rpcData.netTickRate,
                SimulationTickRate = rpcData.simTickRate
            });
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }
}

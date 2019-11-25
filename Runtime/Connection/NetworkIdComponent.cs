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

        public void Serialize(DataStreamWriter writer)
        {
            writer.Write(nid);
            writer.Write(simTickRate);
            writer.Write(netTickRate);
            writer.Write(simMaxSteps);
        }

        public void Deserialize(DataStreamReader reader, ref DataStreamReader.Context ctx)
        {
            nid = reader.ReadInt(ref ctx);
            simTickRate = reader.ReadInt(ref ctx);
            netTickRate = reader.ReadInt(ref ctx);
            simMaxSteps = reader.ReadInt(ref ctx);
        }

        [BurstCompile]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var rpcData = default(RpcSetNetworkId);
            rpcData.Deserialize(parameters.Reader, ref parameters.ReaderContext);

            parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new NetworkIdComponent {Value = rpcData.nid});
            var ent = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, ent, new ClientServerTickRate
            {
                MaxSimulationStepsPerFrame = rpcData.simMaxSteps,
                NetworkTickRate = rpcData.netTickRate,
                SimulationTickRate = rpcData.simTickRate
            });
        }

        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        }
    }
}

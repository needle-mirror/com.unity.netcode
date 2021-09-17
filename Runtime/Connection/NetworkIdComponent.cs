using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    public struct NetworkIdComponent : IComponentData
    {
        public int Value;
    }

    [BurstCompile]
    internal struct RpcSetNetworkId : IComponentData, IRpcCommandSerializer<RpcSetNetworkId>
    {
        public int nid;
        public int simTickRate;
        public int netTickRate;
        public int simMaxSteps;
        public int simMaxStepLength;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in RpcSetNetworkId data)
        {
            writer.WriteInt(data.nid);
            writer.WriteInt(data.simTickRate);
            writer.WriteInt(data.netTickRate);
            writer.WriteInt(data.simMaxSteps);
            writer.WriteInt(data.simMaxStepLength);
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref RpcSetNetworkId data)
        {
            data.nid = reader.ReadInt();
            data.simTickRate = reader.ReadInt();
            data.netTickRate = reader.ReadInt();
            data.simMaxSteps = reader.ReadInt();
            data.simMaxStepLength = reader.ReadInt();
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var rpcData = default(RpcSetNetworkId);
            var rpcSerializer = default(RpcSetNetworkId);
            rpcSerializer.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);

            parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new NetworkIdComponent {Value = rpcData.nid});
            var ent = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, ent, new ClientServerTickRateRefreshRequest
            {
                MaxSimulationStepsPerFrame = rpcData.simMaxSteps,
                NetworkTickRate = rpcData.netTickRate,
                SimulationTickRate = rpcData.simTickRate,
                MaxSimulationLongStepTimeMultiplier = rpcData.simMaxStepLength
            });
            parameters.CommandBuffer.SetName(parameters.JobIndex, parameters.Connection, new FixedString64Bytes(FixedString.Format("NetworkConnection ({0})", rpcData.nid)));
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }
}

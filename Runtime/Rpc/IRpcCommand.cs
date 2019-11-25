using Unity.Burst;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    public interface IRpcCommand : IComponentData
    {
        void Serialize(DataStreamWriter writer);
        void Deserialize(DataStreamReader reader, ref DataStreamReader.Context ctx);
        PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute();
    }
}

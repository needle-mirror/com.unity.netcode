using Unity.Burst;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    public interface IRpcCommand : IComponentData
    {
        void Serialize(ref DataStreamWriter writer);
        void Deserialize(ref DataStreamReader reader);
        PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute();
    }
}

using Unity.Burst;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    public interface IRpcCommand : IComponentData
    {}
    public interface IRpcCommandSerializer<T> where T: struct, IComponentData
    {
        void Serialize(ref DataStreamWriter writer, in T data);
        void Deserialize(ref DataStreamReader reader, ref T data);
        PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute();
    }
}

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    public interface IRpcCommand : IComponentData
    {}

    public struct RpcSerializerState
    {
        public ComponentDataFromEntity<GhostComponent> GhostFromEntity;
    }

    public struct RpcDeserializerState
    {
        public NativeHashMap<SpawnedGhost, Entity> ghostMap;
    }

    public interface IRpcCommandSerializer<T> where T: struct, IComponentData
    {
        void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in T data);
        void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref T data);
        PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute();
    }
}

using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    public interface IGhostDeserializerCollection
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        string[] CreateSerializerNameList();
        int Length { get; }
#endif
        void Initialize(World world);

        void BeginDeserialize(JobComponentSystem system);

        bool Deserialize(int serializer, Entity entity, uint snapshot, uint baseline, uint baseline2, uint baseline3,
            DataStreamReader reader,
            ref DataStreamReader.Context ctx, NetworkCompressionModel compressionModel);

        void Spawn(int serializer, int ghostId, uint snapshot, DataStreamReader reader,
            ref DataStreamReader.Context ctx, NetworkCompressionModel compressionModel);
    }
}

using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    public interface IGhostSerializerCollection
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        string[] CreateSerializerNameList();
        int Length { get; }
#endif
        void BeginSerialize(ComponentSystemBase system);

        int CalculateImportance(int serializer, ArchetypeChunk chunk);

        int GetSnapshotSize(int serializer);

        int Serialize(ref DataStreamWriter dataStream, SerializeData data);
    }
}

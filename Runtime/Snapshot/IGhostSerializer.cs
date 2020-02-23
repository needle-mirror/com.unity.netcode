using Unity.Entities;

namespace Unity.NetCode
{
    public interface IGhostSerializer<T> where T : struct, ISnapshotData<T>
    {
        int SnapshotSize { get; }
        int CalculateImportance(ArchetypeChunk chunk);
        void BeginSerialize(ComponentSystemBase system);

        void CopyToSnapshot(ArchetypeChunk chunk, int ent, uint tick, ref T snapshot,
            GhostSerializerState serializerState);
    }
}

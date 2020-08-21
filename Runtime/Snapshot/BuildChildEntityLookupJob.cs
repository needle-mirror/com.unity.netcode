using Unity.Entities;
using Unity.Collections;

namespace Unity.NetCode
{
    internal struct EntityChunkLookup
    {
        public ArchetypeChunk chunk;
        public int index;
    }
    internal struct BuildChildEntityLookupJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle entityType;
        public NativeHashMap<Entity, EntityChunkLookup>.ParallelWriter childEntityLookup;
        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var entities = chunk.GetNativeArray(entityType);
            for (int i = 0; i < entities.Length; ++i)
            {
                childEntityLookup.TryAdd(entities[i], new EntityChunkLookup {chunk = chunk, index = i});
            }
        }
    }
}

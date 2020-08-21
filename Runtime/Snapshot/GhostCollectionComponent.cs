using Unity.Entities;
using Unity.Collections;

namespace Unity.NetCode
{
    public struct GhostPrefabBuffer : IBufferElementData
    {
        public Entity Value;
    }
    public struct GhostMetaDataBuffer : IBufferElementData
    {
        public int Importance;
        public FixedString32 Name;
    }

    public struct GhostPrefabCollectionComponent : IComponentData
    {
        public Entity serverPrefabs;
        public Entity clientInterpolatedPrefabs;
        public Entity clientPredictedPrefabs;
        public Entity ghostMetaData;
    }
}

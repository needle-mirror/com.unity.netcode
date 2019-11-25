using Unity.Entities;

namespace Unity.NetCode
{
    public struct GhostPrefabBuffer : IBufferElementData
    {
        public Entity Value;
    }

    public struct GhostPrefabCollectionComponent : IComponentData
    {
        public Entity serverPrefabs;
        public Entity clientInterpolatedPrefabs;
        public Entity clientPredictedPrefabs;
    }
}

using Unity.Entities;
using UnityEngine;

namespace Unity.Netcode.Tests
{
    struct SubSceneGhostSpawner : IComponentData
    {
        public Entity Prefab;
        public Entity SecondPrefab;
    }

    class GhostSpawnerAuthoring : MonoBehaviour
    {
        public GameObject Prefab;
        public GameObject SecondPrefab;

        class Baker : Baker<GhostSpawnerAuthoring>
        {
            public override void Bake(GhostSpawnerAuthoring authoring)
            {
                var component = new SubSceneGhostSpawner
                {
                    Prefab = GetEntity(authoring.Prefab, TransformUsageFlags.Dynamic),
                    SecondPrefab = GetEntity(authoring.SecondPrefab, TransformUsageFlags.Dynamic),
                };
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, component);
            }
        }
    }
}

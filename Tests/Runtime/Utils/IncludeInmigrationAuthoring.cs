using Unity.Entities;
using UnityEngine;
using Unity.NetCode.HostMigration;

namespace Unity.NetCode.Tests
{
    internal class IncludeInMigrationAuthoring : MonoBehaviour
    {
    }

    class IncludeInMigrationAuthoringBaker : Baker<IncludeInMigrationAuthoring>
    {
        public override void Bake(IncludeInMigrationAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new IncludeInMigration{});
        }
    }
}

using Unity.Entities;
using UnityEngine;
using Unity.NetCode.HostMigration;

namespace Unity.NetCode.Tests
{
    internal class NonGhostMigrationDataAuthoring : MonoBehaviour
    {
    }

    class NonGhostMigrationDataAuthoringBaker : Baker<NonGhostMigrationDataAuthoring>
    {
        public override void Bake(NonGhostMigrationDataAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new NonGhostTestData {Value = 0});
        }
    }
}

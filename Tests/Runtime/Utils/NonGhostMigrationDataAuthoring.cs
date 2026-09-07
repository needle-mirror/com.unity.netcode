using Unity.Entities;
using UnityEngine;
using Unity.Netcode.HostMigration;

namespace Unity.Netcode.Tests
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

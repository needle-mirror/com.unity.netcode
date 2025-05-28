using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    // used to verify ghost id's and spawn ticks are consistent across migrations
    internal struct GhostIdAndTickChecker : IComponentData
    {
        [GhostField] public int originalGhostId;
        [GhostField] public NetworkTick originalSpawnTick;
    }

    // used to mark ghosts spawned after a migration action (save/load) so they are easy to find
    internal struct CreatedPostHostMigrationAction : IComponentData
    { }

    internal class GhostIdAndTickCheckerAuthoring : MonoBehaviour
    {
    }

    class GhostIdAndTickCheckerAuthoringBaker : Baker<GhostIdAndTickCheckerAuthoring>
    {
        public override void Bake(GhostIdAndTickCheckerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new GhostIdAndTickChecker());
        }
    }
}

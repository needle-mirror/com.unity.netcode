using Unity.Entities;

namespace Unity.NetCode.Tests
{
    static class DebuggingTestUtils
    {
        internal static Entity CreateEntityPrefab(World world, string prefabName = "GhostGenBigStruct")
        {
            var entity = world.EntityManager.CreateEntity(typeof(GhostGenTestTypes.GhostGenBigStruct));
            GhostPrefabCreation.ConvertToGhostPrefab(world.EntityManager, entity, new GhostPrefabCreation.Config
            {
                Name = prefabName,
                SupportedGhostModes = GhostModeMask.Predicted,
            });
            return entity;
        }
    }
}

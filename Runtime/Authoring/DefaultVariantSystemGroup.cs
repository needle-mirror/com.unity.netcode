using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Group that contains all the systems responsible to register/setup the default Ghost Variants (see <see cref="GhostComponentVariationAttribute"/>).
    /// The system group OnCreate method finalize the default mapping inside its own `OnCreate` method, by collecting from all the registered
    /// <see cref="DefaultVariantSystemBase"/> systems the set of variant to use.
    /// The order in which variants are set in the map is governed by the update order (see <see cref="CreateAfterAttribute"/>, <see cref="CreateBeforeAttribute"/>).
    /// <remarks>
    /// The group is present in both baking and client/server worlds.
    /// </remarks>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.BakingSystem)]
    public class DefaultVariantSystemGroup : ComponentSystemGroup
    {
    }
}
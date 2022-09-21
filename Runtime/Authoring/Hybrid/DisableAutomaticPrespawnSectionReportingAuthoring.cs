using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Authoring component which adds the DisableAutomaticPrespawnSectionReporting component to the Entity.
    /// </summary>
    [UnityEngine.DisallowMultipleComponent]
    public class DisableAutomaticPrespawnSectionReportingAuthoring : UnityEngine.MonoBehaviour
    {
        class DisableAutomaticPrespawnSectionReportingBaker : Baker<DisableAutomaticPrespawnSectionReportingAuthoring>
        {
            public override void Bake(DisableAutomaticPrespawnSectionReportingAuthoring authoring)
            {
                DisableAutomaticPrespawnSectionReporting component = default(DisableAutomaticPrespawnSectionReporting);
                AddComponent(component);
            }
        }
    }
}

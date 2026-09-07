using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Unity.Netcode
{
    /// <summary>
    /// Authoring component which adds the DisableAutomaticPrespawnSectionReporting component to the Entity.
    /// </summary>
    [UnityEngine.DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.DisableAutomaticPrespawnSectionReportingAuthoring)]
    [MovedFrom(true, "Unity.NetCode")]
    public class DisableAutomaticPrespawnSectionReportingAuthoring : UnityEngine.MonoBehaviour
    {
        [BakingVersion("cmarastoni", 1)]
        class DisableAutomaticPrespawnSectionReportingBaker : Baker<DisableAutomaticPrespawnSectionReportingAuthoring>
        {
            public override void Bake(DisableAutomaticPrespawnSectionReportingAuthoring authoring)
            {
                DisableAutomaticPrespawnSectionReporting component = default(DisableAutomaticPrespawnSectionReporting);
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, component);
            }
        }
    }
}

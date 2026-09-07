using Unity.Entities;
using UnityEngine.Scripting.APIUpdating;

namespace Unity.Netcode
{
    /// <summary>
    /// Singleton component used to enable/disable the built-in scene auto-tracking.
    /// </summary>
    [MovedFrom(true, "Unity.NetCode")]
    public struct DisableAutomaticPrespawnSectionReporting : IComponentData {}
}

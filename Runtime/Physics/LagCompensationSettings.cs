using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Singleton entity that allow to disable the NetCode LagCompensation system if present
    /// </summary>
    [GenerateAuthoringComponent]
    public struct DisableLagCompensation : IComponentData
    {
    }
}
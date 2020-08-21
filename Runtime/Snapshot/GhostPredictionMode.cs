using Unity.Entities;

namespace Unity.NetCode
{
    public struct GhostOwnerPredictedComponent : IComponentData
    {}
    public struct GhostDefaultPredictedComponent : IComponentData
    {}
    public struct GhostAlwaysPredictedComponent : IComponentData
    {}
    public struct GhostAlwaysInterpolatedComponent : IComponentData
    {}
}
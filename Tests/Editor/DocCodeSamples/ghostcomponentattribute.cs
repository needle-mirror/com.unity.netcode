using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    class ghostcomponentattribute
    {
        #region GhostComponentAttribute
        [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.OnlyInterpolatedClients, SendDataForChildEntity=false)]
        public struct MyComponent : IComponentData
        {
            [GhostField(Quantization = 1000)] public float3 Value;
        }
        #endregion
    }
}

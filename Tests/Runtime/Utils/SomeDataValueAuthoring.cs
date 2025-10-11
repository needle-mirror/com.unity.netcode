using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class SomeDataValueAuthoring : MonoBehaviour
    {
        public int value;
    }

    internal class SomeDataValueAuthoringBaker : Baker<SomeDataValueAuthoring>
    {
        public override void Bake(SomeDataValueAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new SomeData { Value = authoring.value });
        }
    }
}

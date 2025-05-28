using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class SomeDataAuthoring : MonoBehaviour
    {
    }

    class SomeDataAuthoringBaker : Baker<SomeDataAuthoring>
    {
        public override void Bake(SomeDataAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new SomeData {Value = Random.Range(1, 100)});
        }
    }
}

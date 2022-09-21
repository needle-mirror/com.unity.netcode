using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    public class SomeDataElementAuthoring : MonoBehaviour
    {
    }

    class SomeDataElementAuthoringBaker : Baker<SomeDataElementAuthoring>
    {
        public override void Bake(SomeDataElementAuthoring authoring)
        {
            var buffer = AddBuffer<SomeDataElement>();
            buffer.ResizeUninitialized(16);
            for (int i = 0; i < 16; ++i)
                buffer[i] = new SomeDataElement{Value = i};
        }
    }
}

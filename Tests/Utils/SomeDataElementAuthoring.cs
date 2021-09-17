using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    [ConverterVersion("cristian", 1)]
    public class SomeDataElementAuthoring : MonoBehaviour, IConvertGameObjectToEntity
    {
        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            var buffer = dstManager.AddBuffer<SomeDataElement>(entity);
            buffer.ResizeUninitialized(16);
            for (int i = 0; i < 16; ++i)
                buffer[i] = new SomeDataElement{Value = i};
        }
    }
}
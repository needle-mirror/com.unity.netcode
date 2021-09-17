using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    [ConverterVersion("cristian", 1)]
    public class SomeDataAuthoring : MonoBehaviour, IConvertGameObjectToEntity
    {
        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new SomeData {Value = Random.Range(1, 100)});
        }
    }
}
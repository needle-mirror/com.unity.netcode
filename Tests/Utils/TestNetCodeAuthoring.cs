using UnityEngine;
using Unity.Entities;

public class TestNetCodeAuthoring : MonoBehaviour, IConvertGameObjectToEntity
{
    public interface IConverter
    {
        void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem);
    }
    public IConverter Converter;
    public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
    {
        if (Converter != null)
            Converter.Convert(gameObject, entity, dstManager, conversionSystem);
    }
}
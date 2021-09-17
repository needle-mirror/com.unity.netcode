using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    public class NetCodePrespawnAuthoring : MonoBehaviour, IConvertGameObjectToEntity
    {
        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponent<NetCodePrespawnTag>(entity);
        }
    }
}
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    [ConverterVersion("timj", 1)]
    public class GhostOwnerComponentAuthoring : MonoBehaviour, IConvertGameObjectToEntity
    {
        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
           dstManager.AddComponentData(entity, default(GhostOwnerComponent));
        }
    }
}
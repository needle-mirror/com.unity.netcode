using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    public class NetCodeDebugConfigAuthoring : MonoBehaviour, IConvertGameObjectToEntity
    {
        public NetDebug.LogLevelType LogLevel = NetDebug.LogLevelType.Notify;
        public bool DumpPackets;

        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity,
                new NetCodeDebugConfig
                    {LogLevel = LogLevel, DumpPackets = DumpPackets});
        }
    }
}
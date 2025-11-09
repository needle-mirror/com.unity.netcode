using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    partial class logging
    {
        [BurstCompile]
        public partial struct LoggingSystem : ISystem
        {
            #region GhostSnapshotLogging
            [BurstCompile]
            public void OnUpdate(ref SystemState state)
            {
                state.EntityManager.AddComponent<EnablePacketLogging>(SystemAPI.QueryBuilder().WithAll<NetworkId>().WithNone<EnablePacketLogging>().Build());
            }
            #endregion
        }
    }
}

using Unity.Entities;

namespace Unity.NetCode
{
    public struct NetworkProtocolVersion : IComponentData
    {
        public const int k_NetCodeVersion = 1;
        public int NetCodeVersion;
        public int GameVersion;
        public ulong RpcCollectionVersion;
    }

    public struct GameProtocolVersion : IComponentData
    {
        public int Version;
    }
}
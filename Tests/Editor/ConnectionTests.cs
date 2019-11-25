using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class CheckConnectionSystem : ComponentSystem
    {
        public static int IsConnected;
        protected override void OnUpdate()
        {
            if (HasSingleton<NetworkStreamConnection>())
            {
                if (World.GetExistingSystem<ServerSimulationSystemGroup>() != null)
                    IsConnected |= 2;
                else
                    IsConnected |= 1;
            }
        }
    }
    public class ConnectionTests
    {
        [Test]
        public void ConnectSingleClient()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CheckConnectionSystem));
                testWorld.CreateWorlds(true, 1);

                CheckConnectionSystem.IsConnected = 0;

                var ep = NetworkEndPoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.ServerWorld.GetExistingSystem<NetworkStreamReceiveSystem>().Listen(ep);
                testWorld.ClientWorlds[0].GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);

                for (int i = 0; i < 16 && CheckConnectionSystem.IsConnected != 3; ++i)
                    testWorld.Tick(16f/1000f);

                Assert.AreEqual(3, CheckConnectionSystem.IsConnected);
            }
        }

    }
}
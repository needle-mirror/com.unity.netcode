#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
#if NETCODE_DEBUG
using NUnit.Framework;
using Unity.Entities;

namespace Unity.NetCode.Tests
{
    internal class PacketDumpTests
    {
        [Test]
        public void NetDebugPacket_IsInitialized()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.DebugPackets = true;
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();
            testWorld.Tick(); // The loggers take one tick to init, as they are initialized
                              // inside the GhostSendSystem & GhostReceiveSystem respectively.
            RunTest(testWorld.ServerWorld);
            RunTest(testWorld.ClientWorlds[0]);
            void RunTest(World world)
            {
                ref var enablePacketLogging = ref testWorld.GetSingletonRW<EnablePacketLogging>(world).ValueRW;
                Assert.IsTrue(enablePacketLogging.IsPacketCacheCreated);
                enablePacketLogging.LogToPacket("Test that we can write to the packet dump!");
                // Note: Actually reading the packet dump file is difficult as we don't have a path to it,
                // and better profiling tools are coming, so I'm not too concerned about testing that here.
            }
        }
    }
}
#endif

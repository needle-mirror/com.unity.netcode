using Unity.Entities;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    // Using a private class to prevent PVP checks
    class binary_host_mode
    {
        #region BinaryHostBootstrap
        public class MyHostBootstrap : ClientServerBootstrap
        {
            public override bool Initialize(string defaultWorldName)
            {
                // Don't create client or server worlds yet; the main menu drives world creation.
                CreateLocalWorld(defaultWorldName);
                return true;
            }
        }

        public static class HostLauncher
        {
            public static void StartBinaryHost()
            {
                // Server world runs server systems and owns authoritative ghosts.
                var serverWorld = ClientServerBootstrap.CreateServerWorld("ServerWorld");

                // Client world runs client systems for the local player.
                var clientWorld = ClientServerBootstrap.CreateClientWorld("ClientWorld");
            }
        }
        #endregion
    }
}

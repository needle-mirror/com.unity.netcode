using Unity.Entities;
using Unity.Netcode;

namespace DocumentationCodeSamples
{
    // Using a private class to prevent PVP checks
    class client_server
    {
        #region ClientServerSetup
        public class MyClientServerBootstrap : ClientServerBootstrap
        {
            public override bool Initialize(string defaultWorldName)
            {
                // Don't create networked worlds yet; a front-end menu drives world creation.
                CreateLocalWorld(defaultWorldName);
                return true;
            }
        }

        public static class GameLauncher
        {
            // Call this on a dedicated server build, or when a player chooses to host.
            public static void StartServer()
            {
                var serverWorld = ClientServerBootstrap.CreateServerWorld("ServerWorld");
            }

            // Call this on a client build, or when a player chooses to join.
            public static void StartClient()
            {
                var clientWorld = ClientServerBootstrap.CreateClientWorld("ClientWorld");
            }
        }
        #endregion
    }
}

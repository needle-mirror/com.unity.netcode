using Unity.Entities;
using Unity.Netcode;

namespace DocumentationCodeSamples
{
    // Using a private class to prevent PVP checks
    class single_host_world
    {
        #region SingleWorldHostBootstrap
        public class MyHostBootstrap : ClientServerBootstrap
        {
            public override bool Initialize(string defaultWorldName)
            {
                // Don't create networked worlds yet; the main menu drives world creation.
                CreateLocalWorld(defaultWorldName);
                return true;
            }
        }

        public static class HostLauncher
        {
            public static void StartSingleWorldHost()
            {
                // A single world runs both server and client systems for the host in the same world.
                var hostWorld = ClientServerBootstrap.CreateSingleWorldHost("HostWorld");
            }
        }
        #endregion
    }
}

using Unity.Entities;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    // Using a private class to prevent PVP checks
    class client_server_worlds
    {
        #region SystemGroup
        [UpdateInGroup(typeof(GhostInputSystemGroup))]
        public partial class MyInputSystem : SystemBase
        {
            protected override void OnUpdate()
            {
                // ...
            }
        }
        #endregion

        #region WorldSystemFilter
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        public partial class MySystem : SystemBase
        {
            protected override void OnUpdate()
            {
                // ...
            }
        }
        #endregion

        #region CustomBootstrap
        public class MyGameSpecificBootstrap : ClientServerBootstrap
        {
            public override bool Initialize(string defaultWorldName)
            {
                //Create only a local simulation world without any multiplayer and netcode system in it.
                CreateLocalWorld(defaultWorldName);
                return true;
            }
        }
        #endregion

        #region UsingCustomBootstrap
        void OnPlayButtonClicked()
        {
            // Typically this:
            var clientWorld = ClientServerBootstrap.CreateClientWorld("ClientWorld");
            // And/Or this:
            var serverWorld = ClientServerBootstrap.CreateServerWorld("ServerWorld");

            // And/Or something like this, for soak testing:
            AutomaticThinClientWorldsUtility.NumThinClientsRequested = 10;
            AutomaticThinClientWorldsUtility.BootstrapThinClientWorlds();
        }
        #endregion

        #region WorldMigration
        public World MigrateWorld(World sourceWorld)
        {
            DriverMigrationSystem migrationSystem = default;
            foreach (var world in World.All)
            {
                if ((migrationSystem = world.GetExistingSystemManaged<DriverMigrationSystem>()) != null)
                    break;
            }

            var ticket = migrationSystem.StoreWorld(sourceWorld);
            sourceWorld.Dispose();

            var newWorld = migrationSystem.LoadWorld(ticket);

            // NOTE: LoadWorld must be executed before you populate your world with the systems it needs!
            // This is because LoadWorld creates a `MigrationTicket` Component that the NetworkStreamReceiveSystem needs in order to be able to Load
            // the correct Driver.

            return ClientServerBootstrap.CreateServerWorld("ServerWorld");
        }
        #endregion
    }
}

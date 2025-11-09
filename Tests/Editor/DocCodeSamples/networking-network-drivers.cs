using Unity.Entities;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    class networking_network_drivers
    {
        #region CustomerDriverConstructor
        public class MyCustomDriverConstructor : INetworkStreamDriverConstructor
        {
            public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
            {
                var settings = DefaultDriverBuilder.GetNetworkClientSettings();
#if !UNITY_WEBGL
                DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
#else
            DefaultDriverBuilder.RegisterClientWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
            }

            public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
            {
                var settings = DefaultDriverBuilder.GetNetworkServerSettings();
#if !UNITY_WEBGL
                DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
#else
                DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
            }
        }
        #endregion

        public void Method()
        {
            #region CustomStrategy
            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
//The try/finally pattern can be used to avoid any exceptions resetting back to the old default.
            try
            {
                NetworkStreamReceiveSystem.DriverConstructor = new MyCustomDriverConstructor();
                var server = ClientServerBootstrap.CreateServerWorld("ServerWorld");
                var client = ClientServerBootstrap.CreateClientWorld("ClientWorld");
            }
            finally
            {
                NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;
            }
            #endregion
        }

        public void Method2()
        {
            #region DelegateCreation
            var driverStore = new NetworkDriverStore();
            var clientWorld = ClientServerBootstrap.ClientWorld;
            var netDebug = ClientServerBootstrap.ClientWorld.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
// you can use any constructor to initialize the store
            NetworkStreamReceiveSystem.DriverConstructor.CreateClientDriver(clientWorld, ref driverStore, netDebug);
            var networkStreamDriver = ClientServerBootstrap.ClientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
            networkStreamDriver.ResetDriverStore(ClientServerBootstrap.ServerWorld.Unmanaged, ref driverStore);
            #endregion
        }

        public void Method3()
        {
            #region ManuallyPopulate
            var driverStore = new NetworkDriverStore();
            var settings = DefaultDriverBuilder.GetNetworkServerSettings();
            var netDebug = ClientServerBootstrap.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
//register some drivers
            DefaultDriverBuilder.RegisterServerIpcDriver(ClientServerBootstrap.ServerWorld, ref driverStore, netDebug, settings);
            DefaultDriverBuilder.RegisterServerUdpDriver(ClientServerBootstrap.ServerWorld, ref driverStore, netDebug, settings);
//reset
            var networkStreamDriver = ClientServerBootstrap.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
            networkStreamDriver.ResetDriverStore(ClientServerBootstrap.ServerWorld.Unmanaged, ref driverStore);
            #endregion
        }
    }
}

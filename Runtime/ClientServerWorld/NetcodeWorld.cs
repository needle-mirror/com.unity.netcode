using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode
{
    /// <summary>
    /// Provides various utility APIs for the current world. Most of these should have an ECS counter part, this class simply provides direct access
    /// to those various components.
    /// Provides shortcuts to its current <see cref="P:Unity.NetCode.Connection" /> if online, its various singletons, etc.
    /// </summary>
    // Design note: The goal is to store as little state as possible here. That state should be stored ECS side in most cases.
    [DebuggerDisplay("{GetDebugName(this)}")]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    class NetcodeWorld : World
    {
        bool m_Initialized;

        #region connectivity

        Connection m_Connection;
        /// <summary>
        /// Your local connection, for the current client. For a host, this is your "main" virtual local connection.
        /// The network ID and other values default to 0 when there is no connection.
        /// </summary>
        public Connection LocalConnection {
            get
            {
                this.AssertIsClient();
                return m_Connection;
            }
            internal set => m_Connection = value;
        }
        public List<Connection> AllConnections { get; internal set; } = new(); // TODO-release@connection we could potentially have this filled for client worlds as well, containing info about other clients. Could be useful for ownership information? (what would we attach to that NetworkId to make this useful?) or for potentially having a way to send an RPC to another client?

        // TODO-next@connection have a sample showing a state machine using this?
        // TODO-next@connection raise events on a client when other clients join.
        /// <summary>
        /// Event called for any connection event, both client side or server side. A <see cref="NetCodeConnectionEvent.State"/> is available to
        /// differentiate the different events.
        /// This is called from ECS's <see cref="SimulationSystemGroup"/>'s beginning and so should execute right after MonoBehaviour.Update(). See <see cref="ConnectionManagementUpdateConnections"/>. This will also get called on world destruction when shutting down.
        /// </summary>
        public event OnConnectionEventDelegate OnConnectionEvent;

        // Needed since this is an event. This way it can be triggered by a system outside this class
        internal OnConnectionEventDelegate GetCallbackToInvokeOnConnectionEvent()
        {
            return OnConnectionEvent;
        }
        // Some ideas https://github.cds.internal.unity3d.com/unity/dots/pull/9738#discussion_r482299
        // Should handle other players connecting (do sample with a list of all connected players in the ui, like boss room).
        // Could support burst handlers

        #endregion

        #region query caching
        // TODO-next@potentialOptim singleton pointer caching here too, instead of doing a query all the time
        internal EntityQuery RuntimeStripQuery;
        EntityQuery m_NetworkTimeSingletonQuery;
        EntityQuery m_NetworkStreamDriverQuery;
        EntityQuery m_NetworkStreamConnectionQuery;
        EntityQuery m_NetworkIdQuery;
        EntityQuery m_NetDebugQuery;
        EntityQuery m_LocalConnectionQuery;
        EntityQuery m_NetworkIdAllocationDataQuery;

        internal ref NetworkStreamDriver Driver => ref m_NetworkStreamDriverQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW;
        internal ref NetDebug NetDebug => ref m_NetDebugQuery.GetSingletonRW<NetDebug>().ValueRW;

        #endregion

        PredictionSwitching m_PredictionSwitching;
        public PredictionSwitching PredictionSwitching
        {
            get
            {
                this.AssertIsClientOnly();
                return m_PredictionSwitching;
            }
        }

        internal NetcodeWorld(string name, WorldFlags flags = WorldFlags.Simulation)
            : base(name, flags)
        {
            Initialize();
        }

        internal NetcodeWorld(string name, WorldFlags flags, AllocatorManager.AllocatorHandle backingAllocatorHandle)
            : base(name, flags, backingAllocatorHandle)
        {
            Initialize();
        }

        void Initialize()
        {
            if (!Netcode.Instance.m_ActiveWorld.ExistsAndIsCreated() || !Netcode.Instance.m_ActiveWorld.IsServer())
                Netcode.Instance.m_ActiveWorld = this;
            RuntimeStripQuery = GhostCollectionSystem.GetRuntimeStripQuery(this.EntityManager);
            m_NetworkTimeSingletonQuery = this.EntityManager.CreateEntityQuery(typeof(NetworkTime));
            m_NetworkStreamDriverQuery = this.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            m_NetworkStreamConnectionQuery = this.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
            m_NetworkIdQuery = this.EntityManager.CreateEntityQuery(typeof(NetworkId));
            m_NetDebugQuery = this.EntityManager.CreateEntityQuery(typeof(NetDebug));
            m_LocalConnectionQuery = this.EntityManager.CreateEntityQuery(typeof(LocalConnection));
            m_NetworkIdAllocationDataQuery = this.EntityManager.CreateEntityQuery(typeof(NetworkIDAllocationData));

            // sub-manager code (like relevancy or server rewind or prediction switcher) should be initialized here
            if (!this.IsServer())
            {
                this.m_PredictionSwitching = new PredictionSwitching(this.EntityManager);
            }

            // Apply NetcodeConfig in NetcodeWorld creation and create ClientTickRate+, before all other systems could have their OnCreate called. This seeds the different tick rate settings.
            // Users can still override ClientTickRate in code.
            // There's an "apply" button in NetcodeConfigEditor that makes sure we don't continuously override user settings.
            //      if users change their config in code, then it's up to them to apply it to NetcodeConfig before clicking "apply".
            // Editor code auto creates NetcodeConfig asset. There's also logic in place to make sure it's never null.
            var settingsEntity = this.EntityManager.CreateEntity();
            var config = NetCodeConfig.Global;
            var sendData = new GhostSendSystemData();
            sendData.Initialize();
            var cstr = new ClientServerTickRate();
            cstr.ResolveDefaults();
            var ctr = NetworkTimeSystem.DefaultClientTickRate;
            if (config == null)
            {
                Debug.LogError($"Sanity check failed, {nameof(NetCodeConfig)} is null");
            }
            else
            {
                sendData = config.GhostSendSystemData;
                cstr = config.ClientServerTickRate;
                ctr = config.ClientTickRate;
            }
            if (this.IsServer())
            {
                EntityManager.AddComponentData(settingsEntity, sendData);
                EntityManager.AddComponentData(settingsEntity, cstr);
            }
            if (this.IsClient())
            {
                EntityManager.AddComponentData(settingsEntity, ctr);
            }
            EntityManager.SetName(settingsEntity, "Netcode-Settings-Singleton");

            m_Initialized = true;
        }

        public NetworkTime NetworkTime => m_Initialized ? m_NetworkTimeSingletonQuery.GetSingleton<NetworkTime>() : default;
        public float DeltaTime => m_Initialized ? base.Time.DeltaTime : 0f;

        #region connectivity

        /// <summary>
        /// Connect the client to the specified server endpoint.
        /// You can override driver creation by setting <see cref="NetworkStreamReceiveSystem.DriverConstructor"/> with your own constructor.
        /// </summary>
        /// <param name="endpoint">
        /// The <see cref="NetworkEndpoint"/> to connect to. Examples:
        /// <code>
        /// NetworkEndpoint.LoopbackIpv4.WithPort(1234) // to connect to localhost
        /// NetworkEndpoint.Parse("1.2.3.4", 1234) // to parse a string
        /// </code>
        /// </param>
        /// <returns>True if successful, Shutdown will be called on client connection issues.</returns>
        public Connection Connect(NetworkEndpoint endpoint)
        {
            //TODO-next@connection do we only let session management handle relay? Do we need to do anything on our side for this?
            //You can't start as host if the playmode tool does not allow that.
            if (ClientServerBootstrap.RequestedPlayType == ClientServerBootstrap.PlayType.Server)
            {
                Debug.LogError($"Connect is not allowed if playmode is set to {ClientServerBootstrap.PlayType.Server}");
                return default;
            }

            // Make sure if there was an existing driver that we reset it with a new driver
            var driverStore = new NetworkDriverStore();
            // Use the existing driver constructor. Users can override this using the usual N4E way.
            NetworkStreamReceiveSystem.DriverConstructor.CreateClientDriver(this, ref driverStore, NetDebug);
            Driver.ResetDriverStore(Unmanaged, ref driverStore);

            var connectionEntity = Driver.Connect(this.EntityManager, endpoint);
            LocalConnection = new Connection(this, connectionEntity);
            return LocalConnection;
        }

        /// <summary>
        /// Starts the server and listens on the given endpoint.
        /// You can override driver creation by setting <see cref="NetworkStreamReceiveSystem.DriverConstructor"/> with your own constructor.
        /// </summary>
        /// <param name="endpoint">
        /// The <see cref="NetworkEndpoint"/> to listen on. To listen for any address for example, use
        /// <code>
        /// NetworkEndpoint.AnyIpv4.WithPort(123)
        /// </code>
        /// </param>
        /// <returns>True if successful, Shutdown will be called on issues.</returns>
        public bool Listen(NetworkEndpoint endpoint)
        {
            //You can't start as host if the playmode tool does not allow that.
            if (ClientServerBootstrap.RequestedPlayType == ClientServerBootstrap.PlayType.Client)
            {
                UnityEngine.Debug.LogError($"Listen is not allowed if playmode is set to {ClientServerBootstrap.RequestedPlayType}");
                return false;
            }

            // Make sure if there was an existing driver that we reset it with a new driver
            var driverStore = new NetworkDriverStore();
            // Use the existing driver constructor. Users can override this using the usual N4E way.
            NetworkStreamReceiveSystem.DriverConstructor.CreateServerDriver(this, ref driverStore, NetDebug);
            Driver.ResetDriverStore(Unmanaged, ref driverStore);

            var success = Driver.Listen(endpoint);
            if (success)
            {
                if (this.IsHost())
                {
                    var networkIdAllocationData = m_NetworkIdAllocationDataQuery.GetSingleton<NetworkIDAllocationData>();

                    NetworkStreamReceiveSystem.AttemptCreateFakeHostConnection(this.Unmanaged, networkIdAllocationData.NumNetworkIds, m_LocalConnectionQuery);

                    var connectionEntity = m_LocalConnectionQuery.GetSingletonEntity();
                    var networkId = EntityManager.GetComponentData<NetworkId>(connectionEntity);
                    LocalConnection = new Connection(this, connectionEntity);
                }
            }
            else
            {
                Debug.LogError($"Error trying to listen, shutting down world.");
                Shutdown();
            }

            return success;
        }

        /// <summary>
        /// Return the address to use to connect to server for local hosting.
        /// If the server is not configured as host and the driver does not have any IPCNetworkInterface to use,
        /// an invalid address is returned.
        /// </summary>
        /// <returns></returns>
        internal NetworkEndpoint GetIPCEndpoint()
        {
            this.AssertIsServerOnly();
            var driver = Driver;
            if(!driver.DriverStore.IsCreated || !Driver.DriverStore.HasListeningInterfaces)
                return new NetworkEndpoint();
            if(driver.DriverStore.GetDriverType(NetworkDriverStore.FirstDriverId) != TransportType.IPC)
                return new NetworkEndpoint();
            return driver.GetLocalEndPoint(NetworkDriverStore.FirstDriverId);
        }

        /// <summary>
        /// Check if the server world is set up and valid and has a driver instance listening.
        /// </summary>
        /// <returns>True if server is listening.</returns>
        public bool Listening()
        {
            this.AssertIsServer();
            return Driver.DriverStore.HasListeningInterfaces;
        }

        /// <summary>
        /// Flushes all pending messages on connections and requests a disconnect on those connections. This will be processed in the next frame and so
        /// your connections will still be marked as connected after this call.
        /// </summary>
        public void Shutdown()
        {
            if (this.IsClient())
            {
                var connection = m_LocalConnectionQuery.GetSingletonEntity();
                EntityManager.AddComponent<NetworkStreamRequestDisconnect>(connection);
                Driver.DriverStore.ScheduleFlushSendAllDrivers(default).Complete();
            }

            var driver = Driver;

            if (driver.DriverStore.IsCreated)
            {
                if (this.IsServer() && driver.DriverStore.IsCreated)
                {
                    var connectionEntities = m_NetworkStreamConnectionQuery.ToEntityArray(Allocator.Temp);
                    var netStreamConnectionArray = m_NetworkStreamConnectionQuery.ToComponentDataArray<NetworkStreamConnection>(Allocator.Temp);
                    for (int i = 0; i < connectionEntities.Length; i++)
                    {
                        var connection = netStreamConnectionArray[i];
                        driver.Disconnect(connection);
                        var connectionEntity = connectionEntities[i];
                        if (EntityManager.HasComponent<NetworkStreamInGame>(connectionEntity))
                        {
                            EntityManager.RemoveComponent<NetworkStreamInGame>(connectionEntity);
                        }
                    }
                    driver.DriverStore.ScheduleUpdateAllDrivers(default).Complete();
                    AllConnections.Clear();

                    var driverStore = new NetworkDriverStore();
                    // Use the existing driver constructor. Users can override this using the usual N4E way.
                    NetworkStreamReceiveSystem.DriverConstructor.CreateServerDriver(this, ref driverStore, NetDebug);
                    Driver.ResetDriverStore(Unmanaged, ref driverStore);
                }
            }

            if (this.IsHost())
            {
                this.LocalConnection = default;
            }
        }

        public void RequestDisconnectFromServer()
        {
            this.AssertIsClientOnly();
            LocalConnection.RequestDisconnect();
        }

        public void DisconnectAClient(Connection connection)
        {
            DisconnectAClient(connection.NetworkId);
        }

        public void DisconnectAClient(NetworkId clientId)
        {
            this.AssertIsServer();
            using var data = m_NetworkIdQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
            using var entities = m_NetworkIdQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < data.Length; ++i)
            {
                if (clientId.Value == data[i].Value)
                {
                    EntityManager.AddComponent<NetworkStreamRequestDisconnect>(entities[i]);
                }
            }
        }

        public void RequestDisconnectAllClients()
        {
            this.AssertIsServer();
            foreach (var connection in m_NetworkStreamConnectionQuery.ToEntityArray(Allocator.Temp))
            {
                EntityManager.AddComponent<NetworkStreamRequestDisconnect>(connection);
            }
        }

        #endregion

        public static string GetDebugName(NetcodeWorld self)
        {
            if (self.ExistsAndIsCreated())
                return $"{(self.IsThinClient() ? "Thin" : "")} {(self.IsClient() ? self.IsServer() ? "Host" : "Client" : "Server")} NetcodeWorld [NetworkTime {self.NetworkTime.ToString()}] {self.Name}";
            else
                return $"Not created";
        }
    }

#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    static class NetcodeWorldExtensions
    {
        public static bool ExistsAndIsCreated(this World world)
        {
            return world != null && world.IsCreated;
        }

        [Conditional("UNITY_ASSERTIONS")]
        internal static void AssertIsServer(this World self)
        {
            Assert.IsTrue(self.ExistsAndIsCreated() && self.IsServer(), "Should only be called from servers or hosts");
        }

        [Conditional("UNITY_ASSERTIONS")]
        internal static void AssertIsServerOnly(this World self)
        {
            Assert.IsTrue(self.ExistsAndIsCreated() && !self.IsClient(), "Should only be called from dedicated servers");
        }

        [Conditional("UNITY_ASSERTIONS")]
        internal static void AssertIsClientOnly(this World self)
        {
            Assert.IsTrue(self.ExistsAndIsCreated() && self.IsClient() && !self.IsHost(), "Should only be called from standalone clients");
        }

        [Conditional("UNITY_ASSERTIONS")]
        internal static void AssertIsClient(this World self)
        {
            Assert.IsTrue(self.ExistsAndIsCreated() && self.IsClient(), "Should only be called from clients or hosts");
        }

        [Conditional("UNITY_ASSERTIONS")]
        internal static void AssertIsHost(this World self)
        {
            Assert.IsTrue(self.ExistsAndIsCreated() && self.IsHost(), "Should only be called from hosts");
        }
    }
}

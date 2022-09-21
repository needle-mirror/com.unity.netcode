using Unity.Entities;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Collections.LowLevel.Unsafe;
using System;

namespace Unity.NetCode
{
    /// <summary>
    /// Singleton that can hold a reference to the <see cref="NetworkDriverStore"/> and that should be used
    /// to easily listening for new connection or connecting to server.
    /// Provide also other shortcut for retrieving the remote address of a <see cref="NetworkStreamConnection"/> or its
    /// underlying transport state.
    /// </summary>
    public unsafe struct NetworkStreamDriver : IComponentData
    {
        internal struct Pointers
        {
            public NetworkDriverStore DriverStore;
            public ConcurrentDriverStore ConcurrentDriverStore;
        }
        internal NetworkStreamDriver(void* driverStore, NativeReference<int> numIds, NativeQueue<int> freeIds, NetworkEndpoint endPoint)
        {
            m_DriverPointer = driverStore;
            //DriverStore = driverStore;
            //ConcurrentDriverStore = driverStore.ToConcurrent();
            LastEndPoint = endPoint;
            DriverState = (int) NetworkStreamReceiveSystem.DriverState.Default;
            m_NumNetworkIds = numIds;
            m_FreeNetworkIds = freeIds;
            m_ConnectionStatus = ConnectionStatus.None;
        }

        private void* m_DriverPointer;
        internal ref NetworkDriverStore DriverStore => ref UnsafeUtility.AsRef<Pointers>(m_DriverPointer).DriverStore;
        internal ref ConcurrentDriverStore ConcurrentDriverStore => ref UnsafeUtility.AsRef<Pointers>(m_DriverPointer).ConcurrentDriverStore;

        internal NetworkEndpoint LastEndPoint;

        internal int DriverState{get; private set;}

        private NativeReference<int> m_NumNetworkIds;
        private NativeQueue<int> m_FreeNetworkIds;

        internal bool Connected => m_ConnectionStatus == ConnectionStatus.Connected;
        ConnectionStatus m_ConnectionStatus;

        enum ConnectionStatus
        {
            None,
            Binding,
            Connected,
        }

        /// <summary>
        /// Tell all the registered <see cref="NetworkDriverStore"/> drivers to start listening for incoming connections.
        /// </summary>
        /// <param name="endpoint">The local address to use. This is the address that will be used to bind the underlying socket.</param>
        /// <returns></returns>
        internal bool ListenAsync(NetworkEndpoint endpoint)
        {
            // In cases where the driver is not bound yet, we wait for this to happen and then invoke listen.
            bool IsDriverListening(ref NetworkDriverStore.NetworkDriverInstance instance, ref FixedList32Bytes<int> errors, int driverId)
            {
                if (instance.driver.Listening)
                {
                    return true;
                }

                if (!instance.driver.Bound)
                {
                    return false;
                }

                if (instance.driver.Listen() != 0) { errors.Add(driverId); }
                return instance.driver.Listening;
            }

            // Using a relay server driver.Bind will return 0 but will not bind immediately.
            // Therefore, driver.Bound will not be true the first time we invoke this. In this case return false, indicating that the driver is not yet Listening.
            // If we do manage to bind immediately, we invoke Listen.
            // In cases where we Bind and Listen immediately, we do not need to do more, and so we set state to Connected immediately.
            // In cases where Binding and Listening is asynchronous, we set the state to an intermediate state ('Binding'), which will then auto-complete if possible on a later frame.
            bool IsDriverConnected(ref NetworkDriverStore.NetworkDriverInstance instance, ref FixedList32Bytes<int> errors, int driverId)
            {
                if (instance.driver.Bind(endpoint) != 0) { errors.Add(driverId); }
                if (!instance.driver.Bound)
                {
                    return false;
                }

                if (instance.driver.Listen() != 0) { errors.Add(driverId); }

                return instance.driver.Listening;
            }

            // Switching to server mode. Start listening all the driver interfaces
            var errors = new FixedList32Bytes<int>();
            switch (m_ConnectionStatus)
            {
                case ConnectionStatus.None:
                {
                    bool connected = true;
                    DriverStore.ForEachDriver((ref NetworkDriverStore.NetworkDriverInstance instance, int driverId) =>
                    {
                        if (!IsDriverConnected(ref instance, ref errors, driverId))
                        {
                            connected = false;
                        }
                    });
                    if (connected)
                    {
                        m_ConnectionStatus = ConnectionStatus.Connected;
                        break;
                    }

                    m_ConnectionStatus = ConnectionStatus.Binding;
                    goto case ConnectionStatus.Binding;
                }
                case ConnectionStatus.Binding:
                {
                    var listening = true;
                    DriverStore.ForEachDriver((ref NetworkDriverStore.NetworkDriverInstance instance, int driverId) =>
                    {
                        if (!IsDriverListening(ref instance, ref errors, driverId))
                        {
                            listening = false;
                        }
                    });

                    if (listening)
                    {
                        m_ConnectionStatus = ConnectionStatus.Connected;
                    }

                    break;
                }
                case ConnectionStatus.Connected:
                default:
                    break;
            }

            if (!errors.IsEmpty)
            {
                // The inconsistent state will be picked up and fixed by the network stream receive system
                return false;
            }
            // FIXME: bad if this is not a ref of the driver store, but so is the state change for listen / connect
            LastEndPoint = endpoint;
            return true;
        }

        internal bool ConnectAsync(EntityManager entityManager, NetworkEndpoint endPoint, Entity ent = default)
        {
            switch (m_ConnectionStatus)
            {
                case ConnectionStatus.None:
                {
                    var nep = endPoint.Family == NetworkFamily.Ipv6 ? NetworkEndpoint.AnyIpv6 : NetworkEndpoint.AnyIpv4;
                    if (DriverStore.GetNetworkDriver(NetworkDriverStore.FirstDriverId).Bind(nep) != 0)
                    {
                        return false;
                    }

                    m_ConnectionStatus = ConnectionStatus.Binding;
                    goto case ConnectionStatus.Binding;
                }
                case ConnectionStatus.Binding:
                {
                    var networkDriver = DriverStore.GetNetworkDriver(NetworkDriverStore.FirstDriverId);
                    if (!networkDriver.Bound)
                    {
                        return true;
                    }

                    Connect(entityManager, endPoint, ent);
                    m_ConnectionStatus = ConnectionStatus.Connected;
                    break;
                }
                case ConnectionStatus.Connected:
                default:
                    break;
            }

            return true;
        }

        /// <summary>
        /// Tell all the registered <see cref="NetworkDriverStore"/> drivers to start listening for incoming connections.
        /// </summary>
        /// <param name="endpoint">The local address to use. This is the address that will be used to bind the underlying socket.</param>
        /// <returns></returns>
        public bool Listen(NetworkEndpoint endpoint)
        {
            // Switching to server mode. Start listening all the driver interfaces
            var errors = new FixedList32Bytes<int>();
            DriverStore.ForEachDriver((ref NetworkDriverStore.NetworkDriverInstance instance, int driverId) =>
            {
                if(instance.driver.Bind(endpoint) != 0 || instance.driver.Listen() != 0)
                    errors.Add(driverId);
            });
            if(!errors.IsEmpty)
            {
                // The inconsistent state will be picked up and fixed by the network stream receive system
                return false;
            }
            // FIXME: bad if this is not a ref of the driver store, but so is the state change for listen / connect
            LastEndPoint = endpoint;
            return true;
        }

        /// <summary>
        /// Initiate a connection to the remote <paramref name="endpoint"/> address.
        /// </summary>
        /// <param name="entityManager">The entity manager to use to create the new entity, if <paramref name="ent"/> equals <see cref="Entity.Null"/></param>
        /// <param name="endpoint">The remote address we want to connect</param>
        /// <param name="ent">An optional entity to use to create the connection. If not set, a new entity will be create instead</param>
        /// <returns>The entity that hold the <see cref="NetworkStreamConnection"/></returns>
        /// <exception cref="InvalidOperationException">Throw an exception if the driver is not created or if multiple drivers are register</exception>
        public Entity Connect(EntityManager entityManager, NetworkEndpoint endpoint, Entity ent = default)
        {
            LastEndPoint = endpoint;

            if (ent == Entity.Null)
                ent = entityManager.CreateEntity();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (DriverStore.DriversCount == 0)
                throw new InvalidOperationException("Cannot connect to the server. NetworkDriver not created");
            if (DriverStore.DriversCount != 1)
                throw new InvalidOperationException("Too many NetworkDriver created for the client. Only one NetworkDriver instance should exist");
#endif
            var connection = DriverStore.GetNetworkDriver(NetworkDriverStore.FirstDriverId).Connect(endpoint);
            entityManager.AddComponentData(ent, new NetworkStreamConnection{Value = connection, DriverId = 1});
            entityManager.AddComponentData(ent, new NetworkSnapshotAckComponent());
            entityManager.AddComponentData(ent, new CommandTargetComponent());
            entityManager.AddBuffer<IncomingRpcDataStreamBufferComponent>(ent);
            entityManager.AddBuffer<OutgoingCommandDataStreamBufferComponent>(ent);
            entityManager.AddBuffer<IncomingSnapshotDataStreamBufferComponent>(ent);
            entityManager.AddBuffer<LinkedEntityGroup>(ent).Add(new LinkedEntityGroup{Value = ent});
            return ent;
        }

        /// <summary>
        /// The remote connection address. This is the seen public ip address of the connection.
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        public NetworkEndpoint GetRemoteEndPoint(NetworkStreamConnection connection)
        {
            return DriverStore.GetNetworkDriver(connection.DriverId).GetRemoteEndpoint(connection.Value);
        }
        /// <summary>
        /// The current state of the internal transport connection.
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        /// <remarks>
        /// Is different from the <see cref="ConnectionState.State"/> and it is less granular.
        /// </remarks>
        public NetworkConnection.State GetConnectionState(NetworkStreamConnection connection)
        {
            return DriverStore.GetConnectionState(connection);
        }

        internal DriverMigrationSystem.DriverStoreState StoreMigrationState()
        {
            DriverStore.ScheduleFlushSendAllDrivers(default).Complete();
            var driverStoreState = new DriverMigrationSystem.DriverStoreState();
            driverStoreState.DriverStore = DriverStore;
            driverStoreState.LastEp = LastEndPoint;
            driverStoreState.NextId = m_NumNetworkIds.Value;
            driverStoreState.FreeList = m_FreeNetworkIds.ToArray(Allocator.Persistent);
            m_FreeNetworkIds.Clear();

            DriverState = (int) NetworkStreamReceiveSystem.DriverState.Migrating;
            return driverStoreState;
        }
    }
}

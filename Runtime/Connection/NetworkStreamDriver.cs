using Unity.Entities;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Collections.LowLevel.Unsafe;
using System;
using Unity.Networking.Transport.Relay;

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
        internal NetworkStreamDriver(void* driverStore, NativeReference<int> numIds, NativeQueue<int> freeIds, NetworkEndpoint endPoint, NativeList<NetCodeConnectionEvent> connectionEventsList, NativeArray<NetCodeConnectionEvent>.ReadOnly connectionEventsForTick)
        {
            m_DriverPointer = driverStore;
            //DriverStore = driverStore;
            //ConcurrentDriverStore = driverStore.ToConcurrent();
            LastEndPoint = endPoint;
            DriverState = NetworkStreamReceiveSystem.DriverState.Default;
            m_NumNetworkIds = numIds;
            m_FreeNetworkIds = freeIds;
            ConnectionEventsList = connectionEventsList;
            ConnectionEventsForTick = connectionEventsForTick;
            RequireConnectionApprovalInternal = 0;
        }

        private void* m_DriverPointer;

        /// <summary>
        /// A pointer to the underlying <see cref="DriverStore"/>, giving access to raw Transport APIs.<br/>
        /// <b>Warning: You MUST fetch <see cref="NetworkStreamDriver"/> as RW access when performing Driver operations!</b>
        /// </summary>
        /// <remarks>
        /// <see cref="NetworkDriverStore"/> has specific usage patterns (see for loop use-cases below). Use with care!<br/>
        /// Copying such a large struct is expensive, prefer <c>ref var driverStore = ref networkStreamDriver.RefRW.DriverStore;</c> syntax.
        /// </remarks>
        public ref NetworkDriverStore DriverStore => ref UnsafeUtility.AsRef<Pointers>(m_DriverPointer).DriverStore;

        /// <summary>
        /// A reference to the concurrent version of the <see cref="NetworkDriverStore"/> (<see cref="ConcurrentDriverStore"/>), used for send/receiving
        /// messages in jobs.
        /// </summary>
        public ref ConcurrentDriverStore ConcurrentDriverStore => ref UnsafeUtility.AsRef<Pointers>(m_DriverPointer).ConcurrentDriverStore;

        /// <summary>
        /// Convenience. Records the DriverStore used in the latest call to <see cref="Listen"/> or <see cref="Connect"/>.
        /// </summary>
        /// <remarks>
        /// <para>Note that the actual Endpoint used by each <see cref="NetworkStreamDriver"/> may be different,
        /// due to <see cref="IPCNetworkInterface"/>.</para>
        /// <para>See <see cref="SanitizeConnectAddress"/> and <see cref="SanitizeListenAddress"/>.</para>
        /// </remarks>
        public NetworkEndpoint LastEndPoint { get; internal set; }

        internal NetworkStreamReceiveSystem.DriverState DriverState { get; private set; }

        private NativeReference<int> m_NumNetworkIds;
        private NativeQueue<int> m_FreeNetworkIds;

        /// <summary>
        /// Require all incoming connections to all the drivers in the driver store to go through the connection
        /// approval process. If turned off the connections are immediately approved and go from connecting to
        /// the handshake state.
        /// <br/>Server-only. Always false on the client.
        /// </summary>
        public bool RequireConnectionApproval
        {
            get => RequireConnectionApprovalInternal == 1;
            set
            {
                for (var i = DriverStore.FirstDriver; i < DriverStore.LastDriver; ++i)
                {
                    ref readonly var driverInstance = ref DriverStore.GetDriverInstanceRO(i);
                    if (driverInstance.driver.IsCreated && driverInstance.driver.Bound)
                    {
                        UnityEngine.Debug.LogError("Attempting to set RequireConnectionApproval while network driver has already been started. This must be done before connecting/listening.");
                        return;
                    }
                }
                RequireConnectionApprovalInternal = value ? (byte)1 : (byte)0;
            }
        }
        internal byte RequireConnectionApprovalInternal;

        /// <summary>
        ///     <para>
        ///         Stores all <see cref="NetCodeConnectionEvent" />s raised by Netcode for this
        ///         <see cref="SimulationSystemGroup" /> tick,
        ///         which allows user code to subscribe to connection and disconnection events (including
        ///         <see cref="ConnectionState.State.Handshake" /> and <see cref="ConnectionState.State.Approval" />, if
        ///         applicable).
        ///         Refer to the Network connection page
        ///         (https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/manual/network-connection.html)
        ///         for more details.
        ///     </para>
        ///     <para>
        ///         It's a self-cleaning list and therefore has no consume API (in other words, there's no need to explicitly
        ///         remove entries from this collection, which is why it's read-only). This also means that events are only
        ///         valid for a single <see cref="SimulationSystemGroup" /> tick, and therefore must be polled inside this group.
        ///     </para>
        ///     <para>
        ///         This collection is cleared and repopulated in the <see cref="NetworkGroupCommandBufferSystem" />, which
        ///         is also the ECB playback that creates and destroys <see cref="NetworkStreamConnection" /> 'NetworkConnection'
        ///         entities.
        ///         Therefore, if you query this collection after the `NetworkGroupCommandBufferSystem` system (via the
        ///         <see cref="UpdateAfterAttribute" />),
        ///         you'll get the current tick's event data, but if you poll before it, your event data will always be one tick
        ///         out of date.
        ///     </para>
        ///  <code>
        ///      [BurstCompile]
        ///      void ISystem.OnUpdate(ref SystemState state)
        ///      {
        ///          foreach (var evt in SystemAPI.GetSingleton&lt;NetworkStreamDriver&gt;().ConnectionEventsForTick)
        ///          {
        ///              UnityEngine.Debug.Log($"[{state.WorldUnmanaged.Name}] {evt.ToFixedString()}!");
        ///          }
        ///      }</code>
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This collection can be safely passed into jobs, as long as the <see cref="NetworkStreamDriver" /> singleton
        ///         is fetched as read/write.
        ///     </para>
        ///     <para>
        ///         These events are raised on client worlds as well, but only for your own client world.
        ///         I.e. Each client does not receive events regarding other clients. Refer to the PlayerList NetcodeSamples sample
        ///         (https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/PlayerList)
        ///         for an example implementation of RPC logic that actually communicates player join and leave events (with
        ///         display names and <see cref="NetworkStreamDisconnectReason" />s).
        ///     </para>
        /// </remarks>
        public NativeArray<NetCodeConnectionEvent>.ReadOnly ConnectionEventsForTick { get; internal set; }

        /// <summary>
        ///     The raw list of <see cref="NetCodeConnectionEvent"/>'s. <see cref="ConnectionEventsForTick"/>.
        /// </summary>
        internal NativeList<NetCodeConnectionEvent> ConnectionEventsList { get; }

        /// <summary>
        /// Check if the endpoint can be used for listening for the given driver type. At the moment,
        /// rules are enforced for the <see cref="IPCNetworkInterface"/>.
        /// </summary>
        /// <param name="endpoint">The address to validate and sanitise.</param>
        /// <param name="driverId">The id of driver in between [FirstDriver/LastDriver) range.</param>
        /// <returns>A valid address to use for listening, if the address is valid for the driver or if has been possible to sanitise it.
        /// An invalid address otherwise.</returns>
        private NetworkEndpoint SanitizeListenAddress(in NetworkEndpoint endpoint, int driverId)
        {
            if (DriverStore.GetDriverType(driverId) != TransportType.IPC)
                return endpoint;
            //This is just a debug log to remind that you are passing an ANY address and that the listen port is now going to be different for each driver. That would requires some special handling when it comes to the
            //local IPC connection.
            if (endpoint.Port == 0)
            {
                UnityEngine.Debug.Log($"Driver with ID {driverId} uses IPCNetworkInterface. The endpoint used for listening is using Port == 0. A random port will be assigned to this interface. In order to connect to this endpoint, you will need to retrieve the local address. You can use the NetworkStreamDriver.GetLocalEndPoint({driverId}) to retrieve the assigned address.");
            }
            if(!endpoint.IsAny && !endpoint.IsLoopback)
            {
                UnityEngine.Debug.LogWarning($"Driver with ID {driverId} uses IPCNetworkInterface. It must listen to Any:XXX or Loopback:XXX but endpoint is {endpoint.ToFixedString()}. Forcing listening to ANY:{endpoint.Port}");
                if(endpoint.Family == NetworkFamily.Ipv6)
                    return NetworkEndpoint.AnyIpv6.WithPort(endpoint.Port);
                return NetworkEndpoint.AnyIpv4.WithPort(endpoint.Port);
            }

            return endpoint;
        }

        /// <summary>
        /// Check if the address we are trying to connect to is valid for driver type.
        /// </summary>
        /// <param name="endpoint">the endpoint to sanitise</param>
        /// <param name="driverId">the driver we wants to check for</param>
        /// <returns>The address you should to pass to Connect. </returns>
        /// <remarks>
        /// This function always return a valid address.
        /// </remarks>
        #if UNITY_EDITOR || !UNITY_CLIENT
        private NetworkEndpoint SanitizeConnectAddress(in NetworkEndpoint endpoint, int driverId)
        {
            if (endpoint.IsLoopback)
                return endpoint;

            if (DriverStore.GetDriverType(driverId) == TransportType.IPC)
            {
                //When using IPC driver, the address MUST be a loopback. We are enforcing this here
                UnityEngine.Debug.LogWarning(
                    $"Trying to connect to a server at address {endpoint.ToFixedString()} using an IPCNetworkInterface. IPC interfaces only support loopback address. Forcing using the NetworkEndPoint.Loopback address; family (IPV4/IPV6) and port will be preserved");
                if (endpoint.Family == NetworkFamily.Ipv4)
                    return NetworkEndpoint.LoopbackIpv4.WithPort(endpoint.Port);
                return NetworkEndpoint.LoopbackIpv6.WithPort(endpoint.Port);
            }
            return endpoint;
        }
        #endif

        /// <summary>
        /// Tell all the registered <see cref="NetworkDriverStore"/> drivers to start listening for incoming connections.
        /// </summary>
        /// <param name="endpoint">The local address to use. This is the address that will be used to bind the underlying socket.</param>
        /// <returns>Whether the drivers starts listening</returns>
        public bool Listen(NetworkEndpoint endpoint)
        {
            //Check that at least the first driver have been created. This is a sufficient condition.
            if (!DriverStore.m_Driver0.IsCreated)
                throw new InvalidOperationException($"You cannot call Listen on a NetworkStreamDriver for which the DriverStore have been not created. Please ensure the NetworkDriverStore is setup before calling the Listen method.");

            // Switching to server mode. Start listening all the driver interfaces
            var errors = new FixedList32Bytes<int>();
            //It is possible to listen on a specific address/port. However, for IPC drivers there is a restriction:
            //the ip address should be Any or the Loopback address and the port must be != 0.
            //Because it is possible to have multiple drivers, we are going to force the IPC
            //network interface to be bound and listen the ANY.Port or (if a real IP has been provided) to Loopback:Port
            //Also, binding to Any:0 or Loopback:0 should also be considered invalid in this case.
            for(int i=DriverStore.FirstDriver; i<DriverStore.LastDriver;++i)
            {
                var tempAddress = SanitizeListenAddress(endpoint, i);
                //SanitizeListenAddress return an invalid address if the endpoint can't be sanised.
                if(!tempAddress.IsValid)
                {
                    errors.Add(i);
                    continue;
                }
                ref var driverInstance = ref DriverStore.GetDriverInstanceRW(i);
                if(driverInstance.driver.Bind(tempAddress) != 0 || driverInstance.driver.Listen() != 0)
                    errors.Add(i);
            }
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
        /// <returns>The entity that hold the <see cref="NetworkStreamConnection"/>.
        /// If the endpoint is not valid </returns>
        /// <exception cref="InvalidOperationException">Throw an exception if the driver is not created or if multiple drivers are register</exception>
        public Entity Connect(EntityManager entityManager, NetworkEndpoint endpoint, Entity ent = default)
        {
            if (!DriverStore.m_Driver0.IsCreated)
                throw new InvalidOperationException($"You cannot call Connect on a NetworkStreamDriver for which the DriverStore have been not created. Please ensure the NetworkDriverStore is setup before calling the Connect method.");

            var netDebugQuery = entityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<NetDebug>());
            var netDebug = netDebugQuery.GetSingleton<NetDebug>();

            var isIpEndpoint = endpoint.Family == NetworkFamily.Ipv4 || endpoint.Family == NetworkFamily.Ipv6;
            if (!endpoint.IsValid || (isIpEndpoint && endpoint.Port == 0))
            {
                //Can't connect to a any port. This must be a valid address
                netDebug.LogError($"Trying to connect to the address {endpoint.ToFixedString()} that has port == 0. For connection, a port !=0 is required");
                return default;
            }

            //Still storing the last connecting endpoint as it passed
            LastEndPoint = endpoint;

            if (ent == Entity.Null)
                ent = entityManager.CreateEntity();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (DriverStore.DriversCount == 0)
                throw new InvalidOperationException("Cannot connect to the server. NetworkDriver not created");
            if (DriverStore.DriversCount != 1)
                throw new InvalidOperationException("Too many NetworkDriver created for the client. Only one NetworkDriver instance should exist");
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<NetworkSnapshotAck>();
            using var query = entityManager.CreateEntityQuery(builder);
            if (!query.IsEmpty)
                throw new InvalidOperationException("Connection to server already initiated, only one connection allowed at a time.");
#endif
#if UNITY_EDITOR || !UNITY_CLIENT
            endpoint = SanitizeConnectAddress(endpoint, DriverStore.FirstDriver);
#endif
            ref var driver = ref DriverStore.GetDriverRW(NetworkDriverStore.FirstDriverId);
            var connection = driver.Connect(endpoint);
            var state = driver.GetConnectionState(connection).ToNetcodeState(hasHandshaked: false, hasApproval: false);

            entityManager.AddComponent(ent, NetworkStreamConnection.GetEssentialComponentsForConnection());
            entityManager.AddComponentData(ent, new NetworkStreamConnection
            {
                Value = connection,
                DriverId = 1,
                CurrentState = state,
                CurrentStateDirty = true, // Delay the `NetCodeConnectionEvent` for `Connecting` by up to 1 frame,
                                          // so that it gets created and destroyed in line with the others.
            });
            if (entityManager.HasComponent<ConnectionState>(ent))
            {
                entityManager.SetComponentData(ent, new ConnectionState()
                {
                    CurrentState = state
                });
            }
            entityManager.AddComponentData(ent, new NetworkSnapshotAck());
            entityManager.AddBuffer<OutgoingRpcDataStreamBuffer>(ent);
            entityManager.AddBuffer<OutgoingCommandDataStreamBuffer>(ent);
            entityManager.AddBuffer<IncomingSnapshotDataStreamBuffer>(ent);
            entityManager.GetBuffer<LinkedEntityGroup>(ent).Add(new LinkedEntityGroup{Value = ent});
            netDebug.DebugLog($"[{entityManager.WorldUnmanaged.Name}][Connection] Connect called: Connection={connection.ToFixedString()}, State={state}.");
            return ent;
        }

        /// <summary>
        /// The remote connection address. This is the seen public ip address of the connection.
        /// </summary>
        /// <param name="connection">Connection</param>
        /// <returns>
        /// When relay is used, the current relay host address. Otherwise the remote endpoint address.
        /// </returns>
        /// <remarks>
        /// Be aware that this method work sliglty differnetly than the NetworkDriver.GetRemoteEndpoint.
        /// The <see cref="NetworkDriver.GetRemoteEndpoint"/> does not always return a valid address when used with relay
        /// (once the connection is established it become the RelayAllocationId).
        /// We instead wanted a consistent behaviour for this method: always return the address to which this connection is
        /// is connected/connecting to.
        /// </remarks>
        public NetworkEndpoint GetRemoteEndPoint(NetworkStreamConnection connection)
        {
            // TODO - Fetch as readonly when inner methods are marked as readonly (to prevent copy).
            ref var driver = ref DriverStore.GetDriverRW(connection.DriverId);
            if (driver.CurrentSettings.TryGet(out RelayNetworkParameter relayParams))
                return relayParams.ServerData.Endpoint;
            return driver.GetRemoteEndpoint(connection.Value);
        }

        /// <summary>
        /// Check if the given connection is using relay to connect to the remote endpoint
        /// </summary>
        /// <param name="connection">Connection</param>
        /// <returns>
        /// Either if the connection is using the relay or not.
        /// </returns>
        public bool UseRelay(NetworkStreamConnection connection)
        {
            // TODO - Fetch as readonly when inner methods are marked as readonly (to prevent copy).
            ref var driver = ref DriverStore.GetDriverRW(connection.DriverId);
            return driver.CurrentSettings.TryGet(out RelayNetworkParameter _);
        }

        /// <summary>
        /// Get the local endpoint (the endpoint remote peers will use to reach this driver) used by the first driver inside <see cref="NetworkDriverStore"/>.
        /// This is similar to calling <see cref="GetLocalEndPoint(int)"/> with
        /// <see cref="NetworkDriverStore.FirstDriverId">NetworkDriverStore.FirstDriverId</see> as argument.
        /// </summary>
        /// <returns>The local endpoint of the first driver.</returns>
        public NetworkEndpoint GetLocalEndPoint()
        {
            return GetLocalEndPoint(NetworkDriverStore.FirstDriverId);
        }

        /// <summary>
        /// Get the local endpoint used by the driver (the endpoint remote peers will use to reach this driver).
        /// <br/>
        /// When multiple drivers exist, e.g. when using both IPC and Socket connection, multiple drivers will be available
        /// in the <see cref="NetworkDriverStore"/>.
        /// </summary>
        /// <param name="driverId">Id of the driver. See <see cref="NetworkDriverStore.GetDriverRO"/>.</param>
        /// <returns>The local endpoint of the driver.</returns>
        public NetworkEndpoint GetLocalEndPoint(int driverId)
        {
            // TODO - Fetch as readonly when inner methods are marked as readonly (to prevent copy).
            return DriverStore.GetDriverRW(driverId).GetLocalEndpoint();
        }

        /// <summary>
        /// The current state of the internal transport connection.
        /// </summary>
        /// <param name="connection">Connection</param>
        /// <returns>The current state of the internal transport connection</returns>
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

            DriverState = NetworkStreamReceiveSystem.DriverState.Migrating;
            return driverStoreState;
        }

        /// <summary>
        /// Reset the current <see cref="DriverStore"/> by disposing the current instance and its associated
        /// <see cref="ConcurrentDriverStore"/>.
        /// This method can be used to re-create and re-configure the driver after world has been created and before either
        /// <see cref="Listen"/> or <see cref="Connect"/> has been called.
        /// </summary>
        /// <example>
        /// <code>
        /// var driverStore = new NetworkDriverStore();
        /// var constructor = NetworkStreamReceiveSystem.DriverConstructor;
        /// constructor.CreateServerDriver(serverWorld, ref driverStore, netDebug);
        /// var driver = EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton&lt;NetworkStreamDriver&gt;();
        /// driver.ResetDriverStore(driverStore);
        /// var listenEndPoint = NetworkEndpoint.AnyIpv4.WithPort(MyPort);
        /// driver.Listen(listenEndPoint);
        /// </code>
        /// </example>
        /// <param name="world">The world the NetworkStreamDriver singleton is part of.</param>
        /// <param name="driverStore">The new driver store to use.</param>
        public void ResetDriverStore(WorldUnmanaged world, ref NetworkDriverStore driverStore)
        {
            if (UnsafeUtility.AddressOf(ref driverStore) == UnsafeUtility.AddressOf(ref DriverStore))
            {
                //Try to self assign the same instance. Skip. I would say this is an error. Unfortunately, we can't catch the
                //case where the NetworkDriverStore is copied on the stack and assigned.
                return;
            }
            if (world.IsClient() && DriverStore.DriversCount > 1)
                throw new InvalidOperationException($"Cannot assign the NetworkDriverStore to the NetworkStreamDriver for world {world.Name}. Client must configure the driver store to use ONLY ONE network driver, but the {nameof(driverStore)} instance passed as argument has been configured to use {driverStore.DriversCount} network drivers.");

            //If the driver is not the "default" (no registered driver and the first interface is not created) it is valid to dispose the current driver.
            //For example: the server can dispose the driver to stop listening (it is actually the only way to stop listening).
            //In all cases, it is not valid to dispose a driver if there are connections.
            if (DriverStore.IsCreated)
            {
                using var connectionQuery = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
                if (!connectionQuery.IsEmpty)
                    throw new InvalidOperationException($"Cannot assign the NetworkDriverStore to the NetworkStreamDriver for world {world.Name} because there are NetworkStreamConnection entities.\nPlease ensure you are setting up the drivers after you disconnected all the connections and have them properly cleanup by the NetworkStreamReceiveSystem. This will usually require at least one world update (because NetworkStreamConnection are cleanup component).");
            }

            //reset the current driver store any any case. This is a no-op if the current instance is already destroyed.
            DriverStore.Dispose();
            //finalize the driver store by adding any empty drivers. Calling Begin is not required, it is just finalizing the driver creation.
            //Modify an existing driver store is also prohibited. Like calling RegisterDriver after having the driver finalized.
            driverStore.FinalizeDriverStore();
            DriverStore = driverStore;
            ConcurrentDriverStore = driverStore.ToConcurrent();
        }
    }
}

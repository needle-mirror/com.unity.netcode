using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    /// <summary>
    /// The transport category/type use by a NetworkDriver.
    /// </summary>
    public enum TransportType : int
    {
        /// <summary>
        /// Not configured, or unsupported transport interface. The transport type for a registered driver instance
        /// is always valid (not this value, in other words), unless the driver creation failed.
        /// </summary>
        Invalid = 0,
        /// <summary>
        /// An inter-process like communication channel with zero latency, and guaranteed delivery.
        /// </summary>
        IPC,
        /// <summary>
        /// A socket based communication channel. WebSocket, UDP, TCP or any similar communication channels fit that category.
        /// </summary>
        Socket,
    }

    /// <summary>
    /// Store and manage an array of NetworkDriver. The capacity is fixed to <see cref="Capacity"/>.
    /// The driver registration should start by calling BeginDriverRegistration() and terminate with EndDriverRegistration().
    /// The store also provide some accessor and utlilty methods.
    /// </summary>
    public struct NetworkDriverStore
    {
        /// <summary>
        /// Struct that contains a <see cref="NetworkDriver"/> and relative pipelines.
        /// </summary>
        public struct NetworkDriverInstance
        {
            /// <summary>
            /// The <see cref="NetworkDriver"/> instance. Can be invalid if the NetworkDriver instance has not
            /// been initialized.
            /// </summary>
            public NetworkDriver driver;
            /// <summary>
            /// The pipeline used for sending reliable messages
            /// </summary>
            public NetworkPipeline reliablePipeline;
            /// <summary>
            /// The pipeline used for sending unreliable messages and snapshots
            /// </summary>
            public NetworkPipeline unreliablePipeline;
            /// <summary>
            /// The pipeline used for sending big unreliable messages that requires fragmentation.
            /// </summary>
            public NetworkPipeline unreliableFragmentedPipeline;
            /// <summary>
            /// Flag set when the driver pipelines uses the <see cref="SimulatorPipelineStage"/>.
            /// </summary>
            public bool simulatorEnabled
            {
                get => driver.IsCreated && driver.CurrentSettings.TryGet<SimulatorUtility.Parameters>(out _) || driver.CurrentSettings.TryGet<NetworkSimulatorParameter>(out _);
                [Obsolete("This set has no effect on whether or not the simulator is actually enabled, and therefore should not be used.", false)]
                // ReSharper disable once ValueParameterNotUsed
                set { }
            }

            internal void StopListening()
            {
                #pragma warning disable 0618
                driver.StopListening();
                #pragma warning restore 0618
            }
        }

        /// <summary>
        /// Struct that contains a the <see cref="NetworkDriver.Concurrent"/> version of the <see cref="NetworkDriver"/>
        /// and relative pipelines.
        /// </summary>
        public struct Concurrent
        {
            /// <summary>
            /// The <see cref="NetworkDriver.Concurrent"/> version of the network driver.
            /// </summary>
            public NetworkDriver.Concurrent driver;
            /// <summary>
            /// The pipeline used for sending reliable messages
            /// </summary>
            public NetworkPipeline reliablePipeline;
            /// <summary>
            /// The pipeline used for sending unreliable messages and snapshots
            /// </summary>
            public NetworkPipeline unreliablePipeline;
            /// <summary>
            /// The pipeline used for sending big unreliable messages that requires fragmentation.
            /// </summary>
            public NetworkPipeline unreliableFragmentedPipeline;
        }

        internal struct NetworkDriverData
        {
            public NetworkDriverInstance instance;
            public TransportType transportType;

            public void Dispose()
            {
                if (instance.driver.IsCreated)
                    instance.driver.Dispose();
            }

            public bool IsCreated => instance.driver.IsCreated;
        }

        internal NetworkDriverData m_Driver0;
        internal NetworkDriverData m_Driver1;
        internal NetworkDriverData m_Driver2;
        private int m_numDrivers;
        private int m_Finalized;

        /// <summary>
        /// The fixed capacity of the driver container.
        /// </summary>
        public const int Capacity = 3;
        /// <summary>
        /// The first assigned unique identifier to each driver.
        /// </summary>
        public const int FirstDriverId = 1;
        /// <summary>
        /// The number of registered drivers. Must be always less than the total driver <see cref="Capacity"/>.
        /// </summary>
        public readonly int DriversCount => m_numDrivers;
        /// <summary>
        /// The first driver id present in the store.
        /// Can be used to iterate over all registered drivers in a for loop.
        /// </summary>
        /// <example><code>
        /// for(int i= driverStore.FirstDriver; i &lt; driverStore.LastDriver; ++i)
        /// {
        ///      ref var instance = ref driverStore.GetDriverInstance(i);
        ///      ....
        /// }
        /// </code></example>
        public readonly int FirstDriver => FirstDriverId;
        /// <summary>
        /// The last driver id present in the store.
        /// Can be used to iterate over all registered drivers in a for loop.
        /// </summary>
        /// <example><code>
        /// for(int i= driverStore.FirstDriver; i &lt; driverStore.LastDriver; ++i)
        /// {
        ///      ref var instance = ref driverStore.GetDriverInstance(i).
        ///      ....
        /// }
        /// </code></example>
        public readonly int LastDriver => FirstDriverId + m_numDrivers;
        /// <summary>
        /// Return true if the driver store contains a driver that has a simulator pipeline.
        /// </summary>
        public readonly bool IsAnyUsingSimulator
        {
            get
            {
                for (var i = FirstDriver; i < LastDriver; ++i)
                {
                    if (GetDriverInstanceRO(i).simulatorEnabled)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Return true if there is at least one driver listening for incoming connections.
        /// </summary>
        public bool HasListeningInterfaces
        {
            get
            {
                for (var i = FirstDriver; i < LastDriver; ++i)
                {
                    ref readonly var driverInstance = ref GetDriverInstanceRO(i);
                    if (driverInstance.driver.IsCreated && driverInstance.driver.Listening)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Denote if the store has at least one driver registered
        /// </summary>
        public bool IsCreated => m_numDrivers > 0 && m_Driver0.IsCreated;

        /// <summary>
        /// Add a new driver to the store. Throw exception if all drivers slot are already occupied or the driver is not created/valid
        /// </summary>
        /// <returns>The assigned driver id </returns>
        /// <param name="driverType">Driver type</param>
        /// <param name="driverInstance">Instance of driver</param>
        /// <exception cref="InvalidOperationException">Thrown if cannot register or the NetworkDriverStore is finalized.</exception>
        public int RegisterDriver(TransportType driverType, in NetworkDriverInstance driverInstance)
        {
            if (driverInstance.driver.IsCreated == false)
                throw new InvalidOperationException("Cannot register non valid driver (IsCreated == false)");
            if (m_numDrivers == Capacity)
                throw new InvalidOperationException("Cannot register more driver. All slot are already used");
            if(m_Finalized != 0)
                throw new InvalidOperationException("It is invalid to register a NetworkDriver instance to an already finalized NetworkDriverStore.\nIn order to register a new driver, you need to create a new NetworkDriverStore or invoke the RegisterNetworkDriver before the store instance is assigned to NetworkStreamDriver.");
            int nextDriverId = FirstDriverId + m_numDrivers;
            ++m_numDrivers;
            ref var driverRef = ref GetDriverDataRW(nextDriverId);
            if (driverRef.IsCreated)
                driverRef.Dispose();
            driverRef.transportType = driverType;
            driverRef.instance = driverInstance;
            return nextDriverId;
        }


        /// <summary>
        /// Finalize the registration phase by initializing all missing driver instances with a NullNetworkInterface.
        /// This final step is necessary to make the job safety system able to track all the safety handles.
        /// </summary>
        internal void FinalizeDriverStore()
        {
            if (m_Finalized != 0)
                throw new InvalidOperationException("FinalizeDriverStore is called on already finalized NetworkDriverStore instance.");
            //The ifdef is to prevent allocating driver internal data when not necessary.
            //Allocating all drivers is necessary only in case safety handles are enabled.
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_Driver0.IsCreated)
                m_Driver0.instance.driver = NetworkDriver.Create(new NullNetworkInterface());
            if (!m_Driver1.IsCreated)
                m_Driver1.instance.driver = NetworkDriver.Create(new NullNetworkInterface());
            if (!m_Driver2.IsCreated)
                m_Driver2.instance.driver = NetworkDriver.Create(new NullNetworkInterface());
#endif
        }

        /// <summary>
        /// Return a concurrent version of the store that can be used in parallel jobs.
        /// </summary>
        internal ConcurrentDriverStore ToConcurrent()
        {
            var store = new ConcurrentDriverStore();
            //The if is necessary here because if ENABLE_UNITY_COLLECTIONS_CHECKS is not defined we don't
            //create all the drivers instances
            if (m_Driver0.IsCreated)
                store.m_Concurrent0 = new Concurrent
                {
                    driver = m_Driver0.instance.driver.ToConcurrent(),
                    reliablePipeline = m_Driver0.instance.reliablePipeline,
                    unreliablePipeline = m_Driver0.instance.unreliablePipeline,
                    unreliableFragmentedPipeline = m_Driver0.instance.unreliableFragmentedPipeline,
                };
            if (m_Driver1.IsCreated)
                store.m_Concurrent1 = new Concurrent
                {
                    driver = m_Driver1.instance.driver.ToConcurrent(),
                    reliablePipeline = m_Driver1.instance.reliablePipeline,
                    unreliablePipeline = m_Driver1.instance.unreliablePipeline,
                    unreliableFragmentedPipeline = m_Driver1.instance.unreliableFragmentedPipeline,
                };
            if (m_Driver2.IsCreated)
                store.m_Concurrent2 = new Concurrent
                {
                    driver = m_Driver2.instance.driver.ToConcurrent(),
                    reliablePipeline = m_Driver2.instance.reliablePipeline,
                    unreliablePipeline = m_Driver2.instance.unreliablePipeline,
                    unreliableFragmentedPipeline = m_Driver2.instance.unreliableFragmentedPipeline,
                };
            return store;
        }

        /// <summary>
        /// Dispose all the registered drivers instances and their allocated resources.
        /// </summary>
        public void Dispose()
        {
            m_Driver0.Dispose();
            m_Driver1.Dispose();
            m_Driver2.Dispose();
        }

        /// <summary>
        /// Returns the <see cref="NetworkDriverData"/> instance, by readonly ref.
        /// </summary>
        /// <param name="driverId">The index of the target driver. See <see cref="FirstDriver"/> and <see cref="LastDriver"/>.</param>
        /// <returns>The <see cref="NetworkDriverData"/> instance, by readonly ref.</returns>
        /// <exception cref="InvalidOperationException">Throws if driverId is out of range.</exception>
        internal readonly unsafe ref readonly NetworkDriverData GetDriverDataRO(int driverId)
        {
            CheckValid(driverId);
            fixed (NetworkDriverStore* store = &this)
            {
                switch (driverId)
                {
                    case 1: return ref store->m_Driver0;
                    case 2: return ref store->m_Driver1;
                    case 3: return ref store->m_Driver2;
                    default:
                        throw new InvalidOperationException($"Cannot find NetworkDriver with id {driverId}");
                }
            }
        }
        /// <summary>
        /// Returns the <see cref="NetworkDriverData"/> instance, by ref.
        /// </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        internal readonly unsafe ref NetworkDriverData GetDriverDataRW(int driverId)
        {
            CheckValid(driverId);
            fixed (NetworkDriverStore* store = &this)
            {
                switch (driverId)
                {
                    case 1: return ref store->m_Driver0;
                    case 2: return ref store->m_Driver1;
                    case 3: return ref store->m_Driver2;
                    default:
                        throw new InvalidOperationException($"Cannot find NetworkDriver with id {driverId}");
                }
            }
        }

        /// <summary>
        /// Return the <see cref="NetworkDriverInstance"/> instance with the given <see cref="driverId"/>.
        /// </summary>
        /// <remarks>
        /// The method return a copy of the driver instance not a reference. While this is suitable for almost all the use cases,
        /// since the driver is trivially copyable, be aware that calling some of the Driver class methods, like ScheduleUpdate,
        /// that update internal driver data (that aren't suited to be copied around) may not work as expected.
        /// </remarks>
        /// <inheritdoc cref="GetDriverDataRO"/>
        [Obsolete("Prefer GetDriverInstanceRW or GetDriverInstanceRO to avoid copying.", false)]
        public readonly ref NetworkDriverInstance GetDriverInstance(int driverId) => ref GetDriverDataRW(driverId).instance;

        /// <summary>
        /// Return the <see cref="NetworkDriver"/> with the given <see cref="driverId"/>.
        /// </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        [Obsolete("Prefer GetDriverRW or GetDriverRO to avoid copying.", false)]
        public readonly NetworkDriver GetNetworkDriver(int driverId) => GetDriverDataRO(driverId).instance.driver;

        /// <summary>
        ///  Return a reference to the <see cref="NetworkDriverStore.NetworkDriverInstance"/> instance with the given <see cref="driverId"/>.
        ///  </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        public readonly ref NetworkDriverStore.NetworkDriverInstance GetDriverInstanceRW(int driverId) => ref GetDriverDataRW(driverId).instance;

        /// <summary>
        ///  Return a reference to the <see cref="NetworkDriverStore.NetworkDriverInstance"/> instance with the given <see cref="driverId"/>.
        ///  </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        public readonly ref readonly NetworkDriverStore.NetworkDriverInstance GetDriverInstanceRO(int driverId) => ref GetDriverDataRO(driverId).instance;

        /// <summary>
        /// Retrieve a ReadWrite reference to the <see cref="NetworkDriver"/> for the given <see cref="driverId"/>.
        /// </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        public readonly ref NetworkDriver GetDriverRW(int driverId) => ref GetDriverInstanceRW(driverId).driver;

        /// <summary>
        /// Retrieve a Read-Only reference to the <see cref="NetworkDriver"/> for the given <see cref="driverId"/>.
        /// </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        public readonly ref readonly NetworkDriver GetDriverRO(int driverId) => ref GetDriverInstanceRO(driverId).driver;

        /// <summary>
        /// Return the transport type used by the registered driver.
        /// </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        public readonly TransportType GetDriverType(int driverId) => GetDriverDataRO(driverId).transportType;

        /// <summary>
        /// Return the state of the <see cref="NetworkStreamConnection"/> connection.
        /// </summary>
        /// <param name="connection">A client or server connection</param>
        /// <returns>The state of the <see cref="NetworkStreamConnection"/> connection</returns>
        /// <exception cref="InvalidOperationException">Throw an exception if the driver associated to the connection is not found</exception>
        public readonly NetworkConnection.State GetConnectionState(NetworkStreamConnection connection) => GetDriverRW(connection.DriverId).GetConnectionState(connection.Value);

        /// <summary>
        /// Signature for all functions that can be used to visit the registered drivers in the store using the <see cref="ForEachDriver"/> method.
        /// </summary>
        /// <param name="driver">a reference to a <see cref="NetworkDriverInstance"/></param>
        /// <param name="driverId">the id of the driver. Must always greater or equals <see cref="NetworkDriverStore.FirstDriverId"/></param>
        public delegate void DriverVisitor(ref NetworkDriverInstance driver, int driverId);

        /// <summary>
        /// Invoke the delegate on all registered drivers.
        /// </summary>
        /// <param name="visitor">Visitor to invoke with the driver instance and ID</param>
        [Obsolete("The ForEachDriver has been deprecated. Please always iterate over the driver using a for loop, using the FirstDriver and LastDriver ids instead.")]
        public void ForEachDriver(DriverVisitor visitor)
        {
            if (m_numDrivers == 0)
                return;
            visitor(ref m_Driver0.instance, FirstDriverId);
            if (m_numDrivers > 1)
                visitor(ref m_Driver1.instance, FirstDriverId + 1);
            if (m_numDrivers > 2)
                visitor(ref m_Driver2.instance, FirstDriverId + 2);
        }

        /// <summary>
        /// Utility method to disconnect the <see cref="NetworkStreamConnection" /> connection.
        /// </summary>
        /// <inheritdoc cref="GetDriverRW"/>
        public void Disconnect(NetworkStreamConnection connection) => GetDriverRW(connection.DriverId).Disconnect(connection.Value);

        internal JobHandle ScheduleUpdateAllDrivers(JobHandle dependency)
        {
            if (m_numDrivers == 0)
                return dependency;
            JobHandle driver0 = m_Driver0.instance.driver.ScheduleUpdate(dependency);
            JobHandle driver1 = default, driver2 = default;
            if (m_numDrivers > 1)
                driver1 = m_Driver1.instance.driver.ScheduleUpdate(dependency);
            if (m_numDrivers > 2)
                driver2 = m_Driver2.instance.driver.ScheduleUpdate(dependency);
            return JobHandle.CombineDependencies(driver0, driver1, driver2);
        }

        /// <summary>
        /// Invoke <see cref="NetworkDriver.ScheduleFlushSend"/> on all registered drivers in the store
        /// </summary>
        /// <param name="dependency">A job handle whom all flush jobs depend upon</param>
        /// <returns>The combined handle of all the scheduled jobs.</returns>
        public JobHandle ScheduleFlushSendAllDrivers(JobHandle dependency)
        {
            if (m_numDrivers == 0)
                return dependency;
            JobHandle driver0 = m_Driver0.instance.driver.ScheduleFlushSend(dependency);
            JobHandle driver1 = default, driver2 = default;
            if (m_numDrivers > 1)
                driver1 = m_Driver1.instance.driver.ScheduleFlushSend(dependency);
            if (m_numDrivers > 2)
                driver2 = m_Driver2.instance.driver.ScheduleFlushSend(dependency);
            return JobHandle.CombineDependencies(driver0, driver1, driver2);
        }

        private readonly void CheckValid(int driverId)
        {
            var isValidDriverId = driverId >= FirstDriverId && driverId < LastDriver;
            if (!isValidDriverId)
                throw new InvalidOperationException($"DriverId:{driverId} out of range: {FirstDriverId} -> {LastDriver}!");
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        /// <summary>
        /// A do-nothing network interface for internal use. All the NetworkDriver slots in the <see cref="NetworkDriverStore"/>
        /// that are not registered are initialized with this interface.
        /// </summary>
        internal struct NullNetworkInterface : INetworkInterface
        {
            public NetworkEndpoint LocalEndpoint => throw new NotImplementedException();

            public int Bind(NetworkEndpoint endpoint) => throw new NotImplementedException();

            public void Dispose() { }

            public int Initialize(ref NetworkSettings settings, ref int packetPadding) => 0;

            public int Listen() => throw new NotImplementedException();

            public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dep) => throw new NotImplementedException();

            public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dep) => throw new NotImplementedException();
        }
#endif
    }

    /// <summary>
    /// The concurrent version of the DriverStore. Contains the concurrent copy of the drivers and relative pipelines.
    /// </summary>
    public struct ConcurrentDriverStore
    {
        internal NetworkDriverStore.Concurrent m_Concurrent0;
        internal NetworkDriverStore.Concurrent m_Concurrent1;
        internal NetworkDriverStore.Concurrent m_Concurrent2;

        /// <summary>
        /// Get the concurrent driver with the given driver id.
        /// </summary>
        /// <param name="driverId">the id of the driver. Must always greater or equals <see cref="NetworkDriverStore.FirstDriverId"/></param>
        /// <returns>the concurrent version of the NetworkdDriverStore</returns>
        /// <exception cref="InvalidOperationException">Throws if driverId is out of range.</exception>
        public NetworkDriverStore.Concurrent GetConcurrentDriver(int driverId)
        {
            var concurrent = driverId switch
            {
                1 => m_Concurrent0,
                2 => m_Concurrent1,
                3 => m_Concurrent2,
                _ => throw new InvalidOperationException($"Concurrent driverId:{driverId} out of range!"),
            };
            if (!concurrent.driver.m_ConnectionList.IsCreated)
                throw new InvalidOperationException($"Concurrent driverId:{driverId} invalid!");
            return concurrent;
        }
    }
}

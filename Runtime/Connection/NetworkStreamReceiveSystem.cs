using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// Parent group of all systems that; receive data from the server, deal with connections, and
    /// that need to perform operations before the ghost simulation group.
    /// In particular, <see cref="CommandSendSystemGroup"/>,
    /// <see cref="HeartbeatSendSystem"/>, <see cref="HeartbeatReceiveSystem"/> and the <see cref="NetworkStreamReceiveSystem"/>
    /// update in this group.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation,
        WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    public class NetworkReceiveSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// Factory interface that needs to be implemented by a concrete class for creating and registering new <see cref="NetworkDriver"/> instances.
    /// </summary>
    public interface INetworkStreamDriverConstructor
    {
        /// <summary>
        /// Register to the driver store a new instance of <see cref="NetworkDriver"/> suitable to be used by clients.
        /// </summary>
        /// <param name="world"></param>
        /// <param name="driver"></param>
        /// <param name="netDebug"></param>
        void CreateClientDriver(World world, ref NetworkDriverStore driver, NetDebug netDebug);
        /// <summary>
        /// Register to the driver store a new instance of <see cref="NetworkDriver"/> suitable to be used by servers.
        /// </summary>
        /// <param name="world"></param>
        /// <param name="driver"></param>
        /// <param name="netDebug"></param>
        void CreateServerDriver(World world, ref NetworkDriverStore driver, NetDebug netDebug);
    }

    /// <summary>
    /// A system processing NetworkStreamRequestConnect components
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    [BurstCompile]
    public unsafe partial struct NetworkStreamConnectSystem : ISystem
    {
        private EntityQuery m_ConnectionRequestConnectQuery;
        private ComponentLookup<NetworkStreamRequestConnect> m_NetworkStreamRequestConnectFromEntity;
        private ComponentLookup<ConnectionState> m_ConnectionStateFromEntity;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_ConnectionRequestConnectQuery = state.GetEntityQuery(ComponentType.ReadWrite<NetworkStreamRequestConnect>());
            m_NetworkStreamRequestConnectFromEntity = state.GetComponentLookup<NetworkStreamRequestConnect>(true);
            m_ConnectionStateFromEntity = state.GetComponentLookup<ConnectionState>();
            state.RequireForUpdate(m_ConnectionRequestConnectQuery);
            state.RequireForUpdate<NetworkStreamDriver>();
            state.RequireForUpdate<NetDebug>();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState systemState)
        {
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;

            var ents = m_ConnectionRequestConnectQuery.ToEntityArray(Allocator.Temp);
            m_NetworkStreamRequestConnectFromEntity.Update(ref systemState);
            var requestFromEntity = m_NetworkStreamRequestConnectFromEntity;
            m_ConnectionStateFromEntity.Update(ref systemState);
            var stateFromEntity = m_ConnectionStateFromEntity;

            bool connected = true;
            foreach (var ent in ents)
            {
                var endpoint = requestFromEntity[ent].Endpoint;
                if (!networkStreamDriver.ConnectAsync(systemState.EntityManager, endpoint, ent))
                {
                    if (stateFromEntity.HasComponent(ent))
                    {
                        var state = stateFromEntity[ent];
                        state.DisconnectReason = NetworkStreamDisconnectReason.ConnectionClose;
                        state.CurrentState = ConnectionState.State.Disconnected;
                        stateFromEntity[ent] = state;
                    }
                    systemState.EntityManager.DestroyEntity(ent);
                    netDebug.DebugLog("Connect request failed.");
                }

                if (!networkStreamDriver.Connected)
                {
                    connected = false;
                }
            }

            if (connected)
            {
                systemState.EntityManager.RemoveComponent<NetworkStreamRequestConnect>(m_ConnectionRequestConnectQuery);
            }
        }
    }
    /// <summary>
    /// A system processing NetworkStreamRequestListen components
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    [BurstCompile]
    public unsafe partial struct NetworkStreamListenSystem : ISystem
    {
        private EntityQuery m_ConnectionRequestListenQuery;
        private ComponentLookup<NetworkStreamRequestListen> m_NetworkStreamRequestListenFromEntity;
        private ComponentLookup<ConnectionState> m_ConnectionStateFromEntity;
        public void OnCreate(ref SystemState state)
        {
            m_ConnectionRequestListenQuery = state.GetEntityQuery(ComponentType.ReadWrite<NetworkStreamRequestListen>());
            m_NetworkStreamRequestListenFromEntity = state.GetComponentLookup<NetworkStreamRequestListen>(true);
            state.RequireForUpdate(m_ConnectionRequestListenQuery);
            state.RequireForUpdate<NetworkStreamDriver>();
            state.RequireForUpdate<NetDebug>();
        }
        public void OnDestroy(ref SystemState state)
        {
        }
        public void OnUpdate(ref SystemState systemState)
        {
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;

            var ents = m_ConnectionRequestListenQuery.ToEntityArray(Allocator.Temp);
            m_NetworkStreamRequestListenFromEntity.Update(ref systemState);
            var requestFromEntity = m_NetworkStreamRequestListenFromEntity;
            m_ConnectionStateFromEntity.Update(ref systemState);
            var stateFromEntity = m_ConnectionStateFromEntity;

            bool connected = true;
            foreach (var ent in ents)
            {
                var endpoint = requestFromEntity[ent].Endpoint;
                if (!networkStreamDriver.ListenAsync(endpoint))
                {
                    if (stateFromEntity.HasComponent(ent))
                    {
                        var state = stateFromEntity[ent];
                        state.DisconnectReason = NetworkStreamDisconnectReason.ConnectionClose;
                        state.CurrentState = ConnectionState.State.Disconnected;
                        stateFromEntity[ent] = state;
                    }
                    systemState.EntityManager.DestroyEntity(ent);
                    netDebug.DebugLog("Listen request failed.");
                }
                if (!networkStreamDriver.Connected)
                {
                    connected = false;
                }
            }

            if (connected)
            {
                systemState.EntityManager.RemoveComponent<NetworkStreamRequestListen>(m_ConnectionRequestListenQuery);
            }
        }
    }

    /// <summary>
    /// The NetworkStreamReceiveSystem is one of the most important system of the NetCode package and its fundamental job
    /// is to manage all the <see cref="NetworkStreamConnection"/> life-cycles (creation, update, destruction), and receiving all the
    /// <see cref="NetworkStreamProtocol"/> message types.
    /// It is responsible also responsible for:
    /// <para>- creating the <see cref="NetworkStreamDriver"/> singleton (see also <seealso cref="NetworkDriverStore"/> and <seealso cref="NetworkDriver"/>).</para>
    /// <para>- handling the driver migration (see <see cref="DriverMigrationSystem"/> and <see cref="MigrationTicket"/>).</para>
    /// <para>- listening and accepting incoming connections (server).</para>
    /// <para>- exchanging the <see cref="NetworkProtocolVersion"/> during the initial handshake.</para>
    /// <para>- updating the <see cref="ConnectionState"/> state component if present.</para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [CreateAfter(typeof(NetDebugSystem))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    public unsafe partial struct NetworkStreamReceiveSystem : ISystem
    {
        private static INetworkStreamDriverConstructor s_DriverConstructor;

        /// <summary>
        /// Assign your <see cref="INetworkStreamDriverConstructor"/> to customize the <see cref="NetworkDriver"/> construction.
        /// </summary>
        static public INetworkStreamDriverConstructor DriverConstructor
        {
            get
            {
                if (s_DriverConstructor == null)
                    s_DriverConstructor = DefaultDriverBuilder.DefaultDriverConstructor;
                return s_DriverConstructor;
            }
            set
            {
                s_DriverConstructor = value;
            }
        }

        internal enum DriverState : int
        {
            Default,
            Migrating
        }

        private ref NetworkDriverStore DriverStore => ref UnsafeUtility.AsRef<NetworkStreamDriver.Pointers>((void*)m_DriverPointers).DriverStore;
        private NativeReference<int> m_NumNetworkIds;
        private NativeQueue<int> m_FreeNetworkIds;
        private RpcQueue<RpcSetNetworkId, RpcSetNetworkId> m_RpcQueue;

        private EntityQuery m_refreshTickRateQuery;

        private IntPtr m_DriverPointers;
        private ComponentLookup<ConnectionState> m_ConnectionStateFromEntity;
        private ComponentLookup<GhostComponent> m_GhostComponentFromEntity;
        private ComponentLookup<NetworkIdComponent> m_NetworkIdFromEntity;
        private ComponentLookup<ClientServerTickRate> m_ClientServerTickRateFromEntity;
        private ComponentLookup<NetworkStreamRequestDisconnect> m_RequestDisconnectFromEntity;
        private ComponentLookup<NetworkStreamInGame> m_InGameFromEntity;
        private BufferLookup<OutgoingRpcDataStreamBufferComponent> m_OutgoingRpcBufferFromEntity;
        private BufferLookup<IncomingRpcDataStreamBufferComponent> m_RpcBufferFromEntity;
        private BufferLookup<IncomingCommandDataStreamBufferComponent> m_CmdBufferFromEntity;
        private BufferLookup<IncomingSnapshotDataStreamBufferComponent> m_SnapshotBufferFromEntity;

        public void OnCreate(ref SystemState state)
        {
            DriverMigrationSystem driverMigrationSystem = default;
            foreach (var world in World.All)
            {
                if ((driverMigrationSystem = world.GetExistingSystemManaged<DriverMigrationSystem>()) != null)
                    break;
            }

            m_NumNetworkIds = new NativeReference<int>(Allocator.Persistent);
            m_FreeNetworkIds = new NativeQueue<int>(Allocator.Persistent);

            m_RpcQueue = SystemAPI.GetSingleton<RpcCollection>().GetRpcQueue<RpcSetNetworkId, RpcSetNetworkId>();
            m_ConnectionStateFromEntity = state.GetComponentLookup<ConnectionState>(false);
            m_GhostComponentFromEntity = state.GetComponentLookup<GhostComponent>(true);
            m_NetworkIdFromEntity = state.GetComponentLookup<NetworkIdComponent>(true);
            m_ClientServerTickRateFromEntity = state.GetComponentLookup<ClientServerTickRate>();
            m_RequestDisconnectFromEntity = state.GetComponentLookup<NetworkStreamRequestDisconnect>();
            m_InGameFromEntity = state.GetComponentLookup<NetworkStreamInGame>();

            m_OutgoingRpcBufferFromEntity = state.GetBufferLookup<OutgoingRpcDataStreamBufferComponent>();
            m_RpcBufferFromEntity = state.GetBufferLookup<IncomingRpcDataStreamBufferComponent>();
            m_CmdBufferFromEntity = state.GetBufferLookup<IncomingCommandDataStreamBufferComponent>();
            m_SnapshotBufferFromEntity = state.GetBufferLookup<IncomingSnapshotDataStreamBufferComponent>();


            NetworkEndpoint lastEp = default;
            NetworkDriverStore driverStore = default;
            if (SystemAPI.HasSingleton<MigrationTicket>())
            {
                 var ticket = SystemAPI.GetSingleton<MigrationTicket>();
                 // load driver & all the network connection data
                 var driverState = driverMigrationSystem.Load(ticket.Value);
                 driverStore = driverState.DriverStore;
                 lastEp = driverState.LastEp;
                 m_NumNetworkIds.Value = driverState.NextId;
                 foreach (var id in driverState.FreeList)
                     m_FreeNetworkIds.Enqueue(id);
                 driverState.FreeList.Dispose();
            }
            else
            {
                driverStore = new NetworkDriverStore();
                driverStore.BeginDriverRegistration();
                if (state.World.IsServer())
                    DriverConstructor.CreateServerDriver(state.World, ref driverStore, SystemAPI.GetSingleton<NetDebug>());
                else
                    DriverConstructor.CreateClientDriver(state.World, ref driverStore, SystemAPI.GetSingleton<NetDebug>());
                driverStore.EndDriverRegistration();
            }

            m_DriverPointers = (IntPtr)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<NetworkStreamDriver.Pointers>(), UnsafeUtility.AlignOf<NetworkStreamDriver.Pointers>(), Allocator.Persistent);

            ref var store = ref UnsafeUtility.AsRef<NetworkStreamDriver.Pointers>((void*)m_DriverPointers);
            store.DriverStore = driverStore;
            store.ConcurrentDriverStore = driverStore.ToConcurrent();

            var networkStreamEntity = state.EntityManager.CreateEntity(ComponentType.ReadWrite<NetworkStreamDriver>());
            state.EntityManager.SetName(networkStreamEntity, "NetworkStreamDriver");
            SystemAPI.SetSingleton(new NetworkStreamDriver((void*)m_DriverPointers, m_NumNetworkIds, m_FreeNetworkIds, lastEp));
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<NetworkTime>();
            state.RequireForUpdate<NetDebug>();

            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<ClientServerTickRateRefreshRequest>();
            m_refreshTickRateQuery = state.GetEntityQuery(builder);
        }

        public void OnDestroy(ref SystemState state)
        {
            m_NumNetworkIds.Dispose();
            m_FreeNetworkIds.Dispose();

            ref readonly var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRO;
            if ((int)DriverState.Default == networkStreamDriver.DriverState)
            {
                var driverStore = DriverStore;
                foreach (var connection in SystemAPI.Query<RefRO<NetworkStreamConnection>>())
                {
                    driverStore.Disconnect(connection.ValueRO);
                }
                DriverStore.ScheduleUpdateAllDrivers(state.Dependency).Complete();
                DriverStore.Dispose();
            }
            UnsafeUtility.Free((void*)m_DriverPointers, Allocator.Persistent);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            FixedString128Bytes debugPrefix = $"[{state.WorldUnmanaged.Name}][Connection]";

            if (!SystemAPI.HasSingleton<NetworkProtocolVersion>())
            {
                var entity = state.EntityManager.CreateEntity();
                state.EntityManager.SetName(entity, "NetworkProtocolVersion");
                // RW is required because this call marks the collection as final which means no further rpcs can be registered
                var rpcVersion = SystemAPI.GetSingletonRW<RpcCollection>().ValueRW.CalculateVersionHash();
                var componentsVersion = GhostCollectionSystem.CalculateComponentCollectionHash(SystemAPI.GetSingletonBuffer<GhostComponentSerializer.State>());
                var gameVersion = SystemAPI.HasSingleton<GameProtocolVersion>() ? SystemAPI.GetSingleton<GameProtocolVersion>().Version : 0;
                state.EntityManager.AddComponentData(entity, new NetworkProtocolVersion
                {
                    NetCodeVersion = NetworkProtocolVersion.k_NetCodeVersion,
                    GameVersion = gameVersion,
                    RpcCollectionVersion = rpcVersion,
                    ComponentCollectionVersion = componentsVersion
                });
            }
            var freeNetworkIds = m_FreeNetworkIds;
            var updateHandle = state.Dependency;
            var driverListening = DriverStore.DriversCount > 0 && DriverStore.GetDriverInstance(DriverStore.FirstDriver).driver.Listening;
            if (driverListening)
            {
                for (int i = DriverStore.FirstDriver+1; i < DriverStore.LastDriver; ++i)
                {
                    driverListening &= DriverStore.GetDriverInstance(i).driver.Listening;
                }
                // Detect failed listen by checking if some but not all drivers are listening
                if (!driverListening)
                {
                    for (int i = DriverStore.FirstDriver+1; i < DriverStore.LastDriver; ++i)
                    {
                        if (DriverStore.GetDriverInstance(i).driver.Listening)
                            DriverStore.GetDriverInstance(i).StopListening();
                    }
                }
            }
            state.Dependency = DriverStore.ScheduleUpdateAllDrivers(state.Dependency);

            if (driverListening)
            {
                m_GhostComponentFromEntity.Update(ref state);

                // Schedule accept job
                var acceptJob = new ConnectionAcceptJob();
                acceptJob.driverStore = DriverStore;
                acceptJob.commandBuffer = commandBuffer;
                acceptJob.numNetworkId = m_NumNetworkIds;
                acceptJob.freeNetworkIds = m_FreeNetworkIds;
                acceptJob.rpcQueue = m_RpcQueue;
                acceptJob.ghostFromEntity = m_GhostComponentFromEntity;
                SystemAPI.TryGetSingleton<ClientServerTickRate>(out acceptJob.tickRate);
                acceptJob.tickRate.ResolveDefaults();
                acceptJob.protocolVersion = SystemAPI.GetSingleton<NetworkProtocolVersion>();
                acceptJob.netDebug = netDebug;
                acceptJob.debugPrefix = debugPrefix;
                state.Dependency = acceptJob.Schedule(state.Dependency);
            }
            else
            {
                if (!state.WorldUnmanaged.IsServer() && !SystemAPI.HasSingleton<ClientServerTickRate>())
                {
                    var newEntity = state.EntityManager.CreateEntity();
                    var tickRate = new ClientServerTickRate();
                    tickRate.ResolveDefaults();
                    state.EntityManager.AddComponentData(newEntity, tickRate);
                }
                if (!m_refreshTickRateQuery.IsEmptyIgnoreFilter)
                {
                    m_ClientServerTickRateFromEntity.Update(ref state);
                    var refreshJob = new RefreshClientServerTickRate
                    {
                        commandBuffer = commandBuffer,
                        netDebug = netDebug,
                        debugPrefix = debugPrefix,
                        tickRateEntity = SystemAPI.GetSingletonEntity<ClientServerTickRate>(),
                        dataFromEntity = m_ClientServerTickRateFromEntity
                    };
                    state.Dependency = refreshJob.ScheduleByRef(state.Dependency);
                }
                m_FreeNetworkIds.Clear();
            }

            m_ConnectionStateFromEntity.Update(ref state);
            m_NetworkIdFromEntity.Update(ref state);
            m_RequestDisconnectFromEntity.Update(ref state);
            m_InGameFromEntity.Update(ref state);
            // Schedule parallel update job
            m_OutgoingRpcBufferFromEntity.Update(ref state);
            m_RpcBufferFromEntity.Update(ref state);
            m_CmdBufferFromEntity.Update(ref state);
            m_SnapshotBufferFromEntity.Update(ref state);

            // FIXME: because it uses buffer from entity
            var handleJob = new HandleDriverEvents
            {
                commandBuffer = commandBuffer,
                netDebug = netDebug,
                debugPrefix = debugPrefix,
                driverStore = DriverStore,
                networkIdFromEntity = m_NetworkIdFromEntity,
                connectionStateFromEntity = m_ConnectionStateFromEntity,
                requestDisconnectFromEntity = m_RequestDisconnectFromEntity,
                inGameFromEntity = m_InGameFromEntity,
                freeNetworkIds = m_FreeNetworkIds,

                outgoingRpcBuffer = m_OutgoingRpcBufferFromEntity,
                rpcBuffer = m_RpcBufferFromEntity,
                cmdBuffer = m_CmdBufferFromEntity,
                snapshotBuffer = m_SnapshotBufferFromEntity,

                protocolVersion = SystemAPI.GetSingleton<NetworkProtocolVersion>(),
                localTime = NetworkTimeSystem.TimestampMS,
                serverTick = networkTime.ServerTick
            };
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            handleJob.netStats = SystemAPI.GetSingletonRW<GhostStatsCollectionCommand>().ValueRO.Value;
#endif
            state.Dependency = handleJob.ScheduleByRef(state.Dependency);
        }

        [BurstCompile]
        [StructLayout(LayoutKind.Sequential)]
        struct ConnectionAcceptJob : IJob
        {
            public EntityCommandBuffer commandBuffer;
            public NetworkDriverStore driverStore;
            public NativeReference<int> numNetworkId;
            public NativeQueue<int> freeNetworkIds;
            public RpcQueue<RpcSetNetworkId, RpcSetNetworkId> rpcQueue;
            public ClientServerTickRate tickRate;
            public NetworkProtocolVersion protocolVersion;
            public NetDebug netDebug;
            public FixedString128Bytes debugPrefix;
            [ReadOnly] public ComponentLookup<GhostComponent> ghostFromEntity;

            public void Execute()
            {
                for (int i = driverStore.FirstDriver; i < driverStore.LastDriver; ++i)
                {
                    var driver = driverStore.GetNetworkDriver(i);
                    NetworkConnection con;
                    while ((con = driver.Accept()) != default(NetworkConnection))
                    {
                        // New connection can never have any events, if this one does - just close it
                        DataStreamReader reader;
                        var evt = con.PopEvent(driver, out reader);
                        if (evt != NetworkEvent.Type.Empty)
                        {
                            con.Disconnect(driver);
                            netDebug.DebugLog(FixedString.Format("[{0}][Connection] Disconnecting stale connection detected as new (has pending event={1}).",debugPrefix, (int)evt));
                            continue;
                        }

                        //TODO: Lookup for any connection that is already connected with the same ip address or any other player identity.
                        //Relying on the IP is pretty weak test but at least is remove already some positives
                        var connection = new NetworkStreamConnection
                        {
                            Value = con,
                            DriverId = i
                        };
                        var ent = commandBuffer.CreateEntity();
                        commandBuffer.AddComponent(ent, connection);
                        // rpc send buffer might need to be migrated...
                        commandBuffer.AddComponent(ent, new NetworkSnapshotAckComponent());
                        commandBuffer.AddBuffer<PrespawnSectionAck>(ent);
                        commandBuffer.AddComponent(ent, new CommandTargetComponent());
                        commandBuffer.AddBuffer<IncomingRpcDataStreamBufferComponent>(ent);
                        var rpcBuffer = commandBuffer.AddBuffer<OutgoingRpcDataStreamBufferComponent>(ent);
                        commandBuffer.AddBuffer<IncomingCommandDataStreamBufferComponent>(ent);
                        commandBuffer.AddBuffer<LinkedEntityGroup>(ent).Add(new LinkedEntityGroup{Value = ent});

                        RpcCollection.SendProtocolVersion(rpcBuffer, protocolVersion);

                        // Send RPC - assign network id
                        int nid;
                        if (!freeNetworkIds.TryDequeue(out nid))
                        {
                            // Avoid using 0
                            nid = numNetworkId.Value + 1;
                            numNetworkId.Value = nid;
                        }

                        commandBuffer.AddComponent(ent, new NetworkIdComponent {Value = nid});
                        commandBuffer.SetName(ent, new FixedString64Bytes(FixedString.Format("NetworkConnection ({0})", nid)));
                        rpcQueue.Schedule(rpcBuffer, ghostFromEntity, new RpcSetNetworkId
                        {
                            nid = nid,
                            netTickRate = tickRate.NetworkTickRate,
                            simMaxSteps = tickRate.MaxSimulationStepsPerFrame,
                            simMaxStepLength = tickRate.MaxSimulationStepBatchSize,
                            simTickRate = tickRate.SimulationTickRate
                        });
                        netDebug.DebugLog(FixedString.Format("{0} Accepted new connection NetworkId={1} InternalId={2}", debugPrefix, nid, connection.Value.InternalId));
                    }
                }
            }
        }

        [BurstCompile]
        [StructLayout(LayoutKind.Sequential)]
        partial struct RefreshClientServerTickRate : IJobEntity
        {
            public EntityCommandBuffer commandBuffer;
            public NetDebug netDebug;
            public FixedString128Bytes debugPrefix;
            public Entity tickRateEntity;
            public ComponentLookup<ClientServerTickRate> dataFromEntity;
            public void Execute(Entity entity, in ClientServerTickRateRefreshRequest req)
            {
                var tickRate = dataFromEntity[tickRateEntity];
                tickRate.MaxSimulationStepsPerFrame = req.MaxSimulationStepsPerFrame;
                tickRate.NetworkTickRate = req.NetworkTickRate;
                tickRate.SimulationTickRate = req.SimulationTickRate;
                tickRate.MaxSimulationStepBatchSize = req.MaxSimulationStepBatchSize;
                dataFromEntity[tickRateEntity] = tickRate;
                var dbgMsg = FixedString.Format("Using SimulationTickRate={0} NetworkTickRate={1} MaxSimulationStepsPerFrame={2} TargetFrameRateMode={3}", tickRate.SimulationTickRate, tickRate.NetworkTickRate, tickRate.MaxSimulationStepsPerFrame, (int)tickRate.TargetFrameRateMode);
                netDebug.DebugLog(FixedString.Format("{0} {1}", debugPrefix, dbgMsg));
                commandBuffer.RemoveComponent<ClientServerTickRateRefreshRequest>(entity);
            }
        }

        [BurstCompile]
        partial struct HandleDriverEvents : IJobEntity
        {
            public EntityCommandBuffer commandBuffer;
            public NetDebug netDebug;
            public FixedString128Bytes debugPrefix;
            public NetworkDriverStore driverStore;
            [ReadOnly] public ComponentLookup<NetworkIdComponent> networkIdFromEntity;
            public ComponentLookup<ConnectionState> connectionStateFromEntity;
            public ComponentLookup<NetworkStreamRequestDisconnect> requestDisconnectFromEntity;
            public ComponentLookup<NetworkStreamInGame> inGameFromEntity;
            public NativeQueue<int> freeNetworkIds;

            public BufferLookup<OutgoingRpcDataStreamBufferComponent> outgoingRpcBuffer;
            public BufferLookup<IncomingRpcDataStreamBufferComponent> rpcBuffer;
            public BufferLookup<IncomingCommandDataStreamBufferComponent> cmdBuffer;
            public BufferLookup<IncomingSnapshotDataStreamBufferComponent> snapshotBuffer;

            public NetworkProtocolVersion protocolVersion;

            public uint localTime;
            public NetworkTick serverTick;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif
            public void Execute(Entity entity, ref NetworkStreamConnection connection,
                ref NetworkSnapshotAckComponent snapshotAck)
            {
                if (requestDisconnectFromEntity.HasComponent(entity))
                {
                    var disconnect = requestDisconnectFromEntity[entity];
                    var id = -1;
                    if (networkIdFromEntity.HasComponent(entity))
                    {
                        id = networkIdFromEntity[entity].Value;
                        freeNetworkIds.Enqueue(id);
                    }
                    driverStore.Disconnect(connection);
                    if (connectionStateFromEntity.HasComponent(entity))
                    {
                        var state = connectionStateFromEntity[entity];
                        state.DisconnectReason = disconnect.Reason;
                        state.CurrentState = ConnectionState.State.Disconnected;
                        connectionStateFromEntity[entity] = state;
                    }
                    commandBuffer.DestroyEntity(entity); // This can cause issues if some other system adds components while it is in the queue
                    netDebug.DebugLog(FixedString.Format("{0} Disconnecting NetworkId={1} InternalId={2} Reason={3}", debugPrefix, id, connection.Value.InternalId, DisconnectReasonEnumToString.Convert((int)disconnect.Reason)));
                }
                else if (!inGameFromEntity.HasComponent(entity))
                {
                    snapshotAck = new NetworkSnapshotAckComponent
                    {
                        LastReceivedRemoteTime = snapshotAck.LastReceivedRemoteTime,
                        LastReceiveTimestamp = snapshotAck.LastReceiveTimestamp,
                        EstimatedRTT = snapshotAck.EstimatedRTT,
                        DeviationRTT = snapshotAck.DeviationRTT
                    };
                }

                if (!connection.Value.IsCreated)
                    return;
                if (!outgoingRpcBuffer.HasBuffer(entity))
                {
                    var buf = commandBuffer.AddBuffer<OutgoingRpcDataStreamBufferComponent>(entity);
                    RpcCollection.SendProtocolVersion(buf, protocolVersion);
                }
                var driver = driverStore.GetNetworkDriver(connection.DriverId);
                if (connectionStateFromEntity.HasComponent(entity))
                {
                    var state = connectionStateFromEntity[entity];
                    var newState = state;
                    switch (driver.GetConnectionState(connection.Value))
                    {
                    case NetworkConnection.State.Disconnected:
                        newState.CurrentState = ConnectionState.State.Disconnected;
                        break;
                    case NetworkConnection.State.Connecting:
                        newState.CurrentState = ConnectionState.State.Connecting;
                        break;
                    case NetworkConnection.State.Connected:
                        newState.CurrentState = ConnectionState.State.Connected;
                        break;
                    default:
                        newState.CurrentState = ConnectionState.State.Unknown;
                        break;
                    }
                    if (newState.CurrentState == ConnectionState.State.Connected)
                    {
                        if (networkIdFromEntity.HasComponent(entity))
                            newState.NetworkId = networkIdFromEntity[entity].Value;
                        else
                            newState.CurrentState = ConnectionState.State.Handshake;
                    }
                    if (!state.Equals(newState))
                        connectionStateFromEntity[entity] = newState;
                }
                DataStreamReader reader;
                NetworkEvent.Type evt;
                while ((evt = driver.PopEventForConnection(connection.Value, out reader)) != NetworkEvent.Type.Empty)
                {
                    switch (evt)
                    {
                        case NetworkEvent.Type.Connect:
                            break;
                        case NetworkEvent.Type.Disconnect:
                            var reason = NetworkStreamDisconnectReason.ConnectionClose;
                            if (reader.Length == 1)
                                reason = (NetworkStreamDisconnectReason)reader.ReadByte();

                            if (connectionStateFromEntity.HasComponent(entity))
                            {
                                var state = connectionStateFromEntity[entity];
                                state.CurrentState = ConnectionState.State.Disconnected;
                                state.DisconnectReason = reason;
                                connectionStateFromEntity[entity] = state;
                            }

                            commandBuffer.DestroyEntity(entity);

                            if (cmdBuffer.HasBuffer(entity))
                                cmdBuffer[entity].Clear();
                            connection.Value = default(NetworkConnection);
                            var id = -1;
                            if (networkIdFromEntity.HasComponent(entity))
                            {
                                id = networkIdFromEntity[entity].Value;
                                freeNetworkIds.Enqueue(id);
                            }
                            netDebug.DebugLog(FixedString.Format("{0} Connection closed NetworkId={1} InternalId={2} Reason={3}", debugPrefix, id, connection.Value.InternalId, DisconnectReasonEnumToString.Convert((int)reason)));
                            return;
                        case NetworkEvent.Type.Data:
                            // FIXME: do something with the data
                            var msgType = reader.ReadByte();
                            switch ((NetworkStreamProtocol)msgType)
                            {
                                case NetworkStreamProtocol.Command:
                                {
                                    if (!cmdBuffer.HasBuffer(entity))
                                        break;
                                    var buffer = cmdBuffer[entity];
                                    var snapshot = new NetworkTick{SerializedData = reader.ReadUInt()};
                                    uint snapshotMask = reader.ReadUInt();
                                    snapshotAck.UpdateReceivedByRemote(snapshot, snapshotMask);
                                    uint remoteTime = reader.ReadUInt();
                                    uint localTimeMinusRTT = reader.ReadUInt();
                                    uint interpolationDelay = reader.ReadUInt();
                                    uint numLoadedPrefabs = reader.ReadUInt();

                                    snapshotAck.UpdateRemoteAckedData(remoteTime, numLoadedPrefabs, interpolationDelay);
                                    snapshotAck.UpdateRemoteTime(remoteTime, localTimeMinusRTT, localTime);
                                    var tickReader = reader;
                                    var cmdTick = new NetworkTick{SerializedData = tickReader.ReadUInt()};
                                    var isValidCmdTick = !snapshotAck.LastReceivedSnapshotByLocal.IsValid || cmdTick.IsNewerThan(snapshotAck.LastReceivedSnapshotByLocal);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                                    netStats[0] = serverTick.SerializedData;
                                    netStats[1] = (uint)reader.Length - 1u;
                                    if (!isValidCmdTick || buffer.Length > 0)
                                        netStats[2] = netStats[2] + 1;
#endif
                                    // Do not try to process incoming commands which are older than commands we already processed
                                    if (!isValidCmdTick)
                                        break;
                                    snapshotAck.LastReceivedSnapshotByLocal = cmdTick;

                                    buffer.Clear();
                                    buffer.Add(ref reader);
                                    break;
                                }
                                case NetworkStreamProtocol.Snapshot:
                                {
                                    if (!snapshotBuffer.HasBuffer(entity))
                                        break;
                                    uint remoteTime = reader.ReadUInt();
                                    uint localTimeMinusRTT = reader.ReadUInt();
                                    snapshotAck.ServerCommandAge = reader.ReadInt();
                                    snapshotAck.UpdateRemoteTime(remoteTime, localTimeMinusRTT, localTime);

                                    var buffer = snapshotBuffer[entity];
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                                    if (buffer.Length > 0)
                                        netStats[2] = netStats[2] + 1;
#endif
                                    buffer.Clear();
                                    buffer.Add(ref reader);
                                    break;
                                }
                                case NetworkStreamProtocol.Rpc:
                                {
                                    uint remoteTime = reader.ReadUInt();
                                    uint localTimeMinusRTT = reader.ReadUInt();
                                    snapshotAck.UpdateRemoteTime(remoteTime, localTimeMinusRTT, localTime);
                                    var buffer = rpcBuffer[entity];
                                    buffer.Add(ref reader);
                                    break;
                                }
                                default:
                                    netDebug.LogError(FixedString.Format("Received unknown message type {0}", msgType));
                                    break;
                            }

                            break;
                        default:
                            netDebug.LogError(FixedString.Format("Received unknown network event {0}", (int)evt));
                            break;
                    }
                }
            }
        }
    }
}

#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using Unity.Profiling;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode
{
    /// <summary>
    /// Parent group of all systems that; receive data from the server, deal with connections, and
    /// that need to perform operations before the ghost simulation group.
    /// In particular, <see cref="CommandSendSystemGroup"/>, and the <see cref="NetworkStreamReceiveSystem"/>
    /// update in this group.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation,
        WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    [UpdateBefore(typeof(GhostSimulationSystemGroup))]
    public partial class NetworkReceiveSystemGroup : ComponentSystemGroup
    {
    }

    internal struct MigratedNetworkIdsData : IComponentData
    {
        public NativeHashMap<uint, int> MigratedNetworkIds;
    }

    internal struct NetworkIDAllocationData : IComponentData
    {
        public NativeReference<int> NumNetworkIds;
        public NativeQueue<int> FreeNetworkIds;
    }

    /// <summary>
    /// Factory interface that needs to be implemented by a concrete class for creating and registering new <see cref="NetworkDriver"/> instances.
    /// </summary>
    public interface INetworkStreamDriverConstructor
    {
        /// <summary>
        /// Register to the driver store a new instance of <see cref="NetworkDriver"/> suitable to be used by clients.
        /// </summary>
        /// <param name="world">Client world</param>
        /// <param name="driver">Driver store</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        void CreateClientDriver(World world, ref NetworkDriverStore driver, NetDebug netDebug);
        /// <summary>
        /// Register to the driver store a new instance of <see cref="NetworkDriver"/> suitable to be used by servers.
        /// </summary>
        /// <param name="world">Server world</param>
        /// <param name="driver">Driver store</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        void CreateServerDriver(World world, ref NetworkDriverStore driver, NetDebug netDebug);
    }

    /// <summary>
    /// A system processing NetworkStreamRequestConnect components
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    [BurstCompile]
    public partial struct NetworkStreamConnectSystem : ISystem
    {
        EntityQuery m_ConnectionRequestConnectQuery;
        ComponentLookup<ConnectionState> m_ConnectionStateFromEntity;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_ConnectionRequestConnectQuery = state.GetEntityQuery(ComponentType.ReadWrite<NetworkStreamRequestConnect>());
            m_ConnectionStateFromEntity = state.GetComponentLookup<ConnectionState>();
            state.RequireForUpdate<NetworkStreamDriver>();
            state.RequireForUpdate<NetDebug>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState systemState)
        {
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            networkStreamDriver.ConnectionEventsList.Clear();

            if (m_ConnectionRequestConnectQuery.IsEmpty) return;
            m_ConnectionStateFromEntity.Update(ref systemState);
            var stateFromEntity = m_ConnectionStateFromEntity;

            var requests = m_ConnectionRequestConnectQuery.ToComponentDataArray<NetworkStreamRequestConnect>(Allocator.Temp);
            var requetEntity = m_ConnectionRequestConnectQuery.ToEntityArray(Allocator.Temp);
            systemState.EntityManager.RemoveComponent<NetworkStreamRequestConnect>(m_ConnectionRequestConnectQuery);
            if (requests.Length > 1)
            {
                //There is more than 1 request. We don't know what was the last queued (there is not way to detect that reliably with
                //chunk ordering). Unless we put something like a Timestamp (that requires users adding it or we need to provide a proper
                //API. We can eventually support that later. For now we just get the first request and discard the others.
                netDebug.LogError($"Found {requests.Length} pending connection requests. It is required that only one NetworkStreamRequestConnect is queued at any time. Only the connect request to {requests[0].Endpoint.ToFixedString()} will be handled.");

                for (int i = 1; i < requests.Length; ++i)
                {
                    if (stateFromEntity.HasComponent(requetEntity[i]))
                    {
                        var state = stateFromEntity[requetEntity[i]];
                        state.DisconnectReason = NetworkStreamDisconnectReason.ConnectionClose;
                        state.CurrentState = ConnectionState.State.Disconnected;
                        stateFromEntity[requetEntity[i]] = state;
                    }
                    systemState.EntityManager.DestroyEntity(requetEntity[i]);
                }
            }
            //TODO: add a proper handling of request connect and connection already connected.
            //It may required disposing the driver and also some problem with NetworkStreamReceiveSystem
            var connection = networkStreamDriver.Connect(systemState.EntityManager, requests[0].Endpoint, requetEntity[0]);
            if(connection == Entity.Null)
            {
                netDebug.LogError($"Connect request for {requests[0].Endpoint.ToFixedString()} failed.");
                if (stateFromEntity.HasComponent(requetEntity[0]))
                {
                    var state = stateFromEntity[requetEntity[0]];
                    state.DisconnectReason = NetworkStreamDisconnectReason.ConnectionClose;
                    state.CurrentState = ConnectionState.State.Disconnected;
                    stateFromEntity[requetEntity[0]] = state;
                }
                systemState.EntityManager.DestroyEntity(requetEntity[0]);
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
        EntityQuery m_ConnectionRequestListenQuery;
        ComponentLookup<NetworkStreamRequestListenResult> m_ConnectionStateFromEntity;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            m_ConnectionRequestListenQuery = state.GetEntityQuery(ComponentType.ReadWrite<NetworkStreamRequestListen>());
            m_ConnectionStateFromEntity = state.GetComponentLookup<NetworkStreamRequestListenResult>();
            state.RequireForUpdate<NetworkStreamDriver>();
            state.RequireForUpdate<NetDebug>();
        }

        /// <inheritdoc/>
        public void OnUpdate(ref SystemState systemState)
        {
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            networkStreamDriver.ConnectionEventsList.Clear();

            if (m_ConnectionRequestListenQuery.IsEmpty) return;

            m_ConnectionStateFromEntity.Update(ref systemState);
            var stateFromEntity = m_ConnectionStateFromEntity;
            var requestCount = m_ConnectionRequestListenQuery.CalculateEntityCount();
            var requestListens = m_ConnectionRequestListenQuery.ToComponentDataArray<NetworkStreamRequestListen>(Allocator.Temp);
            var requestEntity = m_ConnectionRequestListenQuery.ToEntityArray(Allocator.Temp);
            var endpoint = requestListens[0].Endpoint;
            var requestEnt = requestEntity[0];
            if (requestListens.Length > 1)
            {
                //There is more than 1 request. We don't know what was the last queued (there is not way to detect that reliably with
                //chunk ordering). Unless we put something like a Timestamp (that requires users adding it or we need to provide a proper
                //API). A proper idea can be implemented for 1.1.
                //For now we just get the first request and discard the others.
                netDebug.LogError($"Found {requestCount} pending listen requests. Only one NetworkStreamRequestListen can be queued at any time. Only the request to listen at {requestListens[0].Endpoint.ToFixedString()} will be handled.");
                for (int i = 1; i < requestEntity.Length; ++i)
                {
                    if (stateFromEntity.HasComponent(requestEnt))
                    {
                        stateFromEntity[requestEnt] = new NetworkStreamRequestListenResult
                        {
                            Endpoint = requestListens[0].Endpoint,
                            RequestState = NetworkStreamRequestListenResult.State.RefusedMultipleRequests
                        };
                    }
                }
            }

            var anyInterfaceListening = false;
            for (int i = networkStreamDriver.DriverStore.FirstDriver; i < networkStreamDriver.DriverStore.LastDriver; ++i)
            {
                anyInterfaceListening |= networkStreamDriver.DriverStore.GetDriverInstanceRO(i).driver.Listening;
            }

            //TODO: we can support that but requires some extra work and disposing the drivers.
            //Also because this is done before the NetworkStreamReceiveSystem some stuff may not work.
            if (anyInterfaceListening)
            {
                netDebug.LogError($"Listen request for address {endpoint.ToFixedString()} refused. Driver is already listening");
                if (stateFromEntity.HasComponent(requestEnt))
                {
                    stateFromEntity[requestEnt] = new NetworkStreamRequestListenResult
                    {
                        Endpoint = requestListens[0].Endpoint,
                        RequestState = NetworkStreamRequestListenResult.State.RefusedAlreadyListening
                    };
                }
            }
            else
            {
                if (networkStreamDriver.Listen(endpoint))
                {
                    if (stateFromEntity.HasComponent(requestEnt))
                    {
                        stateFromEntity[requestEnt] = new NetworkStreamRequestListenResult
                        {
                            Endpoint = requestListens[0].Endpoint,
                            RequestState = NetworkStreamRequestListenResult.State.Succeeded
                        };
                    }
                }
                else
                {
                    netDebug.LogError($"Listen request for address {endpoint.ToFixedString()} failed.");
                    if (stateFromEntity.HasComponent(requestEnt))
                    {
                        stateFromEntity[requestEnt] = new NetworkStreamRequestListenResult
                        {
                            Endpoint = requestListens[0].Endpoint,
                            RequestState = NetworkStreamRequestListenResult.State.Failed
                        };
                    }
                }
            }
            //Consume all requests.
            systemState.EntityManager.DestroyEntity(m_ConnectionRequestListenQuery);
        }
    }

    /// <summary>
    /// <para>The NetworkStreamReceiveSystem is one of the most important system of the NetCode package and its fundamental job
    /// is to manage all the <see cref="NetworkStreamConnection"/> life-cycles (creation, update, destruction), and receiving all the
    /// <see cref="NetworkStreamProtocol"/> message types.
    /// It is responsible also responsible for:</para>
    /// <para>- creating the <see cref="NetworkStreamDriver"/> singleton (see also <see cref="NetworkDriverStore"/> and <see cref="NetworkDriver"/>).</para>
    /// <para>- handling the driver migration (see <see cref="DriverMigrationSystem"/> and <see cref="MigrationTicket"/>).</para>
    /// <para>- listening and accepting incoming connections (server).</para>
    /// <para>- exchanging the <see cref="NetworkProtocolVersion"/> during the initial handshake.</para>
    /// <para>- updating the <see cref="ConnectionState"/> state component if present.</para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    public unsafe partial struct NetworkStreamReceiveSystem : ISystem
    {
        static INetworkStreamDriverConstructor s_DriverConstructor;
        static readonly ProfilerMarker k_Scheduling = new ProfilerMarker("NetworkStreamReceiveSystem_Scheduling");

        /// <summary>
        /// Assign your <see cref="INetworkStreamDriverConstructor"/> to customize the <see cref="NetworkDriver"/> construction.
        /// </summary>
        public static INetworkStreamDriverConstructor DriverConstructor
        {
            get { return s_DriverConstructor ??= DefaultDriverBuilder.DefaultDriverConstructor; }
            set => s_DriverConstructor = value;
        }

        internal enum DriverState
        {
            Default,
            Migrating
        }

        ref NetworkDriverStore DriverStore => ref UnsafeUtility.AsRef<NetworkStreamDriver.Pointers>((void*)m_DriverPointers).DriverStore;
        NativeReference<uint> m_RandomIndex;
        NativeReference<int> m_NumNetworkIds;
        NativeQueue<int> m_FreeNetworkIds;
        RpcQueue<ServerApprovedConnection, ServerApprovedConnection> m_ServerApprovedConnectionRpcQueue;
        RpcQueue<RequestProtocolVersionHandshake, RequestProtocolVersionHandshake> m_RequestProtocolVersionHandshakeRpcQueue;
        RpcQueue<ServerRequestApprovalAfterHandshake,ServerRequestApprovalAfterHandshake> m_ServerRequestApprovalRpcQueue;
        NativeList<uint> m_ConnectionUniqueIds;

        EntityQuery m_RefreshTickRateQuery;

        IntPtr m_DriverPointers;
        ComponentLookup<ConnectionState> m_ConnectionStateFromEntity;
        ComponentLookup<GhostInstance> m_GhostComponentFromEntity;
        ComponentLookup<NetworkId> m_NetworkIdFromEntity;
        ComponentLookup<ConnectionUniqueId> m_ConnectionUniqueIdFromEntity;
        ComponentLookup<ConnectionApproved> m_ApprovedFromEntity;
        ComponentLookup<NetworkStreamRequestDisconnect> m_RequestDisconnectFromEntity;
        ComponentLookup<NetworkStreamInGame> m_InGameFromEntity;
        ComponentLookup<EnablePacketLogging> m_EnablePacketLoggingFromEntity;
        BufferLookup<OutgoingRpcDataStreamBuffer> m_OutgoingRpcBufferFromEntity;
        BufferLookup<IncomingRpcDataStreamBuffer> m_RpcBufferFromEntity;
        BufferLookup<IncomingCommandDataStreamBuffer> m_CmdBufferFromEntity;
        BufferLookup<IncomingSnapshotDataStreamBuffer> m_SnapshotBufferFromEntity;
        NativeList<NetCodeConnectionEvent> m_ConnectionEvents;
        private NetworkPipelineStageId m_reliableSequencedPipelineStageId;

        NativeHashMap<uint, int> m_MigrationIds;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            DriverMigrationSystem driverMigrationSystem = default;
            foreach (var world in World.All)
            {
                if ((driverMigrationSystem = world.GetExistingSystemManaged<DriverMigrationSystem>()) != null)
                    break;
            }

            m_RandomIndex = new NativeReference<uint>(Allocator.Persistent);
            m_RandomIndex.Value = (uint)System.Diagnostics.Stopwatch.GetTimestamp();
            m_NumNetworkIds = new NativeReference<int>(Allocator.Persistent);
            m_FreeNetworkIds = new NativeQueue<int>(Allocator.Persistent);
            m_ConnectionEvents = new NativeList<NetCodeConnectionEvent>(32, Allocator.Persistent);
            m_ConnectionUniqueIds = new NativeList<uint>(16, Allocator.Persistent);

            var rpcCollection = SystemAPI.GetSingleton<RpcCollection>();
            m_ServerApprovedConnectionRpcQueue = rpcCollection.GetRpcQueue<ServerApprovedConnection>();
            m_RequestProtocolVersionHandshakeRpcQueue = rpcCollection.GetRpcQueue<RequestProtocolVersionHandshake>();
            m_ServerRequestApprovalRpcQueue = rpcCollection.GetRpcQueue<ServerRequestApprovalAfterHandshake>();
            m_ConnectionStateFromEntity = state.GetComponentLookup<ConnectionState>(false);
            m_GhostComponentFromEntity = state.GetComponentLookup<GhostInstance>(true);
            m_NetworkIdFromEntity = state.GetComponentLookup<NetworkId>(true);
            m_ConnectionUniqueIdFromEntity = state.GetComponentLookup<ConnectionUniqueId>(true);
            m_ApprovedFromEntity = state.GetComponentLookup<ConnectionApproved>(true);
            m_RequestDisconnectFromEntity = state.GetComponentLookup<NetworkStreamRequestDisconnect>();
            m_InGameFromEntity = state.GetComponentLookup<NetworkStreamInGame>();
            m_EnablePacketLoggingFromEntity = state.GetComponentLookup<EnablePacketLogging>();

            m_OutgoingRpcBufferFromEntity = state.GetBufferLookup<OutgoingRpcDataStreamBuffer>();
            m_RpcBufferFromEntity = state.GetBufferLookup<IncomingRpcDataStreamBuffer>();
            m_CmdBufferFromEntity = state.GetBufferLookup<IncomingCommandDataStreamBuffer>();
            m_SnapshotBufferFromEntity = state.GetBufferLookup<IncomingSnapshotDataStreamBuffer>();
            m_reliableSequencedPipelineStageId = NetworkPipelineStageId.Get<ReliableSequencedPipelineStage>();

            AttemptCreateFakeHostConnection(ref state);

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
                if (state.World.IsServer())
                    DriverConstructor.CreateServerDriver(state.World, ref driverStore, SystemAPI.GetSingleton<NetDebug>());
                else
                    DriverConstructor.CreateClientDriver(state.World, ref driverStore, SystemAPI.GetSingleton<NetDebug>());
            }

            m_DriverPointers = (IntPtr)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<NetworkStreamDriver.Pointers>(), UnsafeUtility.AlignOf<NetworkStreamDriver.Pointers>(), Allocator.Persistent);
            UnsafeUtility.MemClear((void*)m_DriverPointers, UnsafeUtility.SizeOf<NetworkStreamDriver.Pointers>());
            var networkStreamEntity = state.EntityManager.CreateEntity(ComponentType.ReadWrite<NetworkStreamDriver>());
            state.EntityManager.SetName(networkStreamEntity, "NetworkStreamDriver");
            SystemAPI.SetSingleton(new NetworkStreamDriver((void*)m_DriverPointers, m_NumNetworkIds, m_FreeNetworkIds, lastEp, m_ConnectionEvents, m_ConnectionEvents.AsReadOnly()));
            SystemAPI.GetSingleton<NetworkStreamDriver>().ResetDriverStore(state.WorldUnmanaged, ref driverStore);

            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<NetworkTime>();
            state.RequireForUpdate<NetDebug>();

            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<ClientServerTickRateRefreshRequest>();
            m_RefreshTickRateQuery = state.GetEntityQuery(builder);

            m_MigrationIds = new NativeHashMap<uint, int>(8, Allocator.Persistent);

            var migratedNetworkIds = state.EntityManager.CreateEntity(ComponentType.ReadWrite<MigratedNetworkIdsData>());
            state.EntityManager.SetName(migratedNetworkIds, "MigratedNetworkIDds");
            state.EntityManager.SetComponentData(migratedNetworkIds, new MigratedNetworkIdsData() { MigratedNetworkIds = m_MigrationIds });

            var networkIDAllocationData = state.EntityManager.CreateEntity(ComponentType.ReadWrite<NetworkIDAllocationData>());
            state.EntityManager.SetName(networkIDAllocationData, "NetworkIDAllocationData");
            state.EntityManager.SetComponentData(networkIDAllocationData, new NetworkIDAllocationData() { FreeNetworkIds = m_FreeNetworkIds, NumNetworkIds = m_NumNetworkIds });
        }


        // The content of this method should shadow the logic in HandleDriverEvents.ApproveConnection
        void AttemptCreateFakeHostConnection(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                // Combined single world host still needs a connection entity
                // Generate a fake connection for handling going in game etc
                var ent = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponent(ent, NetworkStreamConnection.GetEssentialComponentsForConnection());
                state.EntityManager.AddBuffer<OutgoingRpcDataStreamBuffer>(ent);
                // TODO set NetworkStreamInGame on by default? As a single world host, there's not really a case for that to be off. If users rely on this to know if they are ready, they should instead have their own user side signal.
                state.EntityManager.GetBuffer<LinkedEntityGroup>(ent).Add(new LinkedEntityGroup { Value = ent });

                // Avoid using 0
                int nid = m_NumNetworkIds.Value + 1;
                m_NumNetworkIds.Value = nid;

                var networkId = new NetworkId {Value = nid};
                state.EntityManager.AddComponentData(ent, networkId);
                state.EntityManager.AddComponent<LocalConnection>(ent); // we're not doing this for binary world servers, since it doesn't really make sense. For a server world, a local client world shouldn't be different from other client worlds.
                state.EntityManager.SetName(ent, new FixedString64Bytes(FixedString.Format("Host Fake NetworkConnection ({0})", nid)));
            }
        }

        /// <inheritdoc/>
        public void OnDestroy(ref SystemState state)
        {
            m_RandomIndex.Dispose();
            m_NumNetworkIds.Dispose();
            m_FreeNetworkIds.Dispose();
            m_ConnectionEvents.Dispose();
            m_ConnectionUniqueIds.Dispose();
            m_MigrationIds.Dispose();

            ref readonly var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRO;
            if (DriverState.Default == networkStreamDriver.DriverState)
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

            // Force clean-up of ReceivedSnapshotByRemoteMask:
            foreach (var snapshotAck in SystemAPI.Query<RefRW<NetworkSnapshotAck>>())
            {
                if (snapshotAck.ValueRO.ReceivedSnapshotByRemoteMask.IsCreated)
                    snapshotAck.ValueRW.ReceivedSnapshotByRemoteMask.Dispose();
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var commandBuffer = SystemAPI.GetSingleton<NetworkGroupCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            FixedString128Bytes debugPrefix = $"[{state.WorldUnmanaged.Name}][Connection]";

#if UNITY_EDITOR || NETCODE_DEBUG
            if (!state.WorldUnmanaged.IsServer())
            {
                // Not needed for server, we're only gathering client stats for now. Should come back to this if we gather server stats here too, it's also reset in GhostSendSystem
                var numLoadedPrefabs = SystemAPI.GetSingleton<GhostCollection>().NumLoadedPrefabs;
                ref var netStatsSnapshotSingleton = ref SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW;
                netStatsSnapshotSingleton.ResetWriter(numLoadedPrefabs);
            }
#endif

            if (!SystemAPI.HasSingleton<NetworkProtocolVersion>())
            {
                // Fix: Wait for the CreateComponentCollection to have been called, otherwise we'd create a
                // NetworkProtocolVersion with GhostCollection:0.
                var data = SystemAPI.GetSingleton<GhostComponentSerializerCollectionData>();
                if (data.CollectionFinalized.Value != 2)
                    return;

                // RW is required because this call marks the collection as final, which means no further rpcs can be registered.
                ref var rpcCollection = ref SystemAPI.GetSingletonRW<RpcCollection>().ValueRW;
                var serializerState = SystemAPI.GetSingletonBuffer<GhostComponentSerializer.State>();
                var npv = new NetworkProtocolVersion
                {
                    NetCodeVersion = NetworkProtocolVersion.k_NetCodeVersion,
                    GameVersion = SystemAPI.TryGetSingleton(out GameProtocolVersion gameProtocolVersion) ? gameProtocolVersion.Version : 0,
                    RpcCollectionVersion = rpcCollection.CalculateVersionHash(),
                    ComponentCollectionVersion = GhostCollectionSystem.CalculateComponentCollectionHash(serializerState),
                };
                netDebug.DebugLog($"[{state.WorldUnmanaged.Name}] NetworkProtocolVersion finalized with: {npv.ToFixedString()}, DefaultVariants:{data.DefaultVariants.Count}, Serializers:{data.Serializers.Length}, SS:{data.SerializationStrategies.Length}, InputBuffers:{data.InputComponentBufferMap.Count}, RPCs:{rpcCollection.Rpcs.Length}, DynamicAssemblyList:{rpcCollection.DynamicAssemblyList}!");
                state.EntityManager.CreateSingleton(npv);
                npv.AssertIsValid();
            }
            var networkProtocolVersion = SystemAPI.GetSingleton<NetworkProtocolVersion>();

            var driverListening = DriverStore.DriversCount > 0 && DriverStore.GetDriverInstanceRO(DriverStore.FirstDriver).driver.Listening;
            if (driverListening)
            {
                for (int i = DriverStore.FirstDriver + 1; i < DriverStore.LastDriver; ++i)
                {
                    driverListening &= DriverStore.GetDriverInstanceRO(i).driver.Listening;
                }
                // Detect failed listen by checking if some but not all drivers are listening
                if (!driverListening)
                {
                    for (int i = DriverStore.FirstDriver + 1; i < DriverStore.LastDriver; ++i)
                    {
                        ref var instance = ref DriverStore.GetDriverInstanceRW(i);
                        if (instance.driver.Listening)
                            instance.StopListening();
                    }
                }
            }

            k_Scheduling.Begin();
            state.Dependency = DriverStore.ScheduleUpdateAllDrivers(state.Dependency);
            k_Scheduling.End();

            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();
            var timestampMS = NetworkTimeSystem.TimestampMS;

            if (driverListening)
            {
                m_GhostComponentFromEntity.Update(ref state);
                var acceptJob = new ConnectionAcceptJob
                {
                    driverStore = DriverStore,
                    commandBuffer = commandBuffer,
                    connectionEvents = m_ConnectionEvents,
                    serverApprovedConnectionRpcQueue = m_ServerApprovedConnectionRpcQueue,
                    requestProtocolVersionHandshakeQueue = m_RequestProtocolVersionHandshakeRpcQueue,
                    ghostFromEntity = m_GhostComponentFromEntity,
                    protocolVersion = networkProtocolVersion,
                    netDebug = netDebug,
                    debugPrefix = debugPrefix,
                    currentTime = timestampMS,
                    tickRate = tickRate,
                    requireConnectionApproval = networkStreamDriver.RequireConnectionApproval ? (byte) 1 : (byte) 0,
                };
                k_Scheduling.Begin();
                state.Dependency = acceptJob.Schedule(state.Dependency);
                k_Scheduling.End();
            }
            else
            {
                if (!m_RefreshTickRateQuery.IsEmptyIgnoreFilter)
                {
                    if (!SystemAPI.TryGetSingleton(out tickRate))
                        state.EntityManager.CreateSingleton(tickRate);
                    tickRate.ResolveDefaults();
                    var requests = m_RefreshTickRateQuery.ToComponentDataArray<ClientServerTickRateRefreshRequest>(Allocator.Temp);
                    foreach (var req in requests)
                    {
                        req.ApplyTo(ref tickRate);
                        netDebug.DebugLog($"{debugPrefix} Using SimulationTickRate={tickRate.SimulationTickRate} NetworkTickRate={tickRate.NetworkTickRate} MaxSimulationStepsPerFrame={tickRate.MaxSimulationStepsPerFrame} TargetFrameRateMode={tickRate.TargetFrameRateMode} PredictedPhysicsPerTick={tickRate.PredictedFixedStepSimulationTickRatio}.");
                    }
                    SystemAPI.SetSingleton(tickRate);
                    state.EntityManager.DestroyEntity(m_RefreshTickRateQuery);
                }
                m_FreeNetworkIds.Clear();
            }

            // Keep the index incrementing with some added randomeness via server tick. The random generated outputs will never collide with previous outputs
            m_RandomIndex.Value += networkTime.ServerTick.SerializedData;

            // This singleton will only exist on clients as it's used to keep track of this value between connection destroy/recreate
            uint clientConnectionUniqueId = 0;
            if (!state.WorldUnmanaged.IsServer() && SystemAPI.TryGetSingletonRW<ConnectionUniqueId>(out var uniqueId))
                clientConnectionUniqueId = uniqueId.ValueRO.Value;

            m_ConnectionUniqueIdFromEntity.Update(ref state);
            m_ApprovedFromEntity.Update(ref state);
            m_ConnectionStateFromEntity.Update(ref state);
            m_NetworkIdFromEntity.Update(ref state);
            m_RequestDisconnectFromEntity.Update(ref state);
            m_InGameFromEntity.Update(ref state);
            m_EnablePacketLoggingFromEntity.Update(ref state);
            m_OutgoingRpcBufferFromEntity.Update(ref state);
            m_RpcBufferFromEntity.Update(ref state);
            m_CmdBufferFromEntity.Update(ref state);
            m_SnapshotBufferFromEntity.Update(ref state);
            m_GhostComponentFromEntity.Update(ref state);

            // FIXME: because it uses buffer from entity
            var handleJob = new HandleDriverEvents
            {
                commandBuffer = commandBuffer,
                netDebug = netDebug,
                debugPrefix = debugPrefix,
                driverStore = DriverStore,
                networkIdFromEntity = m_NetworkIdFromEntity,
                connectionUniqueIdFromEntity = m_ConnectionUniqueIdFromEntity,
                ghostInstanceFromEntity = m_GhostComponentFromEntity,
                connectionStateFromEntity = m_ConnectionStateFromEntity,
                requestDisconnectFromEntity = m_RequestDisconnectFromEntity,
                requestProtocolVersionHandshakeQueue = m_RequestProtocolVersionHandshakeRpcQueue,
                inGameFromEntity = m_InGameFromEntity,
                enablePacketLoggingFromEntity = m_EnablePacketLoggingFromEntity,
                freeNetworkIds = m_FreeNetworkIds,
                migrationIds = m_MigrationIds,
                connectionEvents = m_ConnectionEvents,
                connectionUniqueIds = m_ConnectionUniqueIds,

                outgoingRpcBuffer = m_OutgoingRpcBufferFromEntity,
                rpcBuffer = m_RpcBufferFromEntity,
                cmdBuffer = m_CmdBufferFromEntity,
                snapshotBuffer = m_SnapshotBufferFromEntity,
                reliableSequencedPipelineStageId = m_reliableSequencedPipelineStageId,

                requireConnectionApproval = networkStreamDriver.RequireConnectionApproval ? (byte)1 : (byte)0,
                protocolVersion = networkProtocolVersion,
                localTime = timestampMS,
                lastServerTick = networkTime.ServerTick,
                tickRate = tickRate,
                randomIndex = m_RandomIndex,
                clientConnectionUniqueId = clientConnectionUniqueId,
                numNetworkId = m_NumNetworkIds,
                connectionApprovedLookup = m_ApprovedFromEntity,
                serverApprovedConnectionRpcQueue = m_ServerApprovedConnectionRpcQueue,
                serverRequestApprovalRpcQueue = m_ServerRequestApprovalRpcQueue,
                ghostFromEntity = m_GhostComponentFromEntity,
                isServer = state.WorldUnmanaged.IsServer(),
            };
#if UNITY_EDITOR || NETCODE_DEBUG
            handleJob.netStats = SystemAPI.GetSingletonRW<GhostStatsCollectionCommand>().ValueRO.Value;
            handleJob.SnapshotStatsWriters = SystemAPI.GetSingleton<GhostStatsSnapshotSingleton>().allGhostStatsParallelWrites.AsArray();
#endif
            k_Scheduling.Begin();
            state.Dependency = handleJob.ScheduleByRef(state.Dependency);
            k_Scheduling.End();
        }


        [BurstCompile]
        [StructLayout(LayoutKind.Sequential)]
        struct ConnectionAcceptJob : IJob
        {
            public EntityCommandBuffer commandBuffer;
            public NetworkDriverStore driverStore;
            public NativeList<NetCodeConnectionEvent> connectionEvents;
            public RpcQueue<ServerApprovedConnection, ServerApprovedConnection> serverApprovedConnectionRpcQueue;
            public RpcQueue<RequestProtocolVersionHandshake, RequestProtocolVersionHandshake> requestProtocolVersionHandshakeQueue;
            public ClientServerTickRate tickRate;
            public NetworkProtocolVersion protocolVersion;
            public NetDebug netDebug;
            public FixedString128Bytes debugPrefix;
            public uint currentTime;
            public byte requireConnectionApproval;
            [ReadOnly] public ComponentLookup<GhostInstance> ghostFromEntity;

            public void Execute()
            {
                for (int i = driverStore.FirstDriver; i < driverStore.LastDriver; ++i)
                {
                    ref var driver = ref driverStore.GetDriverRW(i);
                    NetworkConnection con;
                    while ((con = driver.Accept()) != default)
                    {
                        // New connection can never have any events, if this one does - just close it
                        var evt = con.PopEvent(driver, out _);
                        if (evt != NetworkEvent.Type.Empty)
                        {
                            con.Disconnect(driver);
                            netDebug.DebugLog(FixedString.Format("[{0}][Connection] Disconnecting stale connection detected as new (has pending event={1}).",debugPrefix, (int)evt));
                            continue;
                        }

                        //TODO: Lookup for any connection that is already connected with the same ip address or any other player identity.
                        //Relying on the IP is pretty weak test but at least is remove already some positives
                        Debug.Assert(tickRate.HandshakeApprovalTimeoutMS > 0);
                        var ent = commandBuffer.CreateEntity();
                        commandBuffer.AddComponent(ent, NetworkStreamConnection.GetEssentialComponentsForConnection());
                        var connection = new NetworkStreamConnection
                        {
                            Value = con,
                            DriverId = i,
                            CurrentState = ConnectionState.State.Handshake,
                            CurrentStateDirty = false,
                            ConnectionApprovalTimeoutStart = currentTime,
                        };
                        commandBuffer.AddComponent(ent, connection);
                        commandBuffer.AddComponent(ent, new NetworkSnapshotAck
                        {
                            ReceivedSnapshotByRemoteMask = new UnsafeBitArray((int)math.max(1024, tickRate.SnapshotAckMaskCapacity), Allocator.Persistent),
                        });
                        commandBuffer.AddBuffer<PrespawnSectionAck>(ent);
                        var outgoingBuf = commandBuffer.AddBuffer<OutgoingRpcDataStreamBuffer>(ent);
                        commandBuffer.AddBuffer<IncomingCommandDataStreamBuffer>(ent);
                        commandBuffer.AppendToBuffer(ent, new LinkedEntityGroup{Value = ent});
                        commandBuffer.SetName(ent, (FixedString64Bytes)$"NetworkConnection (Handshake:{tickRate.HandshakeApprovalTimeoutMS}ms)");

                        requestProtocolVersionHandshakeQueue.Schedule(outgoingBuf, ghostFromEntity, new RequestProtocolVersionHandshake
                        {
                            Data = protocolVersion,
                        });

                        connection.CurrentState = ConnectionState.State.Handshake;
                        connection.CurrentStateDirty = false;
                        connectionEvents.Add(new NetCodeConnectionEvent
                        {
                            Id = default,
                            ConnectionId = connection.Value,
                            State = ConnectionState.State.Handshake,
                            DisconnectReason = default,
                            ConnectionEntity = ent,
                        });
                        netDebug.DebugLog((FixedString512Bytes) $"{debugPrefix} Server accepted new connection {connection.Value.ToFixedString()}, waiting for handshake...");
                    }
                }
            }
        }

        [BurstCompile]
        partial struct HandleDriverEvents : IJobEntity
        {
            public EntityCommandBuffer commandBuffer;
            public NetDebug netDebug;
            public FixedString128Bytes debugPrefix;
            public NetworkDriverStore driverStore;
            [ReadOnly] public ComponentLookup<NetworkId> networkIdFromEntity;
            [ReadOnly] public ComponentLookup<ConnectionUniqueId> connectionUniqueIdFromEntity;
            [ReadOnly] public ComponentLookup<GhostInstance> ghostInstanceFromEntity;
            public ComponentLookup<ConnectionState> connectionStateFromEntity;
            public RpcQueue<RequestProtocolVersionHandshake, RequestProtocolVersionHandshake> requestProtocolVersionHandshakeQueue;
            public ComponentLookup<NetworkStreamRequestDisconnect> requestDisconnectFromEntity;
            public ComponentLookup<NetworkStreamInGame> inGameFromEntity;
            public ComponentLookup<EnablePacketLogging> enablePacketLoggingFromEntity;
            public NativeQueue<int> freeNetworkIds;
            public NativeHashMap<uint, int> migrationIds;
            public NativeList<NetCodeConnectionEvent> connectionEvents;
            public NativeList<uint> connectionUniqueIds;

            public BufferLookup<OutgoingRpcDataStreamBuffer> outgoingRpcBuffer;
            public BufferLookup<IncomingRpcDataStreamBuffer> rpcBuffer;
            public BufferLookup<IncomingCommandDataStreamBuffer> cmdBuffer;
            public BufferLookup<IncomingSnapshotDataStreamBuffer> snapshotBuffer;
            public NetworkPipelineStageId reliableSequencedPipelineStageId;

            public NetworkProtocolVersion protocolVersion;

            public byte requireConnectionApproval;
            public ClientServerTickRate tickRate;
            public uint localTime;
            public NetworkTick lastServerTick;

            // Stuff for Approval:
            public uint clientConnectionUniqueId;
            public NativeReference<uint> randomIndex;
            public NativeReference<int> numNetworkId;
            public RpcQueue<ServerApprovedConnection, ServerApprovedConnection> serverApprovedConnectionRpcQueue;
            public RpcQueue<ServerRequestApprovalAfterHandshake, ServerRequestApprovalAfterHandshake> serverRequestApprovalRpcQueue;
            [ReadOnly] public ComponentLookup<GhostInstance> ghostFromEntity;
            [ReadOnly] public ComponentLookup<ConnectionApproved> connectionApprovedLookup;
            public bool isServer;

            [NativeSetThreadIndex] int m_ThreadIndex;

#if UNITY_EDITOR || NETCODE_DEBUG
            public NativeArray<uint> netStats;
            public NativeArray<UnsafeGhostStatsSnapshot> SnapshotStatsWriters;
#endif

            public void Execute(Entity entity, ref NetworkStreamConnection connection, ref NetworkSnapshotAck snapshotAck)
            {
                var disconnectReason = NetworkStreamDisconnectReason.ConnectionClose;
                if (Hint.Unlikely(requestDisconnectFromEntity.TryGetComponent(entity, out var disconnectRequest)))
                {
                    disconnectReason = disconnectRequest.Reason;
                    driverStore.Disconnect(connection);
                    // Disconnect cleanup will be handled below.
                }
                else if (!inGameFromEntity.HasComponent(entity))
                {
                    snapshotAck = new NetworkSnapshotAck
                    {
                        LastReceivedRemoteTime = snapshotAck.LastReceivedRemoteTime,
                        LastReceiveTimestamp = snapshotAck.LastReceiveTimestamp,
                        EstimatedRTT = snapshotAck.EstimatedRTT,
                        DeviationRTT = snapshotAck.DeviationRTT,
                        ReceivedSnapshotByRemoteMask = snapshotAck.ReceivedSnapshotByRemoteMask,
                    };
                }

                if (Hint.Unlikely(!connection.Value.IsCreated))
                {
                    netDebug.LogError($"{debugPrefix} Stale NetworkStreamConnection.Value ({connection.Value.ToFixedString()}, DriverId: {connection.DriverId}, VPVR: {connection.ProtocolVersionReceived}) found on {entity.ToFixedString()}! Did you modify `Value` in your code?");
                    return;
                }

                networkIdFromEntity.TryGetComponent(entity, out var networkId);
                HandleApproval(entity, ref connection, ref networkId, ref disconnectReason);

                // Update State:
                ref var driverInstance = ref driverStore.GetDriverInstanceRW(connection.DriverId);
                ref var driver = ref driverInstance.driver;

                // Event popping:
                NetworkEvent.Type evt;
                while ((evt = driver.PopEventForConnection(connection.Value, out var reader, out var pipelineStage)) != NetworkEvent.Type.Empty)
                {
                    switch (evt)
                    {
                        case NetworkEvent.Type.Connect:
                        {
                            // This event is only invoked on the client. The server bypasses, as part of the Accept() call.
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            Debug.Assert(!isServer, "Sanity check failed: got connect event, but not on server");
                            Debug.Assert(!snapshotAck.ReceivedSnapshotByRemoteMask.IsCreated);
#endif
                            netDebug.DebugLog($"{debugPrefix} Client connected to driver, sending {protocolVersion.ToFixedString()} Connection[UniqueId:{clientConnectionUniqueId}] to server to begin handshake...");
                            snapshotAck.SnapshotPacketLoss = default;
                            var buf = outgoingRpcBuffer[entity];
                            requestProtocolVersionHandshakeQueue.Schedule(buf, ghostInstanceFromEntity, new RequestProtocolVersionHandshake
                            {
                                Data = protocolVersion,
                                ConnectionUniqueId = clientConnectionUniqueId
                            });
                            connectionEvents.Add(new NetCodeConnectionEvent
                            {
                                Id = default,
                                ConnectionId = connection.Value,
                                State = ConnectionState.State.Handshake,
                                DisconnectReason = disconnectReason,
                                ConnectionEntity = entity,
                            });
                            connection.CurrentState = ConnectionState.State.Handshake;
                            connection.ConnectionApprovalTimeoutStart = localTime;
                            connection.CurrentStateDirty = false;
                            break;
                        }
                        case NetworkEvent.Type.Disconnect:
                            if (reader.Length == 1)
                                disconnectReason = (NetworkStreamDisconnectReason) reader.ReadByte();
                            // Disconnect cleanup will be handled below.
                            connection.CurrentState = ConnectionState.State.Disconnected;
                            connection.CurrentStateDirty = false;
                            goto doubleBreak;
                        case NetworkEvent.Type.Data:
                            var msgType = (NetworkStreamProtocol)reader.ReadByte();

                            // Handle connection approval phase, without it we won't process game data further.
                            if (isServer && connection.IsHandshakeOrApproval)
                            {
                                if (msgType != NetworkStreamProtocol.Rpc)
                                {
                                    netDebug.LogError($"{debugPrefix} Ignoring NetworkStreamProtocol msgType {(byte)msgType} as {connection.Value.ToFixedString()} is in approval stage. Only approval RPCs are allowed.");
                                    continue;
                                }
                            }

                            switch (msgType)
                            {
                                case NetworkStreamProtocol.Command:
                                {
                                    if (!cmdBuffer.HasBuffer(entity))
                                        break;
                                    var buffer = cmdBuffer[entity];
                                    var snapshot = new NetworkTick{SerializedData = reader.ReadUInt()};
                                    uint snapshotMask = reader.ReadUInt();
                                    snapshotAck.UpdateReceivedByRemote(snapshot, snapshotMask, out var numSnapshotErrorsRequiringReset);
                                    uint remoteTime = reader.ReadUInt();
                                    uint localTimeMinusRTT = reader.ReadUInt();
                                    uint interpolationDelay = reader.ReadUInt();
                                    uint numLoadedPrefabs = reader.ReadUShort();

                                    snapshotAck.UpdateRemoteAckedData(remoteTime, numLoadedPrefabs, interpolationDelay);
                                    var rtt = NetworkSnapshotAck.CalculateRttViaLocalTime(localTime, localTimeMinusRTT);
                                    snapshotAck.UpdateRemoteTime(remoteTime, rtt, localTime);
                                    var cmdTickIsFull = reader.ReadByte();
                                    var tickReader = reader;
                                    var cmdTick = new NetworkTick{SerializedData = tickReader.ReadUInt()};
                                    var isValidCmdTick = !snapshotAck.LastReceivedSnapshotByLocal.IsValid ||
                                                         cmdTick.IsNewerThan(snapshotAck.LastReceivedSnapshotByLocal) ||
                                                         (snapshotAck.LastReceivedSnapshotByLocal.Equals(cmdTick) && cmdTickIsFull != 0);
#if UNITY_EDITOR || NETCODE_DEBUG
                                    netStats[0] = lastServerTick.SerializedData;
                                    netStats[1] = (uint)reader.Length - 1u;
                                    if (!isValidCmdTick || buffer.Length > 0)
                                    {
                                        netStats[2] = netStats[2] + 1;
                                    }
                                    if(numSnapshotErrorsRequiringReset != 0)
                                    {
                                        var msg = (FixedString512Bytes)$"{connection.Value.ToFixedString()} reported recoverable snapshot read errors. Thus, we have reset their entire ack history. Note: This incurs a bandwidth and CPU cost, as we must resend all relevant ghost chunks again (i.e. as if this was a new joiner).";
                                        netDebug.LogWarning($"{debugPrefix} {msg}");
                                        TryLog(in entity, msg);
                                    }
#endif
                                    // Do not try to process incoming commands which are older than commands we already processed
                                    if (!isValidCmdTick)
                                        break;
                                    snapshotAck.LastReceivedSnapshotByLocal = cmdTick;
                                    snapshotAck.MostRecentFullCommandTick = cmdTick;
                                    if(cmdTickIsFull == 0)
                                        snapshotAck.MostRecentFullCommandTick.Decrement();
                                    buffer.Clear();
                                    buffer.Add(ref reader);
                                    break;
                                }
                                case NetworkStreamProtocol.Snapshot:
                                {
                                    if (Hint.Unlikely(!snapshotBuffer.TryGetBuffer(entity, out var buffer)))
                                        break;
#if UNITY_EDITOR || NETCODE_DEBUG
                                    ref var netStatsSnapshots = ref SnapshotStatsWriters.AsSpan()[m_ThreadIndex];
                                    netStatsSnapshots.SnapshotTotalSizeInBits = (uint)(reader.Length) * 8;
#endif
                                    uint remoteTime = reader.ReadUInt();
                                    uint localTimeMinusRTT = reader.ReadUInt();
                                    snapshotAck.ServerCommandAge = reader.ReadInt();
                                    var rtt = NetworkSnapshotAck.CalculateRttViaLocalTime(localTime, localTimeMinusRTT);
                                    snapshotAck.UpdateRemoteTime(remoteTime, rtt, localTime);

                                    // SSId:
                                    var currentSnapshotSequenceId = reader.ReadByte();

                                    // Copy the reader here, as we want to pass the ServerTick into the GhostReceiveSystem,
                                    // and that'll fail if we read too far.
                                    var copyOfReader = reader;
                                    var currentSnapshotServerTick = new NetworkTick{SerializedData = copyOfReader.ReadUInt()};

                                    // Skip old snapshots:
                                    var isValid = !snapshotAck.LastReceivedSnapshotByLocal.IsValid || currentSnapshotServerTick.IsNewerThan(snapshotAck.LastReceivedSnapshotByLocal);
                                    UpdatePacketLossStats(ref snapshotAck.SnapshotPacketLoss, isValid, currentSnapshotSequenceId, currentSnapshotServerTick, ref snapshotAck, in entity, buffer);
                                    if (!isValid)
                                        break;
                                    //This is partially valid: if we receive 3 packets, it is valid to only ack the last one
                                    if (snapshotAck.LastReceivedSnapshotByLocal.IsValid)
                                    {
                                        //remove the last acked packet, we will never process this if multiple packets are received in the same frame
                                        //so we can't communicate to the server that we have data for that specific server tick.
                                        if (buffer.Length > 0)
                                            snapshotAck.ReceivedSnapshotByLocalMask ^= 0x1;
                                        //shift the ack window. It is correct to shift
                                        var shamt = currentSnapshotServerTick.TicksSince(snapshotAck.LastReceivedSnapshotByLocal);
                                        if (shamt < 32)
                                            snapshotAck.ReceivedSnapshotByLocalMask <<= shamt;
                                        else
                                            snapshotAck.ReceivedSnapshotByLocalMask = 0;
                                    }
                                    snapshotAck.ReceivedSnapshotByLocalMask |= 1;
                                    snapshotAck.LastReceivedSnapshotByLocal = currentSnapshotServerTick;
                                    snapshotAck.CurrentSnapshotSequenceId = currentSnapshotSequenceId;

                                    // Limitation: Clobber any previous snapshot, even if said snapshot has not been processed yet.
                                    if (buffer.Length > 0)
                                    {
#if UNITY_EDITOR || NETCODE_DEBUG
                                        netStats[2] = netStats[2] + 1;
#endif
                                        buffer.Clear();
                                    }

                                    // Save the new snapshot to the buffer, so we can process it in GhostReceiveSystem.
                                    buffer.Add(ref reader);
                                    break;
                                }
                                case NetworkStreamProtocol.Rpc:
                                {
                                    uint remoteTime = reader.ReadUInt();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                    UnityEngine.Debug.Assert(reader.GetBytesRead() == RpcCollection.k_RpcCommonHeaderLengthBytes);
#endif
                                    var rtt = NetworkSnapshotAck.GetRpcRttFromReliablePipeline(connection, ref driver, ref driverInstance, pipelineStage, reliableSequencedPipelineStageId);
                                    snapshotAck.UpdateRemoteTime(remoteTime, rtt, localTime);
                                    var buffer = rpcBuffer[entity];
                                    buffer.Add(ref reader);
                                    break;
                                }
                                default:
                                    netDebug.LogError(FixedString.Format("Received unknown message type {0}", (byte)msgType));
                                    break;
                            }

                            break;
                        default:
                            netDebug.LogError(FixedString.Format("Received unknown network event {0}", (int)evt));
                            break;
                    }
                }
                doubleBreak:

                // Now react to changes:

                // CurrentStateDirty is a bit of a hack: It only exists for:
                // - The `Connecting` state on the client.
                // - The `Approval` state on the client.
                // Note that we intentionally bypass this in most places (see various event evocations scattered around).
                if(Hint.Unlikely(connection.CurrentStateDirty))
                {
                    connection.CurrentStateDirty = false;
                    connectionEvents.Add(new NetCodeConnectionEvent
                    {
                        Id = networkId,
                        ConnectionId = connection.Value,
                        State = connection.CurrentState,
                        DisconnectReason = disconnectReason,
                        ConnectionEntity = entity,
                    });
                }

                // Handle disconnects:
                // Fix for issue where: Transport does not raise the Disconnect event locally for any connection that is manually Disconnected.
                // Thus, we (Netcode) need to duplicate the event via status polling.
                // TODO - Local events will be supported via feature flag `EnableDisconnectEventOnSelf = true` at some point.
                if (Hint.Unlikely(connection.CurrentState == ConnectionState.State.Disconnected
                                  || driver.GetConnectionState(connection.Value) == NetworkConnection.State.Disconnected))
                {
                    commandBuffer.RemoveComponent<NetworkStreamConnection>(entity);
                    commandBuffer.DestroyEntity(entity);

                    if (cmdBuffer.HasBuffer(entity))
                        cmdBuffer[entity].Clear();

                    if (networkId.Value != default)
                        freeNetworkIds.Enqueue(networkId.Value);

                    if (connectionUniqueIdFromEntity.HasComponent(entity))
                    {
                        var cuid = connectionUniqueIdFromEntity[entity].Value;
                        for (int i = 0; i < connectionUniqueIds.Length; ++i)
                        {
                            if (connectionUniqueIds[i] == cuid)
                            {
                                connectionUniqueIds.RemoveAtSwapBack(i);
                                break;
                            }
                        }
                    }

                    netDebug.DebugLog($"{debugPrefix} {connection.Value.ToFixedString()} closed NetworkId={networkId.Value} Reason={disconnectReason.ToFixedString()}.");
                    connectionEvents.Add(new NetCodeConnectionEvent
                    {
                        Id = networkId,
                        ConnectionId = connection.Value,
                        State = ConnectionState.State.Disconnected,
                        DisconnectReason = disconnectReason,
                        ConnectionEntity = entity,
                    });

                    if (snapshotAck.ReceivedSnapshotByRemoteMask.IsCreated)
                        snapshotAck.ReceivedSnapshotByRemoteMask.Dispose();
                    connection.Value = default;
                    connection.CurrentState = ConnectionState.State.Disconnected;
                    connection.CurrentStateDirty = false;
                }

                // Update ConnectionState:
                if (connectionStateFromEntity.TryGetComponent(entity, out var existingState))
                {
                    var newState = existingState;
                    newState.DisconnectReason = disconnectReason;
                    newState.CurrentState = connection.CurrentState;
                    newState.NetworkId = networkId.Value;
                    if (Hint.Unlikely(!existingState.Equals(newState)))
                        connectionStateFromEntity[entity] = newState;
                }
            }

            /// <summary>Called inline, as we need to update the NetworkId as soon as possible.</summary>
            private void HandleApproval(Entity entity, ref NetworkStreamConnection connection, ref NetworkId networkId, ref NetworkStreamDisconnectReason disconnectReason)
            {
                if (!connection.IsHandshakeOrApproval) return;

                // Handle Handshake:
                if (isServer && connection.ProtocolVersionReceived != 0 && connection.CurrentState == ConnectionState.State.Handshake)
                {
                    if (requireConnectionApproval == 0)
                    {
                        var buf = outgoingRpcBuffer[entity];
                        ApproveConnection(entity, ref connection, buf, ref networkId);
                    }
                    else
                    {
                        // Begin approval process:
                        connection.CurrentState = ConnectionState.State.Approval;
                        connection.CurrentStateDirty = false;
                        connectionEvents.Add(new NetCodeConnectionEvent
                        {
                            Id = default,
                            ConnectionId = connection.Value,
                            State = ConnectionState.State.Approval,
                            DisconnectReason = default,
                            ConnectionEntity = entity,
                        });
                        netDebug.DebugLog($"{debugPrefix} Server ProtocolVersion handshake successful for {connection.Value.ToFixedString()}, requesting (and awaiting) valid approval RPC from client...");
                        commandBuffer.SetName(entity, (FixedString64Bytes) $"NetworkConnection (Approval:{tickRate.HandshakeApprovalTimeoutMS}ms)");
                        var buf = outgoingRpcBuffer[entity];
                        serverRequestApprovalRpcQueue.Schedule(buf, ghostFromEntity, new ServerRequestApprovalAfterHandshake());
                    }
                }

                // Handle ConnectionApproved component:
                if (!networkIdFromEntity.HasComponent(entity) && connectionApprovedLookup.HasComponent(entity))
                {
                    if (isServer)
                    {
                        if (requireConnectionApproval != 0)
                        {
                            switch (connection.CurrentState)
                            {
                                case ConnectionState.State.Approval:
                                    var buf = outgoingRpcBuffer[entity];
                                    ApproveConnection(entity, ref connection, buf, ref networkId);
                                    break;
                                case ConnectionState.State.Handshake:
                                    // Waiting for the Handshake to complete...
                                    break;
                                default:
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                    if(!netDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning)
                                        netDebug.LogWarning($"{debugPrefix} Approved {connection.Value.ToFixedString()} but in state {connection.CurrentState.ToFixedString()}.");
#endif
                                    break;
                            }
                        }
                        else
                        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            netDebug.LogWarning($"{debugPrefix} Approved connection {connection.Value.ToFixedString()} but this server does not require connection approval!");
#endif
                        }
                    }
                }

                // Handle timeout: Note that the client can time itself out, too, but only if not in handshake,
                // as it doesn't know the configured timeout duration.
                if (Hint.Unlikely(connection.ConnectionApprovalTimeoutStart != 0))
                {
                    var isClientHandshaking = !isServer && connection.CurrentState == ConnectionState.State.Handshake;
                    if (isClientHandshaking) return;
                    var elapsedSinceApprovalStartMS = localTime - connection.ConnectionApprovalTimeoutStart;
                    if (Hint.Unlikely(elapsedSinceApprovalStartMS >= tickRate.HandshakeApprovalTimeoutMS))
                    {
                        Debug.Assert(connection.CurrentState == ConnectionState.State.Handshake || connection.CurrentState == ConnectionState.State.Approval);
                        netDebug.LogError($"{debugPrefix} {connection.Value.ToFixedString()} timed out after {elapsedSinceApprovalStartMS}ms (threshold:{tickRate.HandshakeApprovalTimeoutMS}ms, state:{connection.CurrentState.ToFixedString()})!");
                        disconnectReason = connection.CurrentState == ConnectionState.State.Handshake
                            ? NetworkStreamDisconnectReason.HandshakeTimeout
                            : NetworkStreamDisconnectReason.ApprovalTimeout;
                        driverStore.Disconnect(connection);
                    }
                }
            }

            /// <summary>Logic to actually fully accept a connection. Only called once handshake + approval (if enabled) is successful.</summary>
            private void ApproveConnection(Entity ent, ref NetworkStreamConnection connection, DynamicBuffer<OutgoingRpcDataStreamBuffer> outgoingBuffer, ref NetworkId networkId)
            {
                // Re-assign previous unique Id in case this is a returning client
                uint connectionUniqueId = 0;
                bool isReconnecting = false;
                if (connectionUniqueIdFromEntity.HasComponent(ent))
                {
                    // Only re-assign if the ID isn't already registered
                    var clientReportedId = connectionUniqueIdFromEntity[ent].Value;
                    if (!connectionUniqueIds.Contains(clientReportedId))
                        connectionUniqueId = clientReportedId;
                    else
                        Debug.LogWarning($"Client is reporting an already reserved connection unique ID {clientReportedId} but this ID is already registered. Generating a new one.");
                    isReconnecting = true;
                }

                var newNetworkId = 0;

                if ( isReconnecting && connectionUniqueId != 0 )
                {
                    migrationIds.TryGetValue(connectionUniqueId, out newNetworkId);
                }

                if (newNetworkId == 0 && !freeNetworkIds.TryDequeue(out newNetworkId))
                {
                    // Avoid using 0
                    newNetworkId = numNetworkId.Value + 1;
                    numNetworkId.Value = newNetworkId;
                }

                if (connectionUniqueId == 0)
                {
                    if (randomIndex.Value == uint.MaxValue)
                        randomIndex.Value = 0;
                    var random = Mathematics.Random.CreateFromIndex(randomIndex.Value);
                    connectionUniqueId = random.NextUInt();
                    int count = 0;
                    while (connectionUniqueIds.Contains(connectionUniqueId))
                    {
                        Debug.LogWarning($"Unique ID collision for ID {connectionUniqueId}, will generate another one.");
                        randomIndex.Value++;
                        random = Mathematics.Random.CreateFromIndex(randomIndex.Value);
                        connectionUniqueId = random.NextUInt();
                        // Doubtful we'll ever have 100 collisions but just to prevent infinite loops
                        if (count++ > 100)
                        {
                            Debug.LogError($"Failed to generate a non-colliding unique ID for network ID {newNetworkId}, unique ID count {connectionUniqueIds.Length}.");
                            break;
                        }
                    }
                    randomIndex.Value++;
                }
                commandBuffer.AddComponent(ent, new ConnectionUniqueId(){ Value = connectionUniqueId });
                connectionUniqueIds.Add(connectionUniqueId);

                // the logic in AttemptCreateFakeHostConnection should shadow the logic here. I.e. If you update this, double check AttemptCreateFakeHostConnection.
                networkId = new NetworkId {Value = newNetworkId};
                commandBuffer.AddComponent(ent, networkId);
                commandBuffer.SetName(ent, new FixedString64Bytes(FixedString.Format("NetworkConnection ({0})", newNetworkId)));
                var serverApprovedConnection = new ServerApprovedConnection();
                serverApprovedConnection.NetworkId = newNetworkId;
                serverApprovedConnection.UniqueId = connectionUniqueId;
                serverApprovedConnection.RefreshRequest.ReadFrom(in tickRate);
                serverApprovedConnectionRpcQueue.Schedule(outgoingBuffer, ghostFromEntity, serverApprovedConnection);
                connection.CurrentState = ConnectionState.State.Connected;
                connection.CurrentStateDirty = false;
                connection.ConnectionApprovalTimeoutStart = 0;
                connectionEvents.Add(new NetCodeConnectionEvent
                {
                    Id = networkId,
                    ConnectionId = connection.Value,
                    State = ConnectionState.State.Connected,
                    DisconnectReason = default,
                    ConnectionEntity = ent,
                });
                netDebug.DebugLog($"{debugPrefix} Server approved connection {connection.Value.ToFixedString()}, assigning NetworkId={newNetworkId} UniqueId={connectionUniqueId} Reconnecting={isReconnecting} State={connection.CurrentState}.");
            }

            /// <summary>
            /// Records SnapshotSequenceId [SSId] statistics, detecting packet loss, packet duplication, and out of order packets.
            /// </summary>
            // ReSharper disable once UnusedParameter.Local
            private void UpdatePacketLossStats(ref SnapshotPacketLossStatistics stats, bool snapshotIsConfirmedNewer,
                in byte currentSnapshotSequenceId, NetworkTick currentSnapshotServerTick, ref NetworkSnapshotAck snapshotAck,
                in Entity entity, DynamicBuffer<IncomingSnapshotDataStreamBuffer> buffer)
            {
                if (stats.NumPacketsReceived == 0) snapshotAck.CurrentSnapshotSequenceId = (byte) (currentSnapshotSequenceId - 1);
                stats.NumPacketsReceived++;

                var sequenceIdDelta = snapshotAck.CalculateSequenceIdDelta(currentSnapshotSequenceId, snapshotIsConfirmedNewer);
                if (snapshotIsConfirmedNewer)
                {
                    // Detect packet loss:
                    var numDroppedPackets = sequenceIdDelta - 1;
                    if (numDroppedPackets > 0)
                    {
                        stats.NumPacketsDroppedNeverArrived += (ulong) numDroppedPackets;
#if NETCODE_DEBUG
                        TryLog(entity, (FixedString512Bytes)$"[SSId:{currentSnapshotSequenceId}, ST:{currentSnapshotServerTick.ToFixedString()}] Inferred {numDroppedPackets} snapshots dropped!");
#endif
                    }

                    // Netcode limitation: We can only process one snapshot per tick!
                    if (buffer.Length > 0)
                    {
                        stats.NumPacketsCulledAsArrivedOnSameFrame++;
#if NETCODE_DEBUG
                        TryLog(entity, (FixedString512Bytes)$"[SSId:{currentSnapshotSequenceId}, ST:{currentSnapshotServerTick.ToFixedString()}] Clobbering previous snapshot, arrived same frame.");
#endif
                    }

#if NETCODE_DEBUG
                    TryLog(entity, (FixedString512Bytes)$"[SSId:{currentSnapshotSequenceId}, ST:{currentSnapshotServerTick.ToFixedString()}] Accepted & queued!");
#endif
                    return;
                }

                // Detect out of order and duplicate packets:
                if (sequenceIdDelta == 0)
                {
                    // We can't track any previous duplicate packets (unless we keep an ack history),
                    // so we don't track it at all. Just log.
#if NETCODE_DEBUG
                    TryLog(entity, (FixedString512Bytes) $"[SSId:{currentSnapshotSequenceId}, ST:{currentSnapshotServerTick.ToFixedString()}] Detected duplicated snapshot packet!");
#endif
                    return;
                }

                stats.NumPacketsCulledOutOfOrder++;
                // Technically a packet we skipped over was counted as dropped, but it just arrived.
                // We may not even know about it, as jitter during connection can cause us to detect
                // dropped packets that we should never have received anyway.
                if (stats.NumPacketsDroppedNeverArrived > 0)
                    stats.NumPacketsDroppedNeverArrived--;
#if NETCODE_DEBUG
                TryLog(entity, (FixedString512Bytes) $"[SSId:{currentSnapshotSequenceId}, ST:{currentSnapshotServerTick.ToFixedString()}] Culled as arrived {Unity.Mathematics.math.abs(sequenceIdDelta)} ServerTicks late!");
#endif
            }


            [Conditional("NETCODE_DEBUG")]
            private void TryLog(in Entity entity, in FixedString512Bytes msg)
            {
#if NETCODE_DEBUG
                if(enablePacketLoggingFromEntity.TryGetComponent(entity, out var comp) && comp.NetDebugPacketCache.IsCreated)
                    comp.NetDebugPacketCache.Log(msg);
#endif
            }
        }
    }
}

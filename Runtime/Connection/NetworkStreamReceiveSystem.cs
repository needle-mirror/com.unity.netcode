using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode
{
    [UpdateInWorld(TargetWorld.ClientAndServer)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    public class NetworkReceiveSystemGroup : ComponentSystemGroup
    {
    }

    public interface INetworkStreamDriverConstructor
    {
        void CreateClientDriver(World world, out NetworkDriver driver, out NetworkPipeline unreliablePipeline, out NetworkPipeline reliablePipeline, out NetworkPipeline unreliableFragmentedPipeline);
        void CreateServerDriver(World world, out NetworkDriver driver, out NetworkPipeline unreliablePipeline, out NetworkPipeline reliablePipeline, out NetworkPipeline unreliableFragmentedPipeline);
    }

    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [AlwaysUpdateSystem]
    public partial class NetworkStreamReceiveSystem : SystemBase, INetworkStreamDriverConstructor
    {
        public static INetworkStreamDriverConstructor s_DriverConstructor;
        public INetworkStreamDriverConstructor DriverConstructor => s_DriverConstructor != null ? s_DriverConstructor : this;
        public NetworkDriver Driver => m_Driver;
        internal NetworkDriver.Concurrent ConcurrentDriver => m_ConcurrentDriver;
        internal JobHandle LastDriverWriter;

        public NetworkPipeline UnreliablePipeline => m_UnreliablePipeline;
        public NetworkPipeline ReliablePipeline => m_ReliablePipeline;
        public NetworkPipeline UnreliableFragmentedPipeline => m_UnreliableFragmentedPipeline;

        private enum DriverState : int
        {
            Default,
            Migrating
        }

        private NetworkDriver m_Driver;
        private int m_DriverState;
        private NetworkDriver.Concurrent m_ConcurrentDriver;
        private NetworkPipeline m_UnreliablePipeline;
        private NetworkPipeline m_ReliablePipeline;
        private NetworkPipeline m_UnreliableFragmentedPipeline;
        private bool m_DriverListening;
        private NativeArray<int> m_NumNetworkIds;
        private NativeQueue<int> m_FreeNetworkIds;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private RpcQueue<RpcSetNetworkId, RpcSetNetworkId> m_RpcQueue;
        private GhostCollectionSystem m_GhostCollectionSystem;
        private EntityQuery m_NetworkStreamConnectionQuery;
        private ServerSimulationSystemGroup m_ServerSimulationSystemGroup;
        private NetDebugSystem m_NetDebugSystem;
        private FixedString128Bytes m_DebugPrefix;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private NativeArray<uint> m_NetStats;
        private GhostStatsCollectionSystem m_GhostStatsCollectionSystem;
#endif

        private EntityQuery m_refreshTickRateQuery;
        private EntityQuery m_pendingConnectionQuery;
        private EntityQuery m_requestDisconnectQuery;
        private EntityQuery m_notInGameQuery;

        public void CreateClientDriver(World world, out NetworkDriver driver, out NetworkPipeline unreliablePipeline, out NetworkPipeline reliablePipeline, out NetworkPipeline unreliableFragmentedPipeline)
        {
            var settings = new NetworkSettings();
            settings.WithReliableStageParameters(windowSize: 32)
                    .WithFragmentationStageParameters(payloadCapacity: 16 * 1024);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var simulatorParams = ClientSimulatorParameters;
            settings.WithNetworkConfigParameters(maxFrameTimeMS: 100);
            settings.AddRawParameterStruct(ref simulatorParams);
            driver = NetworkDriver.Create(settings);
#else
            driver = NetworkDriver.Create(settings);
#endif

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (simulatorParams.PacketDelayMs > 0 || simulatorParams.PacketDropInterval > 0)
            {
                unreliablePipeline = driver.CreatePipeline(
                    typeof(SimulatorPipelineStage),
                    typeof(SimulatorPipelineStageInSend));
                reliablePipeline = driver.CreatePipeline(
                    typeof(ReliableSequencedPipelineStage),
                    typeof(SimulatorPipelineStage),
                    typeof(SimulatorPipelineStageInSend));
                unreliableFragmentedPipeline = driver.CreatePipeline(
                    typeof(FragmentationPipelineStage),
                    typeof(SimulatorPipelineStage),
                    typeof(SimulatorPipelineStageInSend));
            }
            else
#endif
            {
                unreliablePipeline = driver.CreatePipeline(typeof(NullPipelineStage));
                reliablePipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
                unreliableFragmentedPipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage));
            }
        }

        public void CreateServerDriver(World world, out NetworkDriver driver, out NetworkPipeline unreliablePipeline, out NetworkPipeline reliablePipeline, out NetworkPipeline unreliableFragmentedPipeline)
        {
            var settings = new NetworkSettings();
            settings.WithReliableStageParameters(windowSize: 32)
                    .WithFragmentationStageParameters(payloadCapacity: 16 * 1024);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            settings.WithNetworkConfigParameters(maxFrameTimeMS: 100);
            driver = NetworkDriver.Create(settings);
#else
            driver = NetworkDriver.Create(settings);
#endif

            unreliablePipeline = driver.CreatePipeline(typeof(NullPipelineStage));
            reliablePipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            unreliableFragmentedPipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage));
        }

        public bool Listen(NetworkEndPoint endpoint)
        {
            LastDriverWriter.Complete();
            // Switching to server mode
            if (m_Driver.Bind(endpoint) != 0)
                return false;
            if (m_Driver.Listen() != 0)
                return false;
            m_DriverListening = true;
            m_ConcurrentDriver = m_Driver.ToConcurrent();
            m_NetDebugSystem.NetDebug.DebugLog(FixedString.Format("{0} Listening on {1}", m_DebugPrefix, endpoint.Address));
            return true;
        }

        public Entity Connect(NetworkEndPoint endpoint, Entity ent = default)
        {
            LastDriverWriter.Complete();

            if (ent == Entity.Null)
                ent = EntityManager.CreateEntity();
            EntityManager.AddComponentData(ent, new NetworkStreamConnection {Value = m_Driver.Connect(endpoint)});
            EntityManager.AddComponentData(ent, new NetworkSnapshotAckComponent());
            EntityManager.AddComponentData(ent, new CommandTargetComponent());
            EntityManager.AddBuffer<IncomingRpcDataStreamBufferComponent>(ent);
            EntityManager.AddBuffer<OutgoingCommandDataStreamBufferComponent>(ent);
            EntityManager.AddBuffer<IncomingSnapshotDataStreamBufferComponent>(ent);
            EntityManager.AddBuffer<LinkedEntityGroup>(ent).Add(new LinkedEntityGroup{Value = ent});
            m_NetDebugSystem.NetDebug.DebugLog(FixedString.Format("{0} Connecting to {1}", m_DebugPrefix, endpoint.Address));
            return ent;
        }

#if UNITY_EDITOR
        private static int ClientPacketDelayMs => UnityEditor.EditorPrefs.GetInt($"MultiplayerPlayMode_{UnityEngine.Application.productName}_ClientDelay");
        private static int ClientPacketJitterMs => UnityEditor.EditorPrefs.GetInt($"MultiplayerPlayMode_{UnityEngine.Application.productName}_ClientJitter");
        private static int ClientPacketDropRate => UnityEditor.EditorPrefs.GetInt($"MultiplayerPlayMode_{UnityEngine.Application.productName}_ClientDropRate");
#elif DEVELOPMENT_BUILD
        public static int ClientPacketDelayMs = 0;
        public static int ClientPacketJitterMs = 0;
        public static int ClientPacketDropRate = 0;
#endif
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public SimulatorUtility.Parameters ClientSimulatorParameters
        {
            get
            {
                var packetDelay = ClientPacketDelayMs;
                var jitter = ClientPacketJitterMs;
                if (jitter > packetDelay)
                    jitter = packetDelay;
                var packetDrop = ClientPacketDropRate;
                int networkRate = 60; // TODO: read from some better place
                // All 3 packet types every frame stored for maximum delay, doubled for safety margin
                int maxPackets = 2 * (networkRate * 3 * packetDelay + 999) / 1000;
                return new SimulatorUtility.Parameters
                {
                    MaxPacketSize = NetworkParameterConstants.MTU, MaxPacketCount = maxPackets,
                    PacketDelayMs = packetDelay, PacketJitterMs = jitter,
                    PacketDropPercentage = packetDrop
                };
            }
        }
#endif

        private DriverMigrationSystem m_DriverMigrationSystem = default;

        protected override void OnCreate()
        {
            foreach (var world in World.All)
            {
                if ((m_DriverMigrationSystem = world.GetExistingSystem<DriverMigrationSystem>()) != null)
                    break;
            }

            m_NumNetworkIds = new NativeArray<int>(1, Allocator.Persistent);
            m_FreeNetworkIds = new NativeQueue<int>(Allocator.Persistent);

            m_ServerSimulationSystemGroup = World.GetExistingSystem<ServerSimulationSystemGroup>();

            if (HasSingleton<MigrationTicket>())
            {
                var ticket = GetSingleton<MigrationTicket>();

                // load driver & all the network connection data
                var driverState = m_DriverMigrationSystem.Load(ticket.Value);

                m_Driver = driverState.Driver;
                m_ReliablePipeline = driverState.ReliablePipeline;
                m_UnreliablePipeline = driverState.UnreliablePipeline;
                m_UnreliableFragmentedPipeline = driverState.UnreliableFragmentedPipeline;

                m_DriverListening = driverState.Listening;
                m_NumNetworkIds[0] = driverState.NextId;

                foreach (var id in driverState.FreeList)
                {
                    m_FreeNetworkIds.Enqueue(id);
                }
                driverState.FreeList.Dispose();
            }
            else
            {
                if (m_ServerSimulationSystemGroup != null)
                    DriverConstructor.CreateServerDriver(World, out m_Driver, out m_UnreliablePipeline, out m_ReliablePipeline, out m_UnreliableFragmentedPipeline);
                else
                    DriverConstructor.CreateClientDriver(World, out m_Driver, out m_UnreliablePipeline, out m_ReliablePipeline, out m_UnreliableFragmentedPipeline);

                m_DriverListening = false;
            }

            m_ConcurrentDriver = m_Driver.ToConcurrent();
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_RpcQueue = World.GetOrCreateSystem<RpcSystem>().GetRpcQueue<RpcSetNetworkId, RpcSetNetworkId>();
            m_NetworkStreamConnectionQuery = EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
            m_GhostCollectionSystem = World.GetExistingSystem<GhostCollectionSystem>();
            m_NetDebugSystem = World.GetOrCreateSystem<NetDebugSystem>();
            m_DebugPrefix = $"[{World.Name}][Connection]";
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats = new NativeArray<uint>(3, Allocator.Persistent);
            m_GhostStatsCollectionSystem = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif
        }

        protected override void OnDestroy()
        {
            LastDriverWriter.Complete();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats.Dispose();
#endif
            m_NumNetworkIds.Dispose();
            m_FreeNetworkIds.Dispose();

            if ((int)DriverState.Default == m_DriverState)
            {
                var driver = m_Driver;
                Entities.WithoutBurst().ForEach((in NetworkStreamConnection connection) => {
                    driver.Disconnect(connection.Value);
                }).Run();
                m_Driver.ScheduleFlushSend(default).Complete();
                m_Driver.Dispose();
            }
        }

        [BurstCompile]
        struct ConnectionAcceptJob : IJob
        {
            public EntityCommandBuffer commandBuffer;
            public NetworkDriver driver;

            public NativeArray<int> numNetworkId;
            public NativeQueue<int> freeNetworkIds;
            public RpcQueue<RpcSetNetworkId, RpcSetNetworkId> rpcQueue;
            public ClientServerTickRate tickRate;
            public NetworkProtocolVersion protocolVersion;
            public NetDebug netDebug;
            public FixedString128Bytes debugPrefix;
            [ReadOnly] public ComponentDataFromEntity<GhostComponent> ghostFromEntity;

            public void Execute()
            {
                NetworkConnection con;
                while ((con = driver.Accept()) != default(NetworkConnection))
                {
                    // New connection can never have any events, if this one does - just close it
                    DataStreamReader reader;
                    var evt = con.PopEvent(driver, out reader);
                    if (evt != NetworkEvent.Type.Empty)
                    {
                        con.Disconnect(driver);
                        netDebug.DebugLog(FixedString.Format("[{0}][Connection] Disconnecting stale connection detected as new (has pending event={1}).", debugPrefix, (int)evt));
                        continue;
                    }

                    // create an entity for the new connection
                    var ent = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(ent, new NetworkStreamConnection {Value = con});

                    // rpc send buffer might need to be migrated...
                    commandBuffer.AddComponent(ent, new NetworkSnapshotAckComponent());
                    commandBuffer.AddBuffer<PrespawnSectionAck>(ent);
                    commandBuffer.AddComponent(ent, new CommandTargetComponent());
                    commandBuffer.AddBuffer<IncomingRpcDataStreamBufferComponent>(ent);
                    var rpcBuffer = commandBuffer.AddBuffer<OutgoingRpcDataStreamBufferComponent>(ent);
                    commandBuffer.AddBuffer<IncomingCommandDataStreamBufferComponent>(ent);
                    commandBuffer.AddBuffer<LinkedEntityGroup>(ent).Add(new LinkedEntityGroup{Value = ent});

                    RpcSystem.SendProtocolVersion(rpcBuffer, protocolVersion);

                    // Send RPC - assign network id
                    int nid;
                    if (!freeNetworkIds.TryDequeue(out nid))
                    {
                        // Avoid using 0
                        nid = numNetworkId[0] + 1;
                        numNetworkId[0] = nid;
                    }

                    commandBuffer.AddComponent(ent, new NetworkIdComponent {Value = nid});
                    commandBuffer.SetName(ent, new FixedString64Bytes(FixedString.Format("NetworkConnection ({0})", nid)));
                    rpcQueue.Schedule(rpcBuffer, ghostFromEntity, new RpcSetNetworkId
                    {
                        nid = nid,
                        netTickRate = tickRate.NetworkTickRate,
                        simMaxSteps = tickRate.MaxSimulationStepsPerFrame,
                        simMaxStepLength = tickRate.MaxSimulationLongStepTimeMultiplier,
                        simTickRate = tickRate.SimulationTickRate
                    });
                    netDebug.DebugLog(FixedString.Format("{0} Accepted new connection NetworkId={1} InternalId={2}", debugPrefix, nid, con.InternalId));
                }
            }
        }

        protected override void OnUpdate()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_GhostStatsCollectionSystem.AddDiscardedPackets(m_NetStats[2]);
            m_NetStats[2] = 0;
            if (m_NetStats[0] != 0)
            {
                m_GhostStatsCollectionSystem.AddCommandStats(m_NetStats);
                m_NetStats[0] = 0;
                m_NetStats[1] = 0;
            }
#endif
            var commandBuffer = m_Barrier.CreateCommandBuffer().AsParallelWriter();
            var netDebug = m_NetDebugSystem.NetDebug;
            var debugPrefix = m_DebugPrefix;

            if (!HasSingleton<NetworkProtocolVersion>())
            {
                var entity = EntityManager.CreateEntity();
                EntityManager.SetName(entity, "NetworkProtocolVersion");
                var rpcVersion = World.GetExistingSystem<RpcSystem>().CalculateVersionHash();
                var componentsVersion = m_GhostCollectionSystem.CalculateComponentCollectionHash();
                var gameVersion = HasSingleton<GameProtocolVersion>() ? GetSingleton<GameProtocolVersion>().Version : 0;
                EntityManager.AddComponentData(entity, new NetworkProtocolVersion
                {
                    NetCodeVersion = NetworkProtocolVersion.k_NetCodeVersion,
                    GameVersion = gameVersion,
                    RpcCollectionVersion = rpcVersion,
                    ComponentCollectionVersion = componentsVersion
                });
            }
            var concurrentFreeQueue = m_FreeNetworkIds.AsParallelWriter();
            Dependency = m_Driver.ScheduleUpdate(Dependency);
            if (m_DriverListening)
            {
                // Schedule accept job
                var acceptJob = new ConnectionAcceptJob();
                acceptJob.driver = m_Driver;
                acceptJob.commandBuffer = m_Barrier.CreateCommandBuffer();
                acceptJob.numNetworkId = m_NumNetworkIds;
                acceptJob.freeNetworkIds = m_FreeNetworkIds;
                acceptJob.rpcQueue = m_RpcQueue;
                acceptJob.ghostFromEntity = GetComponentDataFromEntity<GhostComponent>(true);
                acceptJob.tickRate = default(ClientServerTickRate);
                if (HasSingleton<ClientServerTickRate>())
                    acceptJob.tickRate = GetSingleton<ClientServerTickRate>();
                acceptJob.tickRate.ResolveDefaults();
                acceptJob.protocolVersion = GetSingleton<NetworkProtocolVersion>();
                acceptJob.netDebug = m_NetDebugSystem.NetDebug;
                acceptJob.debugPrefix = m_DebugPrefix;
                Dependency = acceptJob.Schedule(Dependency);
            }
            else
            {
                if (!HasSingleton<ClientServerTickRate>())
                {
                    var newEntity = World.EntityManager.CreateEntity();
                    var tickRate = new ClientServerTickRate();
                    tickRate.ResolveDefaults();
                    EntityManager.AddComponentData(newEntity, tickRate);
                }
                if (!m_refreshTickRateQuery.IsEmptyIgnoreFilter)
                {
                    var tickRateEntity = GetSingletonEntity<ClientServerTickRate>();
                    Dependency = Entities
                        .WithNone<NetworkStreamDisconnected>()
                        .WithName("RefreshClientServerTickRate")
                        .WithStoreEntityQueryInField(ref m_refreshTickRateQuery)
                        .ForEach((Entity entity, int entityInQueryIndex, in ClientServerTickRateRefreshRequest req) =>
                        {
                            var dataFromEntity = GetComponentDataFromEntity<ClientServerTickRate>();
                            var tickRate = dataFromEntity[tickRateEntity];
                            tickRate.MaxSimulationStepsPerFrame = req.MaxSimulationStepsPerFrame;
                            tickRate.NetworkTickRate = req.NetworkTickRate;
                            tickRate.SimulationTickRate = req.SimulationTickRate;
                            tickRate.MaxSimulationLongStepTimeMultiplier = req.MaxSimulationLongStepTimeMultiplier;
                            dataFromEntity[tickRateEntity] = tickRate;
                            var dbgMsg = FixedString.Format("Using SimulationTickRate={0} NetworkTickRate={1} MaxSimulationStepsPerFrame={2} TargetFrameRateMode={3}", tickRate.SimulationTickRate, tickRate.NetworkTickRate, tickRate.MaxSimulationStepsPerFrame, (int)tickRate.TargetFrameRateMode);
                            netDebug.DebugLog(FixedString.Format("{0} {1}", debugPrefix, dbgMsg));
                            commandBuffer.RemoveComponent<ClientServerTickRateRefreshRequest>(entityInQueryIndex, entity);
                        }).Schedule(Dependency);
                }
                m_FreeNetworkIds.Clear();
            }

            if (!m_pendingConnectionQuery.IsEmptyIgnoreFilter)
            {
                var protocolVersion = GetSingleton<NetworkProtocolVersion>();
                Entities
                    .WithName("CompleteConnection")
                    .WithStoreEntityQueryInField(ref m_pendingConnectionQuery)
                    .WithNone<OutgoingRpcDataStreamBufferComponent>()
                    .ForEach((Entity entity, int nativeThreadIndex, in NetworkStreamConnection con) =>
                {
                    var buf = commandBuffer.AddBuffer<OutgoingRpcDataStreamBufferComponent>(nativeThreadIndex, entity);
                    RpcSystem.SendProtocolVersion(buf, protocolVersion);
                }).ScheduleParallel();
            }

            Dependency = JobHandle.CombineDependencies(Dependency, LastDriverWriter);

            if (!m_requestDisconnectQuery.IsEmptyIgnoreFilter)
            {
                var driver = m_Driver;
                Entities
                    .WithName("ProcessRequestDisconnect")
                    .WithStoreEntityQueryInField(ref m_requestDisconnectQuery)
                    .ForEach((Entity entity, int nativeThreadIndex, ref NetworkStreamConnection connection, in NetworkStreamRequestDisconnect disconnect) =>
                {
                    var id = -1;
                    if (HasComponent<NetworkIdComponent>(entity))
                    {
                        var netIdDataFromEntity = GetComponentDataFromEntity<NetworkIdComponent>(true);
                        id = netIdDataFromEntity[entity].Value;
                    }
                    driver.Disconnect(connection.Value);
                    commandBuffer.AddComponent(nativeThreadIndex, entity, new NetworkStreamDisconnected {Reason = disconnect.Reason});
                    commandBuffer.RemoveComponent<NetworkStreamRequestDisconnect>(nativeThreadIndex, entity);
                    netDebug.DebugLog(FixedString.Format("{0} Disconnecting NetworkId={1} InternalId={2} Reason={3}", debugPrefix, id, connection.Value.InternalId, DisconnectReasonEnumToString.Convert((int)disconnect.Reason)));
                }).Schedule();
            }

            if (!m_notInGameQuery.IsEmptyIgnoreFilter)
            {
                // Clear the ack mask when not in-game
                Entities
                    .WithName("ClearAckMaskWhenNotInGame")
                    .WithStoreEntityQueryInField(ref m_notInGameQuery)
                    .WithNone<NetworkStreamDisconnected>()
                    .WithNone<NetworkStreamRequestDisconnect>()
                    .WithNone<NetworkStreamInGame>()
                    .ForEach((ref NetworkSnapshotAckComponent ack) =>
                {
                    ack = new NetworkSnapshotAckComponent
                    {
                        LastReceivedRemoteTime = ack.LastReceivedRemoteTime,
                        LastReceiveTimestamp = ack.LastReceiveTimestamp,
                        EstimatedRTT = ack.EstimatedRTT,
                        DeviationRTT = ack.DeviationRTT
                    };
                }).Schedule();
            }

            // Schedule parallel update job
            var concurrentDriver = m_ConcurrentDriver;
            var freeNetworkIds = concurrentFreeQueue;
            var networkId = GetComponentDataFromEntity<NetworkIdComponent>();
            var rpcBuffer = GetBufferFromEntity<IncomingRpcDataStreamBufferComponent>();
            var cmdBuffer = GetBufferFromEntity<IncomingCommandDataStreamBufferComponent>();
            var snapshotBuffer = GetBufferFromEntity<IncomingSnapshotDataStreamBufferComponent>();
            var localTime = NetworkTimeSystem.TimestampMS;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var netStats = m_NetStats;
#endif
            uint serverTick = (m_ServerSimulationSystemGroup != null) ? m_ServerSimulationSystemGroup.ServerTick : 0;
            // FIXME: because it uses buffer from entity
            Entities.WithNone<NetworkStreamDisconnected>().WithReadOnly(networkId).
                ForEach((Entity entity, int nativeThreadIndex, ref NetworkStreamConnection connection,
                ref NetworkSnapshotAckComponent snapshotAck) =>
            {
                if (!connection.Value.IsCreated)
                    return;
                DataStreamReader reader;
                NetworkEvent.Type evt;
                while ((evt = concurrentDriver.PopEventForConnection(connection.Value, out reader)) !=
                       NetworkEvent.Type.Empty)
                {
                    switch (evt)
                    {
                        case NetworkEvent.Type.Connect:
                            break;
                        case NetworkEvent.Type.Disconnect:
                            var reason = NetworkStreamDisconnectReason.ConnectionClose;
                            if (reader.Length == 1)
                                reason = (NetworkStreamDisconnectReason)reader.ReadByte();

                            // Flag the connection as lost, it will be deleted in a separate system, giving user code one frame to detect and respond to lost connection
                            commandBuffer.AddComponent(nativeThreadIndex, entity, new NetworkStreamDisconnected
                            {
                                Reason = reason
                            });
                            rpcBuffer[entity].Clear();
                            if (cmdBuffer.HasComponent(entity))
                                cmdBuffer[entity].Clear();
                            connection.Value = default(NetworkConnection);
                            var id = -1;
                            if (networkId.HasComponent(entity))
                            {
                                id = networkId[entity].Value;
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
                                    if (!cmdBuffer.HasComponent(entity))
                                        break;
                                    var buffer = cmdBuffer[entity];
                                    uint snapshot = reader.ReadUInt();
                                    uint snapshotMask = reader.ReadUInt();
                                    snapshotAck.UpdateReceivedByRemote(snapshot, snapshotMask);
                                    uint remoteTime = reader.ReadUInt();
                                    uint localTimeMinusRTT = reader.ReadUInt();
                                    uint interpolationDelay = reader.ReadUInt();
                                    uint numLoadedPrefabs = reader.ReadUInt();

                                    snapshotAck.UpdateRemoteAckedData(remoteTime, numLoadedPrefabs, interpolationDelay);
                                    snapshotAck.UpdateRemoteTime(remoteTime, localTimeMinusRTT, localTime);
                                    var tickReader = reader;
                                    var cmdTick = tickReader.ReadUInt();
                                    var isValidCmdTick = snapshotAck.LastReceivedSnapshotByLocal == 0 || SequenceHelpers.IsNewer(cmdTick, snapshotAck.LastReceivedSnapshotByLocal);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                                    netStats[0] = serverTick;
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
                                    if (!snapshotBuffer.HasComponent(entity))
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
            }).Schedule();
            LastDriverWriter = Dependency;
            m_Barrier.AddJobHandleForProducer(Dependency);
        }

        public void StoreMigrationState(int ticket)
        {
            m_Driver.ScheduleFlushSend(LastDriverWriter).Complete();

            var state = new DriverMigrationSystem.DriverState
            {
                Driver = this.Driver,
                ReliablePipeline = this.ReliablePipeline,
                UnreliablePipeline = this.UnreliablePipeline,
                UnreliableFragmentedPipeline = this.UnreliableFragmentedPipeline,

                Listening = this.m_DriverListening,
                NextId = this.m_NumNetworkIds[0]
            };

            var ids = m_FreeNetworkIds.Count;
            state.FreeList = new NativeArray<int>(ids, Allocator.Persistent);
            for (int i = 0; i < ids; ++i)
            {
                state.FreeList[i] = m_FreeNetworkIds.Dequeue();
            }

            m_DriverState = (int) DriverState.Migrating;

            m_DriverMigrationSystem.Store(state, ticket);
        }
    }
}

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    [DisableAutoCreation]
    [UpdateInWorld(UpdateInWorld.TargetWorld.ClientAndServer)]
    public class NetworkReceiveSystemGroup : ComponentSystemGroup
    {
    }

    public interface INetworkStreamDriverConstructor
    {
        void CreateClientDriver(World world, out NetworkDriver driver, out NetworkPipeline unreliablePipeline, out NetworkPipeline reliablePipeline);
        void CreateServerDriver(World world, out NetworkDriver driver, out NetworkPipeline unreliablePipeline, out NetworkPipeline reliablePipeline);
    }

    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [AlwaysUpdateSystem]
    public class NetworkStreamReceiveSystem : JobComponentSystem, INetworkStreamDriverConstructor
    {
        public static INetworkStreamDriverConstructor s_DriverConstructor;
        public INetworkStreamDriverConstructor DriverConstructor => s_DriverConstructor != null ? s_DriverConstructor : this;
        public NetworkDriver Driver => m_Driver;
        internal NetworkDriver.Concurrent ConcurrentDriver => m_ConcurrentDriver;
        internal JobHandle LastDriverWriter;

        public NetworkPipeline UnreliablePipeline => m_UnreliablePipeline;
        public NetworkPipeline ReliablePipeline => m_ReliablePipeline;

        private NetworkDriver m_Driver;
        private NetworkDriver.Concurrent m_ConcurrentDriver;
        private NetworkPipeline m_UnreliablePipeline;
        private NetworkPipeline m_ReliablePipeline;
        private bool m_DriverListening;
        private NativeArray<int> numNetworkIds;
        private NativeQueue<int> freeNetworkIds;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private RpcQueue<RpcSetNetworkId> rpcQueue;
        private EntityQuery m_NetworkStreamConnectionQuery;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private NativeArray<uint> m_NetStats;
        private GhostStatsCollectionSystem m_GhostStatsCollectionSystem;
#endif

        public void CreateClientDriver(World world, out NetworkDriver driver, out NetworkPipeline unreliablePipeline, out NetworkPipeline reliablePipeline)
        {
            var reliabilityParams = new ReliableUtility.Parameters {WindowSize = 32};

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var netParams = new NetworkConfigParameter
            {
                maxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts,
                connectTimeoutMS = NetworkParameterConstants.ConnectTimeoutMS,
                disconnectTimeoutMS = NetworkParameterConstants.DisconnectTimeoutMS,
                maxFrameTimeMS = 100
            };

            var simulatorParams = ClientSimulatorParameters;
            driver = NetworkDriver.Create(netParams, simulatorParams, reliabilityParams);
#else
            driver = NetworkDriver.Create(reliabilityParams);
#endif

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (simulatorParams.PacketDelayMs > 0 || simulatorParams.PacketDropInterval > 0)
            {
                unreliablePipeline = driver.CreatePipeline(typeof(SimulatorPipelineStage),
                    typeof(SimulatorPipelineStageInSend));
                reliablePipeline = driver.CreatePipeline(typeof(SimulatorPipelineStageInSend),
                    typeof(ReliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
            }
            else
#endif
            {
                unreliablePipeline = driver.CreatePipeline(typeof(NullPipelineStage));
                reliablePipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            }
        }

        public void CreateServerDriver(World world, out NetworkDriver driver, out NetworkPipeline unreliablePipeline, out NetworkPipeline reliablePipeline)
        {
            var reliabilityParams = new ReliableUtility.Parameters {WindowSize = 32};

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var netParams = new NetworkConfigParameter
            {
                maxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts,
                connectTimeoutMS = NetworkParameterConstants.ConnectTimeoutMS,
                disconnectTimeoutMS = NetworkParameterConstants.DisconnectTimeoutMS,
                maxFrameTimeMS = 100
            };
            driver = NetworkDriver.Create(netParams, reliabilityParams);
#else
            driver = NetworkDriver.Create(reliabilityParams);
#endif

            unreliablePipeline = driver.CreatePipeline(typeof(NullPipelineStage));
            reliablePipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
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
            // FIXME: Bind breaks all copies of the driver nad makes them send to the wrong socket
            m_ConcurrentDriver = m_Driver.ToConcurrent();
            return true;
        }

        public Entity Connect(NetworkEndPoint endpoint)
        {
            LastDriverWriter.Complete();

            var ent = EntityManager.CreateEntity();
            EntityManager.AddComponentData(ent, new NetworkStreamConnection {Value = m_Driver.Connect(endpoint)});
            EntityManager.AddComponentData(ent, new NetworkSnapshotAckComponent());
            EntityManager.AddComponentData(ent, new CommandTargetComponent());
            EntityManager.AddBuffer<IncomingRpcDataStreamBufferComponent>(ent);
            EntityManager.AddBuffer<IncomingCommandDataStreamBufferComponent>(ent);
            EntityManager.AddBuffer<IncomingSnapshotDataStreamBufferComponent>(ent);
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

        protected override void OnCreate()
        {
            if (World.GetExistingSystem<ServerSimulationSystemGroup>() != null)
                DriverConstructor.CreateServerDriver(World, out m_Driver, out m_UnreliablePipeline, out m_ReliablePipeline);
            else
                DriverConstructor.CreateClientDriver(World, out m_Driver, out m_UnreliablePipeline, out m_ReliablePipeline);


            m_ConcurrentDriver = m_Driver.ToConcurrent();
            m_DriverListening = false;
            m_Barrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            numNetworkIds = new NativeArray<int>(1, Allocator.Persistent);
            freeNetworkIds = new NativeQueue<int>(Allocator.Persistent);
            rpcQueue = World.GetOrCreateSystem<RpcSystem>().GetRpcQueue<RpcSetNetworkId>();
            m_NetworkStreamConnectionQuery = EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats = new NativeArray<uint>(1, Allocator.Persistent);
            m_GhostStatsCollectionSystem = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif
        }

        protected override void OnDestroy()
        {
            LastDriverWriter.Complete();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats.Dispose();
#endif
            numNetworkIds.Dispose();
            freeNetworkIds.Dispose();
            var driver = m_Driver;
            using (var networkStreamConnections = m_NetworkStreamConnectionQuery.ToComponentDataArray<NetworkStreamConnection>(Allocator.TempJob))
            {
                foreach (var connection in networkStreamConnections)
                {
                    driver.Disconnect(connection.Value);
                }
            }
// TODO: can this be run without safety on the main thread?
//            Entities.ForEach((ref NetworkStreamConnection connection) =>
//            {
//                driver.Disconnect(connection.Value);
//            }).Run();
            m_Driver.Dispose();
        }

        [BurstCompile]
        struct ConnectionAcceptJob : IJob
        {
            public EntityCommandBuffer commandBuffer;
            public NetworkDriver driver;

            public NativeArray<int> numNetworkId;
            public NativeQueue<int> freeNetworkIds;
            public RpcQueue<RpcSetNetworkId> rpcQueue;
            public ClientServerTickRate tickRate;
            public NetworkProtocolVersion protocolVersion;

            public void Execute()
            {
                NetworkConnection con;
                while ((con = driver.Accept()) != default(NetworkConnection))
                {
                    // New connection can never have any events, if this one does - just close it
                    DataStreamReader reader;
                    if (con.PopEvent(driver, out reader) != NetworkEvent.Type.Empty)
                    {
                        con.Disconnect(driver);
                        continue;
                    }

                    // create an entity for the new connection
                    var ent = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(ent, new NetworkStreamConnection {Value = con});
                    commandBuffer.AddComponent(ent, new NetworkSnapshotAckComponent());
                    commandBuffer.AddComponent(ent, new CommandTargetComponent());
                    commandBuffer.AddBuffer<IncomingRpcDataStreamBufferComponent>(ent);
                    var rpcBuffer = commandBuffer.AddBuffer<OutgoingRpcDataStreamBufferComponent>(ent);
                    commandBuffer.AddBuffer<IncomingCommandDataStreamBufferComponent>(ent);
                    commandBuffer.AddBuffer<IncomingSnapshotDataStreamBufferComponent>(ent);

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
                    rpcQueue.Schedule(rpcBuffer, new RpcSetNetworkId
                    {
                        nid = nid,
                        netTickRate = tickRate.NetworkTickRate,
                        simMaxSteps = tickRate.MaxSimulationStepsPerFrame,
                        simTickRate = tickRate.SimulationTickRate
                    });
                }
            }
        }

        [ExcludeComponent(typeof(OutgoingRpcDataStreamBufferComponent))]
        [BurstCompile]
        struct CompleteConnectionJob : IJobForEachWithEntity<NetworkStreamConnection>
        {
            public EntityCommandBuffer.Concurrent commandBuffer;
            public NetworkProtocolVersion protocolVersion;
            public void Execute(Entity entity, int jobIndex, [ReadOnly] ref NetworkStreamConnection con)
            {
                var rpcBuffer = commandBuffer.AddBuffer<OutgoingRpcDataStreamBufferComponent>(jobIndex, entity);
                RpcSystem.SendProtocolVersion(rpcBuffer, protocolVersion);
            }
        }

        [BurstCompile]
        struct DisconnectJob : IJobForEachWithEntity<NetworkStreamConnection, NetworkStreamRequestDisconnect>
        {
            public EntityCommandBuffer.Concurrent commandBuffer;
            public NetworkDriver driver;
            public void Execute(Entity entity, int jobIndex, ref NetworkStreamConnection connection, [ReadOnly] ref NetworkStreamRequestDisconnect disconnect)
            {
                driver.Disconnect(connection.Value);
                commandBuffer.AddComponent(jobIndex, entity, new NetworkStreamDisconnected {Reason = disconnect.Reason});
                commandBuffer.RemoveComponent<NetworkStreamRequestDisconnect>(jobIndex, entity);
            }
        }

        [ExcludeComponent(typeof(NetworkStreamDisconnected))]
        [BurstCompile]
        struct ConnectionReceiveJob : IJobForEachWithEntity<NetworkStreamConnection, NetworkSnapshotAckComponent>
        {
            public EntityCommandBuffer.Concurrent commandBuffer;
            public NetworkDriver.Concurrent driver;
            public NativeQueue<int>.ParallelWriter freeNetworkIds;
            [ReadOnly] public ComponentDataFromEntity<NetworkIdComponent> networkId;
            public BufferFromEntity<IncomingRpcDataStreamBufferComponent> rpcBuffer;
            public BufferFromEntity<IncomingCommandDataStreamBufferComponent> cmdBuffer;
            public BufferFromEntity<IncomingSnapshotDataStreamBufferComponent> snapshotBuffer;
            public uint localTime;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif

            public unsafe void Execute(Entity entity, int index, ref NetworkStreamConnection connection,
                ref NetworkSnapshotAckComponent snapshotAck)
            {
                if (!connection.Value.IsCreated)
                    return;
                DataStreamReader reader;
                NetworkEvent.Type evt;
                while ((evt = driver.PopEventForConnection(connection.Value, out reader)) != NetworkEvent.Type.Empty)
                {
                    switch (evt)
                    {
                        case NetworkEvent.Type.Connect:
                            break;
                        case NetworkEvent.Type.Disconnect:
                            // Flag the connection as lost, it will be deleted in a separate system, giving user code one frame to detect and respond to lost connection
                            commandBuffer.AddComponent(index, entity, new NetworkStreamDisconnected
                            {
                                Reason = NetworkStreamDisconnectReason.ConnectionClose
                            });
                            rpcBuffer[entity].Clear();
                            cmdBuffer[entity].Clear();
                            connection.Value = default(NetworkConnection);
                            if (networkId.Exists(entity))
                                freeNetworkIds.Enqueue(networkId[entity].Value);
                            return;
                        case NetworkEvent.Type.Data:
                            // FIXME: do something with the data
                            switch ((NetworkStreamProtocol) reader.ReadByte())
                            {
                                case NetworkStreamProtocol.Command:
                                {
                                    var buffer = cmdBuffer[entity];
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                                    if (buffer.Length > 0)
                                        netStats[0] = netStats[0] + 1;
#endif
                                    // FIXME: should be handle by a custom command stream system
                                    uint snapshot = reader.ReadUInt();
                                    uint snapshotMask = reader.ReadUInt();
                                    snapshotAck.UpdateReceivedByRemote(snapshot, snapshotMask);
                                    uint remoteTime = reader.ReadUInt();
                                    uint localTimeMinusRTT = reader.ReadUInt();
                                    uint interpolationDelay = reader.ReadUInt();
                                    snapshotAck.UpdateRemoteTime(remoteTime, localTimeMinusRTT, localTime, interpolationDelay);

                                    buffer.Clear();
                                    buffer.Add(ref reader);
                                    break;
                                }
                                case NetworkStreamProtocol.Snapshot:
                                {
                                    uint remoteTime = reader.ReadUInt();
                                    uint localTimeMinusRTT = reader.ReadUInt();
                                    snapshotAck.ServerCommandAge = reader.ReadInt();
                                    snapshotAck.UpdateRemoteTime(remoteTime, localTimeMinusRTT, localTime, 0);

                                    var buffer = snapshotBuffer[entity];
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                                    if (buffer.Length > 0)
                                        netStats[0] = netStats[0] + 1;
#endif
                                    buffer.Clear();
                                    buffer.Add(ref reader);
                                    break;
                                }
                                case NetworkStreamProtocol.Rpc:
                                {
                                    var buffer = rpcBuffer[entity];
                                    buffer.Add(ref reader);
                                    break;
                                }
                                default:
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                    throw new InvalidOperationException("Received unknown message type");
#else
                                    break;
#endif
                            }

                            break;
                        default:
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            throw new InvalidOperationException("Received unknown network event " + evt);
#else
                            break;
#endif
                    }
                }
            }
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_GhostStatsCollectionSystem.AddDiscardedPackets(m_NetStats[0]);
            m_NetStats[0] = 0;
#endif
            if (!HasSingleton<NetworkProtocolVersion>())
            {
                var entity = EntityManager.CreateEntity();
                var rpcVersion = World.GetExistingSystem<RpcSystem>().CalculateVersionHash();
                var gameVersion = HasSingleton<GameProtocolVersion>() ? GetSingleton<GameProtocolVersion>().Version : 0;
                EntityManager.AddComponentData(entity, new NetworkProtocolVersion
                {
                    NetCodeVersion = NetworkProtocolVersion.k_NetCodeVersion,
                    GameVersion = gameVersion,
                    RpcCollectionVersion = rpcVersion
                });
            }
            var concurrentFreeQueue = freeNetworkIds.AsParallelWriter();
            inputDeps = m_Driver.ScheduleUpdate(inputDeps);
            if (m_DriverListening)
            {
                // Schedule accept job
                var acceptJob = new ConnectionAcceptJob();
                acceptJob.driver = m_Driver;
                acceptJob.commandBuffer = m_Barrier.CreateCommandBuffer();
                acceptJob.numNetworkId = numNetworkIds;
                acceptJob.freeNetworkIds = freeNetworkIds;
                acceptJob.rpcQueue = rpcQueue;
                acceptJob.tickRate = default(ClientServerTickRate);
                if (HasSingleton<ClientServerTickRate>())
                    acceptJob.tickRate = GetSingleton<ClientServerTickRate>();
                acceptJob.tickRate.ResolveDefaults();
                acceptJob.protocolVersion = GetSingleton<NetworkProtocolVersion>();
                inputDeps = acceptJob.Schedule(inputDeps);
            }
            else
            {
                freeNetworkIds.Clear();
            }

            var completeJob = new CompleteConnectionJob
            {
                commandBuffer = m_Barrier.CreateCommandBuffer().ToConcurrent(),
                protocolVersion = GetSingleton<NetworkProtocolVersion>()
            };
            inputDeps = completeJob.Schedule(this, inputDeps);

            inputDeps = JobHandle.CombineDependencies(inputDeps, LastDriverWriter);
            var disconnectJob = new DisconnectJob
            {
                commandBuffer = m_Barrier.CreateCommandBuffer().ToConcurrent(),
                driver = m_Driver
            };
            inputDeps = disconnectJob.ScheduleSingle(this, inputDeps);

            // Schedule parallel update job
            var recvJob = new ConnectionReceiveJob();
            recvJob.commandBuffer = m_Barrier.CreateCommandBuffer().ToConcurrent();
            recvJob.driver = m_ConcurrentDriver;
            recvJob.freeNetworkIds = concurrentFreeQueue;
            recvJob.networkId = GetComponentDataFromEntity<NetworkIdComponent>();
            recvJob.rpcBuffer = GetBufferFromEntity<IncomingRpcDataStreamBufferComponent>();
            recvJob.cmdBuffer = GetBufferFromEntity<IncomingCommandDataStreamBufferComponent>();
            recvJob.snapshotBuffer = GetBufferFromEntity<IncomingSnapshotDataStreamBufferComponent>();
            recvJob.localTime = NetworkTimeSystem.TimestampMS;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            recvJob.netStats = m_NetStats;
#endif
            // FIXME: because it uses buffer from entity
            LastDriverWriter = recvJob.ScheduleSingle(this, inputDeps);
            m_Barrier.AddJobHandleForProducer(LastDriverWriter);
            return LastDriverWriter;
        }
    }
}

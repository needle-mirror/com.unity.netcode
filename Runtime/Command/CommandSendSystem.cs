using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    // dependency just for acking
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    public class CommandSendSystem<TCommandData> : JobComponentSystem
        where TCommandData : struct, ICommandData<TCommandData>
    {
        public const uint k_InputBufferSendSize = 4;

        [ExcludeComponent(typeof(NetworkStreamDisconnected))]
        [RequireComponentTag(typeof(NetworkStreamInGame))]
        struct CommandSendJob : IJobForEach<NetworkStreamConnection, NetworkSnapshotAckComponent, CommandTargetComponent>
        {
            public UdpNetworkDriver.Concurrent driver;
            public NetworkPipeline unreliablePipeline;
            [ReadOnly] public BufferFromEntity<TCommandData> inputFromEntity;
            public NetworkCompressionModel compressionModel;
            public uint localTime;
            public uint inputTargetTick;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif

            public void Execute([ReadOnly] ref NetworkStreamConnection connection,
                [ReadOnly] ref NetworkSnapshotAckComponent ack, [ReadOnly] ref CommandTargetComponent state)
            {
                if (typeof(TCommandData) == typeof(NullCommandData) && state.targetEntity != Entity.Null)
                    return;
                if (typeof(TCommandData) != typeof(NullCommandData) && !inputFromEntity.Exists(state.targetEntity))
                    return;
                DataStreamWriter writer = new DataStreamWriter(1200, Allocator.Temp);
                writer.Write((byte) NetworkStreamProtocol.Command);
                writer.Write(ack.LastReceivedSnapshotByLocal);
                writer.Write(ack.ReceivedSnapshotByLocalMask);
                writer.Write(localTime);
                uint returnTime = ack.LastReceivedRemoteTime;
                if (returnTime != 0)
                    returnTime -= (localTime - ack.LastReceiveTimestamp);
                writer.Write(returnTime);
                writer.Write(inputTargetTick);
                if (state.targetEntity != Entity.Null)
                {
                    var input = inputFromEntity[state.targetEntity];
                    TCommandData baselineInputData;
                    input.GetDataAtTick(inputTargetTick, out baselineInputData);
                    baselineInputData.Serialize(writer);
                    for (uint inputIndex = 1; inputIndex < k_InputBufferSendSize; ++inputIndex)
                    {
                        TCommandData inputData;
                        input.GetDataAtTick(inputTargetTick - inputIndex, out inputData);
                        inputData.Serialize(writer, baselineInputData, compressionModel);
                    }

                    writer.Flush();
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats[0] = inputTargetTick;
                netStats[1] = (uint)writer.Length;
#endif

                driver.Send(unreliablePipeline, connection.Value, writer);
            }
        }

        private NetworkStreamReceiveSystem m_ReceiveSystem;
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private NetworkCompressionModel m_CompressionModel;
        private uint m_LastServerTick;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private GhostStatsCollectionSystem m_GhostStatsCollectionSystem;
        private NativeArray<uint> m_NetStats;
#endif

        protected override void OnCreate()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_GhostStatsCollectionSystem = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
            m_NetStats = new NativeArray<uint>(2, Allocator.Persistent);
#endif
            m_ReceiveSystem = World.GetOrCreateSystem<NetworkStreamReceiveSystem>();
            m_ClientSimulationSystemGroup = World.GetOrCreateSystem<ClientSimulationSystemGroup>();
            m_CompressionModel = new NetworkCompressionModel(Allocator.Persistent);
            RequireForUpdate(EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<NetworkStreamConnection>(),
                ComponentType.ReadOnly<NetworkSnapshotAckComponent>(),
                ComponentType.ReadOnly<CommandTargetComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>()));
            if (typeof(TCommandData) != typeof(NullCommandData))
                RequireForUpdate(EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TCommandData>()));
        }

        protected override void OnDestroy()
        {
            m_CompressionModel.Dispose();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats.Dispose();
#endif
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_NetStats[0] != 0)
            {
                m_GhostStatsCollectionSystem.AddCommandStats(m_NetStats);
                m_NetStats[0] = 0;
                m_NetStats[1] = 0;
            }
#endif
            var targetTick = m_ClientSimulationSystemGroup.ServerTick;
            if (m_ClientSimulationSystemGroup.ServerTickFraction < 1)
                --targetTick;
            // Make sure we only send a single ack per tick - only triggers when using dynamic timestep
            if (targetTick == m_LastServerTick)
                return inputDeps;
            m_LastServerTick = targetTick;
            var sendJob = new CommandSendJob
            {
                driver = m_ReceiveSystem.ConcurrentDriver,
                unreliablePipeline = m_ReceiveSystem.UnreliablePipeline,
                inputFromEntity = GetBufferFromEntity<TCommandData>(true),
                compressionModel = m_CompressionModel,
                localTime = NetworkTimeSystem.TimestampMS,
                inputTargetTick = targetTick,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats = m_NetStats
#endif
            };

            var handle = sendJob.ScheduleSingle(this,
                JobHandle.CombineDependencies(inputDeps, m_ReceiveSystem.LastDriverWriter));
            m_ReceiveSystem.LastDriverWriter = handle;
            return handle;
        }
    }

    public struct NullCommandData : ICommandData<NullCommandData>
    {
        public uint Tick => 0;
        public void Serialize(DataStreamWriter writer)
        {
        }
        public void Serialize(DataStreamWriter writer, NullCommandData baseline, NetworkCompressionModel compressionModel)
        {
        }
        public void Deserialize(uint tick, DataStreamReader reader, ref DataStreamReader.Context ctx)
        {
        }
        public void Deserialize(uint tick, DataStreamReader reader, ref DataStreamReader.Context ctx, NullCommandData baseline, NetworkCompressionModel compressionModel)
        {
        }
    }

    public class NullCommandSendSystem : CommandSendSystem<NullCommandData>
    {
    }
}

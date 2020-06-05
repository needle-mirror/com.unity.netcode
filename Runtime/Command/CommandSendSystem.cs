using Unity.Burst;
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

        [BurstCompile]
        struct CommandSendJob : IJobChunk
        {
            public NetworkDriver.Concurrent driver;
            public NetworkPipeline unreliablePipeline;
            public ArchetypeChunkComponentType<NetworkStreamConnection> streamConnectionType;
            public ArchetypeChunkComponentType<NetworkSnapshotAckComponent> snapshotAckType;
            public ArchetypeChunkComponentType<CommandTargetComponent> commmandTargetType;
            [ReadOnly] public BufferFromEntity<TCommandData> inputFromEntity;
            public NetworkCompressionModel compressionModel;
            public uint localTime;
            public uint inputTargetTick;
            public uint interpolationDelay;
            public bool isNullCommandData;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif

            public void LambdaMethod(in NetworkStreamConnection connection, in NetworkSnapshotAckComponent ack,
                in CommandTargetComponent state)
            {
                if (isNullCommandData && state.targetEntity != Entity.Null)
                    return;
                if (!isNullCommandData && !inputFromEntity.Exists(state.targetEntity))
                    return;
                DataStreamWriter writer = driver.BeginSend(unreliablePipeline, connection.Value);
                if (!writer.IsCreated)
                    return;
                writer.WriteByte((byte) NetworkStreamProtocol.Command);
                writer.WriteUInt(ack.LastReceivedSnapshotByLocal);
                writer.WriteUInt(ack.ReceivedSnapshotByLocalMask);
                writer.WriteUInt(localTime);
                uint returnTime = ack.LastReceivedRemoteTime;
                if (returnTime != 0)
                    returnTime -= (localTime - ack.LastReceiveTimestamp);
                writer.WriteUInt(returnTime);
                writer.WriteUInt(interpolationDelay);
                writer.WriteUInt(inputTargetTick);
                if (state.targetEntity != Entity.Null)
                {
                    var input = inputFromEntity[state.targetEntity];
                    TCommandData baselineInputData;
                    input.GetDataAtTick(inputTargetTick, out baselineInputData);
                    baselineInputData.Serialize(ref writer);
                    for (uint inputIndex = 1; inputIndex < k_InputBufferSendSize; ++inputIndex)
                    {
                        TCommandData inputData;
                        input.GetDataAtTick(inputTargetTick - inputIndex, out inputData);
                        inputData.Serialize(ref writer, baselineInputData, compressionModel);
                    }

                    writer.Flush();
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats[0] = inputTargetTick;
                netStats[1] = (uint)writer.Length;
#endif

                driver.EndSend(writer);
            }

            public void Execute(ArchetypeChunk chunk, int orderIndex, int firstEntityIndex)
            {
                var connections = chunk.GetNativeArray(streamConnectionType);
                var snapshotAcks = chunk.GetNativeArray(snapshotAckType);
                var commandTargets = chunk.GetNativeArray(commmandTargetType);

                for (int i = 0; i < chunk.Count; ++i)
                {
                    LambdaMethod(connections[i], snapshotAcks[i], commandTargets[i]);
                }
            }
        }

        private EntityQuery m_entityGroup;
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
            m_entityGroup = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<NetworkStreamConnection>(),
                ComponentType.ReadOnly<NetworkSnapshotAckComponent>(),
                ComponentType.ReadOnly<CommandTargetComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());
            RequireForUpdate(m_entityGroup);
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
                streamConnectionType = GetArchetypeChunkComponentType<NetworkStreamConnection>(),
                snapshotAckType = GetArchetypeChunkComponentType<NetworkSnapshotAckComponent>(),
                commmandTargetType = GetArchetypeChunkComponentType<CommandTargetComponent>(),
                inputFromEntity = GetBufferFromEntity<TCommandData>(true),
                compressionModel = m_CompressionModel,
                localTime = NetworkTimeSystem.TimestampMS,
                inputTargetTick = targetTick,
                interpolationDelay = m_ClientSimulationSystemGroup.ServerTick -
                                     m_ClientSimulationSystemGroup.InterpolationTick,
                isNullCommandData = typeof(TCommandData) == typeof(NullCommandData),
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats = m_NetStats
#endif
            };

            var handle = sendJob.Schedule(m_entityGroup,
                JobHandle.CombineDependencies(inputDeps, m_ReceiveSystem.LastDriverWriter));
            handle = m_ReceiveSystem.Driver.ScheduleFlushSend(handle);
            m_ReceiveSystem.LastDriverWriter = handle;
            return handle;
        }
    }

    public struct NullCommandData : ICommandData<NullCommandData>
    {
        public uint Tick => 0;
        public void Serialize(ref DataStreamWriter writer)
        {
        }
        public void Serialize(ref DataStreamWriter writer, NullCommandData baseline, NetworkCompressionModel compressionModel)
        {
        }
        public void Deserialize(uint tick, ref DataStreamReader reader)
        {
        }
        public void Deserialize(uint tick, ref DataStreamReader reader, NullCommandData baseline, NetworkCompressionModel compressionModel)
        {
        }
    }

    public class NullCommandSendSystem : CommandSendSystem<NullCommandData>
    {
    }
}

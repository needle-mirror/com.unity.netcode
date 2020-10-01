using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Collections;

namespace Unity.NetCode
{
    [UpdateInWorld(UpdateInWorld.TargetWorld.Server)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateAfter(typeof(NetworkStreamReceiveSystem))]
    public class CommandReceiveSystemGroup : ComponentSystemGroup
    {
    }

    [UpdateInGroup(typeof(CommandReceiveSystemGroup))]
    public abstract class CommandReceiveSystem<TCommandDataSerializer, TCommandData> : SystemBase
        where TCommandData : struct, ICommandData
        where TCommandDataSerializer : struct, ICommandDataSerializer<TCommandData>
    {
        protected struct ReceiveJobData
        {
            public BufferFromEntity<TCommandData> commandData;
            public BufferFromEntity<IncomingCommandDataStreamBufferComponent> cmdBuffer;
            public ComponentDataFromEntity<CommandDataInterpolationDelay> delayFromEntity;
            public NetworkCompressionModel compressionModel;
            public ComponentTypeHandle<NetworkSnapshotAckComponent> snapshotAckType;
            [ReadOnly] public ComponentTypeHandle<CommandTargetComponent> commmandTargetType;
            [ReadOnly] public EntityTypeHandle entitiesType;

            public uint serverTick;
            public bool isNullCommandData;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif

            public unsafe void LambdaMethod(Entity entity, int index, in CommandTargetComponent commandTarget,
                ref NetworkSnapshotAckComponent snapshotAck)
            {
                if (isNullCommandData && commandTarget.targetEntity != Entity.Null)
                    return;
                if (!isNullCommandData && !commandData.HasComponent(commandTarget.targetEntity))
                    return;

                var buffer = cmdBuffer[entity];
                if (buffer.Length < 4)
                    return;
                DataStreamReader reader = buffer.AsDataStreamReader();
                var tick = reader.ReadUInt();

                int age = (int) (serverTick - tick);
                age *= 256;
                snapshotAck.ServerCommandAge = (snapshotAck.ServerCommandAge * 7 + age) / 8;

                if (delayFromEntity.HasComponent(commandTarget.targetEntity))
                    delayFromEntity[commandTarget.targetEntity] = new CommandDataInterpolationDelay{ Delay = snapshotAck.RemoteInterpolationDelay};

                if (commandTarget.targetEntity != Entity.Null && buffer.Length > 4)
                {
                    var buffers = new NativeArray<TCommandData>((int)CommandSendSystem<TCommandDataSerializer, TCommandData>.k_InputBufferSendSize, Allocator.Temp);
                    var command = commandData[commandTarget.targetEntity];
                    var baselineReceivedCommand = default(TCommandData);
                    var serializer = default(TCommandDataSerializer);
                    baselineReceivedCommand.Tick = tick;
                    serializer.Deserialize(ref reader, ref baselineReceivedCommand);
                    // Store received commands in the network command buffer
                    buffers[0] = baselineReceivedCommand;
                    for (uint i = 1; i < CommandSendSystem<TCommandDataSerializer, TCommandData>.k_InputBufferSendSize; ++i)
                    {
                        var receivedCommand = default(TCommandData);
                        receivedCommand.Tick = tick - i;
                        serializer.Deserialize(ref reader, ref receivedCommand, baselineReceivedCommand,
                            compressionModel);
                        // Store received commands in the network command buffer
                        buffers[(int)i] = receivedCommand;
                    }
                    // Add the command in the order they were produces instead of the order they were sent
                    for (int i = (int)CommandSendSystem<TCommandDataSerializer, TCommandData>.k_InputBufferSendSize - 1; i >= 0; --i)
                        command.AddCommandData(buffers[i]);
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats[0] = serverTick;
                netStats[1] += (uint)buffer.Length + 16u; // 16 is the ack fields which are already processed
#endif
                buffer.Clear();
            }
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                var entities = chunk.GetNativeArray(entitiesType);
                var snapshotAcks = chunk.GetNativeArray(snapshotAckType);
                var commandTargets = chunk.GetNativeArray(commmandTargetType);

                for (int i = 0; i < chunk.Count; ++i)
                {
                    var snapshotAck = snapshotAcks[i];
                    LambdaMethod(entities[i], orderIndex, commandTargets[i], ref snapshotAck);
                    snapshotAcks[i] = snapshotAck;
                }
            }
        }

        private EntityQuery m_entityQuery;
        private ServerSimulationSystemGroup serverSimulationSystemGroup;
        private NetworkCompressionModel m_CompressionModel;
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
            serverSimulationSystemGroup = World.GetExistingSystem<ServerSimulationSystemGroup>();
            m_CompressionModel = new NetworkCompressionModel(Allocator.Persistent);

            m_entityQuery = GetEntityQuery(
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<NetworkSnapshotAckComponent>(),
                ComponentType.ReadWrite<CommandTargetComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());

            RequireForUpdate(GetEntityQuery(
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<IncomingCommandDataStreamBufferComponent>(),
                ComponentType.ReadOnly<NetworkSnapshotAckComponent>(),
                ComponentType.ReadOnly<CommandTargetComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>()));

            if (typeof(TCommandData) != typeof(NullCommandData))
                RequireForUpdate(EntityManager.CreateEntityQuery(ComponentType.ReadWrite<TCommandData>()));
        }

        protected override void OnDestroy()
        {
            m_CompressionModel.Dispose();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats.Dispose();
#endif
        }

        protected ReceiveJobData InitJobData()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_NetStats[0] != 0)
            {
                m_GhostStatsCollectionSystem.AddCommandStats(m_NetStats);
                m_NetStats[0] = 0;
                m_NetStats[1] = 0;
            }
#endif
            var recvJob = new ReceiveJobData
            {
                commandData = GetBufferFromEntity<TCommandData>(),
                cmdBuffer = GetBufferFromEntity<IncomingCommandDataStreamBufferComponent>(),
                delayFromEntity = GetComponentDataFromEntity<CommandDataInterpolationDelay>(),
                compressionModel = m_CompressionModel,
                snapshotAckType = GetComponentTypeHandle<NetworkSnapshotAckComponent>(),
                commmandTargetType = GetComponentTypeHandle<CommandTargetComponent>(true),
                entitiesType = GetEntityTypeHandle(),
                serverTick = serverSimulationSystemGroup.ServerTick,
                isNullCommandData = typeof(TCommandData) == typeof(NullCommandData),
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats = m_NetStats
#endif
            };
            return recvJob;
        }
        protected void ScheduleJobData<T>(in T sendJob) where T: struct, IJobEntityBatch
        {
            Dependency = sendJob.Schedule(m_entityQuery, Dependency);
        }
    }
}

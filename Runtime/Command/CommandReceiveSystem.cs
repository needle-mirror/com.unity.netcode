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
    public class CommandReceiveSystem<TCommandData> : JobComponentSystem
        where TCommandData : struct, ICommandData<TCommandData>
    {
        [BurstCompile]
        struct ReceiveJob : IJobChunk
        {
            public BufferFromEntity<TCommandData> commandData;
            public BufferFromEntity<IncomingCommandDataStreamBufferComponent> cmdBuffer;
            public ComponentDataFromEntity<CommandDataInterpolationDelay> delayFromEntity;
            public NetworkCompressionModel compressionModel;
            public ArchetypeChunkComponentType<NetworkSnapshotAckComponent> snapshotAckType;
            [ReadOnly] public ArchetypeChunkComponentType<CommandTargetComponent> commmandTargetType;
            [ReadOnly] public ArchetypeChunkEntityType entitiesType;

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
                if (!isNullCommandData && !commandData.Exists(commandTarget.targetEntity))
                    return;

                var buffer = cmdBuffer[entity];
                if (buffer.Length < 4)
                    return;
                DataStreamReader reader = buffer.AsDataStreamReader();
                var tick = reader.ReadUInt();

                int age = (int) (serverTick - tick);
                age *= 256;
                snapshotAck.ServerCommandAge = (snapshotAck.ServerCommandAge * 7 + age) / 8;

                if (delayFromEntity.Exists(commandTarget.targetEntity))
                    delayFromEntity[commandTarget.targetEntity] = new CommandDataInterpolationDelay{ Delay = snapshotAck.RemoteInterpolationDelay};

                if (commandTarget.targetEntity != Entity.Null && buffer.Length > 4)
                {
                    var buffers = new NativeArray<TCommandData>((int)CommandSendSystem<TCommandData>.k_InputBufferSendSize, Allocator.Temp);
                    var command = commandData[commandTarget.targetEntity];
                    var baselineReceivedCommand = default(TCommandData);
                    baselineReceivedCommand.Deserialize(tick, ref reader);
                    // Store received commands in the network command buffer
                    buffers[0] = baselineReceivedCommand;
                    for (uint i = 1; i < CommandSendSystem<TCommandData>.k_InputBufferSendSize; ++i)
                    {
                        var receivedCommand = default(TCommandData);
                        receivedCommand.Deserialize(tick - i, ref reader, baselineReceivedCommand,
                            compressionModel);
                        // Store received commands in the network command buffer
                        buffers[(int)i] = receivedCommand;
                    }
                    // Add the command in the order they were produces instead of the order they were sent
                    for (int i = (int)CommandSendSystem<TCommandData>.k_InputBufferSendSize - 1; i >= 0; --i)
                        command.AddCommandData(buffers[i]);
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats[0] = serverTick;
                netStats[1] += (uint)buffer.Length + 16u; // 16 is the ack fields which are already processed
#endif
                buffer.Clear();
            }
            public void Execute(ArchetypeChunk chunk, int orderIndex, int firstEntityIndex)
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
            var recvJob = new ReceiveJob
            {
                commandData = GetBufferFromEntity<TCommandData>(),
                cmdBuffer = GetBufferFromEntity<IncomingCommandDataStreamBufferComponent>(),
                delayFromEntity = GetComponentDataFromEntity<CommandDataInterpolationDelay>(),
                compressionModel = m_CompressionModel,
                snapshotAckType = GetArchetypeChunkComponentType<NetworkSnapshotAckComponent>(),
                commmandTargetType = GetArchetypeChunkComponentType<CommandTargetComponent>(true),
                entitiesType = GetArchetypeChunkEntityType(),
                serverTick = serverSimulationSystemGroup.ServerTick,
                isNullCommandData = typeof(TCommandData) == typeof(NullCommandData),
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats = m_NetStats
#endif
            };
            return recvJob.ScheduleSingle(m_entityQuery, inputDeps);
        }
    }
    public class NullCommandReceiveSystem : CommandReceiveSystem<NullCommandData>
    {
    }
}

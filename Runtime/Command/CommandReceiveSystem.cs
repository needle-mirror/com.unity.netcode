using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using Unity.Collections;

namespace Unity.NetCode
{
    [UpdateInWorld(UpdateInWorld.TargetWorld.Server)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateAfter(typeof(NetworkStreamReceiveSystem))]
    public class CommandReceiveSystem<TCommandData> : JobComponentSystem
        where TCommandData : struct, ICommandData<TCommandData>
    {
        [ExcludeComponent(typeof(NetworkStreamDisconnected))]
        [RequireComponentTag(typeof(NetworkStreamInGame))]
        struct ReceiveJob : IJobForEachWithEntity<CommandTargetComponent, NetworkSnapshotAckComponent>
        {
            public BufferFromEntity<TCommandData> commandData;
            public BufferFromEntity<IncomingCommandDataStreamBufferComponent> cmdBuffer;
            public NetworkCompressionModel compressionModel;
            public uint serverTick;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif

            public unsafe void Execute(Entity entity, int index, [ReadOnly] ref CommandTargetComponent commandTarget,
                ref NetworkSnapshotAckComponent snapshotAck)
            {
                if (typeof(TCommandData) == typeof(NullCommandData) && commandTarget.targetEntity != Entity.Null)
                    return;
                if (typeof(TCommandData) != typeof(NullCommandData) && !commandData.Exists(commandTarget.targetEntity))
                    return;

                var buffer = cmdBuffer[entity];
                if (buffer.Length < 4)
                    return;
                DataStreamReader reader =
                    DataStreamUnsafeUtility.CreateReaderFromExistingData((byte*) buffer.GetUnsafePtr(), buffer.Length);
                var ctx = default(DataStreamReader.Context);
                var tick = reader.ReadUInt(ref ctx);

                int age = (int) (serverTick - tick);
                age *= 256;
                snapshotAck.ServerCommandAge = (snapshotAck.ServerCommandAge * 7 + age) / 8;

                if (commandTarget.targetEntity != Entity.Null && buffer.Length > 4)
                {
                    var command = commandData[commandTarget.targetEntity];
                    var baselineReceivedCommand = default(TCommandData);
                    baselineReceivedCommand.Deserialize(tick, reader, ref ctx);
                    // Store received commands in the network command buffer
                    command.AddCommandData(baselineReceivedCommand);
                    for (uint i = 1; i < CommandSendSystem<TCommandData>.k_InputBufferSendSize; ++i)
                    {
                        var receivedCommand = default(TCommandData);
                        receivedCommand.Deserialize(tick - i, reader, ref ctx, baselineReceivedCommand,
                            compressionModel);
                        // Store received commands in the network command buffer
                        command.AddCommandData(receivedCommand);
                    }
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats[0] = serverTick;
                netStats[1] += (uint)buffer.Length + 16u; // 16 is the ack fields which are already processed
#endif
                buffer.Clear();
            }
        }

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
            RequireForUpdate(EntityManager.CreateEntityQuery(
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
            var recvJob = new ReceiveJob();
            recvJob.commandData = GetBufferFromEntity<TCommandData>();
            recvJob.cmdBuffer = GetBufferFromEntity<IncomingCommandDataStreamBufferComponent>();
            recvJob.compressionModel = m_CompressionModel;
            recvJob.serverTick = serverSimulationSystemGroup.ServerTick;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            recvJob.netStats = m_NetStats;
#endif
            return recvJob.ScheduleSingle(this, inputDeps);
        }
    }
    public class NullCommandReceiveSystem : CommandReceiveSystem<NullCommandData>
    {
    }
}

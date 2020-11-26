using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostReceiveSystem))]
    [UpdateBefore(typeof(CommandSendSystemGroup))]
    public class GhostInputSystemGroup : ComponentSystemGroup
    {
    }

    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    // dependency just for acking
    [UpdateAfter(typeof(GhostReceiveSystem))]
    [UpdateBefore(typeof(GhostPredictionSystemGroup))]
    public class CommandSendSystemGroup : ComponentSystemGroup
    {
    }

    [UpdateInGroup(typeof(CommandSendSystemGroup))]
    public abstract class CommandSendSystem<TCommandDataSerializer, TCommandData> : SystemBase
        where TCommandData : struct, ICommandData
        where TCommandDataSerializer : struct, ICommandDataSerializer<TCommandData>
    {
        public const uint k_InputBufferSendSize = 4;

        protected struct SendJobData
        {
            public NetworkDriver.Concurrent driver;
            public NetworkPipeline unreliablePipeline;
            public ComponentTypeHandle<NetworkStreamConnection> streamConnectionType;
            public ComponentTypeHandle<NetworkSnapshotAckComponent> snapshotAckType;
            public ComponentTypeHandle<CommandTargetComponent> commmandTargetType;
            [ReadOnly] public BufferFromEntity<TCommandData> inputFromEntity;
            public NetworkCompressionModel compressionModel;
            public uint localTime;
            public uint inputTargetTick;
            public uint interpolationDelay;
            public bool isNullCommandData;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public NativeArray<uint> netStats;
#endif
            public int numLoadedPrefabs;

            public void LambdaMethod(in NetworkStreamConnection connection, in NetworkSnapshotAckComponent ack,
                in CommandTargetComponent state)
            {
                if (isNullCommandData && state.targetEntity != Entity.Null)
                    return;
                if (!isNullCommandData && !inputFromEntity.HasComponent(state.targetEntity))
                    return;
                if (driver.BeginSend(unreliablePipeline, connection.Value, out var writer) == 0)
                {
                    writer.WriteByte((byte) NetworkStreamProtocol.Command);
                    writer.WriteUInt(ack.LastReceivedSnapshotByLocal);
                    writer.WriteUInt(ack.ReceivedSnapshotByLocalMask);
                    writer.WriteUInt(localTime);

                    uint returnTime = ack.LastReceivedRemoteTime;
                    if (returnTime != 0)
                        returnTime -= (localTime - ack.LastReceiveTimestamp);

                    writer.WriteUInt(returnTime);
                    writer.WriteUInt(interpolationDelay);
                    writer.WriteUInt((uint)numLoadedPrefabs);
                    writer.WriteUInt(inputTargetTick);
                    if (state.targetEntity != Entity.Null)
                    {
                        var input = inputFromEntity[state.targetEntity];
                        TCommandData baselineInputData;
                        input.GetDataAtTick(inputTargetTick, out baselineInputData);
                        var serializer = default(TCommandDataSerializer);
                        serializer.Serialize(ref writer, baselineInputData);
                        for (uint inputIndex = 1; inputIndex < k_InputBufferSendSize; ++inputIndex)
                        {
                            TCommandData inputData;
                            input.GetDataAtTick(inputTargetTick - inputIndex, out inputData);
                            serializer.Serialize(ref writer, inputData, baselineInputData, compressionModel);
                        }

                        writer.Flush();
                    }
    #if UNITY_EDITOR || DEVELOPMENT_BUILD
                    netStats[0] = inputTargetTick;
                    netStats[1] = (uint)writer.Length;
    #endif

                    var result = 0;
                    if ((result = driver.EndSend(writer)) <= 0)
                    {
                        UnityEngine.Debug.LogError($"An error occured during EndSend. ErrorCode: {result}");
                    }
                }
            }

            public void Execute(ArchetypeChunk chunk, int orderIndex)
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
            RequireSingletonForUpdate<GhostCollection>();
        }

        protected override void OnDestroy()
        {
            m_CompressionModel.Dispose();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats.Dispose();
#endif
        }

        protected SendJobData InitJobData()
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
            var sendJob = new SendJobData
            {
                driver = m_ReceiveSystem.ConcurrentDriver,
                unreliablePipeline = m_ReceiveSystem.UnreliablePipeline,
                streamConnectionType = GetComponentTypeHandle<NetworkStreamConnection>(),
                snapshotAckType = GetComponentTypeHandle<NetworkSnapshotAckComponent>(),
                commmandTargetType = GetComponentTypeHandle<CommandTargetComponent>(),
                inputFromEntity = GetBufferFromEntity<TCommandData>(true),
                compressionModel = m_CompressionModel,
                localTime = NetworkTimeSystem.TimestampMS,
                inputTargetTick = targetTick,
                interpolationDelay = m_ClientSimulationSystemGroup.ServerTick -
                                     m_ClientSimulationSystemGroup.InterpolationTick,
                isNullCommandData = typeof(TCommandData) == typeof(NullCommandData),
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats = m_NetStats,
#endif
                numLoadedPrefabs = GetSingleton<GhostCollection>().NumLoadedPrefabs
            };
            return sendJob;
        }
        protected void ScheduleJobData<T>(in T sendJob) where T: struct, IJobEntityBatch
        {
            var targetTick = m_ClientSimulationSystemGroup.ServerTick;
            if (m_ClientSimulationSystemGroup.ServerTickFraction < 1)
                --targetTick;
            // Make sure we only send a single ack per tick - only triggers when using dynamic timestep
            if (targetTick == m_LastServerTick)
                return;
            m_LastServerTick = targetTick;

            Dependency = sendJob.Schedule(m_entityGroup, JobHandle.CombineDependencies(Dependency, m_ReceiveSystem.LastDriverWriter));
            Dependency = m_ReceiveSystem.Driver.ScheduleFlushSend(Dependency);
            m_ReceiveSystem.LastDriverWriter = Dependency;
        }
    }

    public struct NullCommandData : ICommandData
    {
        public uint Tick {get; set;}
    }
}

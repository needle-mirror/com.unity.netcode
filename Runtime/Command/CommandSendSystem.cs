using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostReceiveSystem))]
    [UpdateBefore(typeof(CommandSendSystemGroup))]
    public class GhostInputSystemGroup : ComponentSystemGroup
    {
    }

    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    // dependency just for acking
    [UpdateAfter(typeof(GhostReceiveSystem))]
    [UpdateBefore(typeof(GhostPredictionSystemGroup))]
    public class CommandSendSystemGroup : ComponentSystemGroup
    {
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private uint m_lastServerTick;
        protected override void OnCreate()
        {
            base.OnCreate();
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
        }
        protected override void OnUpdate()
        {
            var targetTick = m_ClientSimulationSystemGroup.ServerTick;
            if (m_ClientSimulationSystemGroup.ServerTickFraction < 1)
                --targetTick;
            // Make sure we only send a single ack per tick - only triggers when using dynamic timestep
            if (targetTick == m_lastServerTick)
                return;

            m_lastServerTick = targetTick;
            base.OnUpdate();
        }
    }

    [UpdateInGroup(typeof(CommandSendSystemGroup), OrderLast = true)]
    public partial class CommandSendPacketSystem : SystemBase
    {
        private NetworkStreamReceiveSystem m_ReceiveSystem;
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private NetworkCompressionModel m_CompressionModel;
        private NetDebugSystem m_NetDebugSystem;
        private EntityQuery m_connectionQuery;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private CommandSendSystemGroup m_SendGroup;
        private GhostStatsCollectionSystem m_GhostStatsCollectionSystem;
        private NativeArray<uint> m_NetStats;
#endif

        protected override void OnCreate()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_SendGroup = World.GetExistingSystem<CommandSendSystemGroup>();
            m_GhostStatsCollectionSystem = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
            m_NetStats = new NativeArray<uint>(2, Allocator.Persistent);
#endif
            m_ReceiveSystem = World.GetOrCreateSystem<NetworkStreamReceiveSystem>();
            m_ClientSimulationSystemGroup = World.GetOrCreateSystem<ClientSimulationSystemGroup>();
            m_CompressionModel = new NetworkCompressionModel(Allocator.Persistent);

            m_NetDebugSystem = World.GetOrCreateSystem<NetDebugSystem>();

            RequireSingletonForUpdate<GhostCollection>();
            RequireForUpdate(m_connectionQuery);
        }

        protected override void OnDestroy()
        {
            m_CompressionModel.Dispose();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_NetStats.Dispose();
#endif
        }

        protected unsafe override void OnUpdate()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_NetStats[0] != 0)
            {
                m_GhostStatsCollectionSystem.AddCommandStats(m_NetStats);
                m_NetStats[0] = 0;
                m_NetStats[1] = 0;
            }
#endif
            NetworkDriver.Concurrent driver = m_ReceiveSystem.ConcurrentDriver;
            NetworkPipeline unreliablePipeline = m_ReceiveSystem.UnreliablePipeline;
            var targetTick = m_ClientSimulationSystemGroup.ServerTick;
            if (m_ClientSimulationSystemGroup.ServerTickFraction < 1)
                --targetTick;
            // The time left util interpolation is at the given tick, the delta should be increased by this
            var subTickDeltaAdjust = 1 - m_ClientSimulationSystemGroup.InterpolationTickFraction;
            // The time left util we are actually at the server tick, the delta should be reduced by this
            subTickDeltaAdjust -= 1 - m_ClientSimulationSystemGroup.ServerTickFraction;
            var interpolationDelay = m_ClientSimulationSystemGroup.ServerTick -
                                     m_ClientSimulationSystemGroup.InterpolationTick;
            if (subTickDeltaAdjust >= 1)
                ++interpolationDelay;
            else if (subTickDeltaAdjust < 0)
                --interpolationDelay;
            var localTime = NetworkTimeSystem.TimestampMS;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var netStats = m_NetStats;
#endif
            var numLoadedPrefabs = GetSingleton<GhostCollection>().NumLoadedPrefabs;
            var netDebug = m_NetDebugSystem.NetDebug;
            var inputTargetTick = targetTick;
            Dependency = JobHandle.CombineDependencies(Dependency, m_ReceiveSystem.LastDriverWriter);
            Entities
                .WithName("CommandSendPacket")
                .WithStoreEntityQueryInField(ref m_connectionQuery)
                .WithNone<NetworkStreamDisconnected>()
                .WithAll<NetworkStreamInGame>()
                .ForEach((DynamicBuffer<OutgoingCommandDataStreamBufferComponent> rpcData, in NetworkStreamConnection connection, in NetworkSnapshotAckComponent ack) => {
                // FIXME: support fragmented
                if (driver.BeginSend(unreliablePipeline, connection.Value, out var writer) != 0)
                {
                    rpcData.Clear();
                    return;
                }

                writer.WriteByte((byte) NetworkStreamProtocol.Command);
                writer.WriteUInt(ack.LastReceivedSnapshotByLocal);
                writer.WriteUInt(ack.ReceivedSnapshotByLocalMask);
                writer.WriteUInt(localTime);

                uint returnTime = ack.LastReceivedRemoteTime;
                if (returnTime != 0)
                    returnTime += (localTime - ack.LastReceiveTimestamp);

                writer.WriteUInt(returnTime);
                writer.WriteUInt(interpolationDelay);
                writer.WriteUInt((uint)numLoadedPrefabs);
                writer.WriteUInt(inputTargetTick);

                writer.WriteBytes((byte*) rpcData.GetUnsafeReadOnlyPtr(), rpcData.Length);
                rpcData.Clear();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netStats[0] = inputTargetTick;
                netStats[1] = (uint)writer.Length;
#endif

                var result = 0;
                if ((result = driver.EndSend(writer)) <= 0)
                    netDebug.LogError(FixedString.Format("An error occured during EndSend. ErrorCode: {0}", result));
            }).Schedule();
            Dependency = m_ReceiveSystem.Driver.ScheduleFlushSend(Dependency);
            m_ReceiveSystem.LastDriverWriter = Dependency;
        }
    }

    [UpdateInGroup(typeof(CommandSendSystemGroup))]
    public abstract partial class CommandSendSystem<TCommandDataSerializer, TCommandData> : SystemBase
        where TCommandData : struct, ICommandData
        where TCommandDataSerializer : struct, ICommandDataSerializer<TCommandData>
    {
        public const uint k_InputBufferSendSize = 4;

        protected struct SendJobData
        {
            [ReadOnly] public ComponentTypeHandle<CommandTargetComponent> commmandTargetType;
            [ReadOnly] public ComponentTypeHandle<NetworkIdComponent> networkIdType;
            public BufferTypeHandle<OutgoingCommandDataStreamBufferComponent> outgoingCommandBufferType;
            [ReadOnly] public BufferFromEntity<TCommandData> inputFromEntity;
            [ReadOnly] public ComponentDataFromEntity<GhostComponent> ghostFromEntity;
            [ReadOnly] public ComponentDataFromEntity<GhostOwnerComponent> ghostOwnerFromEntity;
            [ReadOnly] public ComponentDataFromEntity<AutoCommandTarget> autoCommandTargetFromEntity;
            public NetworkCompressionModel compressionModel;
            public uint inputTargetTick;
            public uint prevInputTargetTick;
            [ReadOnly][DeallocateOnJobCompletion] public NativeArray<Entity> autoCommandTargetEntities;
            public ulong stableHash;

            void Serialize(DynamicBuffer<OutgoingCommandDataStreamBufferComponent> rpcData, Entity targetEntity, bool isAutoTarget)
            {
                var input = inputFromEntity[targetEntity];
                TCommandData baselineInputData;
                // Check if the buffer has any data for the ticks we are trying to send, first chck if it has data at all
                if (!input.GetDataAtTick(inputTargetTick, out baselineInputData))
                    return;
                // Next check if we have previously sent the latest input, and the latest data we have would not fit in the buffer
                // The check for previously sent is important to handle really bad client performance
                if (prevInputTargetTick != 0 && !SequenceHelpers.IsNewer(baselineInputData.Tick, prevInputTargetTick) && (inputTargetTick - baselineInputData.Tick) >= k_InputBufferSendSize)
                    return;

                var oldLen = rpcData.Length;
                rpcData.ResizeUninitialized(oldLen + 1024);
                var writer = new DataStreamWriter(rpcData.Reinterpret<byte>().AsNativeArray().GetSubArray(oldLen, 1024));

                writer.WriteULong(stableHash);
                var lengthWriter = writer;
                writer.WriteUShort(0);
                var startLength = writer.Length;
                if (isAutoTarget)
                {
                    var ghostComponent = ghostFromEntity[targetEntity];
                    writer.WriteInt(ghostComponent.ghostId);
                    writer.WriteUInt(ghostComponent.spawnTick);
                }
                else
                {
                    writer.WriteInt(0);
                    writer.WriteUInt(0);
                }

                var serializerState = new RpcSerializerState {GhostFromEntity = ghostFromEntity};
                var serializer = default(TCommandDataSerializer);
                serializer.Serialize(ref writer, serializerState, baselineInputData);
                for (uint inputIndex = 1; inputIndex < k_InputBufferSendSize; ++inputIndex)
                {
                    TCommandData inputData;
                    uint targetTick = inputTargetTick - inputIndex;
                    input.GetDataAtTick(targetTick, out inputData);
                    serializer.Serialize(ref writer, serializerState, inputData, baselineInputData, compressionModel);
                }

                writer.Flush();
                lengthWriter.WriteUShort((ushort)(writer.Length - startLength));
                rpcData.ResizeUninitialized(oldLen + writer.Length);
            }

            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                var commandTargets = chunk.GetNativeArray(commmandTargetType);
                var networkIds = chunk.GetNativeArray(networkIdType);
                var rpcDatas = chunk.GetBufferAccessor(outgoingCommandBufferType);

                for (int i = 0; i < chunk.Count; ++i)
                {
                    var targetEntity = commandTargets[i].targetEntity;
                    var owner = networkIds[i].Value;
                    bool sentTarget = false;
                    for (int ent = 0; ent < autoCommandTargetEntities.Length; ++ent)
                    {
                        var autoTarget = autoCommandTargetEntities[ent];
                        if (ghostOwnerFromEntity[autoTarget].NetworkId == owner && autoCommandTargetFromEntity[autoTarget].Enabled)
                        {
                            Serialize(rpcDatas[i], autoTarget, true);
                            sentTarget |= (autoTarget == targetEntity);
                        }
                    }
                    if (!sentTarget && inputFromEntity.HasComponent(targetEntity))
                        Serialize(rpcDatas[i], targetEntity, false);
                }
            }
        }

        private EntityQuery m_connectionQuery;
        private EntityQuery m_autoTargetQuery;
        private NetworkStreamReceiveSystem m_ReceiveSystem;
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private NetworkCompressionModel m_CompressionModel;
        private uint m_PrevInputTargetTick;

        protected override void OnCreate()
        {
            m_ReceiveSystem = World.GetOrCreateSystem<NetworkStreamReceiveSystem>();
            m_ClientSimulationSystemGroup = World.GetOrCreateSystem<ClientSimulationSystemGroup>();
            m_CompressionModel = new NetworkCompressionModel(Allocator.Persistent);
            m_connectionQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<CommandTargetComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());
            m_autoTargetQuery = GetEntityQuery(ComponentType.ReadOnly<GhostComponent>(),
                ComponentType.ReadOnly<GhostOwnerComponent>(),
                ComponentType.ReadOnly<PredictedGhostComponent>(),
                ComponentType.ReadOnly<TCommandData>(),
                ComponentType.ReadOnly<AutoCommandTarget>());
            RequireForUpdate(m_connectionQuery);
            RequireForUpdate(GetEntityQuery(ComponentType.ReadOnly<TCommandData>()));
            RequireSingletonForUpdate<GhostCollection>();
        }

        protected override void OnDestroy()
        {
            m_CompressionModel.Dispose();
        }

        protected SendJobData InitJobData()
        {
            var targetTick = m_ClientSimulationSystemGroup.ServerTick;
            if (m_ClientSimulationSystemGroup.ServerTickFraction < 1)
                --targetTick;
            var sendJob = new SendJobData
            {
                commmandTargetType = GetComponentTypeHandle<CommandTargetComponent>(true),
                networkIdType = GetComponentTypeHandle<NetworkIdComponent>(true),
                outgoingCommandBufferType = GetBufferTypeHandle<OutgoingCommandDataStreamBufferComponent>(),
                inputFromEntity = GetBufferFromEntity<TCommandData>(true),
                ghostFromEntity = GetComponentDataFromEntity<GhostComponent>(true),
                ghostOwnerFromEntity = GetComponentDataFromEntity<GhostOwnerComponent>(true),
                autoCommandTargetFromEntity = GetComponentDataFromEntity<AutoCommandTarget>(true),
                compressionModel = m_CompressionModel,
                inputTargetTick = targetTick,
                prevInputTargetTick = m_PrevInputTargetTick,
                autoCommandTargetEntities = m_autoTargetQuery.ToEntityArrayAsync(Allocator.TempJob, out var autoHandle),
                stableHash = TypeManager.GetTypeInfo<TCommandData>().StableTypeHash
            };
            m_PrevInputTargetTick = targetTick;
            Dependency = JobHandle.CombineDependencies(Dependency, autoHandle);
            return sendJob;
        }
        protected void ScheduleJobData<T>(in T sendJob) where T: struct, IJobEntityBatch
        {
            Dependency = sendJob.Schedule(m_connectionQuery, Dependency);
        }

        protected bool ShouldRunCommandJob()
        {
            // If there are auto command target entities always run the job
            if (!m_autoTargetQuery.IsEmptyIgnoreFilter)
                return true;
            // Otherwise only run if CommandTarget exists and has this component type
            if (!TryGetSingleton<CommandTargetComponent>(out var commandTarget))
                return false;
            if (!EntityManager.HasComponent<TCommandData>(commandTarget.targetEntity))
                return false;

            return true;
        }
    }
}

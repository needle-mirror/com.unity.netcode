using System.Diagnostics;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Collections;

namespace Unity.NetCode
{
    [UpdateInWorld(TargetWorld.Server)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateAfter(typeof(NetworkStreamReceiveSystem))]
    public class CommandReceiveSystemGroup : ComponentSystemGroup
    {
    }

    [UpdateInGroup(typeof(CommandReceiveSystemGroup), OrderLast = true)]
    public partial class CommandReceiveClearSystem : SystemBase
    {
        ServerSimulationSystemGroup m_ServerSimulationSystemGroup;
        protected override void OnCreate()
        {
            m_ServerSimulationSystemGroup = World.GetExistingSystem<ServerSimulationSystemGroup>();
        }
        protected override void OnUpdate()
        {
            var currentTick = m_ServerSimulationSystemGroup.ServerTick;
            Entities.ForEach((DynamicBuffer<IncomingCommandDataStreamBufferComponent> buffer, ref NetworkSnapshotAckComponent snapshotAck) => {
                buffer.Clear();
                if (snapshotAck.LastReceivedSnapshotByLocal != 0)
                {
                    int age = (int) (currentTick - snapshotAck.LastReceivedSnapshotByLocal);
                    age *= 256;
                    snapshotAck.ServerCommandAge = (snapshotAck.ServerCommandAge * 7 + age) / 8;
                }
            }).ScheduleParallel();
        }
    }


    [UpdateInGroup(typeof(CommandReceiveSystemGroup))]
    public abstract partial class CommandReceiveSystem<TCommandDataSerializer, TCommandData> : SystemBase
        where TCommandData : struct, ICommandData
        where TCommandDataSerializer : struct, ICommandDataSerializer<TCommandData>
    {
        protected struct ReceiveJobData
        {
            public BufferFromEntity<TCommandData> commandData;
            public ComponentDataFromEntity<CommandDataInterpolationDelay> delayFromEntity;
            [ReadOnly] public ComponentDataFromEntity<GhostOwnerComponent> ghostOwnerFromEntity;
            [ReadOnly] public ComponentDataFromEntity<AutoCommandTarget> autoCommandTargetFromEntity;
            public NetworkCompressionModel compressionModel;
            [ReadOnly] public BufferTypeHandle<IncomingCommandDataStreamBufferComponent> cmdBufferType;
            [ReadOnly] public ComponentTypeHandle<NetworkSnapshotAckComponent> snapshotAckType;
            [ReadOnly] public ComponentTypeHandle<NetworkIdComponent> networkIdType;
            [ReadOnly] public ComponentTypeHandle<CommandTargetComponent> commmandTargetType;
            [ReadOnly] public NativeParallelHashMap<SpawnedGhost, Entity> ghostMap;

            public uint serverTick;
            public NetDebug netDebug;
            public ulong stableHash;

            public unsafe void Deserialize(ref DataStreamReader reader, Entity targetEntity,
                uint tick, in NetworkSnapshotAckComponent snapshotAck)
            {
                if (delayFromEntity.HasComponent(targetEntity))
                    delayFromEntity[targetEntity] = new CommandDataInterpolationDelay{ Delay = snapshotAck.RemoteInterpolationDelay};

                var deserializeState = new RpcDeserializerState {ghostMap = ghostMap};
                var buffers = new NativeArray<TCommandData>((int)CommandSendSystem<TCommandDataSerializer, TCommandData>.k_InputBufferSendSize, Allocator.Temp);
                var command = commandData[targetEntity];
                var baselineReceivedCommand = default(TCommandData);
                var serializer = default(TCommandDataSerializer);
                baselineReceivedCommand.Tick = tick;
                serializer.Deserialize(ref reader, deserializeState, ref baselineReceivedCommand);
                // Store received commands in the network command buffer
                buffers[0] = baselineReceivedCommand;
                var inputBufferSendSize = CommandSendSystem<TCommandDataSerializer, TCommandData>.k_InputBufferSendSize;
                for (uint i = 1; i < inputBufferSendSize; ++i)
                {
                    var receivedCommand = default(TCommandData);
                    receivedCommand.Tick = tick - i;
                    serializer.Deserialize(ref reader, deserializeState, ref receivedCommand, baselineReceivedCommand,
                        compressionModel);
                    // Store received commands in the network command buffer
                    buffers[(int)i] = receivedCommand;
                }
                // Add the command in the order they were produces instead of the order they were sent
                for (int i = (int)inputBufferSendSize - 1; i >= 0; --i)
                {
                    if (!SequenceHelpers.IsNewer(serverTick, buffers[i].Tick))
                        command.AddCommandData(buffers[i]);
                    else if (i == 0)
                    {
                        // This is a special case, since this is the latest tick we have for the current server tick
                        // it must be stored somehow. Trying to get the data for previous tick also needs to return
                        // what we actually used previous tick. So we fake the tick of the most recent input we got
                        // to point at the current server tick, even though it was actually for a tick we already
                        // simulated
                        var input = buffers[0];
                        input.Tick = serverTick;
                        command.AddCommandData(input);
                    }
                }
            }
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                var snapshotAcks = chunk.GetNativeArray(snapshotAckType);
                var networkIds = chunk.GetNativeArray(networkIdType);
                var commandTargets = chunk.GetNativeArray(commmandTargetType);
                var cmdBuffers = chunk.GetBufferAccessor(cmdBufferType);

                for (int i = 0; i < chunk.Count; ++i)
                {
                    var owner = networkIds[i].Value;
                    var snapshotAck = snapshotAcks[i];
                    var buffer = cmdBuffers[i];
                    if (buffer.Length < 4)
                        continue;
                    DataStreamReader reader = buffer.AsDataStreamReader();
                    var tick = reader.ReadUInt();
                    while (reader.GetBytesRead() + 10 <= reader.Length)
                    {
                        var hash = reader.ReadULong();
                        var len = reader.ReadUShort();
                        var startPos = reader.GetBytesRead();
                        if (hash == stableHash)
                        {
                            // Read ghost id
                            var ghostId = reader.ReadInt();
                            var spawnTick = reader.ReadUInt();
                            var targetEntity = commandTargets[i].targetEntity;
                            if (ghostId != 0)
                            {
                                targetEntity = Entity.Null;
                                if (ghostMap.TryGetValue(new SpawnedGhost{ghostId = ghostId, spawnTick = spawnTick}, out var ghostEnt))
                                {
                                    if (ghostOwnerFromEntity.HasComponent(ghostEnt) && autoCommandTargetFromEntity.HasComponent(ghostEnt) &&
                                        ghostOwnerFromEntity[ghostEnt].NetworkId == owner && autoCommandTargetFromEntity[ghostEnt].Enabled)
                                        targetEntity = ghostEnt;
                                }
                            }
                            if (commandData.HasComponent(targetEntity))
                            {
                                Deserialize(ref reader, targetEntity, tick, snapshotAck);
                            }
                        }
                        reader.SeekSet(startPos + len);
                    }
                }
            }
        }

        private EntityQuery m_entityQuery;
        private ServerSimulationSystemGroup serverSimulationSystemGroup;
        private GhostSimulationSystemGroup m_GhostSimulationGroup;
        private NetworkCompressionModel m_CompressionModel;
        private NetDebugSystem m_NetDebugSystem;

        protected override void OnCreate()
        {
            serverSimulationSystemGroup = World.GetExistingSystem<ServerSimulationSystemGroup>();
            m_GhostSimulationGroup = World.GetExistingSystem<GhostSimulationSystemGroup>();
            m_CompressionModel = new NetworkCompressionModel(Allocator.Persistent);
            m_NetDebugSystem = World.GetExistingSystem<NetDebugSystem>();
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

            RequireForUpdate(EntityManager.CreateEntityQuery(ComponentType.ReadWrite<TCommandData>()));
        }

        protected override void OnDestroy()
        {
            m_CompressionModel.Dispose();
        }

        protected ReceiveJobData InitJobData()
        {
            var recvJob = new ReceiveJobData
            {
                commandData = GetBufferFromEntity<TCommandData>(),
                delayFromEntity = GetComponentDataFromEntity<CommandDataInterpolationDelay>(),
                ghostOwnerFromEntity = GetComponentDataFromEntity<GhostOwnerComponent>(true),
                autoCommandTargetFromEntity = GetComponentDataFromEntity<AutoCommandTarget>(true),
                compressionModel = m_CompressionModel,
                cmdBufferType = GetBufferTypeHandle<IncomingCommandDataStreamBufferComponent>(true),
                snapshotAckType = GetComponentTypeHandle<NetworkSnapshotAckComponent>(true),
                networkIdType = GetComponentTypeHandle<NetworkIdComponent>(true),
                commmandTargetType = GetComponentTypeHandle<CommandTargetComponent>(true),
                ghostMap = m_GhostSimulationGroup.SpawnedGhostEntityMap,
                serverTick = serverSimulationSystemGroup.ServerTick,
                netDebug = m_NetDebugSystem.NetDebug,
                stableHash = TypeManager.GetTypeInfo<TCommandData>().StableTypeHash
            };
            return recvJob;
        }
        protected void ScheduleJobData<T>(in T recvJob) where T: struct, IJobEntityBatch
        {
            Dependency = JobHandle.CombineDependencies(Dependency, m_GhostSimulationGroup.LastGhostMapWriter);
            Dependency = recvJob.Schedule(m_entityQuery, Dependency);
            m_GhostSimulationGroup.LastGhostMapWriter = Dependency;
        }
    }
}

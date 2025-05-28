#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.Collections;
using UnityEngine.Assertions;

namespace Unity.NetCode.Tests
{
    [BurstCompile]
    internal struct SimpleRpcCommand : IComponentData, IRpcCommandSerializer<SimpleRpcCommand>
    {
        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in SimpleRpcCommand data)
        {
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref SimpleRpcCommand data)
        {
        }

        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            RpcExecutor.ExecuteCreateRequestComponent<SimpleRpcCommand, SimpleRpcCommand>(ref parameters);
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    [BurstCompile]
    internal struct SerializedRpcCommand : IComponentData, IRpcCommandSerializer<SerializedRpcCommand>
    {
        public int intValue;
        public short shortValue;
        public float floatValue;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in SerializedRpcCommand data)
        {
            writer.WriteInt(data.intValue);
            writer.WriteShort(data.shortValue);
            writer.WriteFloat(data.floatValue);
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref SerializedRpcCommand data)
        {
            data.intValue = reader.ReadInt();
            data.shortValue = reader.ReadShort();
            data.floatValue = reader.ReadFloat();
        }

        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var serializedData = default(SerializedRpcCommand);
            serializedData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref serializedData);

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity,
                new ReceiveRpcCommandRequest {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, serializedData);
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    internal struct SerializedSmallRpcCommand : IRpcCommand
    {
        public long Value;
    }

    [BurstCompile]
    internal struct SerializedLargeRpcCommand : IComponentData, IRpcCommandSerializer<SerializedLargeRpcCommand>
    {
        public FixedString512Bytes stringValue;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in SerializedLargeRpcCommand data)
        {
            writer.WriteFixedString512(data.stringValue);
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref SerializedLargeRpcCommand data)
        {
            data.stringValue = reader.ReadFixedString512();
        }

        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var serializedData = default(SerializedLargeRpcCommand);
            serializedData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref serializedData);

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity,
                new ReceiveRpcCommandRequest {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, serializedData);
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    [BurstCompile]
    internal struct SerializedTooBigCommand : IRpcCommand
    {
        public FixedBytes4094 bytes0;
        public FixedBytes4094 bytes1;
        public FixedBytes126 bytes3;
    }

    [BurstCompile]
    internal struct IncorrectDeserializationCommand : IComponentData, IRpcCommandSerializer<IncorrectDeserializationCommand>
    {
        internal enum IncorrectMode : byte
        {
            DeserializeTooManyBytes = 1,
            DeserializeTooFewBytes = 2,
        }
        public IncorrectMode mode;
        public int bytes;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in IncorrectDeserializationCommand data)
        {
            writer.WriteByte((byte)data.mode);
            writer.WriteInt(data.bytes);
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref IncorrectDeserializationCommand data)
        {
            data.mode = (IncorrectMode) reader.ReadByte();
            switch (data.mode)
            {
                case IncorrectMode.DeserializeTooManyBytes:
                    data.bytes = (int) reader.ReadULong();
                    break;
                case IncorrectMode.DeserializeTooFewBytes:
                    data.bytes = reader.ReadByte();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, nameof(Deserialize));
            }
        }

        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var serializedData = default(IncorrectDeserializationCommand);
            serializedData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref serializedData);

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity,
                new ReceiveRpcCommandRequest {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, serializedData);
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    [BurstCompile]
    internal struct ClientIdRpcCommand : IComponentData, IRpcCommandSerializer<ClientIdRpcCommand>
    {
        public int Id;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in ClientIdRpcCommand data)
        {
            writer.WriteInt(data.Id);
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref ClientIdRpcCommand data)
        {
            data.Id = reader.ReadInt();
        }

        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var serializedData = default(ClientIdRpcCommand);
            serializedData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref serializedData);

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity,
                new ReceiveRpcCommandRequest {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, serializedData);
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    [BurstCompile]
    internal struct VariableSizedRpc : IComponentData, IRpcCommandSerializer<VariableSizedRpc>
    {
        public const int Value1Multiplier = 86;
        public const int Value1Baseline = 5;
        public const int Value2Multiplier = 1_000;
        public const int Value2Baseline = 10_000;
        public const int Value3Multiplier = -152;
        public const int Value3Baseline = -152;
        public int Value1;
        public int Value2;
        public int Value3;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in VariableSizedRpc data)
        {
            writer.WriteRawBits(1, 1);
            writer.WritePackedIntDelta(data.Value1, Value1Baseline, state.CompressionModel);
            writer.WritePackedIntDelta(data.Value2, Value2Baseline, state.CompressionModel);
            writer.WritePackedIntDelta(data.Value3, Value3Baseline, state.CompressionModel);
            writer.WriteRawBits(1, 1);
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref VariableSizedRpc data)
        {
            Assert.IsTrue(reader.ReadRawBits(1) == 1, "Sanity bit BEFORE");
            data.Value1 = reader.ReadPackedIntDelta(Value1Baseline, state.CompressionModel);
            data.Value2 = reader.ReadPackedIntDelta(Value2Baseline, state.CompressionModel);
            data.Value3 = reader.ReadPackedIntDelta(Value3Baseline, state.CompressionModel);
            Assert.IsTrue(reader.ReadRawBits(1) == 1, "Sanity bit AFTER");
        }

        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var serializedData = default(VariableSizedRpc);
            serializedData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref serializedData);

            Assert.AreEqual(serializedData.Value1, Value1Multiplier * RpcTests.VariableSizedResultCnt.Data);
            Assert.AreEqual(serializedData.Value2, Value2Multiplier * RpcTests.VariableSizedResultCnt.Data);
            Assert.AreEqual(serializedData.Value3, Value3Multiplier * RpcTests.VariableSizedResultCnt.Data);
            RpcTests.VariableSizedResultCnt.Data++;
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    internal struct InvalidRpcCommand : IComponentData, IRpcCommandSerializer<InvalidRpcCommand>
    {
        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in InvalidRpcCommand data)
        {
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref InvalidRpcCommand data)
        {
        }

        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>();
        }
    }

    [BurstCompile]
    internal struct RpcWithEntity : IComponentData, IRpcCommandSerializer<RpcWithEntity>
    {
        public Entity entity;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in Unity.NetCode.Tests.RpcWithEntity data)
        {
            if (state.GhostFromEntity.HasComponent(data.entity))
            {
                var ghostComponent = state.GhostFromEntity[data.entity];
                writer.WriteInt(ghostComponent.ghostId);
                writer.WriteUInt(ghostComponent.spawnTick.SerializedData);
            }
            else
            {
                writer.WriteInt(0);
                writer.WriteUInt(NetworkTick.Invalid.SerializedData);
            }
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state,  ref RpcWithEntity data)
        {
            var ghostId = reader.ReadInt();
            var spawnTick = new NetworkTick{SerializedData = reader.ReadUInt()};
            data.entity = Entity.Null;
            if (ghostId != 0 && spawnTick.IsValid)
            {
                if (state.ghostMap.TryGetValue(new SpawnedGhost{ghostId = ghostId, spawnTick = spawnTick}, out var ghostEnt))
                    data.entity = ghostEnt;
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            RpcExecutor.ExecuteCreateRequestComponent<RpcWithEntity, RpcWithEntity>(ref parameters);
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }

    [BurstCompile]
    internal struct VeryLargeRPC : IRpcCommand
    {
        public FixedString512Bytes value;
        public FixedString512Bytes value1;
    }

    #region Send Systems
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class ClientRcpSendSystem : SystemBase
    {
        public static int SendCount = 0;
        public Entity Remote = Entity.Null;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkId>();
        }

        protected override void OnUpdate()
        {
            if (SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, new SimpleRpcCommand());
                EntityManager.AddComponentData(req, new SendRpcCommandRequest {TargetConnection = Remote});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class ServerRpcBroadcastSendSystem : SystemBase
    {
        public static int SendCount = 0;

        protected override void OnUpdate()
        {
            if (SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, new SimpleRpcCommand());
                EntityManager.AddComponentData(req,
                    new SendRpcCommandRequest {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class MalformedClientRcpSendSystem : SystemBase
    {
        public static int[] SendCount = new int[2];
        public static ClientIdRpcCommand[] Cmds = new ClientIdRpcCommand[2];

        private int worldId;

        protected override void OnCreate()
        {
            //This is the most correct and best practice to use on the client side.
            //However, it still does not catch the issue when a client enqueue an rpc in the same frame we tag the connection
            //as RequestDisconnected (enqued in the command buffer)
            //Even if we would tag the connection synchronously (in the middle of the frame)
            //if the client system is schedule to execute AFTER the RpcCommandRequestSystem (or the RpcSystem) or the system that
            //change the connection state, clients can still queue commands even though the connection will be closed.
            RequireForUpdate<NetworkId>();
            worldId = NetCodeTestWorld.CalculateWorldId(World);
        }

        protected override void OnUpdate()
        {
            if (SendCount[worldId] > 0)
            {
                var entity = SystemAPI.GetSingletonEntity<NetworkId>();
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, Cmds[worldId]);
                EntityManager.AddComponentData(req, new SendRpcCommandRequest {TargetConnection = Entity.Null});
                --SendCount[worldId];
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class SerializedServerRcpSendSystem : SystemBase
    {
        public static int SendCount = 0;
        public static SerializedRpcCommand Cmd;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkId>();
        }

        protected override void OnUpdate()
        {
            if (SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, Cmd);
                EntityManager.AddComponentData(req,
                    new SendRpcCommandRequest {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class SerializedClientRcpSendSystem : SystemBase
    {
        public static int SendCount = 0;
        public static SerializedRpcCommand Cmd;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkId>();
        }

        protected override void OnUpdate()
        {
            if (SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, Cmd);
                EntityManager.AddComponentData(req,
                    new SendRpcCommandRequest {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class SerializedClientLargeRcpSendSystem : SystemBase
    {
        public static int SendCount = 0;
        public static SerializedLargeRpcCommand LargeCmd;
        public static SerializedSmallRpcCommand SmallCmd;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkId>();
        }

        protected override void OnUpdate()
        {
            while (SendCount > 0)
            {
                var reqLarge = EntityManager.CreateEntity();
                EntityManager.AddComponentData(reqLarge, LargeCmd);
                EntityManager.AddComponentData(reqLarge, new SendRpcCommandRequest {TargetConnection = Entity.Null});
                var reqSmall = EntityManager.CreateEntity();
                EntityManager.AddComponentData(reqSmall, SmallCmd);
                EntityManager.AddComponentData(reqSmall, new SendRpcCommandRequest {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class FlawedClientRcpSendSystem : SystemBase
    {
        public static int SendCount = 0;

        protected override void OnUpdate()
        {
            if (SystemAPI.HasSingleton<NetworkStreamConnection>() && !SystemAPI.HasSingleton<NetworkId>() && SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, default(SimpleRpcCommand));
                EntityManager.AddComponentData(req,
                    new SendRpcCommandRequest {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }
    #endregion

    #region Receive Systems
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class ServerMultipleRpcReceiveSystem : SystemBase
    {
        public static int[] ReceivedCount = new int[2];

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            Entities.WithoutBurst().ForEach((Entity entity, ref ClientIdRpcCommand cmd, ref ReceiveRpcCommandRequest req) =>
            {
                PostUpdateCommands.DestroyEntity(entity);
                if (cmd.Id >= 0 && cmd.Id < 2)
                    ReceivedCount[cmd.Id]++;
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }


    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class MultipleClientBroadcastRpcReceiveSystem : SystemBase
    {
        public static int[] ReceivedCount = new int[2];

        private int worldId;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkId>();
            worldId = NetCodeTestWorld.CalculateWorldId(World);
        }

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            var currentWorldId = worldId;
            Entities.WithoutBurst()
                .WithAll<SimpleRpcCommand>()
                .ForEach((Entity entity, ref ReceiveRpcCommandRequest req) =>
            {
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount[currentWorldId];
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class ServerRpcReceiveSystem : SystemBase
    {
        public static int ReceivedCount = 0;

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            var networkConnections = GetComponentLookup<NetworkStreamConnection>(true);
            Entities.WithoutBurst()
                .WithAll<SimpleRpcCommand>()
                .ForEach((Entity entity, ref ReceiveRpcCommandRequest req) =>
            {
                Assert.IsTrue(networkConnections.HasComponent(req.SourceConnection), "Connection has been deleted and this RPC should not have been triggered");
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount;
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class ClientRpcReceiveSystem : SystemBase
    {
        public int ReceivedCount = 0;

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            Entities.WithoutBurst()
                .WithAll<SimpleRpcCommand>()
                .ForEach((Entity entity, ref ReceiveRpcCommandRequest req) =>
                {
                    PostUpdateCommands.DestroyEntity(entity);
                    ++ReceivedCount;
                }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class SerializedClientRpcReceiveSystem : SystemBase
    {
        public static int ReceivedCount = 0;
        public static SerializedRpcCommand ReceivedCmd;

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            Entities.WithoutBurst().ForEach((Entity entity, ref SerializedRpcCommand cmd, ref ReceiveRpcCommandRequest req) =>
            {
                ReceivedCmd = cmd;
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount;
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class SerializedServerRpcReceiveSystem : SystemBase
    {
        public static int ReceivedCount = 0;
        public static SerializedRpcCommand ReceivedCmd;

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            Entities.WithoutBurst().ForEach((Entity entity, ref SerializedRpcCommand cmd, ref ReceiveRpcCommandRequest req) =>
            {
                ReceivedCmd = cmd;
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount;
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class SerializedServerLargeRpcReceiveSystem : SystemBase
    {
        public static int ReceivedLargeCount = 0;
        public static int ReceivedSmallCount = 0;
        // Test multiple RPCs being sent.
        public static SerializedLargeRpcCommand ReceivedLargeCmd;
        public static SerializedSmallRpcCommand ReceivedSmallCmd;

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            Entities.WithoutBurst().ForEach((Entity entity, ref SerializedLargeRpcCommand cmd, ref ReceiveRpcCommandRequest req) =>
            {
                ReceivedLargeCmd = cmd;
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedLargeCount;
            }).Run();
            Entities.WithoutBurst().ForEach((Entity entity, ref SerializedSmallRpcCommand cmd, ref ReceiveRpcCommandRequest req) =>
            {
                ReceivedSmallCmd = cmd;
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedSmallCount;
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }
    #endregion

    [DisableAutoCreation]
    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    partial struct SerializedLargeRpcCommandRequestSystem : ISystem
    {
        RpcCommandRequest<SerializedLargeRpcCommand, SerializedLargeRpcCommand> m_Request;
        [BurstCompile]
        struct SendRpc : IJobChunk
        {
            public RpcCommandRequest<SerializedLargeRpcCommand, SerializedLargeRpcCommand>.SendRpcData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        public void OnCreate(ref SystemState state)
        {
            m_Request.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var sendJob = new SendRpc{data = m_Request.InitJobData(ref state)};
            state.Dependency = sendJob.Schedule(m_Request.Query, state.Dependency);
        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    partial struct IncorrectDeserializationCommandRequestSystem : ISystem
    {
        RpcCommandRequest<IncorrectDeserializationCommand, IncorrectDeserializationCommand> m_Request;
        [BurstCompile]
        struct SendRpc : IJobChunk
        {
            public RpcCommandRequest<IncorrectDeserializationCommand, IncorrectDeserializationCommand>.SendRpcData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        public void OnCreate(ref SystemState state)
        {
            m_Request.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var sendJob = new SendRpc{data = m_Request.InitJobData(ref state)};
            state.Dependency = sendJob.Schedule(m_Request.Query, state.Dependency);
        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    partial struct SerializedRpcCommandRequestSystem : ISystem
    {
        RpcCommandRequest<SerializedRpcCommand, SerializedRpcCommand> m_Request;
        [BurstCompile]
        struct SendRpc : IJobChunk
        {
            public RpcCommandRequest<SerializedRpcCommand, SerializedRpcCommand>.SendRpcData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        public void OnCreate(ref SystemState state)
        {
            m_Request.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var sendJob = new SendRpc{data = m_Request.InitJobData(ref state)};
            state.Dependency = sendJob.Schedule(m_Request.Query, state.Dependency);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    partial struct NonSerializedRpcCommandRequestSystem : ISystem
    {
        RpcCommandRequest<SimpleRpcCommand, SimpleRpcCommand> m_Request;
        [BurstCompile]
        struct SendRpc : IJobChunk
        {
            public RpcCommandRequest<SimpleRpcCommand, SimpleRpcCommand>.SendRpcData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        public void OnCreate(ref SystemState state)
        {
            m_Request.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var sendJob = new SendRpc{data = m_Request.InitJobData(ref state)};
            state.Dependency = sendJob.Schedule(m_Request.Query, state.Dependency);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    partial struct MultipleClientSerializedRpcCommandRequestSystem : ISystem
    {
        RpcCommandRequest<ClientIdRpcCommand, ClientIdRpcCommand> m_Request;
        [BurstCompile]
        struct SendRpc : IJobChunk
        {
            public RpcCommandRequest<ClientIdRpcCommand, ClientIdRpcCommand>.SendRpcData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        public void OnCreate(ref SystemState state)
        {
            m_Request.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var sendJob = new SendRpc{data = m_Request.InitJobData(ref state)};
            state.Dependency = sendJob.Schedule(m_Request.Query, state.Dependency);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    partial struct InvalidRpcCommandRequestSystem : ISystem
    {
        RpcCommandRequest<InvalidRpcCommand, InvalidRpcCommand> m_Request;
        [BurstCompile]
        struct SendRpc : IJobChunk
        {
            public RpcCommandRequest<InvalidRpcCommand, InvalidRpcCommand>.SendRpcData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        public void OnCreate(ref SystemState state)
        {
            m_Request.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var sendJob = new SendRpc{data = m_Request.InitJobData(ref state)};
            state.Dependency = sendJob.Schedule(m_Request.Query, state.Dependency);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    partial struct RpcWithEntityRpcCommandRequestSystem : ISystem
    {
        RpcCommandRequest<RpcWithEntity, RpcWithEntity> m_Request;
        [BurstCompile]
        struct SendRpc : IJobChunk
        {
            public RpcCommandRequest<RpcWithEntity, RpcWithEntity>.SendRpcData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        public void OnCreate(ref SystemState state)
        {
            m_Request.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var sendJob = new SendRpc{data = m_Request.InitJobData(ref state)};
            state.Dependency = sendJob.Schedule(m_Request.Query, state.Dependency);
        }
    }

    internal struct MyApprovalRpc : IApprovalRpcCommand
    {

    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class VeryLargeRpcReceiveSystem : SystemBase
    {
        public static int ReceivedCount = 0;
        public static VeryLargeRPC ReceivedCmd;

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            Entities.WithoutBurst().ForEach((Entity entity, ref VeryLargeRPC cmd, ref ReceiveRpcCommandRequest req) =>
            {
                ReceivedCmd = cmd;
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount;
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class VeryLargeRcpSendSystem : SystemBase
    {
        public static int SendCount = 0;
        public static VeryLargeRPC Cmd;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkId>();
        }

        protected override void OnUpdate()
        {
            while (SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, Cmd);
                EntityManager.AddComponentData(req,
                    new SendRpcCommandRequest { TargetConnection = Entity.Null });
                --SendCount;
            }
        }
    }

internal struct FastReconnectRpc : IRpcCommand
    {
        public int Value;
    }

    internal struct FastReconnectApprovalRpc : IApprovalRpcCommand
    {
        public int Value;
    }

    internal struct FastReconnectRpcStartedApproval : IComponentData
    { }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkGroupCommandBufferSystem))]
    [RequireMatchingQueriesForUpdate]
    internal partial class FastReconnectRpcConnectBeforeSystem : SystemBase
    {
        public static bool ConnectNow;
        EntityQuery m_ConnectionQuery;

        protected override void OnCreate()
        {
            m_ConnectionQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
        }

        protected override void OnUpdate()
        {
            if (m_ConnectionQuery.CalculateEntityCount() > 0)
                return;
            if (ConnectNow)
            {
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(EntityManager, NetworkEndpoint.LoopbackIpv4.WithPort(7979));
                //UnityEngine.Debug.Log($"[{NetCodeTestWorld.TickIndex}]: Connect via {GetType().FullName}!");
                ConnectNow = false;
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(NetworkGroupCommandBufferSystem))]
    [RequireMatchingQueriesForUpdate]
    internal partial class FastReconnectRpcConnectAfterSystem : SystemBase
    {
        public static bool ConnectNow;
        EntityQuery m_ConnectionQuery;

        protected override void OnCreate()
        {
            m_ConnectionQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
        }

        protected override void OnUpdate()
        {
            if (m_ConnectionQuery.CalculateEntityCount() > 0)
                return;
            if (ConnectNow)
            {
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(EntityManager, NetworkEndpoint.LoopbackIpv4.WithPort(7979));
                //UnityEngine.Debug.Log($"[{NetCodeTestWorld.TickIndex}]: Connect via {GetType().FullName}!");
                ConnectNow = false;
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkGroupCommandBufferSystem))]
    [RequireMatchingQueriesForUpdate]
    internal partial class FastReconnectRpcDisconnectBeforeSystem : SystemBase
    {
        public static int DisconnectDelay = -1;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamConnection>();
        }

        protected override void OnUpdate()
        {
            if (DisconnectDelay-- == 0)
            {
                var clientConnection = SystemAPI.GetSingletonRW<NetworkStreamConnection>();
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.DriverStore.Disconnect(clientConnection.ValueRO);
                //UnityEngine.Debug.Log($"[{NetCodeTestWorld.TickIndex}]: Disconnect via {GetType().FullName}!");
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(NetworkGroupCommandBufferSystem))]
    [RequireMatchingQueriesForUpdate]
    internal partial class FastReconnectRpcDisconnectAfterSystem : SystemBase
    {
        public static int DisconnectDelay = -1;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamConnection>();
        }

        protected override void OnUpdate()
        {
            if (DisconnectDelay-- == 0)
            {
                var clientConnection = SystemAPI.GetSingletonRW<NetworkStreamConnection>();
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.DriverStore.Disconnect(clientConnection.ValueRO);
                //UnityEngine.Debug.Log($"[{NetCodeTestWorld.TickIndex}]: Disconnect via {GetType().FullName}!");
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkGroupCommandBufferSystem))]
    [RequireMatchingQueriesForUpdate]
    internal partial class SendFastReconnectRpc : SystemBase
    {
        protected override void OnUpdate()
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (id, entity) in SystemAPI.Query<RefRO<NetworkId>>().WithEntityAccess().WithNone<NetworkStreamInGame>())
            {
                commandBuffer.AddComponent<NetworkStreamInGame>(entity);
                var req = commandBuffer.CreateEntity();
                commandBuffer.AddComponent<FastReconnectRpc>(req);
                commandBuffer.AddComponent(req, new SendRpcCommandRequest { TargetConnection = entity });
            }
            commandBuffer.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [RequireMatchingQueriesForUpdate]
    internal partial class SendFastReconnectApprovalRpc : SystemBase
    {
        protected override void OnUpdate()
        {
            if (!SystemAPI.GetSingleton<NetworkStreamDriver>().RequireConnectionApproval)
            {
                Enabled = false;
                return;
            }

            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (connection, entity) in SystemAPI.Query<RefRO<NetworkStreamConnection>>().WithEntityAccess().WithNone<FastReconnectRpcStartedApproval>())
            {
                var req = commandBuffer.CreateEntity();
                commandBuffer.AddComponent<FastReconnectApprovalRpc>(req);
                commandBuffer.AddComponent(req, new SendRpcCommandRequest { TargetConnection = entity });
                commandBuffer.AddComponent<FastReconnectRpcStartedApproval>(entity);
            }
            commandBuffer.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [RequireMatchingQueriesForUpdate]
    internal partial class ReceiveFastReconnectApprovalRpc : SystemBase
    {
        protected override void OnUpdate()
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpcRequest, rpcData, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>, RefRO<FastReconnectRpc>>().WithEntityAccess())
            {
                commandBuffer.AddComponent<ConnectionApproved>(rpcRequest.ValueRO.SourceConnection);
                commandBuffer.DestroyEntity(entity);
            }
            commandBuffer.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkGroupCommandBufferSystem))]
    [RequireMatchingQueriesForUpdate]
    internal partial class ReceiveFastReconnectRpcBefore : SystemBase
    {
        protected override void OnUpdate()
        {
            var networkConnections = GetComponentLookup<NetworkStreamConnection>(true);
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpcRequest, rpcData, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>, RefRO<FastReconnectRpc>>().WithEntityAccess())
            {
                Assert.IsTrue(networkConnections.HasComponent(rpcRequest.ValueRO.SourceConnection));
                commandBuffer.DestroyEntity(entity);
            }
            commandBuffer.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(NetworkGroupCommandBufferSystem))]
    [RequireMatchingQueriesForUpdate]
    internal partial class ReceiveFastReconnectRpcAfter : SystemBase
    {
        protected override void OnUpdate()
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpcRequest, rpcData, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>, RefRO<FastReconnectRpc>>().WithEntityAccess())
            {
                // If connection is gone this will throw an exception
                commandBuffer.AddComponent<NetworkStreamInGame>(rpcRequest.ValueRO.SourceConnection);
                commandBuffer.DestroyEntity(entity);
            }
            commandBuffer.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [RequireMatchingQueriesForUpdate]
    internal partial class ReceiveFastReconnectRpc : SystemBase
    {
        protected override void OnUpdate()
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpcRequest, rpcData, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>, RefRO<FastReconnectRpc>>().WithEntityAccess())
            {
                // If connection is gone this will throw an exception
                commandBuffer.AddComponent<NetworkStreamInGame>(rpcRequest.ValueRO.SourceConnection);
                commandBuffer.DestroyEntity(entity);
            }
            commandBuffer.Playback(EntityManager);
        }
    }
}

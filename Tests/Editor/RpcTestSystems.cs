using AOT;
using Unity.Burst;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.Collections;

namespace Unity.NetCode.Tests
{
    [BurstCompile]
    public struct SimpleRpcCommand : IComponentData, IRpcCommandSerializer<SimpleRpcCommand>
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

        [BurstCompile]
        [MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            RpcExecutor.ExecuteCreateRequestComponent<SimpleRpcCommand, SimpleRpcCommand>(ref parameters);
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    [BurstCompile]
    public struct SerializedRpcCommand : IComponentData, IRpcCommandSerializer<SerializedRpcCommand>
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

        [BurstCompile]
        [MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var serializedData = default(SerializedRpcCommand);
            serializedData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref serializedData);

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity,
                new ReceiveRpcCommandRequestComponent {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, serializedData);
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    [BurstCompile]
    public struct SerializedLargeRpcCommand : IComponentData, IRpcCommandSerializer<SerializedLargeRpcCommand>
    {
        public FixedString512 stringValue;

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

        [BurstCompile]
        [MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var serializedData = default(SerializedLargeRpcCommand);
            serializedData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref serializedData);

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity,
                new ReceiveRpcCommandRequestComponent {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, serializedData);
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    [BurstCompile]
    public struct ClientIdRpcCommand : IComponentData, IRpcCommandSerializer<ClientIdRpcCommand>
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

        [BurstCompile]
        [MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            var serializedData = default(ClientIdRpcCommand);
            serializedData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref serializedData);

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity,
                new ReceiveRpcCommandRequestComponent {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, serializedData);
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    public struct InvalidRpcCommand : IComponentData, IRpcCommandSerializer<InvalidRpcCommand>
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
    public struct RpcWithEntity : IComponentData, IRpcCommandSerializer<RpcWithEntity>
    {
        public Entity entity;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in Unity.NetCode.Tests.RpcWithEntity data)
        {
            if (state.GhostFromEntity.HasComponent(data.entity))
            {
                var ghostComponent = state.GhostFromEntity[data.entity];
                writer.WriteInt(ghostComponent.ghostId);
                writer.WriteUInt(ghostComponent.spawnTick);
            }
            else
            {
                writer.WriteInt(0);
                writer.WriteUInt(0);
            }
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state,  ref RpcWithEntity data)
        {
            var ghostId = reader.ReadInt();
            var spawnTick = reader.ReadUInt();
            data.entity = Entity.Null;
            if (ghostId != 0 && spawnTick != 0)
            {
                if (state.ghostMap.TryGetValue(new SpawnedGhost{ghostId = ghostId, spawnTick = spawnTick}, out var ghostEnt))
                    data.entity = ghostEnt;
            }
        }

        [BurstCompile]
        [MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            RpcExecutor.ExecuteCreateRequestComponent<RpcWithEntity, RpcWithEntity>(ref parameters);
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }

    #region Send Systems
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class ClientRcpSendSystem : SystemBase
    {
        public static int SendCount = 0;
        public Entity Remote = Entity.Null;

        protected override void OnCreate()
        {
            RequireSingletonForUpdate<NetworkIdComponent>();
        }

        protected override void OnUpdate()
        {
            if (SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, new SimpleRpcCommand());
                EntityManager.AddComponentData(req, new SendRpcCommandRequestComponent {TargetConnection = Remote});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    public class ServerRpcBroadcastSendSystem : SystemBase
    {
        public static int SendCount = 0;

        protected override void OnUpdate()
        {
            if (SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, new SimpleRpcCommand());
                EntityManager.AddComponentData(req,
                    new SendRpcCommandRequestComponent {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class MalformedClientRcpSendSystem : SystemBase
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
            var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkIdComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>());
            RequireForUpdate(query);

            const int kStringLength = 10; // we name it ClientTest
            worldId = int.Parse(World.Name.Substring(kStringLength, World.Name.Length - kStringLength));
        }

        protected override void OnUpdate()
        {
            if (SendCount[worldId] > 0)
            {
                var entity = GetSingletonEntity<NetworkIdComponent>();
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, Cmds[worldId]);
                EntityManager.AddComponentData(req, new SendRpcCommandRequestComponent {TargetConnection = Entity.Null});
                --SendCount[worldId];
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class SerializedClientRcpSendSystem : SystemBase
    {
        public static int SendCount = 0;
        public static SerializedRpcCommand Cmd;

        protected override void OnCreate()
        {
            RequireSingletonForUpdate<NetworkIdComponent>();
        }

        protected override void OnUpdate()
        {
            if (SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, Cmd);
                EntityManager.AddComponentData(req,
                    new SendRpcCommandRequestComponent {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class SerializedClientLargeRcpSendSystem : SystemBase
    {
        public static int SendCount = 0;
        public static SerializedLargeRpcCommand Cmd;

        protected override void OnCreate()
        {
            RequireSingletonForUpdate<NetworkIdComponent>();
        }

        protected override void OnUpdate()
        {
            while (SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, Cmd);
                EntityManager.AddComponentData(req,
                    new SendRpcCommandRequestComponent {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class FlawedClientRcpSendSystem : SystemBase
    {
        public static int SendCount = 0;

        protected override void OnUpdate()
        {
            if (HasSingleton<NetworkStreamConnection>() && !HasSingleton<NetworkIdComponent>() && SendCount > 0)
            {
                var req = EntityManager.CreateEntity();
                EntityManager.AddComponentData(req, default(SimpleRpcCommand));
                EntityManager.AddComponentData(req,
                    new SendRpcCommandRequestComponent {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }
    #endregion

    #region Receive Systems
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    public class ServerMultipleRpcReceiveSystem : SystemBase
    {
        public static int[] ReceivedCount = new int[2];

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            Entities.WithoutBurst().ForEach((Entity entity, ref ClientIdRpcCommand cmd, ref ReceiveRpcCommandRequestComponent req) =>
            {
                PostUpdateCommands.DestroyEntity(entity);
                if (cmd.Id >= 0 && cmd.Id < 2)
                    ReceivedCount[cmd.Id]++;
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class MultipleClientBroadcastRpcReceiveSystem : SystemBase
    {
        public static int[] ReceivedCount = new int[2];

        private int worldId;

        protected override void OnCreate()
        {
            RequireSingletonForUpdate<NetworkIdComponent>();
            worldId = int.Parse(World.Name.Substring(World.Name.Length - 1, 1));
        }

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            var currentWorldId = worldId;
            Entities.WithoutBurst().ForEach((Entity entity, ref SimpleRpcCommand cmd, ref ReceiveRpcCommandRequestComponent req) =>
            {
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount[currentWorldId];
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    public class ServerRpcReceiveSystem : SystemBase
    {
        public static int ReceivedCount = 0;

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            Entities.WithoutBurst().ForEach((Entity entity, ref SimpleRpcCommand cmd, ref ReceiveRpcCommandRequestComponent req) =>
            {
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount;
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    public class SerializedServerRpcReceiveSystem : SystemBase
    {
        public static int ReceivedCount = 0;
        public static SerializedRpcCommand ReceivedCmd;

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            Entities.WithoutBurst().ForEach((Entity entity, ref SerializedRpcCommand cmd, ref ReceiveRpcCommandRequestComponent req) =>
            {
                ReceivedCmd = cmd;
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount;
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    public class SerializedServerLargeRpcReceiveSystem : SystemBase
    {
        public static int ReceivedCount = 0;
        public static SerializedLargeRpcCommand ReceivedCmd;

        protected override void OnUpdate()
        {
            var PostUpdateCommands = new EntityCommandBuffer(Allocator.Temp);
            Entities.WithoutBurst().ForEach((Entity entity, ref SerializedLargeRpcCommand cmd, ref ReceiveRpcCommandRequestComponent req) =>
            {
                ReceivedCmd = cmd;
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount;
            }).Run();
            PostUpdateCommands.Playback(EntityManager);
        }
    }
    #endregion

    [DisableAutoCreation]
    public class SerializedLargeRpcCommandRequestSystem : RpcCommandRequestSystem<SerializedLargeRpcCommand, SerializedLargeRpcCommand>
    {
        [BurstCompile]
        protected struct SendRpc : IJobEntityBatch
        {
            public SendRpcData data;
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                data.Execute(chunk, orderIndex);
            }
        }
        protected override void OnUpdate()
        {
            var sendJob = new SendRpc{data = InitJobData()};
            ScheduleJobData(sendJob);
        }
    }
    [DisableAutoCreation]
    public class SerializedRpcCommandRequestSystem : RpcCommandRequestSystem<SerializedRpcCommand, SerializedRpcCommand>
    {
        [BurstCompile]
        protected struct SendRpc : IJobEntityBatch
        {
            public SendRpcData data;
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                data.Execute(chunk, orderIndex);
            }
        }
        protected override void OnUpdate()
        {
            var sendJob = new SendRpc{data = InitJobData()};
            ScheduleJobData(sendJob);
        }
    }

    [DisableAutoCreation]
    public class NonSerializedRpcCommandRequestSystem : RpcCommandRequestSystem<SimpleRpcCommand, SimpleRpcCommand>
    {
        [BurstCompile]
        protected struct SendRpc : IJobEntityBatch
        {
            public SendRpcData data;
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                data.Execute(chunk, orderIndex);
            }
        }
        protected override void OnUpdate()
        {
            var sendJob = new SendRpc{data = InitJobData()};
            ScheduleJobData(sendJob);
        }
    }

    [DisableAutoCreation]
    public class MultipleClientSerializedRpcCommandRequestSystem : RpcCommandRequestSystem<ClientIdRpcCommand, ClientIdRpcCommand>
    {
        [BurstCompile]
        protected struct SendRpc : IJobEntityBatch
        {
            public SendRpcData data;
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                data.Execute(chunk, orderIndex);
            }
        }
        protected override void OnUpdate()
        {
            var sendJob = new SendRpc{data = InitJobData()};
            ScheduleJobData(sendJob);
        }
    }

    [DisableAutoCreation]
    public class InvalidRpcCommandRequestSystem : RpcCommandRequestSystem<InvalidRpcCommand, InvalidRpcCommand>
    {
        [BurstCompile]
        protected struct SendRpc : IJobEntityBatch
        {
            public SendRpcData data;
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                data.Execute(chunk, orderIndex);
            }
        }
        protected override void OnUpdate()
        {
            var sendJob = new SendRpc{data = InitJobData()};
            ScheduleJobData(sendJob);
        }
    }

    [DisableAutoCreation]
    class RpcWithEntityRpcCommandRequestSystem : RpcCommandRequestSystem<RpcWithEntity, RpcWithEntity>
    {
        [BurstCompile]
        protected struct SendRpc : IJobEntityBatch
        {
            public SendRpcData data;
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                data.Execute(chunk, orderIndex);
            }
        }
        protected override void OnUpdate()
        {
            var sendJob = new SendRpc{data = InitJobData()};
            ScheduleJobData(sendJob);
        }
    }
}

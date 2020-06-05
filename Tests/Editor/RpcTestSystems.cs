using AOT;
using Unity.Burst;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode.Tests
{
    [BurstCompile]
    public struct SimpleRpcCommand : IRpcCommand
    {
        public void Serialize(ref DataStreamWriter writer)
        {
        }

        public void Deserialize(ref DataStreamReader reader)
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
            RpcExecutor.ExecuteCreateRequestComponent<SimpleRpcCommand>(ref parameters);
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    [BurstCompile]
    public struct SerializedRpcCommand : IRpcCommand
    {
        public int intValue;
        public short shortValue;
        public float floatValue;

        public void Serialize(ref DataStreamWriter writer)
        {
            writer.WriteInt(intValue);
            writer.WriteShort(shortValue);
            writer.WriteFloat(floatValue);
        }

        public void Deserialize(ref DataStreamReader reader)
        {
            intValue = reader.ReadInt();
            shortValue = reader.ReadShort();
            floatValue = reader.ReadFloat();
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
            serializedData.Deserialize(ref parameters.Reader);

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity,
                new ReceiveRpcCommandRequestComponent {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, serializedData);
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    [BurstCompile]
    public struct ClientIdRpcCommand : IRpcCommand
    {
        public int Id;

        public void Serialize(ref DataStreamWriter writer)
        {
            writer.WriteInt(Id);
        }

        public void Deserialize(ref DataStreamReader reader)
        {
            Id = reader.ReadInt();
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
            serializedData.Deserialize(ref parameters.Reader);

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity,
                new ReceiveRpcCommandRequestComponent {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, serializedData);
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
    }

    public struct InvalidRpcCommand : IRpcCommand
    {
        public void Serialize(ref DataStreamWriter writer)
        {
        }

        public void Deserialize(ref DataStreamReader reader)
        {
        }

        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>();
        }
    }

    #region Send Systems
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class ClientRcpSendSystem : ComponentSystem
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
                var req = PostUpdateCommands.CreateEntity();
                PostUpdateCommands.AddComponent(req, new SimpleRpcCommand());
                PostUpdateCommands.AddComponent(req, new SendRpcCommandRequestComponent {TargetConnection = Remote});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    public class ServerRpcBroadcastSendSystem : ComponentSystem
    {
        public static int SendCount = 0;

        protected override void OnUpdate()
        {
            if (SendCount > 0)
            {
                var req = PostUpdateCommands.CreateEntity();
                PostUpdateCommands.AddComponent(req, new SimpleRpcCommand());
                PostUpdateCommands.AddComponent(req,
                    new SendRpcCommandRequestComponent {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class MalformedClientRcpSendSystem : ComponentSystem
    {
        public static int[] SendCount = new int[2];
        public static ClientIdRpcCommand[] Cmds = new ClientIdRpcCommand[2];

        private int worldId;

        protected override void OnCreate()
        {
            RequireSingletonForUpdate<NetworkIdComponent>();

            const int kStringLength = 10; // we name it ClientTest
            worldId = int.Parse(World.Name.Substring(kStringLength, World.Name.Length - kStringLength));
        }

        protected override void OnUpdate()
        {
            if (SendCount[worldId] > 0)
            {
                var req = PostUpdateCommands.CreateEntity();
                PostUpdateCommands.AddComponent(req, Cmds[worldId]);
                PostUpdateCommands.AddComponent(req, new SendRpcCommandRequestComponent {TargetConnection = Entity.Null});
                --SendCount[worldId];
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class SerializedClientRcpSendSystem : ComponentSystem
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
                var req = PostUpdateCommands.CreateEntity();
                PostUpdateCommands.AddComponent(req, Cmd);
                PostUpdateCommands.AddComponent(req,
                    new SendRpcCommandRequestComponent {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }

    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class FlawedClientRcpSendSystem : ComponentSystem
    {
        public static int SendCount = 0;

        protected override void OnUpdate()
        {
            if (HasSingleton<NetworkStreamConnection>() && !HasSingleton<NetworkIdComponent>() && SendCount > 0)
            {
                var req = PostUpdateCommands.CreateEntity();
                PostUpdateCommands.AddComponent<SimpleRpcCommand>(req);
                PostUpdateCommands.AddComponent(req,
                    new SendRpcCommandRequestComponent {TargetConnection = Entity.Null});
                --SendCount;
            }
        }
    }
    #endregion

    #region Receive Systems
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    public class ServerMultipleRpcReceiveSystem : ComponentSystem
    {
        public static int[] ReceivedCount = new int[2];

        protected override void OnUpdate()
        {
            Entities.ForEach((Entity entity, ref ClientIdRpcCommand cmd, ref ReceiveRpcCommandRequestComponent req) =>
            {
                PostUpdateCommands.DestroyEntity(entity);
                if (cmd.Id >= 0 && cmd.Id < 2)
                    ReceivedCount[cmd.Id]++;
            });
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class MultipleClientBroadcastRpcReceiveSystem : ComponentSystem
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
            Entities.ForEach((Entity entity, ref SimpleRpcCommand cmd, ref ReceiveRpcCommandRequestComponent req) =>
            {
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount[worldId];
            });
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    public class ServerRpcReceiveSystem : ComponentSystem
    {
        public static int ReceivedCount = 0;

        protected override void OnUpdate()
        {
            Entities.ForEach((Entity entity, ref SimpleRpcCommand cmd, ref ReceiveRpcCommandRequestComponent req) =>
            {
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount;
            });
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    public class SerializedServerRpcReceiveSystem : ComponentSystem
    {
        public static int ReceivedCount = 0;
        public static SerializedRpcCommand ReceivedCmd;

        protected override void OnUpdate()
        {
            Entities.ForEach((Entity entity, ref SerializedRpcCommand cmd, ref ReceiveRpcCommandRequestComponent req) =>
            {
                ReceivedCmd = cmd;
                PostUpdateCommands.DestroyEntity(entity);
                ++ReceivedCount;
            });
        }
    }
    #endregion

    [DisableAutoCreation]
    public class SerializedRpcCommandRequestSystem : RpcCommandRequestSystem<SerializedRpcCommand>
    {
    }

    [DisableAutoCreation]
    public class NonSerializedRpcCommandRequestSystem : RpcCommandRequestSystem<SimpleRpcCommand>
    {
    }

    [DisableAutoCreation]
    public class MultipleClientSerializedRpcCommandRequestSystem : RpcCommandRequestSystem<ClientIdRpcCommand>
    {
    }

    [DisableAutoCreation]
    public class InvalidRpcCommandRequestSystem : RpcCommandRequestSystem<InvalidRpcCommand>
    {
    }
}

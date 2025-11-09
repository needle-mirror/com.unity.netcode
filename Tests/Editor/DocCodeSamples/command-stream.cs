using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace DocumentationCodeSamples
{
    partial class command_stream
    {
        private struct MyComponent : IComponentData
        {
            public int Value;
        }

        #region GhostOwnerIsLocal
        public partial struct GhostOwnerIsLocalExample : ISystem
        {
            public void OnUpdate(ref SystemState state)
            {
                foreach (var myComponent in SystemAPI.Query<RefRW<MyComponent>>().WithAll<GhostOwnerIsLocal>())
                {
                    // your logic here will be applied only to the entities owned by the local player.
                }
            }
        }
        #endregion

        public partial struct GhostOwnerExampleSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.RequireForUpdate<NetworkId>();
            }

            public void OnUpdate(ref SystemState state)
            {
                #region GhostOwner
                var localPlayerId = SystemAPI.GetSingleton<NetworkId>().Value;
                foreach (var (myComponent, owner) in SystemAPI.Query<RefRW<MyComponent>, RefRO<GhostOwner>>())
                {
                    if(owner.ValueRO.NetworkId == localPlayerId)
                    {
                        // your logic here will be applied only to the entities owned by the local player.
                    }
                }
                #endregion
            }
        }

        #region InputEvents
        public struct PlayerInput : IInputComponentData
        {
            public int Horizontal;
            public int Vertical;
            public InputEvent Jump;
        }
        #endregion

        #region GatherInput
        [UpdateInGroup(typeof(GhostInputSystemGroup))]
        public partial struct GatherInputs : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.RequireForUpdate<PlayerInput>();
            }

            public void OnUpdate(ref SystemState state)
            {
                bool jump = UnityEngine.Input.GetKeyDown("space");
                bool left = UnityEngine.Input.GetKey("left");
                //...

                var networkId = SystemAPI.GetSingleton<NetworkId>().Value;
                foreach (var inputData in SystemAPI.Query<RefRW<PlayerInput>>().WithAll<GhostOwnerIsLocal>())
                {
                    inputData.ValueRW = default;

                    if (jump)
                        inputData.ValueRW.Jump.Set();
                    if (left)
                        inputData.ValueRW.Horizontal -= 1;
                    //...
                }
            }
        }
        #endregion

        private struct PlayerMovement : IComponentData
        {
            public float JumpVelocity;
        }

        #region ProcessInput
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        public partial class ProcessInputs : SystemBase
        {
            protected override void OnCreate()
            {
                RequireForUpdate<PlayerInput>();
            }
            protected override void OnUpdate()
            {
                foreach (var (input, transform, movement) in SystemAPI.Query<RefRO<PlayerInput>, RefRW<LocalTransform>, RefRW<PlayerMovement>>())
                {
                    if (input.ValueRO.Jump.IsSet)
                        movement.ValueRW.JumpVelocity = 10; // start jump routine

                    // handle jump event logic, movement logic etc
                }
            }
        }
        #endregion

        #region ManualSerialization
        [NetCodeDisableCommandCodeGen]
        public struct MyCommand : ICommandData, ICommandDataSerializer<MyCommand>
        {
            public NetworkTick Tick { get; set; }
            public int Value;

            public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in MyCommand data)
            {
                writer.WriteInt(data.Value);
            }

            public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref MyCommand data)
            {
                data.Value = reader.ReadInt();
            }

            public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in MyCommand data, in MyCommand baseline,
                StreamCompressionModel compressionModel)
            {
                // Don't do any delta compression for this example
                Serialize(ref writer, state, data);
            }

            public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref MyCommand data, in MyCommand baseline,
                StreamCompressionModel compressionModel)
            {
                Deserialize(ref reader, state, ref data);
            }
        }

        [UpdateInGroup(typeof(CommandSendSystemGroup))]
        [BurstCompile]
        public partial struct MyCommandSendCommandSystem : ISystem
        {
            CommandSendSystem<MyCommand, MyCommand> m_CommandSend;
            [BurstCompile]
            struct SendJob : IJobChunk
            {
                public CommandSendSystem<MyCommand, MyCommand>.SendJobData data;
                public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
                    bool useEnabledMask, in v128 chunkEnabledMask)
                {
                    data.Execute(chunk, unfilteredChunkIndex);
                }
            }
            [BurstCompile]
            public void OnCreate(ref SystemState state)
            {
                m_CommandSend.OnCreate(ref state);
            }
            [BurstCompile]
            public void OnUpdate(ref SystemState state)
            {
                if (!m_CommandSend.ShouldRunCommandJob(ref state))
                    return;
                var sendJob = new SendJob{data = m_CommandSend.InitJobData(ref state)};
                state.Dependency = sendJob.Schedule(m_CommandSend.Query, state.Dependency);
            }
        }
        [UpdateInGroup(typeof(CommandReceiveSystemGroup))]
        [BurstCompile]
        public partial struct MyCommandReceiveCommandSystem : ISystem
        {
            CommandReceiveSystem<MyCommand, MyCommand> m_CommandRecv;
            [BurstCompile]
            struct ReceiveJob : IJobChunk
            {
                public CommandReceiveSystem<MyCommand, MyCommand>.ReceiveJobData data;
                public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
                    bool useEnabledMask, in v128 chunkEnabledMask)
                {
                    data.Execute(chunk, unfilteredChunkIndex);
                }
            }
            [BurstCompile]
            public void OnCreate(ref SystemState state)
            {
                m_CommandRecv.OnCreate(ref state);
            }
            [BurstCompile]
            public void OnUpdate(ref SystemState state)
            {
                var recvJob = new ReceiveJob{data = m_CommandRecv.InitJobData(ref state)};
                state.Dependency = recvJob.Schedule(m_CommandRecv.Query, state.Dependency);
            }
        }
        #endregion
    }
}

using NUnit.Framework;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace DocumentationCodeSamples
{
    [BurstCompile]
    partial class rpcs
    {
        #region DefineRPC
        public struct OurRpcCommand : IRpcCommand
        {
        }
        #endregion

        #region DefineRPCWithData
        public struct OurRpcCommandWithData : IRpcCommand
        {
            public int intData;
            public short shortData;
        }
        #endregion

        #region SendRPC
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        public partial struct ClientRpcSendSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.RequireForUpdate<NetworkId>();
            }

            public void OnUpdate(ref SystemState state)
            {
                if (Input.GetKey("Space"))
                {
                    state.EntityManager.CreateEntity(typeof(OurRpcCommand), typeof(SendRpcCommandRequest));
                }
            }
        }
        #endregion

        #region ReceiveRPC
        [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
        public partial struct ServerRpcReceiveSystem : ISystem
        {
            public void OnUpdate(ref SystemState state)
            {
                var entityCommandBuffer = new EntityCommandBuffer(state.WorldUpdateAllocator);
                foreach (var (command, request, entity) in SystemAPI.Query<RefRO<OurRpcCommand>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
                {
                    entityCommandBuffer.DestroyEntity(entity);
                    Debug.Log("We received a command!");
                }
                entityCommandBuffer.Playback(state.EntityManager);
            }
        }
        #endregion

        #region RPCSerializer
        [BurstCompile]
        public struct OurRpcCommandSerializer : IComponentData, IRpcCommandSerializer<OurRpcCommandSerializer>
        {
            public int SpawnIndex;
            public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in OurRpcCommandSerializer data)
            {
                // Example writing the delta against a baseline of zero.
                writer.WritePackedIntDelta(data.SpawnIndex, 2, state.CompressionModel);
            }

            public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref OurRpcCommandSerializer data)
            {
                data.SpawnIndex = reader.ReadPackedIntDelta(2, state.CompressionModel);
            }

            public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
            {
                return InvokeExecuteFunctionPointer;
            }

            [BurstCompile(DisableDirectCall = true)]
            private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
            {
                // Insert your RPC execution code here.
            }

            static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer = new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        }
        #endregion

        // Use the whole type below to make sure that the signature is still correct
        [BurstCompile]
        private partial class Wrapper // To use the same name for the type
        {
            [BurstCompile]
            public struct OurRpcCommandSerializer : IComponentData, IRpcCommandSerializer<OurRpcCommandSerializer>
            {
                public int SpawnIndex;

                public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state,
                    in OurRpcCommandSerializer data)
                {
                    // Example writing the delta against a baseline of zero.
                    writer.WritePackedIntDelta(data.SpawnIndex, 2, state.CompressionModel);
                }

                public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state,
                    ref OurRpcCommandSerializer data)
                {
                    data.SpawnIndex = reader.ReadPackedIntDelta(2, state.CompressionModel);
                }

                public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
                {
                    return InvokeExecuteFunctionPointer;
                }

                #region ModifiedInvokeExecute
                [BurstCompile(DisableDirectCall = true)]
                private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
                {
                    RpcExecutor.ExecuteCreateRequestComponent<OurRpcCommandSerializer, OurRpcCommandSerializer>(ref parameters);
                }
                #endregion

                static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
                    new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
            }

            #region ReceiveRPCWithSerializer
            [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
            [CreateAfter(typeof(RpcSystem))]
            [BurstCompile]
            partial struct OurRpcCommandRequestSystem : ISystem
            {
                RpcCommandRequest<OurRpcCommandSerializer, OurRpcCommandSerializer> m_Request;
                [BurstCompile]
                struct SendRpc : IJobChunk
                {
                    public RpcCommandRequest<OurRpcCommandSerializer, OurRpcCommandSerializer>.SendRpcData data;
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
            #endregion
        }

        #region CustomRPCSerializingWithData
        [BurstCompile]
        public struct OurDataRpcCommand : IComponentData, IRpcCommandSerializer<OurDataRpcCommand>
        {
            public int intData;
            public short shortData;

            public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in OurDataRpcCommand data)
            {
                writer.WriteInt(data.intData);
                writer.WriteShort(data.shortData);
            }

            public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref OurDataRpcCommand data)
            {
                data.intData = reader.ReadInt();
                data.shortData = reader.ReadShort();
            }

            public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
            {
                return InvokeExecuteFunctionPointer;
            }

            [BurstCompile(DisableDirectCall = true)]
            private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
            {
                RpcExecutor.ExecuteCreateRequestComponent<OurDataRpcCommand, OurDataRpcCommand>(ref parameters);
            }

            static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer = new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        }
        #endregion

        #region CustomRPCQueue
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        public partial struct ClientQueueRpcSendSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.RequireForUpdate<RpcCollection>();
                state.RequireForUpdate<NetworkId>();
            }

            public void OnUpdate(ref SystemState state)
            {
                if (Input.GetKey("space"))
                {
                    var ghostFromEntity = state.GetComponentLookup<GhostInstance>(true);
                    var rpcQueue = SystemAPI.GetSingleton<RpcCollection>().GetRpcQueue<OurRpcCommandSerializer, OurRpcCommandSerializer>();
                    foreach (var rpcDataStreamBuffer in SystemAPI.Query<DynamicBuffer<OutgoingRpcDataStreamBuffer>>())
                    {
                        rpcQueue.Schedule(rpcDataStreamBuffer, ghostFromEntity, new OurRpcCommandSerializer());
                    }
                }
            }
        }
        #endregion
    }
}

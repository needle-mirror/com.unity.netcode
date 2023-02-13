// THIS FILE IS AUTO-GENERATED BY NETCODE PACKAGE SOURCE GENERATORS. DO NOT DELETE, MOVE, COPY, MODIFY, OR COMMIT THIS FILE.
// TO MAKE CHANGES TO THE SERIALIZATION OF A TYPE, REFER TO THE MANUAL.
using AOT;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Assertions;
using Unity.Networking.Transport;
#region __COMMAND_USING_STATEMENT__
using __COMMAND_USING__;
#endregion


namespace __COMMAND_NAMESPACE__
{
    [System.Runtime.CompilerServices.CompilerGenerated]
    [BurstCompile]
    internal struct __COMMAND_NAME__Serializer : IComponentData, IRpcCommandSerializer<__COMMAND_COMPONENT_TYPE__>
    {
        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in __COMMAND_COMPONENT_TYPE__ data)
        {
            #region __COMMAND_WRITE__
            #endregion
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref __COMMAND_COMPONENT_TYPE__ data)
        {
            #region __COMMAND_READ__
            #endregion
        }
        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            RpcExecutor.ExecuteCreateRequestComponent<__COMMAND_NAME__Serializer, __COMMAND_COMPONENT_TYPE__>(ref parameters);
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }

    [System.Runtime.CompilerServices.CompilerGenerated]
    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    internal struct __COMMAND_NAME__RpcCommandRequestSystem : ISystem
    {
        RpcCommandRequest<__COMMAND_NAME__Serializer, __COMMAND_COMPONENT_TYPE__> m_Request;
        [BurstCompile]
        struct SendRpc : IJobChunk
        {
            public RpcCommandRequest<__COMMAND_NAME__Serializer, __COMMAND_COMPONENT_TYPE__>.SendRpcData data;
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
}

//THIS FILE IS AUTOGENERATED BY GHOSTCOMPILER. DON'T MODIFY OR ALTER.
using AOT;
using Unity.Burst;
using Unity.Networking.Transport;
#region __RPC_USING_STATEMENT__
using __RPC_USING__;
#endregion


namespace __RPC_NAMESPACE__
{
    [BurstCompile]
    public struct __RPC_NAME__Serializer : IComponentData, IRpcCommandSerializer<__RPC_COMPONENT_TYPE__>
    {
        public void Serialize(ref DataStreamWriter writer, in __RPC_COMPONENT_TYPE__ data)
        {
            #region __RPC_WRITE__
            #endregion
        }

        public void Deserialize(ref DataStreamReader reader, ref __RPC_COMPONENT_TYPE__ data)
        {
            #region __RPC_READ__
            #endregion
        }
        [BurstCompile]
        [MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            RpcExecutor.ExecuteCreateRequestComponent<__RPC_NAME__Serializer, __RPC_COMPONENT_TYPE__>(ref parameters);
        }

        static PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }
    class __RPC_NAME__RpcCommandRequestSystem : RpcCommandRequestSystem<__RPC_NAME__Serializer, __RPC_COMPONENT_TYPE__>
    {
    }
}

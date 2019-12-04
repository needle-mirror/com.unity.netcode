# RPCs

NetCode uses a limited form of RPC calls to handle events. A job on the sending side can issue RPC calls, and they then execute on a job on the receiving side. This limits what you can do in an RPC.

To send an RPC, you need to get access to an [RpcQueue](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.RpcQueue-1.html) of the command you want to send. You can create this in OnCreateManager. Call `m_RpcQueue = World.GetOrCreateManager<RpcSystem>().GetRpcQueue<RpcLoadLevel>();` and cache it through the lifetime of the game. When you have the queue, you can get the `OutgoingRpcDataStreamBufferComponent` from an entity to schedule events in the queue and then call `rpcQueue.Schedule(rpcBuffer, new RpcCommand);`. 

You can send an RPC from OnUpdate as follows:
* Get the `OutgoingRpcDataStreamBufferComponent` from the entity you want to send the event to.
* Call `rpcQueue.Schedule(rpcBuffer, new RpcCommand);` to append the RPC data you want to sent to the outgoing RPC buffer (`OutgoingRpcDataStreamBufferComponent`).
* Once this is done, the `NetworkStreamSendSystem` sends the queued RPC to the remote end. 

The RpcCommand interface has three methods: __Serialize, Deserialize__, and __CompileExecute__. __Serialize__ and __Deserialize__ store the data in a packet, while __CompileExecute__ uses Burst to create a `FunctionPointer`. The function it compiles takes one parameter by ref, and a struct that contains:

* `DataStreamReader` reader
* Entity connection
* `EntityCommandBuffer.Concurrent` commandBuffer
* int `jobIndex` 

Because the function is static, it needs to use `Deserialize` to read the struct data before it can execute the RPC. The RPC can then either use the command buffer to modify the connection entity, or use it to create a new request entity for more complex tasks and then apply the command in a separate system at a later time. This means that you don’t need to perform any additional operations to receive and RPC; its `Execute` method is called on the receiving end automatically.

## RPC command request component
Most RPCS create new entities which serve as requests for other systems to perform an operation. To reduce the boilerplate for this, NetCode has a set of helpers that perform these operations. To do this perform the following steps:

* Create a component that extends `IRpcCommand` and implement the interface methods.
* In the execute method, call `RpcExecutor.ExecuteCreateRequestComponent<HeartbeatComponent>(ref parameters);` and add class `HeartbeatComponentRpcCommandRequestSystem : RpcCommandRequestSystem<HeartbeatComponent>{}`  to the codebase. **Note:** In future, the NetCode development team will provide a way to generate code for these methods in a similar way to `GenerateAuthoringComponent` in entities.
* Once you have created a command request, use `IRpcCommand` and `SendRpcCommandRequestComponent` with the target connection entity to send it to a remote end. If the target connection entity is `Entity.Null`, Unity sends it to all connections.

The system automatically finds the requests, sends them, and then deletes the send request. On the remote side they show up as entities with the same `IRpcCommand` and a `ReceiveRpcCommandRequestComponent` which you can use to identify which connection the request was received from.
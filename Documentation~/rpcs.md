# Communicating with RPCs

Use remote procedure calls (RPCs) to communicate high-level game flow events and send one-off, non-predicted commands from the client to the server. A job on the sending side can issue RPCs, and the RPCs then execute on a job on the receiving side. This limits what you can do in an RPC, such as what data you can read and modify, and what calls you're allowed to make from the engine. For more information on the job system, refer to the Unity User Manual documentation on the [C# Job System](https://docs.unity3d.com/Manual/JobSystem.html).

To make the system a bit more flexible in the Netcode for Entities context, you can create an entity that contains specific Netcode components such as [`SendRpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SendRpcCommandRequest.html) and [`ReceiveRpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ReceiveRpcCommandRequest.html), which this page outlines.

## Comparing ghosts and RPCs

You can use both [ghosts](ghost-snapshots.md#ghosts) and RPCs in your game. Each one has specific use cases where it excels compared to the other, and you should choose which one to use based on the requirements for a given scenario.

### Ghost use cases

Use ghosts to:

* Replicate spatially local, ephemeral, and relevant per-entity data.
* Enable [client prediction](intro-to-prediction.md) of ghost entities, which is the most effective latency-hiding multiplayer technique.

### RPC use cases

Use RPCs to:

* Communicate high-level game flow events. For example, making every client do a certain thing, like load a specific level.
* Send one-off, non-predicted commands from the client to the server. For example: Join this squad. Send a chat message. Unmute this player. Request to exit this realm.

### Key differences

* RPCs are one-off events, and are therefore not automatically persisted.
    * For example, if you send an RPC when a treasure chest is opened, the if a player disconnects and reconnects the chest will appear closed.
* Ghost data persists for the lifetime of its ghost entity (and the lifetime of the ghost entity is itself replicated). Therefore, long-lived user-interactable entities should have their persistent state stored in ghost components.
    * For example, a chest's finite-state machine (FSM) can be stored as an `enum` on a component. If a player opens the chest, disconnects, then reconnects, they will re-receive the chest, as well as its open state.
* RPCs are sent as reliable packets, while ghosts snapshots are unreliable (with eventual consistency).
* RPC data is sent and received without modification, while ghost data goes through optimizations like diff and delta-compression, and can go through value smoothing when received.
* RPCs aren't tied to any particular tick or other snapshot timing data. They are processed on the frame that they are received.
* Ghost snapshot data can work with interpolation and prediction (with snapshot history), and thus history, rollback, and resimulation.
* Ghost snapshot data can be bandwidth optimized via relevancy and importance. RPCs are either broadcast, or sent to a single client.

## Extend `IRpcCommand`

To use RPCs in Netcode for Entities, create a command by extending the [`IRpcCommand`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IRpcCommand.html):

```c#
public struct OurRpcCommand : IRpcCommand
{
}
```

Or, if you need some data in your RPC:

```c#
public struct OurRpcCommand : IRpcCommand
{
    public int intData;
    public short shortData;
}
```

This generates all the code you need for serialization and deserialization as well as registration of the RPC.

## Sending and receiving commands

To complete the example, you need to create some entities to send and receive the commands you created. To send the command, you need to create an entity and add the command and the special component [`SendRpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SendRpcCommandRequest.html) to it. This component has a member called `TargetConnection` that refers to the remote connection you want to send this command to.

> [!NOTE]
> If `TargetConnection` is set to `Entity.Null`, the message is broadcast to all clients. You don't have to set this value on a client, because clients can only send RPCs send to the server.

The following is an example of a simple send system which sends a command if the user presses the space bar on their keyboard.

```c#
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public class ClientRpcSendSystem : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<NetworkId>();
    }

    protected override void OnUpdate()
    {
        if (Input.GetKey("space"))
        {
            EntityManager.CreateEntity(typeof(OurRpcCommand), typeof(SendRpcCommandRequest));
        }
    }
}
```

When the RPC is received, an entity that you can filter on is created by a code-generated system. To test if this works, the following example creates a system that receives the `OurRpcCommand`:

```c#
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public class ServerRpcReceiveSystem : SystemBase
{
    protected override void OnUpdate()
    {
        Entities.ForEach((Entity entity, ref OurRpcCommand cmd, ref ReceiveRpcCommandRequest req) =>
        {
            PostUpdateCommands.DestroyEntity(entity);
            Debug.Log("We received a command!");
        }).Run();
    }
}
```

The [`RpcSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.RpcSystem.html) automatically finds all of the requests, sends them, and then deletes the send request. On the remote side they show up as entities with the same `IRpcCommand` and a [`ReceiveRpcCommandRequestComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ReceiveRpcCommandRequestComponent.html), which you can use to identify which connection the request was received from.

## Creating an RPC without generating code

Code generation for RPCs is optional. If you don't want to use it, you need to create a component and a serializer. These can be the same struct or two different ones. To create a single struct which is both the component and the serializer you need to add:

```c#
[BurstCompile]
public struct OurRpcCommand : IComponentData, IRpcCommandSerializer<OurRpcCommand>
{
    public int SpawnIndex;
    public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in OurRpcCommand data)
    {
        // Example writing the delta against a baseline of zero.
        writer.WritePackedIntDelta(data.SpawnIndex, 2, state.CompressionModel);
    }

    public void Deserialize(ref DataStreamReader reader, in RpcSerializerState state, ref OurRpcCommand data)
    {
        data.SpawnIndex = reader.ReadPackedIntDelta(2, state.CompressionModel);
    }

    public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
    {
    }

    [BurstCompile(DisableDirectCall = true)]
    private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
    {
    }

    static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer = new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
}
```

The [`IRpcCommandSerializer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IRpcCommandSerializer.html) interface has three methods: `Serialize`, `Deserialize`, and `CompileExecute`. `Serialize` and `Deserialize` store the data in a packet, while `CompileExecute` uses Burst to create a `FunctionPointer`. The function it compiles takes an [`RpcExecutor.Parameters`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.RpcExecutor.Parameters.html) by reference that contains entries that you're able to use as needed.

> [!NOTE]
> Don't read from (or write to) the struct field values themselves (do not read or write in-place), instead read from (and write to) the by-ref argument `data`.
<!--
TODO enable with single world host
> [!NOTE]
> When using a single-world host, local RPCs bypass the serialization/deserialization flow and are executed locally. You can access local RPC data using the `RpcExecutor.Parameters.GetPassthroughActionData` method and test whether you're in a passthrough situation using the `RpcExecutor.Parameters.IsPassthroughRPC` bool.
-->

Because the function is static, it needs to use `Deserialize` to read the struct data before it executes the RPC. The RPC then either uses the command buffer to modify the connection entity, or uses it to create a new request entity for more complex tasks. It then applies the command in a separate system at a later time. This means that you don't need to perform any additional operations to receive an RPC; its `Execute` method is called on the receiving end automatically.

To create an entity that holds an RPC, use the function `ExecuteCreateRequestComponent<T>`. To do this, extend the previous `InvokeExecute` function example with:

```c#
[BurstCompile(DisableDirectCall = true)]
private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
{
    RpcExecutor.ExecuteCreateRequestComponent<OurRpcCommand, OurRpcCommand>(ref parameters);
}
```

This creates an entity with a [`ReceiveRpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ReceiveRpcCommandRequest.html) and `OurRpcCommand` components.

> [!NOTE]
> You don't need to create a receiving RPC entity here if you don't need one.
> For example, for an RPC denoting new chat messages, it may be simpler to append your chat message to a buffer on the
> NetworkConnection entity, then consume said buffer directly via a system.

Once you create an [`IRpcCommandSerializer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IRpcCommanSerializer.html), you need to make sure that the [`RpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.RpcCommandRequestSystem-1.html) system picks it up. To do this, you can create a system that invokes the `RpcCommandRequest`, as follows:

```c#
[UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
[CreateAfter(typeof(RpcSystem))]
[BurstCompile]
partial struct OurRpcCommandRequestSystem : ISystem
{
    RpcCommandRequest<OurRpcCommand, OurRpcCommand> m_Request;
    [BurstCompile]
    struct SendRpc : IJobChunk
    {
        public RpcCommandRequest<OurRpcCommand, OurRpcCommand>.SendRpcData data;
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
```

The `RpcCommandRequest` system uses an [`RpcQueue`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.RpcQueue-1.html) internally to schedule outgoing RPCs.

## Serializing RPCs

You might have data that you want to attach to the `RpcCommand`. To do this, you need to add the data as a member of your command and then use the `Serialize` and `Deserialize` functions to decide what data should be serialized. Refer to the following code for an example of this:

```c#
[BurstCompile]
public struct OurDataRpcCommand : IComponentData, IRpcCommandSerializer<OurDataRpcCommand>
{
    public int intData;
    public short shortData;

    public void Serialize(ref DataStreamWriter writer, in OurDataRpcCommand data)
    {
        writer.WriteInt(data.intData);
        writer.WriteShort(data.shortData);
    }

    public void Deserialize(ref DataStreamReader reader, ref OurDataRpcCommand data)
    {
        data.intData = reader.ReadInt();
        data.shortData = reader.ReadShort();
    }

    public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
    {
    }

    [BurstCompile(DisableDirectCall = true)]
    private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
    {
        RpcExecutor.ExecuteCreateRequestComponent<OurDataRpcCommand, OurDataRpcCommand>(ref parameters);
    }

    static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer = new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
}
```

> [!NOTE]
> To avoid problems, make sure the `Serialize` and `Deserialize` calls are symmetric. The example above writes an `int` then a `short`, so your code needs to read an `int` then a `short` in that order. If you omit reading a value, forget to write a value, or change the order of the way the code reads and writes, you might encounter problems.

## `RpcQueue`

The [`RpcQueue`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.RpcQueue-1.html) is used internally to schedule outgoing RPCs. However, you can manually create your own queue and use it to schedule RPCs.

To do this, call `GetSingleton<RpcCollection>().GetRpcQueue<OurRpcCommand>();`. You can either call it in `OnUpdate` or call it in `OnCreate` and cache the value through the lifetime of your application. If you do call it in `OnCreate`, you must make sure that the system calling it is created after `RpcSystem`.

When you have the queue, get the `OutgoingRpcDataStreamBuffer` from an entity to schedule events in the queue and then call `rpcQueue.Schedule(rpcBuffer, new OurRpcCommand);`, as follows. This example sends an RPC using the `RpcQueue` when the user presses the space bar on their keyboard.

```c#
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public class ClientQueueRpcSendSystem : ComponentSystem
{
    protected override void OnCreate()
    {
        RequireForUpdate<NetworkId>();
    }

    protected override void OnUpdate()
    {
        if (Input.GetKey("space"))
        {
            var rpcQueue = GetSingleton<RpcCollection>().GetRpcQueue<OurRpcCommand, OurRpcCommand>();
            Entities.ForEach((Entity entity, ref NetworkStreamConnection connection) =>
            {
                var rpcFromEntity = GetBufferLookup<OutgoingRpcDataStreamBuffer>();
                if (rpcFromEntity.Exists(entity))
                {
                    var buffer = rpcFromEntity[entity];
                    rpcQueue.Schedule(buffer, new OurRpcCommand());
                }
            });
        }
    }
}
```

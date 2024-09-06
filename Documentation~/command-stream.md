# Command stream

The client sends a continuous command stream to the server when the `NetworkStreamConnection` is tagged to be "in-game". This stream includes all inputs and acknowledgements of the last received snapshot.

The connection is always kept alive, even if the client doesn't control any entities or generate any inputs that need to be transmitted to the server. The command packet is sent at a regular interval (every full simulated tick) to automatically acknowledge received snapshots, and to report other important information to the server.

## Creating inputs (commands)

To create a new input type, create a struct that implements the `ICommandData` interface. To implement that interface you need to provide a property for accessing the `Tick`.

The serialization and registration code for the `ICommandData` is generated automatically, but you can also disable that and write the serialization [manually](#manual-serialization).

The `ICommandData` buffer can be added to the entity controlled by the player either at baking time (using an authoring component) or at runtime. When adding the buffer at runtime, make sure that the dynamic buffer is present on both server and client.

### Handling input on the client

The client is responsible for polling the input source and adding `ICommand` to the buffer for the entities it controls. The queued commands are then sent automatically at regular intervals by `CommandSendPacketSystem`.

The systems responsible for writing to the command buffers must all run inside the [GhostInputSystemGroup](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostInputSystemGroup.html).

### `ICommandData` serialization and payload limit

When using `ICommand`, Netcode for Entities automatically generates command serialization code in the `CommandSendSystemGroup`. Each individual command is serialized and queued in the `OutgoingCommandDataStreamBuffer` (present on the network connection) by its own code-generated system. The `CommandSendPacketSystem` is then responsible for flushing the outgoing buffer at the `SimulationTickRate` interval.

In addition to the most recent input, the previous three inputs are also included to provide redundancy in the case of packet loss. Each redundant command is delta compressed against the command for the current tick. The final serialized data looks something like the following:

```
| Tick, Command | CommandDelta(Tick-1, Tick) | CommandDelta(Tick-2, Tick) | CommandDelta(Tick-3, Tick)|
```

A size limit of 1024 bytes is enforced on the command payload and checked when the command is serialized into the outgoing buffer. An error is reported to the application if the encoded payload is greater than 1024 bytes.

### Receiving commands on the server

`ICommandData` is automatically received by the `NetworkStreamReceiveSystem` on the server and added to the `IncomingCommandDataStreamBuffer` buffer. The `CommandReceiveSystem` is then responsible for dispatching the command data to the target entity (that the command belongs to).

> [!NOTE]
> The server should only receive commands from the clients. It should never overwrite or change the input received by the client.

## Automatically handling commands (`AutoCommandTarget`)

You can automatically send commands to the server if you add your `ICommandData` component to a ghost and set the following **GhostAuthoring** options:

1. `Has Owner` set
2. `Support Auto Command Target`

<img src="images/enable-autocommand.png" width="500" alt="enable-autocommand"/>

For automatic command targeting to work, the following must also be true of your ghost:

- The ghost must be owned by your client (requiring the server to set the `GhostOwner` to your `NetworkId.Value`).
- The ghost is `Predicted` or `OwnerPredicted` (you can't use an `ICommandData` to control interpolated ghosts).
- The [`AutoCommandTarget.Enabled`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.AutoCommandTarget.html) flag must be set to true.

If you're not using `AutoCommandTarget`, your game code must set the [`CommandTarget`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.CommandTarget.html) on the connection entity to reference the entity that the `ICommandData` component has been attached to. You can have multiple `ICommandData` in your game, and Netcode for Entities will only send the `ICommandData` for the entity that `CommandTarget` points to.

When you need to access inputs from the buffer, you can use an extension method for `DynamicBuffer<ICommandData>` called `GetDataAtTick`, which gets the matching tick for a specific frame. You can also use the `AddCommandData` utility method (which adds more commands to the ring-buffer for you).

> [!NOTE]
> When you update the state of your simulation inside the prediction loop, you must rely only on the commands present in the `ICommandData` buffer (for a given input type). Polling input directly using `UnityEngine.Input` or relying on input information not present in the struct implementing the `ICommandData` interface can cause client mis-prediction.

## Checking ghost ownership on the client

> [!NOTE]
> You must use (and implement) the `GhostOwner` functionality for the following commands to work properly. For example, by checking the 'Has Owner' checkbox in the `GhostAuthoringComponent`.

Ghosts often share the same `CommandBuffer`, making it necessary to check which entities are owned by the local player before adding new inputs to the buffer, to avoid overwriting inputs from other players.

You can check ghost ownership in two ways: using the [`GhostOwnerIsLocal` component (recommended)](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostOwnerIsLocal.html) or by using the [`GhostOwner` component](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostOwner.html).

### Use the `GhostOwnerIsLocal` component (recommended)

All ghosts have a `GhostOwnerIsLocal` component that you can enable to filter out ghosts not owned by the local player.

For example:
```c#
Entities
    .WithAll<GhostOwnerIsLocal>()
    .ForEach((ref MyComponent myComponent)=>
    {
        // your logic here will be applied only to the entities owned by the local player.    
    }).Run();
```
### Use the `GhostOwner` component

You can filter entities manually by checking that the `GhostOwner.NetworkId` of the entity equals the `NetworkId` of the player.

```c#
var localPlayerId = GetSingleton<NetworkId>().Value;
Entities
    .ForEach((ref MyComponent myComponent, in GhostOwner owner)=>
    {
        if(owner.NetworkId == localPlayerId)
        {
            // your logic here will be applied only to the entities owned by the local player.
        }                
    }).Run();
```

## Automatic command input (`IInputComponentData`)

> [!NOTE]
> You must use (and implement) the `GhostOwner` functionality for the following commands to work properly. For example, by checking the 'Has Owner' checkbox in the `GhostAuthoringComponent`.

Most of the functionality above can be managed automatically if you create an input component data struct that inherits the `IInputComponentData` interface. Then adding command data to the buffer and retrieving it when processing inputs will be handled automatically via code-generated systems, as long as you set up the input gathering and input processing systems separately.

Because the input struct implementing `IInputComponentData` is baked by `ICommandData`, [the 1024 bytes limit for the payload](#ICommandData-serialization-and-payload-limit) also applies.

> [!NOTE]
> Per prefab overrides done in the ghost authoring component inspector are disabled for input components and their companion buffer. You can add a ghost component attribute on the input component in code and it will apply to the buffer as well.

### Input events

By using the `InputEvent` type within `IInputComponentData` inputs, you can guarantee that one-off events (such as those gathered by `UnityEngine.Input.GetKeyDown`) are synchronized properly with the server and registered exactly once, even when the exact input tick where the input event was first registered is dropped on its way to the server.

### How it works

In a standard input component data struct you'll have these systems set up:

- Gather input system (client loop)
  - Take input events and save them in the input component data. This happens in `GhostInputSystemGroup`.
- Process input system (server or prediction loop)
  - Take current input component and process the values. This usually happens in `PredictedSimulationSystemGroup`.

With `IInputComponentData` handling it looks like this with code-generated systems:

- Gather input system (client loop)
  - Take input events and save them in the input component data. This happens in `GhostInputSystemGroup`.
- Copy input to command buffer (client loop)
  - Take current input data component and add to command buffer, also recording current tick.
- Apply inputs for current tick to input component data (server or prediction loop)
  - Retrieve inputs from command buffer for current tick and apply to input component. With prediction multiple input values could be applied as prediction rolls back (see [Prediction](intro-to-prediction.md)).
- Process input system (server or prediction loop)
  - Take current input component and process the values. This usually happens in `PredictedSimulationSystemGroup`.

The first and last steps are the same as with the single-player input handling, and these are the only systems you need to write/manage. An important difference, with netcode-enabled input, is that the processing system can be called multiple times per tick as previous ticks (rollback) are handled.

### Example code

Simple input values for character movement (with jumping):

```c#
using Unity.Entities;
using Unity.NetCode;

[GenerateAuthoringComponent]
public struct PlayerInput : IInputComponentData
{
    public int Horizontal;
    public int Vertical;
    public InputEvent Jump;
}
```

The input gathering system, which takes current inputs and applies them to the input component data on the local player's entity.

```c#
[UpdateInGroup(typeof(GhostInputSystemGroup))]
[AlwaysSynchronizeSystem]
public partial class GatherInputs : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<PlayerInput>();
    }

    protected override void OnUpdate()
    {
        bool jump = UnityEngine.Input.GetKeyDown("space");
        bool left = UnityEngine.Input.GetKey("left");
        //...

        var networkId = GetSingleton<NetworkId>().Value;
        Entities.WithName("GatherInput").WithAll<GhostOwnerIsLocal>().ForEach((ref PlayerInput inputData) =>
            {
                inputData = default;

                if (jump)
                    inputData.Jump.Set();
                if (left)
                    inputData.Horizontal -= 1;
                //...
            }).ScheduleParallel();
    }
}
```

The processing input system, which takes the current input values stored on the player's input component and applies the equivalent movement actions.

```c#
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    public partial class ProcessInputs : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<PlayerInput>();
        }
        protected override void OnUpdate()
        {
            var movementSpeed = Time.DeltaTime * 3;
            Entities.WithAll<Simulate>().WithName("ProcessInputForTick").ForEach(
                (ref PlayerInput input, ref Translation trans, ref PlayerMovement movement) =>
                {
                    if (input.Jump.IsSet)
                        movement.JumpVelocity = 10; // start jump routine

                    // handle jump event logic, movement logic etc
                }).ScheduleParallel();
        }
    }
```

## Manual serialization

To manually serialize commands, you need to add the `[NetCodeDisableCommandCodeGen]` attribute to the struct implementing the `ICommandData` interface and create a struct implementing `ICommandDataSerializer<T>`, where `<T>` is your `ICommandData` struct.

[ICommandDataSerializer](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ICommandDataSerializer-1.html) has two __Serialize__ and two __Deserialize__ methods: one pair for raw values, and one pair for delta compressed values. The system sends multiple inputs in each command packet. The first packet contains raw data but the rest are compressed using delta compression. Delta compression compresses inputs well because the rate of change is low.

As well as creating a struct, you also need to create specific instances of the generic systems `CommandSendSystem` and `CommandReceiveSystem`. To do this, extend the base system, for example with:

```c#
[UpdateInGroup(typeof(CommandSendSystemGroup))]
[BurstCompile]
public partial struct MyCommandSendCommandSystem : ISystem
{
    CommandSendSystem<MyCommandSerializer, MyCommand> m_CommandSend;
    [BurstCompile]
    struct SendJob : IJobChunk
    {
        public CommandSendSystem<MyCommandSerializer, MyCommand>.SendJobData data;
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
    CommandReceiveSystem<MyCommandSerializer, MyCommand> m_CommandRecv;
    [BurstCompile]
    struct ReceiveJob : IJobChunk
    {
        public CommandReceiveSystem<MyCommandSerializer, MyCommand>.ReceiveJobData data;
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
```

# Command stream

The client continuously sends a command stream to the server. This stream includes all inputs and acknowledgements of the last received snapshot. When no commands are sent a [NullCommandSendSystem](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NullCommandSendSystem.html) sends acknowledgements for received snapshots without any inputs. This is an automatic system to make sure the flow works automatically when the game does not need to send any inputs.

To create a new input type, create a struct that implements the `ICommandData` interface. To implement that interface you need to provide a property for accessing the `Tick`.

The serialization and registration code for the `ICommandData` will be generated automatically, but it is also possible to disable that and write the serialization manually.

If you add your `ICommandData` component to a ghost which has `Has Owner` and `Support Auto Command Target` enabled in the autoring component the commands for that ghost will automatically be sent if the ghost is owned by you, is predicted, and [AutoCommandTarget](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.AutoCommandTarget.html).Enabled has not been set to false.

If you are not using `Auto Command Target`, your game code must set the [CommandTargetComponent](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.CommandTargetComponent.html) on the connection entity to reference the entity that the `ICommandData` component has been attached to.

You can have multiple command systems, and NetCode selects the correct one based on the `ICommandData` type of the entity that points to `CommandTargetComponent`.

When you need to access inputs on the client and server, it is important to read the data from the `ICommandData` rather than reading it directly from the system. If you read the data from the system the inputs won’t match between the client and server so your game will not behave as expected.

When you need to access the inputs from the buffer, you can use an extension method for `DynamicBuffer<ICommandData>` called `GetDataAtTick` which gets the matching tick for a specific frame. You can also use the `AddCommandData` utility method which adds more commands to the buffer.

## Automatic command input setup using IInputComponentData

It's possible to have most of the things mentioned above for command data usage set up automatically for you given an input component data struct you have set up. You need to inherit the `IInputComponentData` interface on the input struct and the task of adding it to the command data buffer and retrieving back from the buffer when processing inputs will be handled automatically via code generated systems. For this to work it is required to have input gathering and input processing (like movement system) set up in two separate systems.

> [!NOTE]
> It is required you use the `GhostOwnerComponent` functionality, for example by checking the `Has Owner` checkbox in the ghost authoring component for this to work.
>
>[!NOTE]
> Per prefab overrides done in the ghost authoring component inspector are disabled for input components and their companion buffer. You can add a ghost component attribute on the input component in code and it will apply to the buffer as well.

### Input events

By using the `InputEvent` type within `IInputComponentData` inputs you can guarantee one off events (for example gathered by `UnityEngine.Input.GetKeyDown`) will be synchronized properly with the server and registered exactly once. Even when the exact input tick where the input event was first registered is dropped on its way to the server.

### How it works

Given an input component data you'll have these systems set up.

- Gather input system
  - Take input events and save in the input component data
- Process input system
  - Take current input component and process the values

With `IInputComponentData` netcode handling it looks like this with code generated systems.

- _Gather input system_
  - _Take input events and save in an input component data_
- Copy input to command buffer
  - Take current input data component and add to command buffer, also recording current tick
- Apply inputs for current tick to input component data
  - Retrieve inputs from command buffer for current tick and apply to input component. With prediction multiple input values could be applied as prediction rolls back (see [Prediction](prediction.md)).
- _Process input system_
  - _Take current input component and process the values_

The first and last steps are the same as with the single player input handling, and these are the only systems you need to write/manage. An important difference, with netcode enabled input, is that the processing system can be called multiple times per tick as previous ticks (rollback) are handled.

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

Input gathering system, it basically takes current inputs and applies to the input component data on the local players entity.

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

        var networkId = GetSingleton<NetworkIdComponent>().Value;
        Entities
            .WithName("GatherInput")
            .ForEach((ref PlayerInput inputData, ref GhostOwnerComponent owner) =>
            {
                if (owner.NetworkId != networkId)
                    return;

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

Processing input system, takes current input values stored on the players input component and applies the equivalent movement actions.

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

In order to implement serialization manually you need to add the `[NetCodeDisableCommandCodeGen]` attribute to the struct implementing the `ICommandData` interface.

You will also need to create a struct implementing `ICommandDataSerializer<T>` - where `<T>` is your `ICommandData` struct.

[ICommandDataSerializer](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ICommandDataSerializer-1.html) has two __Serialize__ and two __Deserialize__ methods: one pair for raw values, and one pair for delta compressed values. The system sends multiple inputs in each command packet. The first packet contains raw data but the rest are compressed using delta compression. Delta compression compresses inputs well because the rate of change is low.

As well as creating a struct you need to create specific instances of the generic systems `CommandSendSystem` and `CommandReceiveSystem`. To do this, extend the base system, for example with
```c#
[UpdateInGroup(typeof(CommandSendSystemGroup))]
[BurstCompile]
public partial struct MyCommandSendCommandSystem : ISystem
{
    CommandSendSystem<MyCommandSerializer, MyCommand> m_CommandSend;
    [BurstCompile]
    struct SendJob : IJobEntityBatch
    {
        public CommandSendSystem<MyCommandSerializer, MyCommand>.SendJobData data;
        public void Execute(ArchetypeChunk chunk, int orderIndex)
        {
            data.Execute(chunk, orderIndex);
        }
    }
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        m_CommandSend.OnCreate(ref state);
    }
    [BurstCompile]
    public void OnDestroy(ref SystemState state)
    {}
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
    struct ReceiveJob : IJobEntityBatch
    {
        public CommandReceiveSystem<MyCommandSerializer, MyCommand>.ReceiveJobData data;
        public void Execute(ArchetypeChunk chunk, int orderIndex)
        {
            data.Execute(chunk, orderIndex);
        }
    }
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        m_CommandRecv.OnCreate(ref state);
    }
    [BurstCompile]
    public void OnDestroy(ref SystemState state)
    {}
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var recvJob = new ReceiveJob{data = m_CommandRecv.InitJobData(ref state)};
        state.Dependency = recvJob.Schedule(m_CommandRecv.Query, state.Dependency);
    }
}
```

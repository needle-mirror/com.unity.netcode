# Command stream

The client continuously sends a command stream to the server. This stream includes all inputs and acknowledgements of the last received snapshot. When no commands are sent a [NullCommandSendSystem](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NullCommandSendSystem.html) sends acknowledgements for received snapshots without any inputs. This is an automatic system to make sure the flow works automatically when the game does not need to send any inputs.

To create a new input type, create a struct that implements the `ICommandData` interface. To implement that interface you need to provide methods for accessing the `Tick` as well as `Serialize` and `Deserialize`.

[ICommandData](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ICommandData-1.html) has two __Serialize__ and two __Deserialize__ methods: one pair for raw values, and one pair for delta compressed values. The system sends multiple inputs in each command packet. The first packet contains raw data but the rest are compressed using delta compression. Delta compression compresses inputs well because the rate of change is low.

As well as creating a struct you need to create specific instances of the generic systems `CommandSendSystem` and `CommandReceiveSystem`. To do this, extend the base system, for example with `class MyCommandSendSystem : CommandSendSystem<MyCommand>{}.`

As well as setting the input buffer on an entity, your game code must also set the [CommandTargetComponent](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.CommandTargetComponent.html) on the connection entity to reference the entity that the `ICommandData` component has been attached to.

You can have multiple command systems, and NetCode selects the correct one based on the `ICommandData` type of the entity that points to `CommandTargetComponent`.

When you need to access inputs on the client and server, it is important to read the data from the `ICommandData` rather than reading it directly from the system. If you read the data from the system the inputs won’t match between the client and server so your game will not behave as expected. 

When you need to access the inputs from the buffer, you can use an extension method for `DynamicBuffer<ICommandData>` called `GetDataAtTick` which gets the matching tick for a specific frame. You can also use the `AddCommandData` utility method which adds more commands to the buffer.
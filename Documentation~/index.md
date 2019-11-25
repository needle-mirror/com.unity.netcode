# Unity NetCode

The `com.unity.netcode` package make it possible to write multiplayer games using a dedicated server model with client prediction. The main feature areas of the NetCode package are listed in this document.

This document is a brief description of the various parts that makes up the new netcode in Unity, it is not yet full documentation of how it works and what the APIs are since everything is still in development.

The new NetCode for Unity is being prototyped in a small asteroids game. We deliberately chose a very simple game for prototyping since it allows us to focus on the netcode rather than the gameplay logic. The NetCode is also used in the DotsSample to get a more realistic test.

The netcode is still in a very early stage. The main focus has been on figuring out a good architecture for synchronizing entities when using ECS, there has not yet been a lot of focus on how we can make it easy to add new types of replicated entities or integrate it with the gameplay logic. These are areas we will be focusing on going forward, and areas where we want feedback.

## Client / Server world

The first part of the netcode is a strong separation of client and server logic into separate worlds. This is based on the new hierarchical update system in ECS. By default the NetCode package will place systems in both the client and the server world, but not in the default world. The exception to this is systems updating in the ```PresentationSystemGroup``` which will only be added to the client world.

It is possible to override this behaviout using the ```UpdateInWorld``` attribute or by using the ```UpdateInGroup``` attribute with an explicit client/server system group. The available explicit client/server groups are ```ClientInitializationSystemGroup```, ```ServerInitializationSystemGroup```, ```ClientAndServerInitializationSystemGroup```, ```ClientSimulationSystemGroup```, ```ServerSimulationSystemGroup```, ```ClientAndServerSimulationSystemGroup``` and ```ClientPresentationSystemGroup```. Note that there is no server presentation system group.

In addition to the attributes there is a small inspector under `Multiplayer > PlayMode Tools` which you can use to choose what should happen when entering PlayMode, you can make PlayMode client only, server only or client with in-proc server, and you can run multiple clients in the same process when entering PlayMode - all in separate worlds. In the same inspector you can disconnect clients and decide which client should be presented if you have multiple. The switching of presented client simply stops calling update on the ```ClientPresentationSystemGroup``` for the worlds which are not presented, so your game code needs to be able to handle that.
It is also possible to add thin clients instead of regular clients. Thin clients cannot be presented and they never spawn any entities received from the server, but they can still generate fake input to send to the server in order to simulate realisitc load.

### Bootstrap

The default bootstrap will create the client and server worlds automatically at startup and populate them with the systems defined by the attributes mentioned above. This is usually what you want to do in the editor - but when building a standalone game you sometimes want to delay the world creation so the same executable can be used as both a client and a server.
In order to do this it is possible to override the default bootstrap by creating a class extending ```ClientServerBootstrap```. You need to implement ```Initialize``` and create the default world, at a later point you can create the client and server worlds manually by calling ```ClientServerBootstrap.CreateClientWorld(defaultWorld, "WorldName");``` or ```ClientServerBootstrap.CreateServerWorld(defaultWorld, "WorldName");```.
```c#
public class ExampleBootstrap : ClientServerBootstrap
{
    public override bool Initialize(string defaultWorldName)
    {
        var systems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default);
        GenerateSystemLists(systems);

        var world = new World(defaultWorldName);
        World.DefaultGameObjectInjectionWorld = world;

        DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, ExplicitDefaultWorldSystems);
        ScriptBehaviourUpdateOrder.UpdatePlayerLoop(world);
        return true;
    }

}
```

### Fixed / dynamic timestep

When writing a game with the Unity NetCode the server will always update at a fixed timestep. The maximum number of iterations are limited to make sure it does not end up in a state where it takes several seconds to simulate a single frame.
The fixed update is not using the standard Unity update frequency, it is controlled by a singleton entity in the server world with a ```ClientServerTickRate``` component. The ```ClientServerTickRate``` component can control ```SimulationTickRate``` - number of simulation ticks per second, ```NetworkTickRate``` - number of simulation states send from the server per second, ```SimulationTickRate``` must be divisible by ```NetworkTickRate```. The default for these are 60 for both. The component also has values for ```MaxSimulationStepsPerFrame``` controlling how many simulation the server may run in a single frame and ```TargetFrameRateMode``` which controls how the server should keep the tick rate. Possible values are ```BusyWait``` for runnin at maximum speed, ```Sleep``` for used ```Application.TargetFrameRate``` to reduce CPU load or ```Auto``` to use ```Sleep``` on headless servers and ```BusyWait``` otherwise.

The client is by default updating at a dynamic time step, with the exception of prediction code which is always running at fixed time step to match the server. The prediction runs in the ```GhostPredictionSystemGroup``` and it applies its own fixed time step specifically for prediction. It is possible to make the client use a fixed timestep by creating a singleton entity with the ```FixedClientTickRate``` component. When using fixed tick rate there are systems which can handle render interpolation if you add a ```CurrentSimulatedPosition``` and ```CurrentSimulatedRotation``` component to the entity you want to interpolate.

## Network connection

The network connection uses the Unity Transport package and stores each connection as an entity. Each connection entity has a ```NetworkStreamConnection``` component with the Transport handle for the connection. The connection will also have a ```NetworkStreamDisconnected``` component for one frame after disonnecting before the entity is destroyed. Disconnect can be requested by adding a ```NetworkStreamRequestDisconnect``` component to the entity, directly disconnecting using the driver is not supported.
A connection can be marked as being in-game by adding the ```NetworkStreamInGame``` component. This is never done automatically, it must be done by the game. Before this component is added to the connection sending of commands and snapshots is not enabled.
In order for commands to be stored in the correct buffer each connection also has a ```CommandTargetComponent``` which must point to the entity where the received commands should be stored. THe game is reponsible for keeping this entity reference up to date.
Each connection has three incoming buffers for each type of stream, command, rpc and snapshot. There is an outgoing buffer for rpcs but snapshots and commands are gathered and sent in their respective send systems. When a snapshot is received it is available in the incoming snapshot buffer. The same method is used for the command stream and the RPC stream.

When the game starts you need to manually start listening for connection on the server or conenct to a server from the client. This is not done automatically since there are not sensible defaults. To establish a connection you need to get the ```NetworkStreamReceiveSystem``` from the correct worl and call either ```Connect``` or ```Listen``` on it.

## RPCs

The netcode handles events by a limited form of RPC calls. The RPC calls can be issued from a job on the sending side, and they will execute in a job on the receiving side - which limits what you can do in an RPC.
In order to send an RPC you first need to get access to an RpcQueue for the command you want to send. This can be created in OnCreateManager - by calling ```m_RpcQueue = World.GetOrCreateManager<RpcSystem>().GetRpcQueue<RpcLoadLevel>();``` -  and cached through the lifetime of the game. Once you have the queue you can schedule events in it by getting the ```OutgoingRpcDataStreamBufferComponent``` buffer from the entity representing the connection you wish to send the event to and calling ```rpcQueue.Schedule(rpcBuffer, new RpcCommand);```. That will append the correct RPC data to the outgoing RPC buffer (```OutgoingRpcDataStreamBufferComponent```) so it can be sent to the remote end by ```NetworkStreamSendSystem```.
The RpcCommand interface has three methods, Serialize and Deserialize for storing the data in a packet and a CompileExecute method which should use burst to create a FunctionPointer. The function it compiles takes one parameter by ref, a struct containing: ```DataStreamReader reader, Entity connection, EntityCommandBuffer.Concurrent commandBuffer, int jobIndex``` . Since the function is static it must read the struct data using ```Deserialize``` before it can execute the RPC. The RPC can either modify the connection entity using the command buffer, or it can create a new request entity using the command buffer for more complex tasks, and apply the command in a separate system at a later time. This means that there is no need to do anything special to receive an RPC, it will have its ```Execute``` method called on the receiving end automatically.

### RPC Command Request component

Creating new entities which server as requests for another system to perform processing is by far the most common operation. To reduce the boilerplate for this case there is a set of helpers for doing this. You create a component extending ```IRpcCommand``` and implement the interface methods. In the execute method you compile you call ```RpcExecutor.ExecuteCreateRequestComponent<HeartbeatComponent>(ref parameters);``` and you add ```class HeartbeatComponentRpcCommandRequestSystem : RpcCommandRequestSystem<HeartbeatComponent>{}``` somewhere in out codebase. The goal is to generate the code for these methods in the future in a similar way to ```[GenerateAuthoringComponent]``` in entities.
Once the command request has been created you can send it to a remote end by creating and entity with the specific ```IRpcCommand``` and a ```SendRpcCommandRequestComponent``` with the target connection entity. If the target connection entity is ```Entity.Null``` the request will be sent to all connections. The system will automatically find these request, send them and delete the send request. On the remote side they will show up as entities with the same ```IRpcCommand``` and a ```ReceiveRpcCommandRequestComponent``` which can be used to identify which connection the request was received from.

## Command stream

The client will continuously send a command stream to the server. This stream includes all inputs and acknowledgements of the last received snapshot. When no commands are being sent a ```NullCommandSendSystem``` sends acks for received snapshots without any inputs. This is an automatic system to make sure the flow works automatically when the game does not need to send any inputs.

To create a new input type you must create a struct implementing the ```ICommandData``` interface. In order to implement that interface you must provide methods for accessing the ```Tick``` as well as ```Serialize``` and ```Deserialize```. There are two variations of serialize and deserialize, one which can delta compress and one which sends raw values. The system sends multiple inputs in each command packet, the first one is raw data but the rest are compressed using delta compression. Inputs tend to compress very well using delta compression since the rate of change is low.

In addition to creating a struct you need to create specific instances of the generic systems ```CommandSendSystem``` and ```CommandReceiveSystem```. You can do that by extending the base system, for example ```class MyCommandSendSystem : CommandSendSystem<MyCommand>{}```.

Just having the input buffer on an entity is not enough for the netcode to start using it. The game code must also set the ```CommandTargetComponent``` on the connection entity to reference the entity to which the ```ICommandData``` component has been attached.

It is possible to have multiple command systems, the netcode will pick the correct one based on which ```ICommandData``` type the entity pointed to be ```CommandTargetComponent``` has.

When accessing inputs on the client and server it is important to read the data from the ```ICommandData``` rather than reading it directly from the system. If the data is read directly from the system the inputs will not match between the client and server so the game will not behave as expected. When accessing the inputs from the buffer there is an extension method for ```DynamicBuffer<ICommandData>``` called ```GetDataAtTick``` which can be used to get the matching tick for a specific frame. There is also an ```AddCommandData``` utility method which should be used to add more commands to the buffer.

## Ghost snapshots

The ghost snapshot system is the most complex part of the netcode. It is responsible for synchronizing entities which exist on the server to all clients. In order to make it perform well the server will do all processing per ECS chunk rather than per entity. On the receiving side the processing is done per entity. The reason for this is that it is not possible to process per chunk on both sides, and the server has more connections than clients.

### Ghost authoring component
The system is based on specifying ghosts as prefabs with the ```GhostAuthoringComponent``` on them. The ```GhostAuthoringComponent``` has a small editor which is used to configure how the various components and fields in the prefab will be synchronized as well as where the components will be available.

When a ```GhostAuthoringComponent``` has been added to the prefab you need to click the "Update component list" button in the inspector. This will determine which entities the prefab will have after conversion and add them to the component list.

The list is used to configure the ghost. There are checkboxes which are used to select which version of the prefab will have a component. If you for example uncheck "server" from "RenderMesh" the ghost will not have a RenderMesh when it is instantiated on the server, but it will have it when instantiated on the client. The defaults for these checkboxes can be controlled from source by adding the ```GhostDefaultComponentAttribute``` to an IComponentData.

For each component you need to decide which values to synchronize and which quality they need. You can setup defaults for the values by adding the ```GhostDefaultFieldAttribute``` attribute to the fields in an IComponentData.
The default values for synchronization can be overridden for a specific ghost by checking the "Manual Field List" checkbox and editing them.

In addition to adding attributes it is possible to override defaults for components which you do not have source access to by creating an entry to ```GhostAuthoringComponentEditor.GhostDefaultOverrides```.

There are a few more parameters which need to be setup on each ghost. The importance and default client instantiation type. The importance is used to control which entities are sent when there is not enough bandwidth to send all, higher value makes it more likely that the ghost will be sent.
The default client instantiation type can be `Interpolated` which means all ghosts received from the server will be treated as interpolated, `Predicted` which means all ghost received will be treated as prediced, or `Owner predicted` which means the ghost will be predicted for the client that owns it and interpolated for all otehr clients. When selecting `Owner predicted` you must also specify which field the owner network id is stored in. This field is compared to each clients network id to find the correct owner.
The default client instantiation is just the default. It is possible to override it by creating a partial class with the same name as the ghost spawner and implement ```MarkPredictedGhosts```.

The paths to all generated code needs to be specified. It is possible to override the defaults for this path on a per project basis to avoid having to manually edit all of them, see the statics in ```GhostAuthoringComponentEditor```, ```DefaultRootPath```, ```DefaultSnapshotDataPrefix```, ```DefaultUpdateSystemPrefix``` and ```DefaultSerializerPrefix```. There is also a static to put all generated code in a namespace, ```DefaultNamespace```.

Once everything is configured you must click the generate code button. When a component is changed you must also click both these buttons to make sure the changes are detected and new code is generated.

### Ghost collection

In order for the ghost systems to have a way of identifying the ghosts between the client and server there must be a `GhostCollection`. A GhostCollection is an entity containing a list of all ghosts the netcode can handle. It is used for identifying ghost types and also to spawn ghosts on the client with the correct prefab. It can also be used to spawn ghosts on the server in a programatic way.

When the server determinies which ghost type to use for a ghost it goes through the list of ghosts in the GhostCollection and picks the first one which matches. This means that the order of ghosts in the collection is important.

The inspector for ```GhostCollectionAuthoringComponent``` has buttons to scan for prefabs with ```GhostAuthoringComponent```, regenerate the code for all ghosts in the list and generate the code for the collection.

For the netcode to work the code for the collection must be generated and the ghost collection must be part of the cient and server entity worlds.

### Value types

The codegen does not support all value types, but it is possible to add support for more types by creating a new template and registering it by adding a custom ```GhostSnapshotValue``` to ```GhostSnapshotValue.GameSpecificTypes```.

### Importance
The server operates on a fixed bandwidth, sending a single packet with snapshot data of customizable size every network tick. The packet is filled with the entities of the highest importance. Once a packet is full it is sent and remaining entities will be missing from the snapshot. Since the age influences the importance it is more likely that those entities will be included in the next snapshot. The importance is only calculated per chunk, not per entity.

#### Distance based importance
The importance per chunk can be scaled by a custom user defined function. If a singleton entity with the ```GhostDistanceImportance``` component on it exists on the server the netcode will make sure that all ghosts in the world are split into groups based on the tiles they are in defined by the tile size in that singleton. Each connection must have a ```GhostConnectionPosition``` component added to them to determine which tile the connection should prioritize.
This information is passed to the ```ScaleImportanceByDistance``` in ```GhostDistanceImportance``` which can use it to scale the importance of a chunk based on distance in tiles or some other metric.

### Entity spawning

When a new ghost is received on the client side it will be spawned by a user defined spawn system. There is no specific spawn message, receiving an unknown ghost id counts as an implicit spawn. The spawn system can be code generated along with the serializer and the logic for snapshot handling in general for he entity (snapshot updates).

Because of how snapshot data is interpolated the entity spawning/creation needs to be handled in a special manner. The entity can't be spawned immediately unless it was preemptively spawned (like with spawn prediction), since the data is not ready to be interpolated yet. Otherwise the object would appear and then not get any more updates until the interpolation is ready. Therefore normal spawns happen in a delayed manner. Spawning can be split into 3 main types.

1. Delayed or interpolated spawning. Entity is spawned when the interpolation system is ready to apply updates. This is how remote entities are handled as they are being interpolated in a straightforward manner.
2. Predicted spawning for the client predicted player object. The object is being predicted so input handling applies immediately, therefore it doesn't need to be delay spawned. As snapshot data for this object arrives the update system handles applying the data directly to the object and then playing back the local inputs which have happened since that time (correcting mistakes in prediction).
3. Predicted spawning for player spawned objects. These are objects spawned from player input, like bullets or rockets being fired by the player. The spawn code needs to run on the client, in the client prediction system, then when the first snapshot update for the entity arrives it will apply to that predict spawned object (no new entity is created). After this the snapshot updates are applied just like in case 2.

Handling the third case of predicted spawning requires some user code to be implemented to handle it. Code generation will handle creating some boilerplate code around a prefab ```GhostAuthoringComponent``` entity definition. The entity specific spawning can be extended by implementing a partial class of the same name as the generated spawn class. A function called ```MarkPredictedGhosts``` is called so you can assign a specific ID for that type of prediction. This method must contain user defined logic to match a ghost from the server with a predicted spawned local entity.

Eentities on the client are spawned with a prefab stored in the ```GhostPrefabCollectionComponent``` singleton. The prefab is created by having a ```GhostCollectionAuthoringComponent``` and a ```ConvertToClientServerEntity``` component using Client as the conversion target.

### Snapshot visualization

In order to reason about what is being put on the wire in the netcode we have a small prototype of a visualization tool in the Stats folder. The tool will display one vertical bar for each received snapshot with breakdown of ghost types in that snapshot. Clicking a bar will display more detailed stats about the snapshot. This tool is a prototype, in the future it will be integrated with the unity profiler to make it easier to correlate network traffic with memory usage and CPU performance.
The tool can be opened from the `Multiplayer > Open NetDbg` menu.

## Time synchronization

Since the netcode is using a server authoritative model the server is executing a fixed time step based on how much time has passed, but the client needs to match the server time at all time for the model to work.

The client should present the tick the server will simulate right after the commands the client sends it now arrives, which has not yet happened. This is called the prediction time.
Calculating which server time to present on the client is handled by the ```NetworkTimeSystem```. The network time system will calculate an initial estimate of server time based on round trip time and latest received snapshot from the server.
Once the client has an initial estimate it will try to adjust to changes be making time progress slightly faster or slower rather than doing big changes to current time.
In order to make acurate adjustments the server will track how long before they are used the commands arrive. This is sent back to the client and the client tries to adjust its time in a way that commands arrive a little bit before they are required.

The prediction time - as the name implices - should only be used for predicted object like the local player. For interpolated objects the client should present them in a state it has received data for.
This time, called interpolation time, is calculated as a time offset from the prediction time. The offset is called prediction delay and it is slowly adjusted up and down in small increments to keep the interpolation time advancing at a smooth rate.
The interpolation delay is calculated from round trip time and jitter in a way that the data is generally available. The delay also adds additional time based on the network tick rate to make sure it can handle a packet being lost.

The time offsets and scales discussed in this section can be visualized as graphs in NetDbg.

# Getting started

If you just want to get started making a multiplayer game the [quickstart](quickstart.md) will walk you through how to setup a simple multiplayer enabled project.

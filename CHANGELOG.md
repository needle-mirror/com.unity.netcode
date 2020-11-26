# Change log

## [0.6.0] - 2020-11-26
### New features
* Added DynamicBuffers serialization support to ghosts. Like IComponentData, is now possible to annotate IBufferElementData with GhostComponentAttribute and members with GhostFieldAttribute
and having the buffers replicated through the network.
* ICommandData are now serializable and can be sent to the remote players.
* Added new SendToOwner property to the GhostComponentAttribute that can be used to configure to witch
subset of players the component should be sent to: only to the owner, only to the non owner, or all.
* Ghost Component Serialization Variant. A new GhostComponentVariation attribute has been introduced that let you to specify different serialization options for a component or buffer, by overriding
the `[GhostField]` and `[GhostComponent]` properties present in the original type definition.
* It is possible to prevent a component to support variation by using the `[DontSupportVariation]` attribute. When present, if a GhostComponentVariation is defined for that type, an exception is triggered.
* Ghost components attributes and serialization variants can be customized per prefabs. For every component in a ghost prefab, it is possible to change:
    * PrefabType
    * GhostSendType
    * The variant to use, if variants for that component exist.
* Is possible to prevent a component to support per-prefab overrides by using the [DontSupportPrefabOverride] attribute. When present, the component can't be further customized in the inspector.
* It's now possible to register a prediction smoothing function, by calling the `GhostPredictionSmoothingSystem.RegisterSmoothingAction<ComponentType>(SmoothingActionDelegate)` and supplying a `ComponentType` and `GhostPredictionSmoothingSystem.SmoothingActionDelegate` (see Runtime/Snapshot/DefaultUserParams.cs for an example).
* Added a new `ClientTickRate` component which when added to a singleton entity controls the interpolation times used to calculate time on the client. The default values can be accessed through the static `NetworkTimeSystem.DefaultClientTickRate`.
* Added support for extrapolation when the tick being applied to interpolated ghosts on the client has not been received yet and is outside the interpolation delay. Set the new `Smoothing` field in the `GhostField` attribute to `SmoothingAction.InterpolateAndExtrapolate` to enable extrapolation.
* Added a `MaxSmoothingDistance` parameter to the `[GhostField]` attribute. If specified interpolation will be disabled when the values change more than that limit between two snapshots. This is useful for dealing with teleportation and similar changes which should not be interpolated.

### Changes
* It is no longer required to create a ghost collection, as long as there is a prefab for a ghost it will be picked up automatically. You can create a prefab by referencing it in a spawner component or by placing a pre spawned instance of a ghost.

### Fixes
* Fixed an issue where the elapsed time was not using the max simulation rate - causing the fixed time step physics to take more and more time.
* Fixed an issue causing time rollbacks when running client and server in the editor if performance is too low.

### Upgrade guide
The `Interpolate` bool in the `GhostField` attribute has been replaced with `Smoothing`. Replace `Interpolate=true` with `Smoothing=SmoothingAction.Interpolate` to keep the old value, or set it to `SmoothingAction.InterpolateAndExtrapolate` to enable extrapolation.

## [0.5.0] - 2020-10-01
### New features
* Added RpcSystem.DynamicAssemblyList which can be used to delay the checksums for RPCs and ghost components when the set of assemblies are different on the client and server.
* Added to RPC and Command the possiblity to send Entity reference from both client and server.

### Changes
* Change the system ordering to be compatible with latest physics. `NetworkTimeSystem` has moved to `ClientInitializationSystemGroup`. The SimulationSystemGroup runs `GhostSpawnSystemGroup` (client), `GhostReceiveSystemGroup` and `GhostSimulationSystemGroup` before `FixedStepSimulationSystemGroup` where physics is running. `RpcCommandRequestSystemGroup`, `RpcSystem` and `GhostSendSystem` (server) is running at the end of the frame, after all simulation code. Other systems has been moved into one of the groups.
* Created a new `GhostInputSystemGroup` where systems adding inputs to the input buffer should run.

### Fixes
### Upgrade guide
* The systems adding input to the `ICommandData` buffer needs to be moved to `GhostInputSystemGroup`

## [0.4.0] - 2020-09-10
### New features
* Code gen support for ICommandData, serialization for command data can now be generated instead of hand-written. You can opt out of code generation by adding `[NetCodeDisableCommandCodeGen]`.
* `NetCodeConversionSettings` has a new Client And Server mode, which makes it possible to build a single standalong build supporting both client and server.
* There is a new static method to generate predicted spawn version of a prefab, `GhostCollectionSystem.CreatePredictedSpawnPrefab`.

### Changes
* When not using code-gen for rpcs or commands the systems for registering them (the ones extending `RpcCommandRequestSystem<TActionSerializer, TActionRequest>`, `CommandSendSystem<TCommandDataSerializer, TCommandData>` and `CommandReceiveSystem<TCommandDataSerializer, TCommandData>`) need some more code to setup the jobs.
* The `ICommandData` interface no longer takes an additional generic type.
* Added a `CommandSendSystemGroup` and a `CommandReceiveSystemGroup` which can be used for dependencies when generating code for `ICommandData`.
* Moved the GameObjects used for authoring to a separate assembly.
* Fixed tickrate on the client is no longer supported. This also means that the render interpolation has been removed.
* Using multiple rendering clients in the editor is no longer supported, thin clients are still supported.
* The `GhostPrefabCollectionComponent` now only contains a single prefab list, and the `GhostPrefabBuffer` for it is attached to the same entity.

### Deprecated
* Deprecated `ConvertToClientServerEntity`, please use the sub-scene conversion workflow instead.

### Fixes
* Fixed a compile error in the generated code for components containing multiple ghosted Entity references.
* Fixed a bug where predicted spawn ghosts were not destroyed on mis-prediction.
* Fixed a bug where data for child entities on predicted ghosts could be corrupted.

### Upgrade guide
* The predicted spawn code must switch to using the new `GhostCollectionSystem.CreatePredictedSpawnPrefab` utility method since there is only a single prefab on the client and it requires some patching before it can be used.
* When using the `GhostPrefabCollectionComponent` to find a prefab to find a ghost prefab on the server you must change the code to read the `GhostPrefabBuffer` from the same entity as `GhostPrefabCollectionComponent`.
* If you are using fixed tickrate mode on the client you need to remove the creation of the `FixedClientTickRate` singleton and remove the `CurrentSimulatedPosition` and `CurrentSimulatedRotation` if using them.
* If you are using "Num Clients" in the PlayMode tools you need to move to using "Num Thin Clients" instead.
* RPCs not using code-gen needs to add more code to the `RpcCommandRequestSystem`. The new implementation should look like this:
```c#
class MyRequestRpcCommandRequestSystem : RpcCommandRequestSystem<MyRequestSerializer, MyRequest>
{
    [BurstCompile]
    protected struct SendRpc : IJobEntityBatch
    {
        public SendRpcData data;
        public void Execute(ArchetypeChunk chunk, int orderIndex)
        {
            data.Execute(chunk, orderIndex);
        }
    }
    protected override void OnUpdate()
    {
        var sendJob = new SendRpc{data = InitJobData()};
        ScheduleJobData(sendJob);
    }
}
```
* The `Tick` property in `ICommandData` now requires both a getter and a setter.
* ICommandData structs no longer need serialization or implementaions of `CommandSendSystem` and `CommandReceiveSystem` if you are using code-gen, and the interface changed from `ICommandData<T>` to `ICommandData`.
* When manually writing serialization code for `ICommandData` you need to move the serialization code to a struct implementing `ICommandDataSerialize<T>`, and the `CommandSendSystem` and `CommandReceiveSystem` implementations need code to schedule the jobs like this:
```c#
public class MyCommandSendCommandSystem : CommandSendSystem<MyCommandSerializer, MyCommand>
{
    [BurstCompile]
    struct SendJob : IJobEntityBatch
    {
        public SendJobData data;
        public void Execute(ArchetypeChunk chunk, int orderIndex)
        {
            data.Execute(chunk, orderIndex);
        }
    }
    protected override void OnUpdate()
    {
        var sendJob = new SendJob{data = InitJobData()};
        ScheduleJobData(sendJob);
    }
}
public class MyCommandReceiveCommandSystem : CommandReceiveSystem<MyCommandSerializer, MyCommand>
{
    [BurstCompile]
    struct ReceiveJob : IJobEntityBatch
    {
        public ReceiveJobData data;
        public void Execute(ArchetypeChunk chunk, int orderIndex)
        {
            data.Execute(chunk, orderIndex);
        }
    }
    protected override void OnUpdate()
    {
        var recvJob = new ReceiveJob{data = InitJobData()};
        ScheduleJobData(recvJob);
    }
}
```

## [0.3.0-preview.3] - 2020-08-21
### New features
* New workflow for generating serialization code for ghosts. In the new workflow code is generated per component and there is no codegen per ghost type prefab.
  * The ghost fields and ghost components are now configured in code with `GhostField` and `GhostComponent` attributes where you can configure parameters for prediction, interpolation, quantization and so on.
  * The ghost component and collection inspectors now only show you how the ghosts have been configured in code.
  * Ghosts can now be generated on demand and you don't need to explicitly push a button to do it.
  * A new ghost compiler windows allows you to change how the generation is handled, like changing from on demand generation to manual) and shows you if any ghost is out of sync.
* Code gen support for RPCs, an RPC can be created without writing serialization by hand. Implement the `IRpcCommand` interface and write it like you would a regular `IComponentData` - the serialization code will be generated for you.
* GhostGroups are now supported. A ghost prefab can have a `GhostGroup` buffer added to it at authoring time, all ghosts listed in that buffer are guaranteed to be sent together with the main entity. In order to be in a group the child ghost must have the `GhostChildEntityComponent` component added to it. The `GhostChildEntityComponent` can be added at runtime when moving the child entity into the group.
* Relevancy support has been added. By changing `GhostSendSystem.GhostRelevancyMode` to `GhostRelevancyMode.SetIsRelevant` or `GhostRelevancyMode.SetIsIrrelevant` on the server and adding ghosts to `GhostSendSystem.GhostRelevancySet` you can limit the set of ghosts which are sent to a specific client.
* Added an optimization mode to ghosts, the new static optimization mode will use less aggressive delta compression which allows us to stop sending data completely when no entities in a chunk have been modified.
* Added visualization of prediction errors to the NetDbg.
* A connection entity on the server can have a `NetworkStreamSnapshotTargetSize` which is used to control the target size for snapshots.
* Added `GhostReceiveSystem.GhostCountOnServer` and `GhostReceiveSystem.GhostCountOnClient` which can be used to check how many ghosts a client should have and how many it does have.

### Changes
* Support for `NativeString64` has been replaced by support for `FixedString64`. Support for `FixedString32`, `FixedString128`, `FixedString512` and `FixedString4096` has also been added.
* In dynamic timestep mode it is now possible to resume prediction from the last full predicted tick instead of rolling back to the latest received snapshot when no new data has been received.
* Added a `DisableLagCompensationComponent` which when added as a singleton prevents the lag compensation system from running.

### Fixes
* Quaternions are renormalized after dequantization to make sure they are still valid rotations.
* Floats are rounded to the nearest int after quantization to improve acuracy.
* It is now possible to send more than one packet with RPC commands per frame, previously commands could be silently dropped when doing that.

### Upgrade guide
* `NativeString64` is no longer uspported, change your code to use `FixedString64` instead.
* `GhostUpdateSystemGroup` no longer exists, references to it for update order should be replaced with `GhostUpdateSystem`
* NetCode now requires Unity 2020.1.2.

#### New ghost workflow
* Change all `[GhostDefaultField]` to `[GhostField]` and all `[GhostDefaultComponent]` to `[GhostComponent]` in your components. The parameters to the constructors have also changed, you need to specify `[GhostField(Quantization=100, Interpolate=true)]` instead of `[GhostDefaultField(100, true)]`.
* For all ghosts which manually adds fields you must add `GhostField` attributes to the component since manual overrides are no longer supported.
* For all ghosts which removes a component from `Server`, `Interpolated Client` or `Predicted Client` you must add a `[GhostComponent(PrefabType=<type>)]` attribute to the component where `<type>` matches what you had before.
* For all components which you do not want to synchronize when they are on child entities of a ghost you need to add `[GhostComponent(SendDataForChildEntity = false)]`.
* Open all prefabs and verify that `Name`, `Importance` and `Default ghost mode` are still correct. `Supported Ghost Mode` and `Optimization Mode` are new fields and the default values matches what the old workflow did.
* For all ghosts which uses the owner predicted mode you must add a `GhostOwnerComponent` and make sure your code sets the `NetworkId` of that component correctly. Previously you could store the network id in any component and point the `GhostAuthoringComponent` to it.
* For all components which you were only being sent to either interpolated or predicted ghosts when used on owner predicted ghosts you need to add `[GhostComponent(OwnerPredictedSendType = <type>)]` where `<type>` is either `GhostSendType.Interpolated` or `GhostSendType.Predicted`.
* Delete the generated code from the old NetCode version.
* If you are using predictive spawning the new way to request a predictive spawn is to instantiate the predicted client version of the ghost prefab and add a `PredictedGhostSpawnRequestComponent` to the entity.
* Any custom spawn behavior - including matching entities for pre-spawned ghosts - previously implemented in `MarkPredictedGhosts` must be moved to a spawn classification system.
* Any custom code to modify spawned ghosts previously implemented in `UpdateNewPredictedEntities` or `UpdateNewInterpolatedEntities` must be moved to systems running in the `GhostSpawnSystemGroup` after `GhostSpawnSystem`. Use tag components to deterct which ghosts are new.

#### RPC
* If your `IRpcCommand` component only uses `RpcExecutor.ExecuteCreateRequestComponent` in the execute method you can remove the implementations for `Serialize`, `Deserialize`, `CompileExecute` along with the execute method and burst function pointer for it. You also need to remove the `CommandRequestSystem` implementationf for your component. All of those will be generated by code-gen.
* All RPC implementations which still needs manual serialization or execute must be changed to implement `public struct MyRequest : IComponentData, IRpcCommandSerializer<MyRequest>` instead of `public stuct MyRequest : IRpcCommand`.
* The signature for RPC serialization has changed to `void Serialize(ref DataStreamWriter writer, in MyRequest data)` and deserialization has changed to `void Deserialize(ref DataStreamReader reader, ref MyRequest data)`.
* The CommandRequestSystem for rpcs with manual serialization/execute must be changed from `class MyRequestCommandRequestSystem : RpcCommandRequestSystem<MyRequest>` to `class MyRequestCommandRequestSystem : RpcCommandRequestSystem<MyRequest, MyRequest>`

## [0.2.0-preview.5] - 2020-06-05
### New features
* Support for pre-spawned ghosts. When prefab ghost instances are placed into subscenes they will be present on server and clients when they load the scene. They are then automatically connected together and will work just like normally spawned ghosts after that.

### Changes
* Changed how snapshot size is limited to make it more robust and give more clear errors.
* Added `Name` field to the `GhostAuthoringComponent`  which is used during code generation to identify the ghost prefab. By default this is the prefab name but can be changed.
* `ClientServerBoostrap` now correctly use two-phase initialization to initialise all the systems
* Changed `PhysicsWorldHistory.CollisionHistoryBuffer` to return a safe memory reference to the `CollisionHistoryBuffer` instead of copy a large amount of data on the stack.
* Upgrade to Entities 0.11

### Fixes
* Fixed issue with ghost prefabs when they were Variant or Model Prefabs.
* Fixed issue with datastream going out of sync when snapshot desync was detected
* Fixed an issue with `RegisterRPC` in case you try to register a malformed RPC with an invalid pointer
* Fixed an issue with `ServerTick` that does not monotonically increase in presence of high ping
* Fixed an issue with `ClientServerTickRate` being created multiple times if the client connect and disconnect from the server
* Fixed an issue with `ClientServerTickRate` not reused by the client if it was already present in the world
* Fixed an issue with `ClientServerBootstrap` and the fact `TypeManager` was not initialised when we generate client/server world's the system lists

### Upgrade guide

* A `Name` field was added to the `GhostAuthoringComponent` and as a result all prefabs with this component need to be opened and close to serialize the field. It's used as a prefix name during code generation so it might also be neccessary to press the _Generate Code_ button again

## [0.1.0-preview.6] - 2020-02-24
### New features
* Added integration with UnityPhysics, including the lag compensation from DotsSample. To use it you must have the UnityPhysics added to your project.

### Changes
* Unity Transport has been upgraded to 0.3.0 which required some API changes - see "Upgrade guide".
* All `FunctionPointer` instances are cached in statics to reduce the number of calls to compile.
* The helper method RpcExecutor.ExecuteCreateRequestComponent returns the entity it creates.
* Added an interface to NetworkStreamReceiveSystem which is used when creating the driver. It is possible to set NetworkStreamReceiveSystem.s_DriverConstructor to a custom instance during bootstrapping to create drivers in a custom way.
* Removed World.Active workaround since it has been deprecated for a while and is causing problems with conversion at runtime.
* Slightly improved performance by ensuring that all jobs that can be Burst compiled are
* Ghost types are now selected based on the guid of the ghosts prefab asset instead of the archetype. This makes it possible to have multiple different ghosts with the same archetype. If a ghost is not a valid prefab you will get an error during conversion.

### Fixes
* Fixed an issue where ghost prefabs created from GameObject instances were processed by all systems.
* The code gen now only writes files if they are modified.
* Disposing a client or server world will now unregister it from the tick system to prevent errors.
* Take the latency of command age updates into account when calculating time scale to get more stable inputs with high ping.

### Upgrade guide
Unity Transport has been upgraded to 0.3.0 which changes the API for `DataStreamReader` and `DataStreamWriter`.

The `IRpcCommand` and `ICommandData` have been changed to not take a `DataStreamReader.Context`.

The `ISnapshotData` and GhostCollection interfaces have been changed to not take a `DataStreamReader.Context`, all ghosts and collections must be regenerated.

`GhostDistanceImportance.NoScale` and `GhostDistanceImportance.DefaultScale` have been replaced by `GhostDistanceImportance.NoScaleFunctionPointer` and `GhostDistanceImportance.DefaultScaleFunctionPointer` which are compiled function pointers rather than methods.

## [0.0.4-preview.0] - 2019-12-12
### New features
### Changes
* Changed the codegen for NativeString64 to use the serialization in DataStream.

### Fixes
### Upgrade guide

## [0.0.3-preview.2] - 2019-12-05
### New features
### Changes
* Updated the documentation and added a section about prediction.
* Upgraded entities to 0.3.0.

### Fixes
* Fixed a crash when multiple clients disconnected on the same frame.
* Fixed read / write access specifiers in AfterSimulationInterpolationSystem.
* Fixed build errors in non-development standalone builds.

### Upgrade guide

## [0.0.2-preview.1] - 2019-11-28
### New features
### Changes
### Fixes
* Fix compile error in generated serialization code for strings.
* Fix warning when entering playmode with the netcode disabled.

### Upgrade guide

## [0.0.1-preview.6] - 2019-11-26
### New features
* Made it possible to scale importance based on distance to support more ghosts.
* Nested entities constaining replicated data are now supported.
* Entity references now supported as ghost fields. The references are weak references which will resolve to Entity.Null if the target is not guaranteed to exist.
* NativeString64, enums and bools are supported as ghost fields.
* `ClientServerTickRate` added where you can configure certain behavior relating to timestepping. A headless server can be configured to sleep after hitting target framerate to conserve CPU usage.
* Send different data depending on if the entity is predicted or interpolated, some savings can be done in the predicted case.
* Added a protocol version which must match for the connection to succeed.
* Added time graphs and server view to the network debugger.
* Network simulator now supports jitter.

### Changes
* The authoring flow has been improved.
  * `GhostAuthoringComponent` now automatically detects what components an entity has after conversion runs and automatically populates them when you press the "Update component list" button. You no longer need to manually type in each component name.
  * Default values can be defined for certain component types, for example with Translation components you usually want to synchronize the Value field. When default handling has been defined the ghost authoring component uses that when it parses the entity component list.
  * `[GhostDefaultField]` attribute added. This can be added to ghost variable which are to be synchronized. The GhostAuthoringComponent detects these fields.
  * `[GhostDefaultComponent]` attribute added. This can be used to define default behavior for how a component should be synchronized, InterpolatedClient, PredictedClient and Server.
  * `GhostCollectionAuthoringComponent` added. This is where all spawned prefabs can be registered
  * Paths are easier in general as you can set up the root of where you want generated files placed and defaults can be specified in code.
  * Which components result in variable data being replicated are marked in bold in the inspector, so it's easier to see how much data will be sent per ghost.
* Improved snapshot prediction handling
  * Uses the servers delta times now instead of the clients.
  * Support for dynamic timestep and fractional tick prediction.
  * Handles stalls and won't try to replay to far back in the rollback (64 frames is maximum)
  * Less boilerplate code needed to setup a predicted entity, more default handling moved to code generation
  * Added `GhostPredictionSystemGroup` with better calculations for the currently predicting tick on the client
  * Interpolation time is an offset from prediction time to make sure they do not drift.
* Multiple inputs are sent together so dropped inputs have less effect on misprediction.
* Thin clients added, these use fewer resources than full client simulations so it's easier to test with many clients now.
* An RPC heartbeat system was added which only runs when nothing is being sent from the client to the server, preventing a disconnect timeout from happening. As soon as inputs are sent and snapshot synchronization starts, the system stops running.
* RPC boilerplate code reduced, when a component inheriting`IRpcCommandRequestComponentData` is added to an entity along with `SendRpcCommandRequestComponent` it will be sent automatically.
* The client/server world bootstrapping has been simplified, you can now use your own custom bootstrapping more easily and create the client/server worlds depending on which one you want at runtime. By default the world creation in the editor is controlled by the playmode tools.
* `NetCodeConversionSettings` added which makes it possible to specify which type of build you want (client/server) in the subscene build settings workflow.
* Detect when ackmask desyncs occur
* Improved ghost code generation to make it able to regenerate code when there are compile errors.
* Snapshots are now acknowleged when there is no CommandSendSystem.
* Use the entities TimeData struct instead of getting time from Client/ServerSimulationSystemGroup

### Fixes
* The code generation in Ghost Authoring Components now generate imports for user namespaces.
* Code generation triggers an asset database refresh so the modified files are compiled.
* Command inputs now correctly respects the `NetworkStreamInGame` being present before starting transmission.
* Acks can now arrive with bigger interval than 64 ticks
* Generated code no longer requires unsafe code to be enabled in the project.

### Upgrade guide
* Unity 2019.3 is now required (beta 11 minimum) and Entities 0.2.0-preview.
* The `NetCode` folder was moved to a proper package, `com.unity.netcode` which should now be used instead.
* All the netcode was moved to a `Unity.NetCode` namespace.
* `[NotClientServerSystem]` attribute removed, use `[UpdateInWorld(UpdateInWorld.TargetWorld.Default)]` instead, it will do the same thing.
* `GhostPrefabAuthoringComponent` removed, use the new `GhostCollectionAuthoringComponent` instead for setting up ghost data.
* `ClientServerSubScene` removed, it's not needed anymore.
* `NetworkTimeSystem.predictTargetTick` removed, use `GhostPredictionSystemGroup.PredictingTick` instead.
* The interface for RPCs has changed and they no longer require a generated collection.

## [0.0.1-preview.2] - 2019-07-17
### New features
* Added a prefab based workflow for specifying ghosts. A prefab can contain a `GhostAuthoringComponent` which is used to generate code for a ghost. A `GhostPrefabAuthoringComponent` can be used to instantiate the prefab when spawning ghosts on the client. This replaces the .ghost files, all projects need to be updated to the new ghost definitions.
* Added `ConvertToClientServerEntity` which can be used instead of `ConvertToEntity` to target the client / server worlds in the conversion workflow.
* Added a `ClientServerSubScene` component which can be used together with `SubScene` to trigger sub-scene streaming in the client/ server worlds.

### Changes
* Changed the default behavior for systems in the default groups to be included in the client and server worlds unless they are marked with `[NotClientServerSystem]`. This makes built-in systems work in multiplayer projects.
* Made standalone player use the same network simulator settings as the editor when running a development player
* Made the Server Build option (UNITY_SERVER define) properly set up the right worlds for a dedicated server setup. Setting UNITY_CLIENT in the player settings define results in a client only build being made.
* Debugger now shows all running servers and clients.

### Fixes
* Change `World.Active` to the executing world when updating systems.
* Improve time calculations between client and server.

### Upgrade guide
All ghost definitions specified in .ghost files needs to be converted to prefabs. Create a prefab containing a `GhostAuthoringComponent` and authoring components for all required components. Use the `GhostAuthoringComponent` to update the component list and generate code.

## [0.0.1-preview.1] - 2019-06-05
### New features
* Added support systems for prediction and spawn prediction in the NetCode. These can be used to implement client-side prediction for networked objects.
* Added some support for generating the code required for replicated objects in the NetCode.
* Generalized input handling in the NetCode.
* New fixed timestep code custom for multiplayer worlds.

### Changes
* Split the NetCode into a separate assembly and improved the folder structure to make it easier to use it in other projects.
* Split the Asteroids sample into separate assemblies for client, server and mixed so it is easier to build dedicated servers without any client-side code.
* Upgraded Entities to preview 33.

### Fixes
### Upgrade guide

## [0.0.1-preview.0] - 2019-04-16
### New features
* Added a new sample asteroids game which we will be using to develop the new netcode.

### Changes
* Update to Unity.Entities preview 26

### Fixes
### Upgrade guide
Unity 2019.1 is now required.

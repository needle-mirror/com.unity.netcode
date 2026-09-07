# Upgrading from 1.x to 7.0

## NetCode to Netcode rename

This package's namespace used NetCode with capital C in quite a few places. This version updates all namespaces and types names that uses the NetCode casing to use the Netcode with lower case c name. Auto updaters are in place to make this transition easier. Please report any issues with the auto update process.

### Manual Updates needed

There's cases where the auto updaters won't work.
1. Custom serializers (see NetcodeSamples' Assets/Samples/CustomSerializer/CustomChunkSerializer.cs for an example) aren't updatable automatically. To update, change Unity_NetCode_Generated_Unity_NetCode to Unity_NetCode_Generated_Unity_Netcode and update your Unity.NetCode using to use Unity.Netcode.
2. Users that have defined custom templates (see NetcodeSamples' Assets/Samples/NetCodeGen/UserDefined/UserDefinedTemplates.cs and Assets/Samples/NetCodeGen/UserDefined/UserDefinedGhostSubTypes.cs) will have to fix their Netcode namespace themselves. Unity.NetCode to Unity.Netcode.

### Duplicate Type Suggestions in your IDE

Types like NetcodeConfig have shims to allow for auto updating that still live in the old namespace. If trying to "Find Symbol" or auto complete on those few types, your IDE might suggest two versions, one with capital C and the other with lower case c. You can setup IDEs to ignore EditorBrowsable(never) APIs. Most don't by default so you'll likely have to change that setting.
We have added a NETCODE_NO_OBSOLETE_HELPER define symbol you can set in your project settings to define out those duplicated types once you're done with updating your project.

### Auto Updater Notes

It can happen the auto updater doesn't fix all your assemblies in one go. If this happens, please retrigger your compilation a second time to let the auto updater continue applying its changes.

The auto updater will prefix all your usages of netcode types with a Unity.Netcode. e.g. `new GhostOwner` becomes `new Unity.Netcode.GhostOwner`. This can be noisy. You might want to add a `using Unity.Netcode;` to all your files and let your IDE automatically cleanup those prefixes. The auto updater will help your project get into a working state, not necessarily a clean code one.
For example with bash you can do the following
```
git diff -G "Unity.NetCode" --name-only -- "*.cs" > filesWithDiff.txt
cat filesWithDiff.txt | while IFS= read -r f; do { echo "using Unity.Netcode;"; cat "$f"; } > "$f.tmp" && mv "$f.tmp" "$f"; done
```
And then with your favorite IDE do a pass of "remove redundant qualifiers".
Be careful if you're doing manual changes, NetworkTime has changed namespace and went from Unity.NetCode to Unity.Netcode.NetcodeTime.

### Hash Changes

Variant hashes are name based. We have namespace migration logic for the GhostAuthoringInspectionComponent that will automatically try to find the hash with the old namespace and update your prefabs with the new hash. If you see hash changes in your prefabs, that's why.

### System Ordering Changes

Since entities uses system's full name to generate a hash that's used to order when there's no ordering attributes like `[UpdateBefore]`, this namespace rename basically shuffles all systems that weren't properly configured. Please keep this in mind if you encounter behaviour breaking changes when updating to this new version.


## NetcodeWorld

Various Netcode APIs dealing with worlds used to fail at runtime if using a non-netcode world. NetcodeWorld, previously internal only, is now public and is used in many of the Netcode public APIs in place of the ECS defined World to produce early compile errors instead of runtime errors. Since several public APIs now use NetcodeWorld in place of the ECS defined Wolrd, you could run into compilation errors. You can resolve them by swapping to NetcodeWorld. As it inherits from World, this should work in most cases.

## Auto Connect Port

Auto Connect Port is now non-zero by default. First time user experience will be with netcode worlds automatically created and connecting instead of just created, but not connected.
To revert back to the old behaviour, update your bootstrapper to set Auto Connect Port to 0;

## Single-world host mode is now the default host mode

Single-world host mode is no longer experimental: the `NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST` scripting define symbol has been removed and can be deleted from your project's **Scripting Define Symbols**.

**Breaking change:** client-hosted games (`PlayType.ClientAndServer`) now default to a single-world host instead of separate client and server worlds connected through IPC. `NetCodeConfig.HostWorldModeSelection` defaults to `HostWorldMode.SingleWorld`. To keep the previous behaviour, set **Host World Mode Selection** to **Binary Worlds** on your global `NetCodeConfig` asset. Refer to [Single-world host mode](single-world-host-mode.md) for behavioural differences, such as prediction switching and partial ticks not being supported on the host.

## Removed and hard-error obsolete APIs

APIs that were previously deprecated (and only produced warnings) are now hard errors (`[Obsolete(..., true)]`) and no longer functional. They are still present as compile-time guidance and will be deleted in a later release, so update your code to the replacements below:

| Removed / hard-error API | Replacement |
| --- | --- |
| `NetworkDriverStore.GetDriverInstance` | `GetDriverInstanceRW` / `GetDriverInstanceRO` (avoids copying) |
| `NetworkDriverStore.GetNetworkDriver` | `GetDriverRW` / `GetDriverRO` (avoids copying) |
| `NetworkDriverStore.ForEachDriver` | Iterate manually between the `FirstDriver` and `LastDriver` ids |
| `NetworkDriverStore.Disconnect` | `NetworkStreamDriver.Disconnect` |
| `GhostSendSystemData.MaxSendEntities` | `MaxSendChunks` and `MaxIterateChunks` |
| `GhostCount.GhostCountOnClient` | `GhostCountInstantiatedOnClient` or `GhostCountReceivedOnClient` |
| `GhostDistanceImportance.ScaleFunctionPointer` (single-scale path) | `GhostDistanceImportance.BatchScaleFunctionPointer` / `GhostImportance.BatchScaleImportanceDelegate` |
| `GhostComponentSerializer.SerializeChild` / `SerializeChildDelegate` | `Serialize` / `SerializeDelegate` |
| `GhostComponentSerializer.SendMask` | `GhostSendType` |
| `IGhostSerializer<TComponent, TSnapshot>` | `IGhostComponentSerializer` |
| `SimulatorPreset`'s legacy constructor | The current `SimulatorPreset` constructor |
| The `Disabled` simulator option | `MultiplayerPlayModePreferences.SimulatorEnabled` |
| `NetDebugPacket`, `NetDebug.DisconnectReasonEnumToString` | The `ToFixedString` extension methods |
| `SupportsPrefabOverridesAttribute` | No longer needed; prefab overrides are now always supported |

The code-generation-only input helpers (`CopyInputToCommandBuffer`, `CopyInputToBufferJob`, `ApplyCurrentInputBufferElementToInputData`, `ApplyInputDataFromBufferJob`) and `DebugGhostDrawer.RefreshWorldCaches` were also promoted to hard errors; they were meant for internal/code-generated use and have no replacement.

`DebugGhostDrawer.FirstServerWorld` and `DebugGhostDrawer.FirstClientWorld` have been removed entirely. Use `ClientServerBootstrap.ServerWorld` and `ClientServerBootstrap.ClientWorld` instead.



# Upgrading from Entities 0.51 to 1.0

The Netcode for Entities introduces many changes and the upgrade process from 0.51 to 1.0 can be a little laborious.

## Classed renamed and moved in other assemblies

* The following components have been renamed and will be automatically updated:

| Original Name                             | New Name                         |
|-------------------------------------------|----------------------------------|
| NetworkSnapshotAckComponent               | NetworkSnapshotAck               |
| IncomingSnapshotDataStreamBufferComponent | IncomingSnapshotDataStreamBuffer |
| IncomingRpcDataStreamBufferComponent      | IncomingRpcDataStreamBuffer      |
| OutgoingRpcDataStreamBufferComponent      | OutgoingRpcDataStreamBuffer      |
| IncomingCommandDataStreamBufferComponent  | IncomingCommandDataStreamBuffer  |
| OutgoingCommandDataStreamBufferComponent  | OutgoingCommandDataStreamBuffer  |
| NetworkIdComponent                        | NetworkId                        |
| CommandTargetComponent                    | CommandTarget                    |
| GhostComponent                            | GhostInstance                    |
| GhostChildEntityComponent                 | GhostChildEntity                 |
| GhostOwnerComponent                       | GhostOwner                       |
| PredictedGhostComponent                   | PredictedGhost                   |
| GhostTypeComponent                        | GhostType                        |
| SharedGhostTypeComponent                  | GhostTypePartition               |
| GhostCleanupComponent                     | GhostCleanup                     |
| GhostPrefabMetaDataComponent              | GhostPrefabMetaData              |
| PredictedGhostSpawnRequestComponent       | PredictedGhostSpawnRequest       |
| PendingSpawnPlaceholderComponent          | PendingSpawnPlaceholder          |
| ReceiveRpcCommandRequestComponent         | ReceiveRpcCommandRequest         |
| SendRpcCommandRequestComponent            | SendRpcCommandRequest            |

* The `DefaultUserParams` has been renamed to `DefaultSmoothingActionUserParams`.
* The `DefaultTranslateSmoothingAction` has been renamed to `DefaultTranslationSmoothingAction`.
* `ClientServerTickRate.MaxSimulationLongStepTimeMultiplier` has been renamed to `ClientServerTickRate.MaxSimulationStepBatchSize`
* The `NetworkCompressionModel` has been moved to Unity.Collection and renamed to `StreamCompressionModel`.
* The utility method `GhostPredictionSystemGroup.ShouldPredict` has been moved to the `PredictedGhostComponent`.
* `GhostComponentAttribute.OwnerPredictedSendType` has been renamed to `GhostComponentAttribute.SendTypeOptimization`.
* `ClientServerTickRate.MaxSimulationLongStepTimeMultiplier` is renamed to `ClientServerTickRate.MaxSimulationStepBatchSize`.
* `GhostPredictionSystemGroup` has been renamed to `PredictedSimulationSystemGroup`.

## PredictedTick, ServerTick and in general time information.
All the information in regards the current simulated tick MUST be retrieved from the `NetworkTim` singleton. In particular:
* The `GhostPredictionSystemGroup.PredictedTick` has been removed.
You must always use the `NetworkTime.ServerTick` instead, that will always correcly reflect the current predicted tick when inspected inside the prediction loop.
* The `GhostPredictionSystemGroup.IsFinalPredictionTick` has been removed. Use the `NetworkTime.IsFinalPredictionTick` property instead.
* The `ClientSimulationSystemGroup ServerTick`, `ServerTickFraction`, `InterpolationTick` and `InterpolationTickFraction` has been removed. You can retrieve the same properties from the `NetworkTime` singleton.

Please refer to the `NetworkTime` component documentation for further information about the different timing properties and the flags behaviours.

## Use the new singletons to access APIs and shared data.
All Netcode systems (apart some exception) should be considered stateless. All the public and accessible data is store inside entities singletons. We removed many APIs from system and moved instead into this new singleton components:

* When using the netcode logging system calls to `GetExistingSystem<NetDebugSystem>().NetDebug` must be replaced with `GetSingleton<NetDebug>()`, or `GetSingletonRW<NetDebug>` if you are changing the log level.
* The `Connect` and `Listen` methods have moved to the `NetworkStreamDriver` singleton.
* `GhostSimulationSystemGroup.SpawnedGhostEntityMap` has been replaced by a `SpawnedGhostEntityMap` singleton.
* The ghost relevancy map and mode has moved from the `GhostSendSystem` to a `GhostRelevancy` singleton.
* `GhostCountOnServer` and `GhostCountOnClient` has been moved from `GhostReceiveSystem` to a singleton API `GhostCount`
* The API to register smoothing functions for prediction has moved from the `GhostPredictionSmoothingSystem` system to the `GhostPredictionSmoothing` singleton.
* The API to register RPCs and get RPC queues has moved from `RpcSystem` to the singleton `RpcCollection`
* Calls to `GetExistingSystem<GhostSimulationSystemGroup>().SpawnedGhostEntityMap` must be replaced with `GetSingleton<SpawnedGhostEntityMap>().Value`. Waiting for or setting `LastGhostMapWriter` is no longer required and should be removed.
* Calls to `GetExistingSystem<GhostSendSystem>().GhostRelevancySet` and `GetExistingSystem<GhostSendSystem>().GhostRelevancyMode` must be replaced with `GetSingletonRW<GhostRelevancy>.GhostRelevancySet` and `GetSingletonRW<GhostRelevancy>.GhostRelevancyMode`. Waiting for or setting `GhostRelevancySetWriteHandle` is no longer required and should be removed.
* Calls to `GetExistingSystem<NetworkStreamReceiveSystem>().Connect` and `GetExistingSystem<NetworkStreamReceiveSystem>().Listen` must be replaced with `GetSingletonRW<NetworkStreamDriver>.Connect` and `GetSingletonRW<NetworkStreamDriver>.Listen`.

## Changes in visiblity and depracted APIs.
* The `LagCompensationConfig` has been removed. Use the unified`NetCodePhysicsConfig` authoring component instead of using the `LagCompensationConfig` authoring component to enable lag compensation.
* Any calls to the static `RpcSystem.DynamicAssemblyList` should be replaced with instanced calls to the property with the same name. Ensure you do so during world creation, before `RpcSystem.OnUpdate` is called. You can see an exaple of this in our NetcodeSamples.
* Any editor-only calls to `ClientServerBootstrap.RequestedAutoConnect` should be replaced with `ClientServerBootstrap.TryFindAutoConnectEndPoint`, which handles all `PlayTypes`.

* The `GhostCollectionSystem.CreatePredictedSpawnPrefab` API has been removed as clients will now automatically have predict spawned ghost prefabs set up for them. They can instantiate prefabs the normal way and don't need to call this API.
* The `PrespawnsSceneInitialized`, `SubScenePrespawnBaselineResolved`, `PrespawnGhostBaseline`, `PrespawnSceneLoaded`, `PrespawnGhostIdRange` have internal visibility.
* The `PrespawnSubsceneElementExtensions` has internal visibility.
* The `LiveLinkPrespawnSectionReference` are now internal. Used only in the Editor as a work around to entities conversion limitation. It should not be a public component that can be added by the user.
* The `GhostCollectionSystem.CreatePredictedSpawnPrefab` API has been deprected. The clients will now automatically have predict spawned ghost prefabs set up for them and just instantiate prefabs the normal way.
* The static bool `RpcSystem.DynamicAssemblyList` has been removed, replaced by a non-static property with the same name.
* `ClientServerBootstrap.RequestedAutoConnect` (an editor only property) has been replaced with `ClientServerBootstrap.TryFindAutoConnectEndPoint`.
* `ThinClientComponent` has been removed, use `World.IsThinClient()` instead.
* The `NetworkStreamDisconnected` component has been removed, add a `ConnectionState` component to connections you want to detect disconnects for and use a reactive system.
* The `CommandReceiveClearSystem` and `CommandSendPacketSystem` are not internal
* The `StartStreamingSceneGhosts` and `StopStreamingSceneGhosts` to be internal RPC. If user wants to customise the prespawn scene flow, they need to add their own RPC.

## New way to pass templates to source generator
* Netcode source generator templates should now use the passed to the generators using `additional files`. The template must have a `NetCodeSourceGenerator.additionalfile` extension, and should be identified using a unique id, that must be present in the first line of the template. </br>
  Find more information about [writing a template](ghost-types-templates.md#writing-a-template) and [registering a template](ghost-types-templates.md#registering-a-template) in the documentation.


## Netcode groups, world filtering and detect world types.
* Use `IsClient`, `IsServer` and `IsThinClient` helper methods on `World` and `WorldUnmanaged` to inspect if a world is client, server or thin-client respectively.
* The netcode specific top-level system groups and `[UpdateInWorld]` have been removed, the replacement is `[WorldSystemFilter]` and the mappings are

| Old                                                                 | New                                                                                                                                                           |
|---------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `[UpdateInGroup(typeof(ClientInitializationSystemGroup))]`          | `[UpdateInGroup(typeof(InitializationSystemGroup))][WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]`                                              |
| `[UpdateInGroup(typeof(ClientSimulationSystemGroup))]`              | `[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]`                                                                                                |
| `[UpdateInGroup(typeof(ClientPresentationSystemGroup))]`            | `[UpdateInGroup(typeof(PresentationSystemGroup)]`                                                                                                             |
| `[UpdateInGroup(typeof(ServerInitializationSystemGroup))]`          | `[UpdateInGroup(typeof(InitializationSystemGroup))][WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]`                                              |
| `[UpdateInGroup(typeof(ServerSimulationSystemGroup))]`              | `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]`                                                                                                |
| `[UpdateInGroup(typeof(ClientAndServerInitializationSystemGroup))]` | `[UpdateInGroup(typeof(InitializationSystemGroup))][WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation\|WorldSystemFilterFlags.ClientSimulation)]` |
 | `[UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]`     | `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation\|WorldSystemFilterFlags.ClientSimulation)]`                                                   |
| `[UpdateInWorld(TargetWorld.Client)]`                               | `[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]`                                                                                                |
| `[UpdateInWorld(TargetWorld.Server)]`                               | `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]`                                                                                                |
| `[UpdateInWorld(TargetWorld.ClientAndServer)]`                      | `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation\|WorldSystemFilterFlags.ClientSimulation)]`                                                   |
| `[UpdateInWorld(TargetWorld.Default)]`                              | `[WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]`                                                                                                 |
| `if (World.GetExistingSystem<ServerSimulationSystemGroup>()!=null)` | `if (World.IsServer())`                                                                                                                                       |
| `if (World.GetExistingSystem<ClientSimulationSystemGroup>()!=null)` | `if (World.IsClient())`                                                                                                                                       |

## Major changes for ghost field serialization
* All child entities in Ghosts now default to the `DontSerializeVariant` as serializing child ghosts is relatively expensive (due to poor 'locality of reference' of child entities in other chunks, and the random-access nature of iterating child entities). Thus, `GhostComponentAttribute.SendDataForChildEntity = false` is now the default, and you'll need to set this flag to true for all types that should be sent for children. If you'd like to replicate hierarchies, we strongly encourage you to create multiple ghost prefabs, with custom, faked transform parenting logic that keeps the hierarchy flat. Explicit child hierarchies should only be used if the snapshot updates of one hierarchy must be in sync.
* `RegisterDefaultVariants` has changed signature to now use a `Rule`. This forces users to be explicit about whether or not they want their user-defined defaults to apply to child entities too.
* All `GhostAuthoringComponent` `ComponentOverrides` have been clobbered during the upgrade (apologies!). Please re-apply all `ComponentOverrides` via the new (optional) `GhostAuthoringInspectionComponent`.
* Inside your `RegisterDefaultVariants` method, replace all `defaultVariants.Add(new ComponentType(typeof(SomeType)), typeof(SomeTypeDefaultVariant));` with `defaultVariants.Add(new ComponentType(typeof(SomeType)), Rule.OnlyParent(typeof(SomeTypeDefaultVariant)));`, unless you _also_ want this variant to be applied to children (in which case, use `Rule.ParentAndChildren(typeof(SomeTypeDefaultVariant))`).
Caveat: Prefer to use attributes wherever possible, as this "manual" form of overriding should only be used for one-off differences that you're unable to express via attributes.





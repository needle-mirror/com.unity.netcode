# Entities list

This page contains a list of all entities used by the Netcode package.

## Connection

A connection entity is created for each network connection. You can think of these entities as your network socket, but they do contain a bit more data and configuration for other Netcode systems.

| Component | Description | Condition |
| --------- | ----------- | --------- |
|[__NetworkStreamConnection__](xref:Unity.NetCode.NetworkStreamConnection) | The Unity Transport `NetworkConnection` used to send and receive data.
|[__NetworkSnapshotAckComponent__](xref:Unity.NetCode.NetworkSnapshotAckComponent) | Data used to keep track of what data has been received.
|[__CommandTargetComponent__](xref:Unity.NetCode.CommandTargetComponent) | A pointer to the entity where commands should be read from or written too. The target entity must have a `ICommandData` component on it.
|[__IncomingRpcDataStreamBufferComponent__](xref:Unity.NetCode.IncomingRpcDataStreamBufferComponent) | A buffer of received RPC commands which will be processed by the RpcSystem. Intended for internal use only.
|[__IncomingCommandDataStreamBufferComponent__](xref:Unity.NetCode.IncomingCommandDataStreamBufferComponent) | A buffer of received commands which will be processed by a generated CommandReceiveSystem. Intended for internal use only. | Server only
|[__OutgoingCommandDataStreamBufferComponent__](xref:Unity.NetCode.OutgoingCommandDataStreamBufferComponent) | A buffer of commands generated be a CommandSendSystem which will be sent to the server. Intended for internal use only. | Client only
|[__IncomingSnapshotDataStreamBufferComponent__](xref:Unity.NetCode.IncomingSnapshotDataStreamBufferComponent) | A buffer of received snapshots which will be processed by the GhostReceiveSystem. Intended for internal use only. | Client only
|[__OutgoingRpcDataStreamBufferComponent__](xref:Unity.NetCode.OutgoingRpcDataStreamBufferComponent) | A buffer of RPC commands which should be sent by the RpcSystem. Intended for internal use only, use an `RpcQueue` or `IRpcCommand` component to write RPC data.
|[__NetworkIdComponent__](xref:Unity.NetCode.NetworkIdComponent) | The network id is used to uniquely identify a connection. If this component does not exist the connection is not yet complete. | Added automatically when connection is complete
|[__NetworkStreamInGame__](xref:Unity.NetCode.NetworkStreamInGame) | A component used to signal that a connection should send and receive snapshots and commands. Before adding this component the connection only processes RPCs. | Added by game logic to start sending snapshots and commands.
|[__NetworkStreamRequestDisconnect__](xref:Unity.NetCode.NetworkStreamRequestDisconnect) | A component used to signal that the game logic wants to close the connection. | Added by game logic to disconnect.
|[__NetworkStreamDisconnected__](xref:Unity.NetCode.NetworkStreamDisconnected) | A component used to signal that a connection has been disconnected. The entity will exist with this component for one frame, after that it is automatically deleted. | Added automatically when a connection is disconnected.
|[__NetworkStreamSnapshotTargetSize__](xref:Unity.NetCode.NetworkStreamSnapshotTargetSize) | Used to tell the `GhostSendSystem` on the server to use a non-default packet size for snapshots. | Added by game logic to change snapshot packet size.
|[__GhostConnectionPosition__](xref:Unity.NetCode.GhostConnectionPosition) | Used by the distance based importance system to scale importance of ghosts based on distance from the player. | Added by game logic to specify the position of the player for a connection.
|[__PrespawnSectionAck__](xref:Unity.NetCode.PrespawnSectionAck) | Used by the server to track which subscenes the client has loaded. | Server only
|[__EnablePacketLogging__](xref:Unity.NetCode.EnablePacketLogging) | Added by game logic to enable packet dumps for a single connection. | Only when enabling packet dumps

## Ghost

A ghost is an entity on the server which is ghosted (replicated) to the clients. It is always instantiated from a ghost prefab and has user defined data in addition to the components listed here which control its behavior.

| Component | Description | Condition |
| --------- | ----------- | --------- |
|[__GhostComponent__](xref:Unity.NetCode.GhostComponent) | Identifying an entity as a ghost.
|[__GhostTypeComponent__](xref:Unity.NetCode.GhostTypeComponent) | The type this ghost belongs to.
|__GhostSystemStateComponent__| This component exists for only for internal use in the NetCode package. Used to track despawn of ghosts on the server. | Server only
|[__SharedGhostTypeComponent__](xref:Unity.NetCode.SharedGhostTypeComponent) | A shared component version of the `GhostTypeComponent`, used on the server only to make sure different ghost types never share the same chunk. | Server only
|[__SnapshotData__](xref:Unity.NetCode.SnapshotData) | A buffer with meta data about the snapshots received from the server. | Client only
|[__SnapshotDataBuffer__](xref:Unity.NetCode.SnapshotDataBuffer) | A buffer with the raw snapshot data received from the server. | Client only
|[__SnapshotDynamicDataBuffer__](xref:Unity.NetCode.SnapshotDynamicDataBuffer) | A buffer with the raw snapshot data for buffers received from the server. | Client only, ghosts with buffers only
|[__PredictedGhostComponent__](xref:Unity.NetCode.PredictedGhostComponent) | Identify predicted ghosts. On the server all ghosts are considered predicted and have this component. | Predicted only
|[__GhostDistancePartition__](xref:Unity.NetCode.GhostDistancePartition) | Added to all ghosts with a `Translation` when distance based importance is used. | Only for distance based importance
|[__GhostDistancePartitionShared__](xref:Unity.NetCode.GhostDistancePartitionShared) | Added to all ghosts with a `Translation` when distance based importance is used. | Only for distance based importance
|[__GhostPrefabMetaDataComponent__](xref:Unity.NetCode.GhostPrefabMetaDataComponent) | The meta data for a ghost, adding durin conversion and used to setup serialiation. This is not required on ghost instances, only on prefabs, but it is only removed from pre-spawned right now. | Not in pre-spawned
|[__GhostChildEntityComponent__](xref:Unity.NetCode.GhostChildEntityComponent) | Disable the serialization of this entity because it is part of a ghost group and will be serialized as part of that. | Only children in ghost groups
|[__GhostGroup__](xref:Unity.NetCode.GhostGroup) | Added to all ghosts which can be the owner of a ghost group. Must be added to the prefab at conversion time. | Only ghost group root
|[__PredictedGhostSpawnRequestComponent__](xref:Unity.NetCode.PredictedGhostSpawnRequestComponent) | This instance is not a ghost received from the server, but a request to predictively spawn a ghost which the client expets the server to spawn soon. This should only be added by calling `GhostCollectionSystem.CreatePredictedSpawnPrefab` | Only predicted spawn requests
|[__GhostOwnerComponent__](xref:Unity.NetCode.GhostOwnerComponent) | Identiy the owner of a ghost, specified as a network id. | Optional
|[__AutoCommandTarget__](xref:Unity.NetCode.AutoCommandTarget) | Automatically send all `ICommandData` if the ghost is owned by the current connection, `AutoCommandTarget.Enabled` is true and the ghost is predicted. | Optional
|[__SubSceneGhostComponentHash__](xref:Unity.NetCode.SubSceneGhostComponentHash) | The hash of all pre-spawned ghosts in a subscene, used for sorting and grouping. This is a shared component. | Only pre-spawned
|[__PreSpawnedGhostIndex__](xref:Unity.NetCode.PreSpawnedGhostIndex) | Unique index of a pre-spawned ghost within a subscene. | Only pre-spawned
|[__PrespawnGhostBaseline__](xref:Unity.NetCode.PrespawnGhostBaseline) | The snapshot data a pre-spawned ghost had in the scene data. Used as a fallback baseline. | Only pre-spawned
|[__GhostPrefabRuntimeStrip__](xref:Unity.NetCode.GhostPrefabRuntimeStrip) | Added to prefabs and pre-spawned during conversion to client and server data to trigger runtime stripping of component. | Only on prefabs in client and server scenes before they are initialized
|[__LiveLinkPrespawnSectionReference__](xref:Unity.NetCode.LiveLinkPrespawnSectionReference) | Component present in editor on the scene section entity when the sub-scene is open for edit. | Only in Editor

|[__PreSerializedGhost__](xref:Unity.NetCode.PreSerializedGhost) | Enable pre-serialization for a ghost, added at conversion time based on ghost settings. | Only ghost using pre-serialization
|[__SwitchPredictionSmoothing__](xref:Unity.NetCode.SwitchPredictionSmoothing) | Added temporarily when switching a ghost between predicted / interpolated with a transition time to handle transform smoothing. | Only ghost in the process of switching prediction mode
|[__PrefabDebugName__](xref:Unity.NetCode.PrefabDebugName) | Name of the prefab used for debugging. | Only on prefabs when NETCODE_DEBUG is enabled

### Placeholder ghost

When a ghost is received but is not yet supposed to be spawned the client will create a placeholder to store the data until it is time to spawn it. The placeholder ghosts only exist on clients and have these components
| Component | Description | Condition |
| --------- | ----------- | --------- |
|[__GhostComponent__](xref:Unity.NetCode.GhostComponent) | Identifying an entity as a ghost.
|[__PendingSpawnPlaceholderComponent__](xref:Unity.NetCode.PendingSpawnPlaceholderComponent) | Identify the ghost as a placeholder and not a proper ghost.
|[__SnapshotData__](xref:Unity.NetCode.SnapshotData) | A buffer with meta data about the snapshots received from the server. | Client only
|[__SnapshotDataBuffer__](xref:Unity.NetCode.SnapshotDataBuffer) | A buffer with the raw snapshot data received from the server.
|[__SnapshotDynamicDataBuffer__](xref:Unity.NetCode.SnapshotDynamicDataBuffer) | A buffer with the raw snapshot data for buffers received from the server. | Ghosts with buffers only

## RPC

RPC entities are created with a send request in order to send RPCs. When they are received the system will create entities with the RPC component and a receive request.

| Component | Description | Condition |
| --------- | ----------- | --------- |
|[__IRpcCommand__](xref:Unity.NetCode.IRpcCommand) | A specific implementation of the IRpcCommand interface.
|[__SendRpcCommandRequestComponent__](xref:Unity.NetCode.SendRpcCommandRequestComponent) | Specify that this RPC is to be sent. | Added by game logic, only for sending.
|[__ReceiveRpcCommandRequestComponent__](xref:Unity.NetCode.ReceiveRpcCommandRequestComponent) | Specify that this RPC is received. | Added automatically, only for receiving.

### Netcode RPCs

| Component | Description |
| --------- | ----------- |
| __RpcSetNetworkId__ | Special RPC only sent on connect.
| __ClientServerTickRateRefreshRequest__ | Special RPC only sent on connect.
| __HeartbeatComponent__ | Send at regular intervals when not in game to make sure the connection does not time out.
| __StartStreamingSceneGhosts__ | Sent from client to server when a subscene has been loaded.
| __StopStreamingSceneGhosts__ | Sent from client to server when a subscene will be unloaded.

### CommandData

Every connection which is receiving commands from a client needs to have an entity to hold the command data. This can be a ghost, the connection entity itself or some other entity.

| Component | Description | Condition |
| --------- | ----------- | --------- |
|[__ICommandData__](xref:Unity.NetCode.ICommandData)| A specific implemenation of the ICommandData interface. This can be added to any entity, the connections `CommandTargetComponent` must point to an entity containing this.
|[__CommandDataInterpolationDelay__](xref:Unity.NetCode.CommandDataInterpolationDelay)| Optional component used to access the interpolation delay in order to implement lag compensation on hte server. Also exists on predicted clients but always has an interpolation delay of 0 there. | Added by game logic, predicted only

### Netcode CommandData
| Component | Description |
| --------- | ----------- |
| __NullCommandData__ | Special CommandData sent when command target is null to make sure ping and ack messages still work.

## SceneSection
When using pre-spawned ghosts Netcode will add some components to the SceneSection entity containing the ghosts.
| Component | Description | Condition |
| --------- | ----------- | --------- |
|[__SubSceneWithPrespawnGhosts__](xref:Unity.NetCode.SubSceneWithPrespawnGhosts)| Added during convertion to track which section contains pre-spawned ghosts.
|[__SubSceneWithGhostStateComponent__](xref:Unity.NetCode.SubSceneWithGhostStateComponent)| Used to track unloading of scenes. | Processed sections.
|[__PrespawnsSceneInitialized__](xref:Unity.NetCode.PrespawnsSceneInitialized)| Tag to specify that a section has been processed. | Processed sections.
|[__SubScenePrespawnBaselineResolved__](xref:Unity.NetCode.SubScenePrespawnBaselineResolved)| Tag to specify that a section has resolved baselines. This is a partially initialized state. | Partially processed sections.

## Netcode created singletons

### PredictedGhostSpawnList
A singleton with a list of all predicted spawned ghosts which are waiting for a ghost from the server. This is needed when writing logic matching an incoming ghost with a pre-spawned one.
| Component | Description |
| --------- | ----------- |
|[__PredictedGhostSpawnList__](xref:Unity.NetCode.PredictedGhostSpawnList)| A tag for finding the predicted spawn list.
|[__PredictedGhostSpawn__](xref:Unity.NetCode.PredictedGhostSpawn)| A lis of all predictively spawned ghosts.

### Ghost Collection
| Component | Description |
| --------- | ----------- |
|[__GhostCollection__](xref:Unity.NetCode.GhostCollection) | Identify the singleton containing ghost prefabs.
|[__GhostCollectionPrefab__](xref:Unity.NetCode.GhostCollectionPrefab) | A list of all ghost prefabs which can be instantiated.
|[__GhostCollectionPrefabSerializer__](xref:Unity.NetCode.GhostCollectionPrefabSerializer) | A list of serializers for all ghost prefabs. The index in this list is identical to `GhostCollectionPrefab`, but it can temporarily have fewer entries when a prefab is loading. This references a range in the `GhostCollectionComponentIndex` list.
|[__GhostCollectionComponentType__](xref:Unity.NetCode.GhostCollectionComponentType) | The set of serializers in the `GhostComponentSerializer.State` which can be used for a given type. This is used internally to setup the `GhostCollectionPrefabSerializer`.
|[__GhostCollectionComponentIndex__](xref:Unity.NetCode.GhostCollectionComponentIndex) | A list of mappings from prefab serializer index to a child entity index and a `GhostComponentSerializer.State` index. This mapping is there to avoid having to duplicate the full serialization state for each prefab using the same component.
|[__GhostComponentSerializer.State__](xref:Unity.NetCode.GhostComponentSerializer.State) | Serialization state - including function pointers for serialization - for a component type and variant. There can be more than one entry for a given component type if there are serialization variants.

### Spawn queue
| Component | Description |
| --------- | ----------- |
|[__GhostSpawnQueueComponent__](xref:Unity.NetCode.GhostSpawnQueueComponent)| Identifier for the ghost spawn queue.
|[__GhostSpawnBuffer__](xref:Unity.NetCode.GhostSpawnBuffer)| A list of ghosts in the spawn queue. This queue is written by the `GhostReceiveSystem` and read by the `GhostSpawnSystem`. A classification system running between those two can change the type of ghost to sapwn and match incomming ghosts with pre-spawned ghosts.
|[__SnapshotDataBuffer__](xref:Unity.NetCode.SnapshotDataBuffer)| Raw snapshot data for the new ghosts in the `GhostSpawnBuffer`.

### NetworkProtocolVersion
| Component | Description |
| --------- | ----------- |
|[__NetworkProtocolVersion__](xref:Unity.NetCode.NetworkProtocolVersion)| The network protocol version for RPCs, ghost component serializers, netcode version and game version. At connection time netcode will validate that the client and server has the same version.

### PrespawnGhostIdAllocator
| Component | Description |
| --------- | ----------- |
|[__PrespawnGhostIdRange__](xref:Unity.NetCode.PrespawnGhostIdRange) | The set of ghost ids assosiated with a subscene. Used by the server to map prspawned ghosts for a subscene to proper ghost ids.

### PrespawnSceneLoaded
This singleton is a special kind of ghost without a prefab asset.
| Component | Description |
| --------- | ----------- |
|[__PrespawnSceneLoaded__](xref:Unity.NetCode.PrespawnSceneLoaded) | The set of scenes with pre-spawned ghosts loaded by the server. This is ghosted to clients.

### MigrationTicket
| Component | Description |
| --------- | ----------- |
|[__MigrationTicket__](xref:Unity.NetCode.MigrationTicket) | Created in the new world when using world migration, triggers the restore part of migration.

### SmoothingAction
| Component | Description |
| --------- | ----------- |
|__SmoothingAction__ | Singleton created when a smothing action is registered in order to enable the smoothing system.

## User create singletons (settings)

### ClientServerTickRate
| Component | Description |
| --------- | ----------- |
|[__ClientServerTickRate__](xref:Unity.NetCode.ClientServerTickRate) | The tick rate settings for the server. Automatically sent and set on the client based on the values specified on the server.

### ClientTickRate
| Component | Description |
| --------- | ----------- |
|[__ClientTickRate__](xref:Unity.NetCode.ClientTickRate) | The tick rate settings for the client which are not controlled by the server (interpolation time etc.). Use the defaults from `NetworkTimeSystem.DefaultClientTickRate` instead of default values.

### ThinClient
| Component | Description |
| --------- | ----------- |
|[__ThinClientComponent__](xref:Unity.NetCode.ThinClientComponent) | The world is a thin client world and will not process incomming snapshots. Automatically create for thin client worlds, creating this singleton in a user spawned world makes it a thin client. |

### LagCompensationConfig
| Component | Description |
| --------- | ----------- |
|[__LagCompensationConfig__](xref:Unity.NetCode.LagCompensationConfig) | Configuration for the `PhysicsWorldHistory` system which is used to implement lag compensation on the server. If the singleton does not exist `PhysicsWorldHistory` will no be run.

### GameProtocolVersion
| Component | Description |
| --------- | ----------- |
|[__GameProtocolVersion__](xref:Unity.NetCode.GameProtocolVersion) | The game specific version to use for protcol validation on connection. If this does not exist 0 will be used, but the protocol will still validate netcode version, ghost components and rpcs

### Ghost distance importance
| Component | Description |
| --------- | ----------- |
|[__GhostDistanceImportance__](xref:Unity.NetCode.GhostDistanceImportance)| Settings for distance based importance. If the singleton does not exist distance based importance is not used.

### PredictedPhysicsConfig
| Component | Description |
| --------- | ----------- |
|[__PredictedPhysicsConfig__](xref:Unity.NetCode.PredictedPhysicsConfig)| Create a singleton with this to enable and configure predicted physics.

### NetCodeDebugConfig
| Component | Description |
| --------- | ----------- |
|[__NetCodeDebugConfig__](xref:Unity.NetCode.NetCodeDebugConfig)| Create a singleton with this to configure log level and packet dump for all connections. See `EnabledPacketLogging` on the connection for enabling packet dumps for a subset of the connections.

### DisableAutomaticPrespawnSectionReporting
| Component | Description |
| --------- | ----------- |
|[__DisableAutomaticPrespawnSectionReporting__](xref:Unity.NetCode.DisableAutomaticPrespawnSectionReporting)| Disable the automatic tracking of which sub-scenes the client has loaded. When creating this singleton you must implement custom logic to make sure the server does not send pre-spawned ghosts which the client has not loaded.

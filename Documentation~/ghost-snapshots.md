# Ghost snapshots

A ghost is a networked object that the server simulates. During every frame, the server sends a snapshot of the current state of all ghosts to the client. The client presents them, but cannot directly control or affect them because the server owns them.

The ghost snapshot system synchronizes entities which exist on the server to all clients. To make it perform properly, the server processes per ECS chunk rather than per entity. On the receiving side the processing is done per entity. This is because it is not possible to process per chunk on both sides, and the server has more connections than clients.

## Ghost authoring component
The ghost authoring component is based on specifying ghosts as Prefabs with the __GhostAuthoringComponent__ on them. The __GhostAuthoringComponent__ has a small editor which you can use to configure how NetCode synchronizes the various components and fields in the Prefab, as well as where the components will be available.

When you add a __GhostAuthoringComponent__ to the Prefab, select the __Update component list__ button in the Inspector. This determines which entities the Prefab will have after Unity converts it, and then adds them to the component list.

![Ghost Authoring Component](images/ghost-config.png)<br/>_Ghost Authoring Component_

Unity uses the list to configure the ghost. You can enable or disable the properties in the Inspector (__Server, Interpolated Client, Predacted Client__) to select which version of the Prefab will have a component. For example, if you disable the __Server__ property from RenderMesh, the ghost won’t have a RenderMesh when it is instantiated on the server, but it will have it when instantiated on the client. To control the defaults for these checkboxes from source, add the `GhostDefaultComponentAttribute` to an `IComponentData`.

For each component, you need to set the values to synchronize and their quality. To set up default values, add the `GhostDefaultFieldAttribute` attribute to the fields in an `IComponentData`. To override the default values for synchronization for a specific ghost, enable the __Manual Field List__ property and edit them.

As well as adding attributes, you can override the defaults for components which you do not have source access to by creating an entry to `GhostAuthoringComponentEditor.GhostDefaultOverrides`.

You must also set the __Importance__ and __Default Client Instantiation__ property on each ghost. Unity uses the __Importance__ property to control which entities are sent when there is not enough bandwidth to send all. A higher value makes it more likely that the ghost will be sent. 

You can select from three different __Default Client Instantiation__ types:

* __Interpolated__ - all ghosts Unity receives from the server are treated as interpolated.
* __Predicted__ - all ghosts Unity receives from the server are treated as predicted.
* __Owner predicted__ - the ghost is predicted for the client that owns it, and interpolated for all other clients. When you select this property, you must also specify which field the owner network ID is stored in. Unity compares this field to each clients’ network ID to find the correct owner.

To override the default client instantiation you can create a partial class with the same name as the ghost spawner and implement `MarkPredictedGhosts`.

You need to specify the paths to all generated code. You can override the defaults for this path on a per-Project basis to avoid having to manually edit all of them. For more information, see the statics in [GhostAuthoringComponentEditor](https://docs.unity3d.com/Packages/com.unity.netcode@latest/api/Unity.NetCode.Editor.GhostAuthoringComponentEditor.html), `DefaultRootPath`, `DefaultSnapshotDataPrefix`, `DefaultUpdateSystemPrefix` and `DefaultSerializerPrefix`. There is also a static to put all generated code in a namespace, `DefaultNamespace`.

Once you have configured the ghost, select the __Generate code__ button. When a component is changed you must select both the __Update component__ list and __Generate code__ buttons to make sure Unity detects the changes and generates new code.


## Ghost collection

The `GhostCollection` entity enables the ghost systems to identify the ghosts between the client and server. It contains a list of all ghosts the netcode can handle. You can use it to identify ghost types and to spawn ghosts on the client with the correct Prefab. You can also use this collection to instantiate ghosts on the server at runtime. 
When the server determines which ghost type to use, it goes through the list of ghosts in the GhostCollection and picks the first one which matches. As such, the order of ghosts in the collection is important.

In the Inspector for the __GhostCollectionAuthoringComponent__, there are three buttons you can select: 
* __Update ghost list__, which scans for Prefabs with __GhostAuthoringComponent__. 
* __Regenerate all ghosts__, which regenerates the code for all ghosts in the list.
* __Generate collection code__

For the netcode to work, you must generate the code for the collection, and the ghost collection must be part of the client and server entity worlds.

## Value types

The codegen does not support all value types, but you can create a new template and register it by adding a custom `GhostSnapshotValue` to `GhostSnapshotValue.GameSpecificTypes` to add support for more types.

## Importance
The server operates on a fixed bandwidth, and sends a single packet with snapshot data of customizable size every network tick. It fills the packet with the entities of the highest importance. Several factors determine the importance of the entities: you can specify the base importance per ghost type, which Unity then scales by age. You can also supply your own method to scale the importance on a per-chunk basis.  

Once a packet is full, the server sends it and the remaining entities are missing from the snapshot. Because the age of the entity influences the importance, it is more likely that the server will include those entities in the next snapshot. The importance is only calculated per chunk, not per entity.


### Distance based importance
You can use a custom function to scale the importance per chunk. For example, if a singleton entity with the `GhostDistanceImportance` component on it exists on the server, the netcode makes sure that all the ghosts in the World are split into groups based on the tile size in that singleton. 

You must add a `GhostConnectionPosition` component to each connection to determine which tile the connection should prioritize. This `GhostSendSystem` passes this information to the `ScaleImportanceByDistance` in `GhostDistanceImportance` which then uses it to scale the importance of a chunk based on its distance in tiles or any other metric you define in your code.

## Entity spawning

When the client side receives a new ghost, a user-defined spawn system spawns it. There is no specific spawn message, and when the client receives an unknown ghost ID, it counts as an implicit spawn. Your code can generate the spawn system, along with the serializer and the logic for snapshot handling  for the entity (snapshot updates).

Because the client interpolates snapshot data, Unity cannot spawn entities immediately, unless it was preemptively spawned, such as with spawn prediction. This is because the data is not ready for the client to interpolate it. Otherwise, the object would appear and then not get any more updates until the interpolation is ready. 

Therefore normal spawns happen in a delayed manner. Spawning is split into three main types as follows:
* __Delayed or interpolated spawning.__ The entity is spawned when the interpolation system is ready to apply updates. This is how remote entities are handled, because they are interpolated in a straightforward manner.
* __Predicted spawning for the client predicted player object.__ The object is predicted so the input handling applies immediately. Therefore, it doesn't need to be delay spawned. While the snapshot data for this object arrives, the update system applies the data directly to the object and then plays back the local inputs which have happened since that time, and corrects mistakes in the prediction.
* __Predicted spawning for player spawned objects.__ These are objects that the player input spawns, like in-game bullets or rockets that the player fires. The spawn code needs to run on the client, in the client prediction system, then when the first snapshot update for the entity arrives it will apply to that predict spawned object (no new entity is created). After this, the snapshot updates are applied the same as in the predicted spawning for client predicted player object model.

You need to implement some specific code to handle the predicted spawning for player spawned objects. Code generation handles creating some boilerplate code around a Prefab `GhostAuthoringComponent` entity definition. You can extend the entity-specific spawning by implementing a partial class of the same name as the generated spawn class. You can call the `MarkPredictedGhosts` function so that you can assign a specific ID for that type of prediction. This method must contain user defined logic to match a ghost from the server with a predicted spawned local entity.

NetCode spawns entities on the client with a Prefab stored in the `GhostPrefabCollectionComponent` singleton. A `GhostCollectionAuthoringComponent` and a `ConvertToClientServerEntity` component that uses Client as the conversion target creates the Prefab.

## Snapshot visualization tool

To understand what is being put on the wire in the netcode, you can use the prototype snapshot visualization tool, __NetDbg__ in the Stats folder. To open the tool, go to menu: __Multiplayer &gt; Open NetDbg__, and the tool opens in a browser window. It displays a vertical bar for each snapshot Unity receives, with a breakdown of the snapshot’s ghost types. To see more detailed information about the snapshot, click on one of the bars. __Note:__ This tool is a prototype. In future versions of the package it will integrate with the Unity Profiler so you can easily correlate network traffic with memory usage and CPU performance.
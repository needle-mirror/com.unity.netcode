# Ghost snapshots

A ghost is a networked object that the server simulates. During every frame, the server sends a snapshot of the current state of all ghosts to the client. The client presents them, but cannot directly control or affect them because the server owns them.

The ghost snapshot system synchronizes entities which exist on the server to all clients. To make it perform properly, the server processes per ECS chunk rather than per entity. On the receiving side the processing is done per entity. This is because it is not possible to process per chunk on both sides, and the server has more connections than clients.

## Ghost authoring component
The ghost authoring component is based on specifying ghosts as Prefabs with the __GhostAuthoringComponent__ on them. The __GhostAuthoringComponent__ has a small editor which you can use to configure how NetCode synchronizes the Prefab.

![Ghost Authoring Component](images/ghost-config.png)<br/>_Ghost Authoring Component_

You must set the __Name__, __Importance__, __Supported Ghost Mode__, __Default Ghost Mode__ and __Optimization Mode__ property on each ghost. Unity uses the __Importance__ property to control which entities are sent when there is not enough bandwidth to send all. A higher value makes it more likely that the ghost will be sent.

You can select from three different __Supported Ghost Mode__ types:

* __All__ - this ghost supports both being interpolated and predicted.
* __Interpolated__ - this ghost only supports being interpolated, it cannot be spawned as a predicted ghost.
* __Predicted__ - this ghost only supports being predicted, it cannot be spawned as a interpolated ghost.

You can select from three different __Default Ghost Mode__ types:

* __Interpolated__ - all ghosts Unity receives from the server are treated as interpolated.
* __Predicted__ - all ghosts Unity receives from the server are treated as predicted.
* __Owner predicted__ - the ghost is predicted for the client that owns it, and interpolated for all other clients. When you select this property, you must also add a __GhostOwnerComponent__ and set its __NetworkId__ field in your code. Unity compares this field to each clients’ network ID to find the correct owner.

You can select from two different __Optimization Mode__ types:

* __Dynamic__ - the ghost will be optimized for having small snapshot size both when changing and when not changing.
* __Static__ - the ghost will not be optimized for having small snapshot size when changing, but it will not be sent at all when it is not changing.

To override the default client instantiation you can create a classification system updating after __ClientSimulationSystemGroup__ and before __GhostSpawnClassificationSystem__ which goes through the __GhostSpawnBuffer__ buffer on the singleton entity with __GhostSpawnQueueComponent__ and change the __SpawnType__.

Unity uses attributes in C# to configure which components and fields are synchronized as part of a ghost. You can see the current configuration in the __GhostAuthoringComponent__ by selecting __Update component list__, but you cannot modify it from the inspector.

To change which versions of a Prefab a component is available on you use __PrefabType__ in a __GhostComponentAttribute__ on the component. __PrefabType__ can be on of the these types:
* __InterpolatedClient__ - the component is only available on clients where the ghost is interpolated.
* __PredictedClient__ - the component is only available on clients where the ghost is predicted.
* __Client__ - the component is only available on the clients, both when the ghost is predicted and interpolated.
* __Server__ - the component is only available on the server.
* __AllPredicted__ - the component is only available on the server and on clients where the ghost is predicted.
* __All__ - the component is available on the server and all clients.

For example, if you add `[GhostComponent(PrefabType=GhostPrefabType.Client)]` to RenderMesh, the ghost won’t have a RenderMesh when it is instantiated on the server, but it will have it when instantiated on the client.

A component can set __OwnerPredictedSendType__ in the __GhostComponentAttribute__ to control which clients the component is sent to when it is owner predicted. The available modes are:
* __Interpolated__ - the component is only sent to clients which are interpolating the ghost.
* __Predicted__ - the component is only sent to clients which are predicting the ghost.
* __All__ - the component is sent to all clients.

If a component is not sent to a client NetCode will not modify the component on the client which did not receive it.

A component can also set __SendDataForChildEntity__ to true or false in order to control if the component it sent when it is part of a child entity of a ghost with multiple entities.

For each component, you need to add an attribute to the values you want to sent. Add a `[GhostField]` attribute to the fields you want to send in an `IComponentData`. The __GhostFieldAttribute__ can specify `Quantization` for floating point numbers. The floating point number will be multiplied by this number and converted to an integer in order to save bandwidth. Specifying a `Quantization` is mandatory for floating point numbers and not supported for integer numbers. To send a floating point number unquantized you have to explicitly specify `[GhostField(Quantization=0)]`.
The __GhostFieldAttribute__ also has a `Interpolate` property which controls if the field will be interpolated or not on clients which are not predicting the ghost.
Finally the __GhostFieldAttribute__ has a `SubType` property which can be set to an integer value to use special serialization rules supplied by the game for that specific field.

As well as adding attributes, you can specify rules for components which you do not have source access to by creating an assembly with a name ending with `.NetCodeGen`. This assembly should contain a class implementing the interface __IGhostDefaultOverridesModifier__. Implement the method `public void Modify(Dictionary<string, GhostAuthoringComponentEditor.GhostComponent> overrides)` and add an entry to the dictionary with the full name of the component as key and a value containing the desired override attributes.


## Ghost collection

The `GhostCollection` entity enables the ghost systems to identify the ghosts between the client and server. It contains a list of all ghosts the netcode can handle. You can use it to identify ghost types and to spawn ghosts on the client with the correct Prefab. You can also use this collection to instantiate ghosts on the server at runtime.

In the Inspector for the __GhostCollectionAuthoringComponent__, there is one button you can select:
* __Update ghost list__, which scans for Prefabs with __GhostAuthoringComponent__.

For the netcode to work, the ghost collection must be part of the client and server entity worlds.

## Value types

The codegen does not support all value types, but you can create an assembly with a name ending with `.NetCodeGen`. This assembly should contain a class implementing the interface __IGhostDefaultOverridesModifier__. Implement the method `public void ModifyTypeRegistry(TypeRegistry typeRegistry, string netCodeGenAssemblyPath)` and register additional types in the typeRegistry. The types you register will be used by the code-gen.


## Importance
The server operates on a fixed bandwidth, and sends a single packet with snapshot data of customizable size every network tick. It fills the packet with the entities of the highest importance. Several factors determine the importance of the entities: you can specify the base importance per ghost type, which Unity then scales by age. You can also supply your own method to scale the importance on a per-chunk basis.

Once a packet is full, the server sends it and the remaining entities are missing from the snapshot. Because the age of the entity influences the importance, it is more likely that the server will include those entities in the next snapshot. The importance is only calculated per chunk, not per entity.


### Distance based importance
You can use a custom function to scale the importance per chunk. For example, if a singleton entity with the `GhostDistanceImportance` component on it exists on the server, the netcode makes sure that all the ghosts in the World are split into groups based on the tile size in that singleton.

You must add a `GhostConnectionPosition` component to each connection to determine which tile the connection should prioritize. This `GhostSendSystem` passes this information to the `ScaleImportanceByDistance` in `GhostDistanceImportance` which then uses it to scale the importance of a chunk based on its distance in tiles or any other metric you define in your code.

## Entity spawning

When the client side receives a new ghost, the ghost type is determined by a set of classification systems and then a spawn system spawns it. There is no specific spawn message, and when the client receives an unknown ghost ID, it counts as an implicit spawn.

Because the client interpolates snapshot data, Unity cannot spawn entities immediately, unless it was preemptively spawned, such as with spawn prediction. This is because the data is not ready for the client to interpolate it. Otherwise, the object would appear and then not get any more updates until the interpolation is ready.

Therefore normal spawns happen in a delayed manner. Spawning is split into three main types as follows:
* __Delayed or interpolated spawning.__ The entity is spawned when the interpolation system is ready to apply updates. This is how remote entities are handled, because they are interpolated in a straightforward manner.
* __Predicted spawning for the client predicted player object.__ The object is predicted so the input handling applies immediately. Therefore, it doesn't need to be delay spawned. While the snapshot data for this object arrives, the update system applies the data directly to the object and then plays back the local inputs which have happened since that time, and corrects mistakes in the prediction.
* __Predicted spawning for player spawned objects.__ These are objects that the player input spawns, like in-game bullets or rockets that the player fires. The spawn code needs to run on the client, in the client prediction system. Spawn the predicted client version of the ghost prefab and add a __PredictedGhostSpawnRequestComponent__. Then when the first snapshot update for the entity arrives it will apply to that predict spawned object (no new entity is created). After this, the snapshot updates are applied the same as in the predicted spawning for client predicted player object model.

You need to implement some specific code to handle the predicted spawning for player spawned objects. You need to create a system updating in the __ClientSimulationSystemGroup__ after __GhostSpawnClassificationSystem__. The system needs to go through the __GhostSpawnBuffer__ buffer stored on a singleton with a __GhostSpawnQueueComponent__. For each entry in that list it should compare to the entries in the __PredictedGhostSpawn__ buffer on the singleton with a __PredictedGhostSpawnList__ component. If the two entries are the same the classification system should set the __PredictedSpawnEntity__ property in the __GhostSpawnBuffer__ and remove the entry from __GhostSpawnBuffer__.

NetCode spawns entities on the client with a Prefab stored in the `GhostPrefabCollectionComponent` singleton. A `GhostCollectionAuthoringComponent` and a `ConvertToClientServerEntity` component that uses Client as the conversion target creates the Prefab.

## Prespawned ghosts

A ghost instance (an instance of a ghost prefab) can be placed in a subscene in the Unity editor so that it will be treated just like a normal spawned ghost when the player has loaded the data. There are two restrictions for prespwaned ghosts. Firstly, it must be an instance of a ghost prefab which has been registered in the ghost collection. Secondly, it must be place in a subscene.

The ghost authoring component on the prespawned ghost cannot be configured differently than the ghost prefab source, since that data is handled on a ghost type basis.

Each subscene applies prespawn IDs to the ghosts it contains in a deterministic manner. The subscene hashes the component data on the ghosts, which currently is only the `Rotation` and `Translation` components. It also keeps a single hash composed of all the ghost data for the subscene itself.

At runtime, when all subscenes have been loaded, there is a process which applies the prespawn ghost IDs to the ghosts as normal runtime ghost IDs. This has to be done after all subscenes have finished loading and the game is ready to start. It is also done deterministically, so that for each player (server or client), the ghost IDs are applied in exactly the same way. This happens when the `NetworkStreamInGame` component has been added to the network connection. Currently, there is no reliable builtin way to detect when subscenes have been loaded. However, it's possible to do so manually. To do this, add a custom tag to every subscene, then count the number of tags to detect when all subscenes are ready.

An alternative way to detect whether subscenes have finished loading without using tags is to check if the prespawn ghost count is correct. The following example shows one possible solution for checking this number, in this case testing for 7 ghosts across all loaded subscenes:

```c#
[UpdateInGroup(typeof(ClientSimulationSystemGroup))]
public class GoInGameClientSystem : ComponentSystem
{
    public int ExpectedGhostCount = 7;
    protected override void OnUpdate()
    {
        var prespawnCount = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostId>()).CalculateEntityCount();
        Entities.WithNone<NetworkStreamInGame>().ForEach((Entity ent, ref NetworkIdComponent id) =>
        {
            if (ExpectedGhostCount == prespawnCount)
                PostUpdateCommands.AddComponent<NetworkStreamInGame>(ent);
        });
    }
}
```

To create a prespawned ghost from a normal scene you can do the following:
* Right click on the *Hierarchy* in the inspector and click *New Sub Scene*.
* Drag an instance of a ghost prefab into the newly created subscene.

This feature is new and is liable to change in the future. The current implementation has some limitations which are listed below:
* With regard to using subscenes, when placing an object in a subscene, you no longer place the `ConvertToClientServerEntity` component on it as being in a subscene implies conversion to an Entity. Also, it means the option of making an entity only appear on the client or server is now missing. Prespawned ghosts always appear on both client and server as they are just like a normal spawned ghost, and will always be synchronized (as configured) after the game starts.
* Loading a new subscene with prespawned ghosts after starting (entering the game) is currently not supported.
* Only the `Translation` and `Rotation` `IComponentData` components, converted from the `Transform` component, are currently used to generate the prespawn IDs. This means that the prespawn ghosts cannot be placed in the same location and these components are required to use prespawn ghosts.

To look at an example of a scene using prespawn ghosts, see the test in *Assets/Tests/PrespawnTests*. This is used in some of the tests inside the sample project.

## Snapshot visualization tool

To understand what is being put on the wire in the netcode, you can use the prototype snapshot visualization tool, __NetDbg__ in the Stats folder. To open the tool, go to menu: __Multiplayer &gt; Open NetDbg__, and the tool opens in a browser window. It displays a vertical bar for each snapshot Unity receives, with a breakdown of the snapshot’s ghost types. To see more detailed information about the snapshot, click on one of the bars.
> [!NOTE]
> This tool is a prototype. In future versions of the package it will integrate with the Unity Profiler so you can easily correlate network traffic with memory usage and CPU performance.

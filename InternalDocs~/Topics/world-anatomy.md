# NETCODE WORLD UPDATE AND SYSTEMS

Netcode add a lot of systems to the default Entities World. Based on the world type (client, server ,thin-client) the set of systems added to the world are slightly differetnt.

> Note: Server and Thin-Client worlds (headless client) are more streamlined than pure Client in that respect, mostly because the lack of presentation (for both) and simulation (for the latter).

## Worlds creation and execution order

All the Worlds created by Netcode using the `ClientServerBootstrap` interface are automatically updated by the the Unity Engine.

When using the default bootstrapping, the order of creation is the following:
- Server
- Client
- Thin Clients


When a world is created, its three root `ComponentSystemGroup` groups \(Initialization, Simulation, Presentation\)
update are invoked by the UniytEngine Player Loop.

The groups are then tick every single frame, in creation order.

```
<UnityEngine.Initialization>
    (All other engine initalization systems)
    World1.InitializationSystemGroup
    World2.InitializationSystemGroup
    ..
    WorldN.InitializationSystemGroup

<UnityEngine.Update>
    (MonoBehavior and co-routine updates)
    World1.SimulationSystemGroup
    World2.SimulationSystemGroup
    ...
    WorldN.SimulationSystemGroup

<UnityEngine.PreLateUpdate>
    (after MonoBehaviour LateUpdate)
    World1.PresentationSystemGroup
    World2.PresentationSystemGroup
    ..
    WorldN.PresentationSystemGroup
```

### Basic info role of the update phases

The `Initialization` phase is executing at the beginning of the frame. This is where usually early engine system initialize their state
(i.e time). Netcode has very few systems here (more on that later).

The `Update` phase is where most of the GameObject and co-routine update take place (among other thing in the engine) at the beginning of the frame. This is where usually early engine system initialize their state
(i.e time). Netcode has very few systems here (more on that later).
The `SimulationSystemGroup` is appended at the end of that phase, right after all the Monobehaviour Update and co-routine "null" (or let's call it default) update.

The `PreLateUpdate` phase is where all the Monobehaviour LateUpdate (and other internal system) runs. The `PresentationSystemGroup` is intented to runs
all systems that deal with "graphics" or in general with presentation and not simulation. The system is again appended at the end of phase.
That has some interesting consequences:
- The sytems will runs after any physics late-update logic.
- All animators and update already run and scheduled their update jobs.
- All AI post update alrady executed.

> That make a little complex interacting with certain GameObject component at this point (i.e Animators) because by the time they system runs it may be too late, therefore
introducing one frame delay in some cases in committing the necessary changes.


## Netcode Systems map

At a very high level, Netcode adds three new top-level groups to the `SimulationSystemGroup`:

```
- SimulationSystemGroup
    - * NetworkSystemGroup
    - * GhostSimulationSystemGroup
    - * PredictedSimulationSystemGroup
    - FixedUpdateSimulationSystemGroup
    - VariableRateSimulationSystemGroup
    - TransformSystemGroup
    - LateSimulationSystemGroup
```

### NetworkSystemGroup

The `NetworkSystemGroup` was ideally designed to contains all systems that are related to connections, commands and networking.
With time the rule as become lazy, and some other systems, unrelated to that are updating there.

### GhostSimulationSystemGroup

The `GhostSimulationSystemGroup` is a mixed bag of system, all related to:
- update or received ghost data from the network
- spawning and despawning of ghosts
- handling ghost prefabs and serialization setup
- in general everything related to "ghost" entities.

Again, with time the idea has become more vague, and it become a container of every systems that need to run before the `PredictionSimulationSystemGroup` (pretty much).
Some notable responsibilty of that group are:

- **handling of inputs**
- **spawn classification**
- **dispatch commands to the server**

User defined systems *rarely need to update* inside the `NetworkSystemGroup` or `GhostSimulationSystemGroup`.
There are two notable exceptions to that rule (we are going to cover that later), that are:
- systems that polls inputs
- custom user spawn classification systems

### PredictedSimulationSystemGroup

The `PredictedSimulationSystemGroup` as the name implies is responsible for the predictive simulation behaviour of Netcode.
This is the group where all the systems that need to update and modify the state of the simulation, and that need to be "predicted" by the client
should run.

In general, all the systems that are responsible to perform the "simulaton" of the world, and in particular update the state of predicted objects
(and thus simulated also by the client), `should` run inside the `PredictedSimulationSystemGroup`, that guaranteed the necessary "deterministic" update.

> to reiterate on the determinism, we don't require bit-level determinstic behaviour of system, nor Netcode for Entities assure full deterministic output.
> However, under the necessary and sufficient conditions, we expect that the systems should bahave as close as possible as deterministic.

For the Server world, gameplay logic that simulate/change/update the state of replicated objects that are
"only interpolated by the client" (interpolated ghosts) can also run outside  the prediction loop.

While there are not particular reasons for that (the system group run only once per tick as all the others in SimulationSystemGroup) there are a couple of notable exceptions:

- Systems that needs to run at fixed update rate, but different than the simulation tick rate
- Systems that need or prefer to work in "world space" (in case of hierarchy) and require to access the LocalToWorld matrix. Therefore, they usually runs after the
  `TransformSystemGroup`.
- Systems that need to calculate they own LocalToWorld trasnsforms.

## Netcode system map and purpose

To have a more clear picture about the complexity of the World update, at least in term of systems added,
and understand what each individual do/contribute to, it is necessary to expand all the major and top-level Netcode groups.

> All Netcode systems MUST specify exactly what world they are executing into by relying on the `WorldSystemFilter` and/or the`UpdateInGroup` attributes.

```csharp

// Group that run at variable frame rate, on both server and client (unless server sleep in betwen updates)
InitializationSystemGroup
    //work-around for a limitation in the conversion workdflow when a sub-scene is open for edit.
    PrespawnedGhostPreprocessScene (editor only)
    //keep the client ahead of the server (time sync) and calculate the next InterpolatedTick and ServerTick for this frame
    NetworkTimeSystem
    //flush stats to the Netdbg if any is attached.
    GhostStatsFlushSystem
```

```csharp
// Group that run:
// - Variable frame rate on Client
// - Fixed Tick rate on server
SimulationSystemGroup
    BeginSimulationEntityCommandBufferSystem
    // Spawning ghost received from network
    GhostSpawnSystemGroup (client only)
        GhostSpawnSystem (client only)

    //All network and commands related operation. But not the RPC send or receive
    NetworkReceiveSystemGroup
        GhostStatsCollectionSystem
        GhostPresentationGameObjectSystem
        //All network connections update
        NetworkStreamReceiveSystem
        //All decoding or commands and enqueuing in command buffer
        CommandReceiveSystemGroup (server only)
            //Calculate the command age, how on-time the commands are received by the server
            CommandReceiveClearSystem (server only)
        NetworkGroupCommandBufferSystem

    GhostSimulationSystemGroup
        //partition chunks in tiles
        GhostDistancePartitioningSystem  (server only)
        //handle and processes ghost prefabs and build the serialization metedata
        GhostCollectionSystem
        //All prespawn handling
        PrespawnGhostSystemGroup
            /// Track prespawn section load/unload events and send rpc to server to ack the loaded scene for that client
            ClientPrespawnAckSystem
            // Handle acked section reported by the client
            ServerPrespawnAckSystem
            //auto-track section unloaded / loaded on the client. Logic slightly different than server
            ClientTrackLoadedPrespawnSections
            //wait and pre-initialize prespawned ghost (strip components and calculate baselines)
            PrespawnGhostInitializationSystem
            //client-side mapping of id to ghost using the per-sub-scene id-ranges reported by the server
            ClientPopulatePrespawnedGhostsSystem
            //track and map prespawn section to different id-ranges, map ghosts to ids
            ServerPopulatePrespawnedGhostsSystem
            //auto-track section unloaded / loaded
            ServerTrackLoadedPrespawnSections
        //despawn ghosts
        GhostDespawnSystem  (client only)
        NetDebugSystem
        //Receiving and decoding of ghosts
        GhostReceiveSystem  (client only)
        //Has multiple responsibility:
        // - apply the latest data received for predicted ghost
        // - interpolate the ghost snapshot for interpolated ghosts
        // - handle partial tick rollback by re-apply the back to all predicted ghost
        GhostUpdateSystem  (client only)
        // classify how ghost need to be spawn and handle predicted spawning
        GhostSpawnClassificationSystemGroup (client only)
            SpawnClassificationSystem (client only)
            // predicted spawning mapping on tick timing
            DefaultGhostSpawnClassification (client only)
        //All input sampling from user should be done here.
        GhostInputSystemGroup (client only)
            //copy input component data to their internal buffer
            CopyInputToCommandBufferSystemGroup (client only)
        //compare if input are different for the given tick and report that to the prediction loop
        CompareCommandSystemGroup (client only)
        //Send commands to the server for this tick
        CommandSendSystemGroup (client only)
        DebugConnections
        //report RPC errors
        RpcSystemErrors
        //switch ghost mode from predicted or interpolated and vicevrersa
        GhostPredictionSwitchingSystem (client only)
        //placeholder for initializing some data for thin
        GhostPredictionSwitchingSystemForThinClient (client only)

    //Handle the client-side prediction for client using partial execution and drive
    //the fixed step simulation on the server
    PredictedSimulationSystemGroup
        //enable/disable Simulate tag based on the current tick simulated
        GhostPredictionDisableSimulateSystem (client only)
        //buffer -> input component copy. Handle InputEvent count
        CopyCommandBufferToInputSystemGroup
        //Mostly for handling Physics replication
        PredictedFixedStepSimulationSystemGroup
           PhysicsSystemGroup
              ...
              // Copy/Clone the broadphase for the current tick
              PhysicsWorldHistory
        //Playable animation updated during prediction
        GhostAnimationControllerPredictionSystem
        //Calculate the prediction error after the rollback.
        GhostPredictionDebugSystem (client only)
        //Smooth the prediction error
        GhostPredictionSmoothingSystem (client only)
        //Backup the current state of all predicted ghosts on last full tick. Used for partial-tick rollback
        GhostPredictionHistorySystem (client only)
        //Re-enable all ghosts Simulate tag at the end of the prediction
        GhostPredictionEnableSimulateSystem (client only)

    //Aggregate together all systems that setup the current "Serialization" variants.
    DefaultVariantSystemGroup
        TransformDefaultVariantSystem //netcode default variant if nothing is setup
        PhysicsDefaultVariantSystem //netcode default variant if nothing is setup
    GhostComponentSerializerCollectionSystemGroup  //collect all the code-generated serializers

    TransformSystemGroup
        //smooth the local to world transform position of ghost that are switching mode
        PreditionSwitchingSmoothingSystem (client only)
        LocalToWorldSystem
        //Sync the presentation gameobject transform to be identical to the entity local to world matrix.
        GhostPresentationGameObjectTransformSystem

    //Configure what baking configuration to use for this world translate NetCodeConfig to entities data
    ConfigureServerWorldSystem (server only)
    //Configure what baking configuration to use for this world translate NetCodeConfig to entities data
    ConfigureClientWorldSystem (client only)
    //Configure what baking configuration to use for this world translate NetCodeConfig to entities data
    ConfigureThinClientWorldSystem (thin client only)

    EndSimulationEntityCommandBufferSystem
    //Update the GhostAnimationController playables animation graphs.
    GhostAnimationControllerServerSystem  (server only)
    //process RPCs entity request and serialize them intothe outgoing rpc buffer for the given connection
    RpcCommandRequestSystemGroup
    //send and receive RPCs
    RpcSystem
    //send the state snapshot to the client
    GhostSendSystem  (server only)

    PresentationSystemGroup (client only)
        //update the playable animation graph for interpolated ghosts only
        GhostAnimationControllerInterpolationSystem  (client only)
```

There are some other systems (minor) that are purposely left behind for sake of simplificy and clarity,
but the above hierarcny should give a precise-enough idea of the system ordering.


-




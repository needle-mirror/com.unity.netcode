# Getting started with NetCode
This documentation provides a walkthrough of how to create a very simple client server based simulation. This walkthrough describes how to spawn and control a simple Prefab.

## Set up the Project
Open the __Unity Hub__ and create a new Project.

>[!NOTE]
> To use Unity NetCode you must have at least Unity 2020.1.2 installed.

Open the Package Manager (menu: __Window &gt; Package Manager__). At the top of the window, under __Advanced__, select __Show preview packages__. Add the Entities, Hybrid Renderer, NetCode, and Transport packages.

>[!WARNING]
> As of Unity version 2020.1, in-preview packages no longer appear in the Package Manager. To use preview packages, either manually edit your [project manifest](https://docs.unity3d.com/2020.1/Documentation/Manual/upm-concepts.html?_ga=2.181752096.669754589.1597830146-1414726221.1582037216#Manifests) or search for the package in the **Add package from Git URL** field in the Package Manager. For more information, see the [announcement blog for these changes to the Package Manager.](https://blogs.unity3d.com/2020/06/24/package-manager-updates-in-unity-2020-1/?_ga=2.84647326.669754589.1597830146-1414726221.1582037216)

The NetCode package requires the Entities, Hybrid Renderer, and Transport packages to work. To install these packages while they are still in preview, either edit your project manifest to include the target package name, or type the name of the package you want to install into the **Add package from git URL** menu in the Package Manager.

For example, to install the Transport package using the Package Manager, go to **Window** > **Package Manager**, click on the plus icon to open the **Add package from...** sub-menu and click on **Add package from git url...**, then type "com.unity.transport" into the text field and press **Enter**. To install the same package through your package.json manifest file, add "com.unity.transport": "0.4.0-preview.1" to your dependencies list. Version 0.4.0-preview.1 is used here as an example and is not a specific version dependency.

## Create an initial Scene

To begin, you need to set up a way to share data between the client and the server. To achieve this separation in NetCode, you need to create a different World for each client and the server. To share data between the server and the client, create an empty GameObject (called __SharedData__ in the example), and add the __ConvertToClientServerEntity__ component.

![Empty SharedData GameObject](images/world-game-objects.png)<br/>_Empty SharedData GameObject_

![ConvertToClientServerEntity component](images/mixed-world.png)<br/>_ConvertToClientServerEntity component_

Once you set this up you can, for example, spawn a plane in both the client and the server world. To do this, right click the __SharedData__ Prefab and select __3D Object &gt; Plane__ which then creates a plane that is nested under __SharedData__.

![Scene with a plane](images/initial-scene.png)<br/>_Scene with a plane_

## Create a ghost Prefab

To make your Scene run with a client / server setup you need to create a definition of the networked object, which is called a **ghost**.

To create a ghost Prefab, create a cube in the Scene (right click on the Scene and select __3D Object &gt; Cube__). Then select the Cube GameObject under the Scene and drag it into the Project’s __Asset__ folder. This creates a Prefab of the Cube.

![Create a Cube Prefab](images/cube-prefab.png)<br/>_Create a Cube Prefab_

To identify the Cube Prefab, create a simple component with the following code:

```c#
using Unity.Entities;
using Unity.NetCode;

[GenerateAuthoringComponent]
public struct MovableCubeComponent : IComponentData
{
}
```

If you want to add a serialized value to the component, use the __GhostField Attribute__:
```c#
using Unity.Entities;
using Unity.NetCode;

[GenerateAuthoringComponent]
public struct MovableCubeComponent : IComponentData
{
    [GhostField]
    public int ExampleValue;
}
```

Once you create this component, add it to the Cube Prefab. Then, in the Inspector, add the __Ghost Authoring Component__ to the Prefab.

When you do this, Unity automatically adds default values to the Translation and Rotation components.

Start by adding a __Ghost Owner Component__ and changing the __Default Ghost Mode__ to __Owner Predicted__. The __NetworkId__ member of the __Ghost Owner Component__ needs to be set by your code, more on this later. This makes sure that you predict your own movement.

![The Ghost Authoring component](images/ghost-config.png)<br/>_The Ghost Authoring component_

## Hook up the collections
To tell NetCode which Ghosts to use, you need to set up a GhostCollection. Right click on SharedData and select __Create Empty__. Rename it to __GhostCollection__ and then add a __GhostCollectionAuthoringComponent__. Because both the client and the server need to know about these Ghosts, add it to the __SharedData__ Scene.

In the Inspector, select the __Update ghost list__ button.

![Ghost Collection settings](images/ghost-collection.png)<br/>_Ghost Collection settings_

## Establish a connection
Next, you need to make sure that the server starts listening for connections, the client connects, and all connections are marked as "in game" so NetCode can start sending snapshots. You don’t need a full flow in this case, so write the minimal amount of code to set it up.

Create a file called *Game.cs* in your __Assets__ folder and add the following code to the file:

```c#
using System;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Burst;

// Control system updating in the default world
[UpdateInWorld(UpdateInWorld.TargetWorld.Default)]
public class Game : ComponentSystem
{
    // Singleton component to trigger connections once from a control system
    struct InitGameComponent : IComponentData
    {
    }
    protected override void OnCreate()
    {
        RequireSingletonForUpdate<InitGameComponent>();
        // Create singleton, require singleton for update so system runs once
        EntityManager.CreateEntity(typeof(InitGameComponent));
    }

    protected override void OnUpdate()
    {
        // Destroy singleton to prevent system from running again
        EntityManager.DestroyEntity(GetSingletonEntity<InitGameComponent>());
        foreach (var world in World.AllWorlds)
        {
            var network = world.GetExistingSystem<NetworkStreamReceiveSystem>();
            if (world.GetExistingSystem<ClientSimulationSystemGroup>() != null)
            {
                // Client worlds automatically connect to localhost
                NetworkEndPoint ep = NetworkEndPoint.LoopbackIpv4;
                ep.Port = 7979;
                network.Connect(ep);
            }
            #if UNITY_EDITOR
            else if (world.GetExistingSystem<ServerSimulationSystemGroup>() != null)
            {
                // Server world automatically listens for connections from any host
                NetworkEndPoint ep = NetworkEndPoint.AnyIpv4;
                ep.Port = 7979;
                network.Listen(ep);
            }
            #endif
        }
    }
}
```

Next you need to tell the server you are ready to start playing. To do this, use the `Rpc` calls that are available in the NetCode package.

In *Game.cs*, create the following [RpcCommand](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.IRpcCommand.html). This code tells the server that you are ready to start playing.

```c#
public struct GoInGameRequest : IRpcCommand
{
}
```

To make sure you can send input from the client to the server, you need to create an `ICommandData` struct. This struct is responsible for serializing and deserializing the input data. Create a script called *CubeInput.cs* and write the `CubeInput CommandData` as follows:

```c#
public struct CubeInput : ICommandData
{
    public uint Tick {get; set;}
    public int horizontal;
    public int vertical;
}
```

The command stream consists of the current tick and the horizontal and vertical movements. The serialization code for the data will be automatically generated.

To sample the input, send it over the wire. To do this, create a System for it as follows:

```c#
[UpdateInGroup(typeof(ClientSimulationSystemGroup))]
public class SampleCubeInput : ComponentSystem
{
    protected override void OnCreate()
    {
        RequireSingletonForUpdate<NetworkIdComponent>();
    }

    protected override void OnUpdate()
    {
        var localInput = GetSingleton<CommandTargetComponent>().targetEntity;
        if (localInput == Entity.Null)
        {
            var localPlayerId = GetSingleton<NetworkIdComponent>().Value;
            Entities.WithAll<MovableCubeComponent>().WithNone<CubeInput>().ForEach((Entity ent, ref GhostOwnerComponent ghostOwner) =>
            {
                if (ghostOwner.NetworkId == localPlayerId)
                {
                    PostUpdateCommands.AddBuffer<CubeInput>(ent);
                    PostUpdateCommands.SetComponent(GetSingletonEntity<CommandTargetComponent>(), new CommandTargetComponent {targetEntity = ent});
                }
            });
            return;
        }
        var input = default(CubeInput);
        input.Tick = World.GetExistingSystem<ClientSimulationSystemGroup>().ServerTick;
        if (Input.GetKey("a"))
            input.horizontal -= 1;
        if (Input.GetKey("d"))
            input.horizontal += 1;
        if (Input.GetKey("s"))
            input.vertical -= 1;
        if (Input.GetKey("w"))
            input.vertical += 1;
        var inputBuffer = EntityManager.GetBuffer<CubeInput>(localInput);
        inputBuffer.AddCommandData(input);
    }
}
```

Finally, create a system that can read the `CommandData` and move the player.

```c#
[UpdateInGroup(typeof(GhostPredictionSystemGroup))]
public class MoveCubeSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        var group = World.GetExistingSystem<GhostPredictionSystemGroup>();
        var tick = group.PredictingTick;
        var deltaTime = Time.DeltaTime;
        Entities.ForEach((DynamicBuffer<CubeInput> inputBuffer, ref Translation trans, ref PredictedGhostComponent prediction) =>
        {
            if (!GhostPredictionSystemGroup.ShouldPredict(tick, prediction))
                return;
            CubeInput input;
            inputBuffer.GetDataAtTick(tick, out input);
            if (input.horizontal > 0)
                trans.Value.x += deltaTime;
            if (input.horizontal < 0)
                trans.Value.x -= deltaTime;
            if (input.vertical > 0)
                trans.Value.z += deltaTime;
            if (input.vertical < 0)
                trans.Value.z -= deltaTime;
        });
    }
}
```

## Tie it together

The final step is to create the systems that handle when you enter a game on the client and what to do when a client connects on the server. You need to be able to send an `Rpc` to the server when you connect that tells it you are ready to start playing.

```c#
// When client has a connection with network id, go in game and tell server to also go in game
[UpdateInGroup(typeof(ClientSimulationSystemGroup))]
public class GoInGameClientSystem : ComponentSystem
{
    protected override void OnCreate()
    {
    }

    protected override void OnUpdate()
    {
        Entities.WithNone<NetworkStreamInGame>().ForEach((Entity ent, ref NetworkIdComponent id) =>
        {
            PostUpdateCommands.AddComponent<NetworkStreamInGame>(ent);
            var req = PostUpdateCommands.CreateEntity();
            PostUpdateCommands.AddComponent<GoInGameRequest>(req);
            PostUpdateCommands.AddComponent(req, new SendRpcCommandRequestComponent { TargetConnection = ent });
        });
    }
}
```

On the server you need to make sure that when you receive a `GoInGameRequest`, you create and spawn a Cube for that player.

```c#
// When server receives go in game request, go in game and delete request
[UpdateInGroup(typeof(ServerSimulationSystemGroup))]
public class GoInGameServerSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        Entities.WithNone<SendRpcCommandRequestComponent>().ForEach((Entity reqEnt, ref GoInGameRequest req, ref ReceiveRpcCommandRequestComponent reqSrc) =>
        {
            PostUpdateCommands.AddComponent<NetworkStreamInGame>(reqSrc.SourceConnection);
            UnityEngine.Debug.Log(String.Format("Server setting connection {0} to in game", EntityManager.GetComponentData<NetworkIdComponent>(reqSrc.SourceConnection).Value));
            var ghostCollection = GetSingletonEntity<GhostPrefabCollectionComponent>();
            var prefab = Entity.Null;
            var prefabs = EntityManager.GetBuffer<GhostPrefabBuffer>(ghostCollection);
            for (int ghostId = 0; ghostId < prefabs.Length; ++ghostId)
            {
                if (EntityManager.HasComponent<MovableCubeComponent>(prefabs[ghostId].Value))
                    prefab = prefabs[ghostId].Value;
            }
            var player = EntityManager.Instantiate(prefab);
            EntityManager.SetComponentData(player, new GhostOwnerComponent { NetworkId = EntityManager.GetComponentData<NetworkIdComponent>(reqSrc.SourceConnection).Value});
            PostUpdateCommands.AddBuffer<CubeInput>(player);

            PostUpdateCommands.SetComponent(reqSrc.SourceConnection, new CommandTargetComponent {targetEntity = player});

            PostUpdateCommands.DestroyEntity(reqEnt);
        });
    }
}
```

## Test the code

Now you have set up your code, open __Multiplayer &gt; PlayMode Tools__ and set the __PlayMode Type__ to __Client & Server__. Enter Play Mode, and the Cube spawns. Press the __A,S,D,__ and __W__ keys to move the Cube around.

To recap this workflow:

1. Create a GameObject and add the __ConvertToClientServerEntity__ component to hold __SharedData__ between the client and the server.
1. Create a Prefab out of a simple 3D Cube and add a __GhostAuthoringComponent__, a __MovableCubeComponent__ and a __GhostOwnerComponent__.
1. Add the __GhostCollectionAuthoringComponent__ to an empty GameObject to create a GhostCollection. Update the ghost list.
1. Establish a connection between the client and the server.
1. Write an `Rpc` to tell the server you are ready to play.
1. Write an `ICommandData` to serialize game input.
1. Write a client system to send an `Rpc`.
1. Write a server system to handle the incoming `Rpc`.

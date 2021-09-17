# Physics
The NetCode package has some integration with Unity Physics which makes it easier to use physics in a networked game. The integration handles interpolated ghosts with physics, and you can manually enable support for predicted ghosts with physics.

## Interpolated ghosts
For interpolated ghosts it is important that the physics simulation only runs on the server. On the client the ghosts position and rotation are controlled by the snapshots from the server and the client should not run the physics simulation for interpolated ghosts.
In order to make sure this is true NetCode will add a [`PhysicsMassOverride`](https://docs.unity3d.com/Packages/com.unity.physics@0.6/api/Unity.Physics.PhysicsMassOverride.html) component to every ghost which is also a dynamic physics object on the client. The `PhysicsMassOverride` will mark the objects as kinematic - meaning they will not be moved by the physics simulation.
This means that when using NetCode and Unity Physics you cannot use `PhysicsMassOverride` for game specific purposes on the client.

## Predicted ghosts
To use physics simulation for predicted ghosts you must enable it by creating a singleton with the [`PredictedPhysicsConfig`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedPhysicsConfig.html) component. The singleton must exist on both the client and server with compatible values (same simulation frequency). The component lets you specify the physics tick rate as a multiple of the NetCode simulation tick rate, so you can run for example a 60Hz game simulation with 120Hz physics simulation.
When the singleton exists NetCode will move the physics simulation systems into the [`PredictedPhysicsSystemGroup`]((https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedPhysicsSystemGroup.html)) in the [`GhostPredicitonSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostPredictionSystemGroup.html). That means all dynamic physics objects that exist must be ghosts - but it is possible to mix predicted and interpolated ghosts.
Predicted ghosts will also add the `PhysicsMassOverride` component on the client and it will change the component to make the objects kinematic or dynamic depending on if they should be predicted or not.

## Limitations
As mentioned on this page there are some limitations you must be aware of to use physics and NetCode together.
* NetCode will use `PhysicsMassOverride` on the client, you cannot use it for game specific purposes.
* When using physics in client prediction all dynamic physics objects must be ghosts, you cannot have any client-only physics which means you cannot have particles or debris colliding against dynamic physics objects.
* The physics systems will be moved when used in prediciton, along with any system that has an ordering constraint (`UpdateBefore`/`UpdateAfter`) against a system which is part of physics. Any system with implicit ordering requirements (based on which group it is in for example) might need to be modified to maintain correct ordering.
* Physics simulation will not use partial ticks on the client, you must use physics interpolation if you want physics to update more frequently than it is simulating.
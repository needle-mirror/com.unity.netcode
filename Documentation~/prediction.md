# Prediction

Prediction in a multiplayer games means that the client is running the same simulation as the server for the local player. The purpose of running the simulation on the client is so it can predictively apply inputs to the local player right away to reduce the input latency.

Prediction should only run for entities which have the [PredictedGhostComponent](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedGhostComponent.html). Unity adds this component to all predicted ghosts on the client and to all ghosts on the server. On the client, the component also contains some data it needs for the prediction - such as which snapshot has been applied to the ghost.

The prediction is based on a [GhostPredictionSystemGroup](https://docs.unity3d.com/Packages/com.unity.netcode@0latest/index.html?subfolder=/api/Unity.NetCode.GhostPredictionSystemGroup.html) which always runs at a fixed timestep to get the same results on the client and server.

## Client

The basic flow on the client is:
* NetCode applies the latest snapshot it received from the server to all predicted entities.
* While applying the snapshots, NetCode also finds the oldest snapshot it applied to any entity.
* Once NetCode applies the snapshots, the [GhostPredictionSystemGroup](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostPredictionSystemGroup.html) runs from the oldest tick applied to any entity, to the tick the prediction is targeting.
* When the prediction runs, the `GhostPredictionSystemGroup` sets the correct time for the current prediction tick in the ECS TimeData struct. It also sets [GhostPredictionSystemGroup.PredictingTick](https://docs.unity3d.com/Packages/com.unity.netcode@lates/index.html?subfolder=/api/Unity.NetCode.GhostPredictionSystemGroup.html#Unity_NetCode_GhostPredictionSystemGroup_PredictingTick) to the tick being predicted.

Because the prediction loop runs from the oldest tick applied to any entity, and some entities might already have newer data, you must check whether each entity needs to be simulated or not. To perform these checks, call the static method  [GhostPredictionSystemGroup.ShouldPredict](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostPredictionSystemGroup.html#Unity_NetCode_GhostPredictionSystemGroup_ShouldPredict_System_UInt32_Unity_NetCode_PredictedGhostComponent_) before updating an entity. If it returns `false` the update should not run for that entity.

If an entity did not receive any new data from the network since the last prediction ran, and it ended with simulating a full tick (which is not always true when you use a dynamic timestep), the prediction continues from where it finished last time, rather than applying the network data. 

## Server

On the server the prediction loop always runs exactly once, and does not update the TimeData struct because it is already correct. It still sets `GhostPredictionSystemGroup.PredictingTick` to make sure the exact same code can be run on both the client and server.

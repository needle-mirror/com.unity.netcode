# Unity Netcode for Entities
The Netcode for Entities package provides a dedicated server model with client prediction that you can use to create multiplayer games. This documentation covers the main features of the Netcode package.

## Preview package
This package is available as a preview, so it is not ready for production use. The features and documentation in this package might change before it is verified for release.

### Development status
The Netcode for Entities developers are prototyping the package in a simple multi-directional shooter game, similar to Asteroids, as well as another set of samples, publicly available [here](https://github.com/Unity-Technologies/multiplayer), which are used for both showcasing the package features and testing the package internally.

The main focus of the Netcode development team has been to figure out a good architecture to synchronize entities when using ECS. As such, there has not yet been much exploration into how to make it easy to add new types of replicated entities or integrate it with the gameplay logic. The development team will focus on these areas going forward, and are areas where they want feedback. To give feedback on this package, post on the [Unity DOTS Netcode Forum](https://forum.unity.com/forums/dots-netcode.425/).

## Installation

To install this package, follow the [installation](installation.md) instructions.

## Requirements

Netcode for Entities requires you to have Unity version __2022.2.0b8__ or higher.

This package uses Unity’s [Entity Component System (ECS)](https://docs.unity3d.com/Packages/com.unity.entities@latest) as a foundation. As such, you must know how to use ECS to use this package.

## Known issues

* Modifying a variant in the `GhostAuthoringInspectionComponent` back to the default variant throws the following error: `InvalidOperationException: Sequence contains no matching element`. To fix this, remove the override via the right click menu, or by setting the `VariantHash` to 0 in the prefab YAML.
* Making IL2CPP build with code stripping set low or higher crashes the player (missing constructors). Code stripping must be always set to none/minimal.
* When connecting to a server build with the editor as a client (like from frontend menu), make sure the auto-connect ip/port fields in the playmode tools are empty, if not it will get confused and create two connections to the server.
* When using Built-in build, after switching from a Dedicated Server build to a normal Standalone Player (via) the define UNITY_SERVER is not removed from the project.

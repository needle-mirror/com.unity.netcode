# Unity NetCode
The Unity NetCode package provides a dedicated server model with client prediction that you can use to create multiplayer games. This documentation covers the main features of the NetCode package.

## Preview package
This package is available as a preview, so it is not ready for production use. The features and documentation in this package might change before it is verified for release.

### Development status
The Unity NetCode developers are prototyping the package in a simple multidirectional shooter game, similar to Asteroids. The development team chose a very simple game to prototype with because it means they can focus on the netcode rather than the gameplay logic. The under-development [DotsSample](https://github.com/Unity-Technologies/DOTSSample) package also uses the NetCode package to get a more realistic test.

The main focus of the NetCode development team has been to figure out a good architecture to synchronize entities when using ECS. As such, there has not yet been much exploration into how to make it easy to add new types of replicated entities or integrate it with the gameplay logic. The development team will focus on these areas going forward, and are areas where they want feedback. To give feedback on this package, post on the [Unity DOTS Forum](https://forum.unity.com/forums/data-oriented-technology-stack.147/).

## Installation
To install this package, follow the instructions in the [Package Manager documentation](https://docs.unity3d.com/Manual/upm-ui-install.html). Make sure you enable __Preview Packages__ in the Package Manager window.

## Requirements
This version of Unity NetCode is compatible with the following versions of the Unity Editor:

* 2020.1.2 and later (recommended)

This package uses Unity’s [Entity Component System (ECS)](https://docs.unity3d.com/Packages/com.unity.entities@latest) as a foundation. As such, you must know how to use ECS to use this package.

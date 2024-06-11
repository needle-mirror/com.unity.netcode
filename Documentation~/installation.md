# Netcode for Entities project setup

To set up Netcode for Entities, you need to make sure you're using the correct version of the Editor.

## Unity Editor version

Netcode for Entities requires you to have Unity version __2022.3.0f1__ or higher.

## IDE support

The Entities package uses [Roslyn Source Generators](https://docs.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/source-generators-overview). For a better editing experience, we suggest using an IDE that's compatible with source generators. The following IDEs are compatible with source generators:

* Visual Studio 2022+
* Rider 2021.3.3+

## Project setup

1. Open the __Unity Hub__ and create a new __URP Project__.
1. Navigate to the __Package Manager__ (__Window__ > __Package Manager__).
1. Add the following packages using __Add package by name__ under the __+__ menu at the top left of the Package Manager.
    - com.unity.netcode
    - com.unity.entities.graphics

When the Package Manager is done, you can continue with the [next steps](networked-cube.md).

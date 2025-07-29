# Netcode Project Settings reference

Netcode for Entities uses classes from the [Entities](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/index.html) **DOTS Settings** to define Netcode-specific settings. To open these project settings, go to **Edit** > **Project Settings** > **Entities**.

## Netcode Client Target

The **Netcode Client Target** dropdown menu determines whether the resulting client build supports hosting server worlds in-process.

| Netcode Client Target               | Use-cases      |
|-------------------|--------------------------------------------|
| `ClientAndServer` | Users can host their own servers (via UI) in the main game executable. Calling `ClientServerBootstrap.CreateServerWorld` will work.  |
| `ClientOnly`      | The server can only be hosted by you, the developer. Use this option to ship a DGS (Dedicated Game Server) executable alongside your game executable. Use `ClientOnly` for the game client build and `ClientAndServer` for the DGS build (automatic). Your players won't have access to server hosting functionality and calling `ClientServerBootstrap.CreateServerWorld` throws a `NotSupportedException`. |

The **Build Type** setting is only valid for non-DGS build targets. Client-hosted servers are supported in standalone, console, and mobile builds.

| Build Type            | Netcode Client Target | Defines                                                                                                |
|-----------------------|-----------------------|-------------------------------------------------------------------------------------------------------|
| Standalone Client     | `ClientAndServer`      | Neither the `UNITY_CLIENT`, nor the `UNITY_SERVER` are set (not in built players, nor in-Editor). |
| Standalone Client     | `ClientOnly`           | The `UNITY_CLIENT` define is set in the build (but not in-Editor).                            |
| Dedicated Game Server | N/A                   | The `UNITY_SERVER` define is set in the build (but not in-Editor).                           |

For either build type, specific baking filters can be specified in the DOTS project settings, as described in the following section.

### Excluded Baking System Assemblies

To build a standalone server, you need to switch to a `Dedicated Server` platform. When building a server, the `UNITY_SERVER` define is set automatically (and also automatically set in the Editor). The DOTS project setting will reflect this change by using the setting for the server build type.

### Additional Scripting Defines

Use the following scripting defines to determine mode-specific baking settings (via `Excluded Baking System Assemblies` and `Additional Scripting Defines`) for both the Editor and builds. For example, the inclusion and exclusion of specific C# assemblies.

| Setting                           | Description    |
|---------------------------------------|-------------------|
| **Netcode Client Target**            | Determine whether or not you want the resulting client build to support hosting a game (as a server). |
| **Excluded Baking System Assemblies** | Add assembly definition assets to exclude from the baking system. You can set this for both client and server setups. |
| **Additional Scripting Defines**      | Add additional [scripting defines](https://docs.unity3d.com/Manual/CustomScriptingSymbols.html) to exclude specific client or server code from compilation. |

## `NetCodeConfig` ScriptableObject

Netcode for Entities has a [ScriptableObject](https://docs.unity3d.com/Manual/class-ScriptableObject.html) called `NetCodeConfig` that allows you to change `ClientServerTickRate`, `ClientTickRate`, `GhostSendSystemData`, and `NetworkConfigParameter` (from Unity Transport) parameters without writing any C#. It also has a dedicated 'Netcode for Entities' Project Settings page under **Edit** > **Project Settings** > **Multiplayer**.  Refer to the [`NetCodeConfig` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetCodeConfig.html) for more information on each property.

You can also refer to the API documentation for [`ClientServerTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html), [`ClientTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientTickRate.html), [GhostSendSystemData](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostSendSystemData.html), and [`NetworkConfigParameter`](https://docs.unity3d.com/Packages/com.unity.transport@latest/index.html?subfolder=/api/Unity.Networking.Transport.NetworkConfigParameter.html).

### Using  `NetCodeConfig`

1. Create a `NetCodeConfig` ScriptableObject via either Unity's **Create** menu, the **Multiplayer** menu, or the **Project Settings** helper button. Default values are the recommended defaults.
2. Open the **Multiplayer Project Settings** window, and set your ScriptableObject as the global one.
    * **Warning**: This action may cause runtime errors in your project, as this config will clobber any user-code which adds, removes, or modifies these singleton components directly.
3. Modify any settings you'd like to. Most fields support live-tweaking, and those that don't are disabled during Play mode.

## Additional resources

* [Entities Project Settings reference](https://docs.unity3d.com/Packages/com.unity.entities@latest/index.html?subfolder=/manual/editor-project-settings.html)

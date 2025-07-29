# Host migration in Asteroids

This sample demonstrates an implementation of [host migration](host-migration.md) in Netcode for Entities using the Asteroids sample as a base.

For more general information about host migration in Netcode for Entities, refer to the [host migration page](host-migration.md).

## Requirements

* The Asteroids sample project requires Unity 6 (at least 6.0.23f1), because it uses new features in both the [Dedicated Server](https://docs.unity3d.com/Packages/com.unity.dedicated-server@latest?subfolder=/manual/index.html) and [Multiplayer Play Mode](https://docs-multiplayer.unity3d.com/mppm/current/about/) packages. The host migration API itself, however, works in Unity 2022.3 like the Netcode for Entities package itself does.
* This sample project needs to be linked to a project in the Unity Cloud Dashboard. It needs to be configured to use [Player Authentication](https://docs.unity.com/ugs/en-us/manual/authentication/manual/get-started), [Relay](https://docs.unity.com/ugs/en-us/manual/relay/manual/get-started), and [Lobby](https://docs.unity.com/ugs/en-us/manual/lobby/manual/get-started) services.

## Sample steps

1. Create a new Unity project and link it to a project ID as described in the [requirements](#requirements).
    * Optionally, navigate to **Lobby** > **Config** in the Unity Cloud Dashboard and change the **Active Lifespan** to 120s, **Disconnect Removal Timeout** to 60s and **Disconnect Host Migration Time** to 5s.
2. Open the Multiplayer Play Mode window by navigating to **Window** > **Multiplayer Play Mode** and start two virtual player instances. Set both to **Client and Server** roles.
3. Enter Play mode in the _Frontend_ scene (inside _Assets/Samples/HelloNetcode/1_Basics/01_BootstrapAndFrontend_) and select the _Asteroids_ sample in the drop-down list. Select the **Enable Host Migration** toggle.
    * If you change the lobby name, make sure it's also changed on all other instances.
    * To spawn the player ship press the **spacebar** key
4. Pick the Editor or one of the virtual players to run as the initial host and select **Start Client & Server**.
5. Wait for the host migration statistics to appear in the lower right corner. When they appear, the lobby connection is ready to handle host migrations.
    * The lower right corner also displays whether the connected instance is a server or client.
6. In the other instances, select **Join Existing Game**.
7. Select the **Return To Main Menu** button in the corner on the host to exit the game and lobby and trigger a host migration.
    * To trigger a host migration with a timeout it's better to use a standalone build and terminate the player
8. One of the other instances will become the host and display migration update statistics in the corner. Other instances will join it automatically.
9. The host data should have been migrated between hosts. All the asteroids and ship positions will be the same as they were before, the ship colors will also be the same and stay that way when the ship is destroyed and re-spawns.

## Ensuring that state is migrated properly in Asteroids

* A color is applied to each player ship. To ensure that this color is kept consistent across host migration, you need to associate the player color to a connection and use a special ghost prefab to contain the host data that needs to be migrated.
  * The server assigns a `PlayerColor` component to each connection when it's accepted. When a player ship owned by that connection spawns, the color component is added to the ship.
  * When a migration occurs, all the user components on the connection entity are migrated as well, including the `PlayerColor` component.
  * A `PlayerColorNext` server-only ghost component is present on a special `HostConfig` ghost entity. This ghost only contains server data and thus has nothing to replicate to clients. The `HostConfig` entity stores which color will be assigned to the next client connection (an integer incrementing from 1 until it wraps after 12 connections, refer to [`Unity.NetCode.NetworkIdDebugColorUtility`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkIdDebugColorUtility.html) helper class).
* When a reconnected connection is detected on the client, it's placed in-game by adding a [`NetworkStreamInGame`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamInGame.html) component to it when the `LevelComponent` has been created. This means that the client has configured the level according to the server command and is ready to start play. Refer to `Asteroids.Client.HostMigrationSystem`.
* The server automatically spawns extra asteroids when their number is lower than the limit configured for the level. During host migration this system needs to pause and wait until the host migration data has been deployed, or it will spawn the full amount of asteroids before the host migration process also spawns the asteroids included in the host data. It does this by exiting the system's update loop when a [`HostMigrationInProgress`](host-migration-api) component is detected.


## Additional resources

* [Host migration](host-migration.md)

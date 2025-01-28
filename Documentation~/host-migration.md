# Host migration

Host migration improves session persistence when using client hosting where every client is capable of picking up the hosting role. The current host will save relevant session data to the cloud and if it leaves (gracefully or because of accidental disconnection), a client will download this data, restore the session and accept connection from the other clients.

Host migration in Netcode for Entities contains functions for saving current host data, restoring it in new server worlds and reconnecting clients to such new hosts. For detecting the host connection status and handling actual migration of clients to new hosts a project needs to uses the Unity Cloud services. See the [host migration feature](https://docs.unity.com/ugs/manual/lobby/manual/host-migration) of the [Unity Lobby service](https://docs.unity.com/ugs/manual/lobby/manual/unity-lobby-service), [Unity Relay](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction), and [Unity Authentication](https://docs.unity.com/ugs/en-us/manual/authentication/manual/overview). This requires your project to be linked to a project in the [Unity Cloud Dashboard](https://cloud.unity.com/).

Refer to the [Host migration in Asteroids sample](host-migration-sample.md) for an example implementation of host migration, which is described further in the [Host migration process section](#host-migration-process).

## Host migration process

Hosts upload information at regular intervals to the lobby, including the list of connected clients, added components, loaded scenes, and all ghost and ghost prefab information.

When the host leaves or is disconnected, the relay connection is lost and the lobby notifies all connected clients. One of the clients is chosen as the new host, requests a new [relay allocation](https://docs.unity.com/ugs/en-us/manual/relay/manual/connection-flow#1), and updates the lobby data with the new relay allocation information. The other clients can then join the new relay allocation when they receive the lobby update.

After the host role is migrated, the new host downloads the host migration data (storage location is provided by lobby) and creates a new server world based on that data. Ghosts are instantiated and their ghost component data deployed such that they're in the same state as when the last lobby update was collected. When connections from clients arrive, the lobby identifies which ones were previously connected and which ghosts were owned by them, ensuring that the game state is maintained for all clients.

## Host migration API

| `HostMigration` class | Description |
|-|-|
| `GetHostMigrationData(data)` | Get the current host migration data from the host migration system, and copy into the given native list. The list is resized to fit the data if needed. |
| `TryGetHostMigrationData(data, size)` | Get the current host migration data from the host migration system, and copy into the given buffer. The amount of data copied is placed in the size variable. If the buffer is too small the required size will be be in the size variable. |
| `ConfigureClientAndConnect(clientWorld, driverConstructor, serverEndpoint)` | Connect to a specific endpoint after a host migration. The client driver store and network driver will be reconfigured to use the latest relay data. |
| `MigrateDataToNewServerWorld(driverConstructor, migrationData)` | Create a server world and populate it with the given host migration data blob (downloaded from lobby). A relay allocation will be created and the lobby updated with latest join code. The local client will call the above API and connect directly to the new server world via IPC driver interface. |

| Components | Description |
|-|-|
| `IsReconnected` | This component is added to all connections on clients and the server so they can react to being reconnected. The spawned ghosts on the new host also receive this component, so if there are any fixes needed you can query against this component. |
| `EnableHostMigration` | Enable the host migration system. When enabled the system will collect the host migration data at the interval specified in the config, and update the last update time in the stats component. |
| `HostMigrationInProgress` | This component is used to detect when a host migration is in progress and when it's complete. |
| `HostMigrationConfig` | A singleton component that exposes a few options to modify in the host migration system. |
| `HostMigrationStats` | A few statistics about the lobby operations like the data blob size. This component also contains the last update time of the host migration data which can be used to see when it's time to upload it again.|

### `HostMigrationConfig` component options

The `HostMigrationConfig` component has the following options:

* `StorageMethod`: Switch between using plain JSON or binary serializers from the serialization package, or the `DataWriter` buffer which is compressed and Base64 encoded (the default).
* `StoreOwnGhosts`: Enable or disable saving of local client-owned ghosts on the host. When the host disconnects this client also disappears, so this data might be unnecessary (defaults to false).
* `MigrationTimeout`: How long to wait for ghost prefabs to be loaded (defaults to 10 seconds).
* `ServerUpdateInterval`: At what interval should host migration data be updated. The lowest possible value is 1 second because of rate limits (default is 2 seconds).

## Adding host migration to your project

Certain things need to be considered when making use of host migration. The following data is saved and restored on the new host:

- All user components on the connection entity on the server as well as the `NetworkStreamInGame` component presence. The connection entity on the client has no special handling.
- All ghosts and their ghost components. The full component data of ghost components is saved/restored, not just ghost fields.
- Server-only components with at least one variable marked with a `GhostField` attribute. 
- Only data which is normally included in [snapshots](ghost-snapshots.md) is supported (components and dynamic buffers). For example, native containers aren't included in the migration data.

[`NetworkStreamInGame`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamInGame.html) requires special consideration on the client. After a migration, the server creates existing and new connections in-game after all subscenes have been loaded. No ghosts are respawned before at least one connection is in-game (this is usually the local client which immediately connects after the local server starts). On the client side, however, it can be difficult to tell when it's safe to start streaming ghost snapshots and `NetworkStreamInGame` needs to be added manually. To treat re-connections differently, you can check if the connection on the client has the `IsReconnected` component added. If there's no difference in the connection flow depending on re-connections or new connections then nothing needs to be done. For example, if the server is sending a `LoadLevel` style RPC to the client after which it will place itself in game.

If there are entity variables in ghost components or any data that won't have a valid reference when it's been moved between hosts, then these instances need to be fixed after a migration. This can be done by querying against particular components on ghost entities with the `IsReconnected` tag added to identify respawned ghosts. Some other data needs to added instead or in addition to this to help correct these variables to reflect the new host state. For example, the entity could be looked up via some other component data (ID or name) used to identify it.

> [!NOTE]
> Ghost IDs and connection Network IDs will not be identical on the new host compared to the old host. When the new host respawns ghosts they're being allocated fresh IDs from the new host ghost ID pool. The same goes for the Network IDs on connection: they will be assigned from 1 and onwards on the new host, and the ordering will likely be different than on the old host.

## Known issues

- Sometimes during a host migration the new host will fail to establish a connection to the relay server. This will fail the host migration and the clients will be kept waiting for the new host to report the new relay join code to the lobby for them to connect to. If the host join code is reported before the relay connection is fully established, the clients may fail with "allocation ID not found" errors. If this happens often it may help to _switch to the UDP connection type_ when setting up the relay data before connecting/listening.
- Sometimes no lobbies are detected, but you can still connect directly to the lobby via its ID. Waiting a bit and trying again usually works.
- When burst is enabled the json/binary serialization methods will not work. `DataStorageMethod.StreamCompressed` must be selected in the host migration config.
- Sometimes migration of prespawn ghost data will fail with different errors like _"Failed to decode ghost -2147483647 of type ScoreTracker(6), got 9 bits, expected 4 bits"_, _"Received a ghost (ID -2147483647 Entity(40:3)) with an invalid ghost type 5 (expected 6)"_ or the singleton component `PrespawnSceneLoaded` will have multiple instances.



## Additional resources

* [Host migration in Asteroids sample](host-migration-sample.md)
* [Unity Lobby documentation](https://docs.unity.com/ugs/en-us/manual/lobby/manual/unity-lobby-service)
* [Unity Relay documentation](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction)
* [Unity Authentication documentation](https://docs.unity.com/ugs/en-us/manual/authentication/manual/overview)
* [`NetworkStreamInGame` API documentation](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamInGame.html)

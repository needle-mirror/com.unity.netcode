# Host migration API and components

> [!NOTE]
> Host migration is an experimental feature so the API and implementation can change in the future. By default it's not exposed, enable it by adding the `ENABLE_HOST_MIGRATION` define in the __Scripting Define Symbols__ in the __Player__ tab of the project settings.

Understand the host migration API, components, and component options.

## Host migration API

| `HostMigration` class | Description |
|-|-|
| `GetHostMigrationData(world, data)` | Get the current host migration data from the host migration system in the given server world and copy it into the native list. The list is resized to fit the data if required. |
| `TryGetHostMigrationData(world, data, size)` | Get the current host migration data from the host migration system in the given server world and copy it into the buffer. The amount of data copied is placed in the size variable. If the buffer is too small, the required size will be in the size variable. |
| `SetHostMigrationData(world, data)` | Deploy the given migration data in the target server world. This would be the data downloaded from the lobby service but with a world manually created. |
| `ConfigureClientAndConnect(clientWorld, driverConstructor, serverEndpoint)` | Connect to a specific endpoint after a host migration. The client driver store and network driver will be reconfigured to use the latest relay data. |
| `MigrateDataToNewServerWorld(driverConstructor, migrationData)` | Create a server world and populate it with the given host migration data blob (downloaded from lobby). A relay allocation will be created and the lobby updated with latest join code. The local client will call the above API and connect directly to the new server world via IPC driver interface. |

## Host migration components

| Components | Description |
|-|-|
| `NetworkStreamIsReconnected` | This component is added to all connections on clients and the server so that they can react to being reconnected. The spawned ghosts on the new host also receive this component, so if there are any fixes needed you can query against this component. |
| `EnableHostMigration` | Enable the host migration system. When enabled, the system collects host migration data at the interval specified in `HostMigrationConfig`, and updates the last update time in `HostMigrationStats`. |
| `HostMigrationInProgress` | This component is used to detect when a host migration is in progress and when it's complete. |
| `HostMigrationConfig` | A singleton component that exposes a few options to modify in the host migration system. |
| `HostMigrationStats` | A few statistics about the lobby operations like the data blob size. This component also contains the last update time of the host migration data which can be used to see when it's time to upload it again.|

### `HostMigrationConfig` component options

The `HostMigrationConfig` component has the following options:

* `StorageMethod`: Switch between using plain JSON or binary serializers from the serialization package, or the `DataWriter` buffer which is compressed and Base64 encoded (the default).
* `StoreOwnGhosts`: Enable or disable saving of local client-owned ghosts on the host. When the host disconnects this client also disappears, so this data might be unnecessary (defaults to false).
* `MigrationTimeout`: How long to wait for ghost prefabs to be loaded (defaults to 10 seconds).
* `ServerUpdateInterval`: At what interval should host migration data be updated. The lowest possible value is 1 second because of rate limits (default is 2 seconds).

## Additional resources

* [Introduction to host migration](host-migration-intro.md)
* [Add host migration to your project](add-host-migration.md)

# Host migration API and components

Understand the host migration API, components, and component options.

## Host migration API

| `HostMigrationData` class | Description |
|-|-|
| `Get(fromWorld, toData)` | Get the current host migration data from the host migration system in the given server world and copy it into the native list. The list is resized to fit the data if required. |
| `Set(fromData, toWorld)` | Deploy the given migration data in the target server world. This would be the data downloaded from the lobby service but with a world manually created. |

## Host migration components

| Components | Description |
|-|-|
| `NetworkStreamIsReconnected` | This component is added to all connections on clients and the server so that they can react to being reconnected. The spawned ghosts on the new host also receive this component, so if there are any fixes needed you can query against this component. |
| `EnableHostMigration` | Enable the host migration system. When enabled, the system collects host migration data at the interval specified in `HostMigrationConfig`, and updates the last update time in `HostMigrationStats`. |
| `HostMigrationInProgress` | This component is used to detect when a host migration is in progress and when it's complete. |
| `HostMigrationConfig` | A singleton component that exposes a few options to modify in the host migration system. |
| `HostMigrationStats` | A few statistics about the lobby operations like the data blob size. This component also contains the last update time of the host migration data which can be used to see when it's time to upload it again.|

The `HostMigrationConfig` component has the following options:

* `StoreOwnGhosts`: Enable or disable saving of local client-owned ghosts on the host. When the host disconnects this client also disappears, so this data might be unnecessary (defaults to false).
* `MigrationTimeout`: How long to wait for ghost prefabs to be loaded (defaults to 10 seconds).
* `ServerUpdateInterval`: The interval at which host migration data should be updated. The default is 2 seconds. 0 seconds means data is collected on every system update.

The `HostMigrationStats` component has the following information:

* `GhostCount`: How many ghosts are present in the host migration data.
* `PrefabCount`: How many ghost prefabs are in the host migration data.
* `UpdateSize`: The size of the last serialized host migration data blob.
* `TotalUpdateSize`: The total size collected so far from the host migration system.
* `LastDataUpdateTime`: How many ghosts are present in the host migration data.


## Additional resources

* [Introduction to host migration](host-migration-intro.md)
* [Add host migration to your project](add-host-migration.md)

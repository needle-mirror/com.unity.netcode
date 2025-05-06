# Host migration systems and data

> [!NOTE]
> Host migration is an experimental feature so the API and implementation can change in the future. By default it's not exposed, enable it by adding the `ENABLE_HOST_MIGRATION` define in the __Scripting Define Symbols__ in the __Player__ tab of the project settings.

Set up host migration systems in your project to enable host migration in a client hosted networking experience.

Netcode for Entities contains [APIs](host-migration-api.md) for gathering host migration data into a buffer, which can then be used with the Multiplayer Services SDK to upload to the host migration service, and for deploying this data to a new server after a migration.

Use the information on this page in conjunction with [Lobby and Relay integrations](lobby-relay-integrations.md) to set up host migration in your project.

## Start the host migration systems

To enable host migration, create an `EnableHostMigration` singleton component in the server world. A default `HostMigrationConfig` is created, which has [configuration options](host-migration-api.md#hostmigrationconfig-component-options) for host migration, including how frequently the [host migration data](host-migration-intro.md#host-migration-data) should be automatically gathered.

```
var serverWorld = ClientServerBootstrap.ServerWorld;
serverWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
```

## Get host migration data for uploading

Every time the host migration data is updated, the timestamp in the `HostMigrationStats` singleton component is also updated (`LastDataUpdateTime`). You can use this to automate the upload of host migration data to the lobby on a regular basis, as in the code example below.

```
var uploadData = new NativeList<byte>(Allocator.Temp);
if (SystemAPI.TryGetSingleton<HostMigrationStats>(out var stats) && stats.LastDataUpdateTime > m_LastUpdateTime)
{
    HostMigration.GetHostMigrationData(ref uploadData);
    var uploadArray = uploadData.AsArray().ToArray();
    LobbyService.Instance.UploadMigrationDataAsync(m_MigrationConfig, uploadArray, new LobbyUploadMigrationDataOptions());
}
```

## Deploy host migration data to a new server

When a host migration event occurs, the client that's becoming the new host needs to download the host migration data and deploy it to a new server world. This world then needs to take over hosting responsibilities in the lobby. There's a helper function you can use to create a new server world with the provided driver constructor (which contains relay information) and migrate the provided migration data into this world. Then finally start listening and connect the local client world to this new server instance.

```
var migrationData = await LobbyService.Instance.DownloadMigrationDataAsync(m_MigrationConfig, new LobbyDownloadMigrationDataOptions());


var allocation = await RelayService.Instance.CreateAllocationAsync(10);
var hostRelayData = allocation.ToRelayServerData("dtls");
var driverConstructor = new HostMigrationDriverConstructor(hostRelayData, new RelayServerData());


var arrayData = new NativeArray<byte>(migrationData.Data.Length, Allocator.Temp);
var slice = new NativeSlice<byte>(arrayData);
slice.CopyFrom(migrationData.Data);


if (!HostMigration.MigrateDataToNewServerWorld(driverConstructor, ref arrayData))
{
    Debug.LogError($"Host migration failed while migrating data to new server world");
}
```

## Connect clients to the new host

After a host migration event every client needs to connect to the new host (except the client that became the new host). To do this, each client needs to recreate the network driver in the client world to set up the new relay allocation from the new host. There's a helper function called `HostMigration.ConfigureClientAndConnect` which makes this task simpler and tags the new connection with the `NetworkStreamIsReconnected` component.

```
var allocation = await RelayService.Instance.JoinAllocationAsync(newJoinCode);
var relayData = allocation.ToRelayServerData("dtls");
var driverConstructor = new HostMigrationDriverConstructor(new RelayServerData(), relayData);
HostMigration.ConfigureClientAndConnect(ClientServerBootstrap.ClientWorld, driverConstructor, relayData.Endpoint);
```

## Additional resources

* [Host migration API and components](host-migration-api.md)
* [Lobby and Relay integrations](lobby-relay-integration.md)
